using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ForgePilot.Services.ClaudeCli.Permissions;

public sealed class PermissionBroker : IPermissionBroker
{
    private readonly ILogger<PermissionBroker> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();

    /// <summary>
    /// Tools the user chose not to be asked about again.
    ///
    /// The MCP permission protocol carries only allow and deny — there is no
    /// wire-level "remember this" — so "don't ask again" is enforced here, by
    /// auto-allowing later requests for the same tool without raising
    /// <see cref="PermissionRequested"/>.
    ///
    /// Scope is deliberately narrow: the broker is per chat session, the set
    /// lives only in memory, and nothing is written to disk. A remembered
    /// allowance therefore dies with the session rather than silently widening
    /// what the CLI may do in some later one.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _alwaysAllowedTools =
        new(StringComparer.OrdinalIgnoreCase);

    public PermissionBroker(ILogger<PermissionBroker> logger)
    {
        _logger = logger;
    }

    public event Action<PermissionRequest>? PermissionRequested;

    /// <summary>
    /// Stops prompting for <paramref name="toolName"/> for the rest of this session.
    /// </summary>
    public void AlwaysAllowTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        _alwaysAllowedTools[toolName] = 0;
        _logger.LogInformation("[PermissionBroker] Auto-allowing {Tool} for the rest of the session", toolName);
    }

    public Task<PermissionDecision> SubmitAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        // Short-circuit before the UI ever sees it.
        if (_alwaysAllowedTools.ContainsKey(request.ToolName))
        {
            var inputJson = request.Input.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : request.Input.GetRawText();
            return Task.FromResult(PermissionDecision.Allow(inputJson));
        }

        var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, tcs))
        {
            _logger.LogWarning("[PermissionBroker] Duplicate request id {Id}", request.Id);
            return Task.FromResult(PermissionDecision.Deny("Duplicate permission request id"));
        }

        // Cancellation: if the CLI/process is torn down before the user replies,
        // synthesize a deny so the MCP child doesn't hang forever.
        var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
                pending.TrySetResult(PermissionDecision.Deny("Cancelled"));
        });
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        try
        {
            PermissionRequested?.Invoke(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionBroker] PermissionRequested handler threw");
        }

        return tcs.Task;
    }

    public void Resolve(string requestId, PermissionDecision decision)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(decision);
        }
        else
        {
            _logger.LogWarning("[PermissionBroker] Resolve for unknown request id {Id}", requestId);
        }
    }

    public void CancelAllPending()
    {
        // Stopping a turn also forgets any remembered allowances. Stop is the
        // user pulling the handbrake; silently keeping tools pre-approved after
        // it would be the opposite of what they asked for.
        _alwaysAllowedTools.Clear();

        var deny = PermissionDecision.Deny("Cancelled by user");
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                _logger.LogInformation("[PermissionBroker] CancelAllPending denying {Id}", key);
                tcs.TrySetResult(deny);
            }
        }
    }
}
