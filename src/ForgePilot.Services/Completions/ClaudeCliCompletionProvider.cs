using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ForgePilot.Services.ClaudeCli;
using ForgePilot.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgePilot.Services.Completions;

/// <summary>
/// Inline completions through the Claude Code CLI, authenticated by the same
/// subscription login the chat window uses. No API key, and nothing billed per
/// request.
///
/// <para><b>Why a long-lived process.</b> Measured on this machine, a one-shot
/// <c>claude -p</c> costs 7.5–11s end to end, and a trivial prompt costs nearly
/// as much as a real one — the overhead is per-invocation CLI setup, not
/// generation, so no amount of prompt trimming touches it. (Node boot alone is
/// ~1s, so it is not process spawn either.) Keeping one process alive pays that
/// cost once and leaves only the model round trip per completion.</para>
///
/// <para><b>Why not the chat's process host.</b> That host is wired to the MCP
/// permission pipe and the question broker, and its conversation is the user's.
/// Completions must not appear in that transcript, must not trigger permission
/// banners, and must not queue behind a long agent turn.</para>
///
/// <para><b>Flags.</b> <c>--safe-mode</c> drops CLAUDE.md, MCP servers, hooks
/// and plugins while explicitly leaving auth working; <c>--tools ""</c> removes
/// the tool set entirely, so a completion can never touch the filesystem.
/// <c>--bare</c> looks similar but is unusable here: it reads auth strictly
/// from <c>ANTHROPIC_API_KEY</c>, never the OAuth login — the exact thing this
/// provider exists to avoid.</para>
/// </summary>
public sealed class ClaudeCliCompletionProvider : ICompletionProvider, IDisposable
{
    /// <summary>
    /// Every completion is a fresh turn appended to the same CLI conversation,
    /// so history grows without ever being useful — earlier completions are
    /// unrelated to the current caret. The process is recycled after this many
    /// turns to keep the prompt small and predictable.
    /// </summary>
    private const int MaxTurnsPerProcess = 20;

    /// <summary>
    /// Ceiling on one completion. Past this the suggestion is worthless anyway,
    /// and holding the single-request lock any longer would stall the next one.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private const string SystemPrompt =
        "You are a code completion engine. You will be given a file with <CURSOR> marking a " +
        "caret position. Reply with ONLY the code that should be inserted at <CURSOR>. " +
        "No prose, no explanation, no markdown fences, and never repeat code that already " +
        "surrounds the cursor. If nothing sensible can be completed, reply with nothing at all.";

    private readonly ForgePilotOptions _options;
    private readonly CompletionOptions _completionOptions;
    private readonly ILogger _logger;

    // One request at a time: the process is a single conversation, so two
    // concurrent turns would interleave on the same stdin/stdout pair.
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _turnsOnCurrentProcess;
    private bool _disposed;

    public ClaudeCliCompletionProvider(
        ForgePilotOptions options,
        CompletionOptions completionOptions,
        ILogger<ClaudeCliCompletionProvider>? logger = null)
    {
        _options = options;
        _completionOptions = completionOptions;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Always true: authentication belongs to the CLI, so there is nothing to
    /// configure here. A CLI that is not logged in surfaces that on first use
    /// rather than being predictable up front.
    /// </summary>
    public bool IsConfigured => true;

    public async Task<string?> CompleteAsync(CompletionContext context, CancellationToken cancellationToken)
    {
        if (_disposed || context.IsTriviallyEmpty) return null;

        // Don't queue: if a completion is already running, the caret has moved
        // on and waiting would only produce a stale suggestion late.
        if (!await _requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            return await RequestAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the user keeps typing. Not an error, but the
            // process may be mid-response, so it cannot be reused as-is.
            StopProcess();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Completions] CLI request failed");
            StopProcess();
            return null;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string?> RequestAsync(CompletionContext context, CancellationToken cancel)
    {
        EnsureProcess();

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = BuildPrompt(context)
                    }
                }
            }
        });

        await _stdin!.WriteLineAsync(payload).ConfigureAwait(false);
        await _stdin!.FlushAsync().ConfigureAwait(false);

        _turnsOnCurrentProcess++;

        var text = await ReadResultAsync(cancel).ConfigureAwait(false);

        // Recycle before the conversation grows enough to slow every later
        // completion down.
        if (_turnsOnCurrentProcess >= MaxTurnsPerProcess) StopProcess();

        return Clean(text);
    }

    /// <summary>
    /// Reads stream-json lines until the turn's <c>result</c> event arrives.
    /// Everything else on the stream (init, partial assistant messages) is
    /// ignored — only the final text is wanted.
    /// </summary>
    private async Task<string?> ReadResultAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var line = await _stdout!.ReadLineAsync().ConfigureAwait(false);

            // Null means the CLI exited — usually a login problem or a bad CLI
            // path. Surface nothing and let the next call restart it.
            if (line is null) return null;
            if (line.Length == 0) continue;

            JsonElement evt;
            try
            {
                using var doc = JsonDocument.Parse(line);
                evt = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (!evt.TryGetProperty("type", out var type) || type.GetString() != "result")
                continue;

            if (evt.TryGetProperty("is_error", out var isError) &&
                isError.ValueKind == JsonValueKind.True)
            {
                _logger.LogWarning("[Completions] CLI reported an error result");
                return null;
            }

            return evt.TryGetProperty("result", out var result) ? result.GetString() : null;
        }

        cancel.ThrowIfCancellationRequested();
        return null;
    }

    private string BuildPrompt(CompletionContext context)
    {
        var language = string.IsNullOrWhiteSpace(context.LanguageId) ? "code" : context.LanguageId;

        return new StringBuilder()
            .Append("Language: ").Append(language).Append('\n')
            .Append("File: ").Append(SafeFileName(context.FilePath)).Append("\n\n")
            .Append("<code>\n")
            .Append(context.Prefix)
            .Append("<CURSOR>")
            .Append(context.Suffix)
            .Append("\n</code>")
            .ToString();
    }

    private void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;

        StopProcess();

        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeCliPath,
            Arguments = BuildArguments(),
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{_options.ClaudeCliPath}'.");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _turnsOnCurrentProcess = 0;

        // Drained on a background task: a full stderr pipe blocks the child,
        // which would look like a completion that never returns.
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await _process!.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogWarning("[Completions] CLI stderr: {Stderr}", stderr.Trim());
            }
            catch { /* the process is gone; nothing to report */ }
        });

        // The CLI is killed with the IDE rather than outliving it.
        ChildProcessTracker.AddProcess(_process);

        _logger.LogInformation("[Completions] Started CLI completion process (model={Model})",
            _completionOptions.Model);
    }

    private string BuildArguments()
    {
        var sb = new StringBuilder();
        sb.Append("-p --input-format stream-json --output-format stream-json --verbose");

        // No tools at all: a completion has no business reading or writing
        // anything, and the tool definitions are prompt weight on every turn.
        sb.Append(" --tools \"\"");

        // Drops CLAUDE.md, MCP servers, hooks and plugins. Auth is explicitly
        // unaffected, which is what makes this usable on a subscription.
        sb.Append(" --safe-mode");
        sb.Append(" --disable-slash-commands");

        if (!string.IsNullOrWhiteSpace(_completionOptions.Model))
        {
            sb.Append(" --model ");
            sb.Append(EscapeArgument(_completionOptions.Model.Trim()));
        }

        sb.Append(" --system-prompt ");
        sb.Append(EscapeArgument(SystemPrompt));

        return sb.ToString();
    }

    /// <summary>
    /// Strips the wrappers the model still occasionally adds despite the system
    /// prompt, and rejects whitespace-only replies — those would render as an
    /// invisible suggestion the user can "accept" to no effect.
    /// </summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var result = StripCodeFence(text!);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return "";

        var body = trimmed.Substring(firstNewline + 1);
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body.Substring(0, closing).TrimEnd('\n') : body;
    }

    private static string EscapeArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    private static string SafeFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "untitled";
        try { return Path.GetFileName(path); }
        catch { return "untitled"; }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _stdin = null;
        _stdout = null;
        _turnsOnCurrentProcess = 0;

        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch { /* already gone */ }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopProcess();
        _requestGate.Dispose();
    }
}
