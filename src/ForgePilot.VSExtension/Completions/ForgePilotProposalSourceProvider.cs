using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using ForgePilot.Services.Completions;
using ForgePilot.VSExtension.Options;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ForgePilot.VSExtension.Completions;

/// <summary>
/// MEF entry point for inline completions. The editor asks this for a proposal
/// source per text view; one source is cached per view so its debounce and
/// cancellation state survive between keystrokes.
///
/// Settings and the API key are read fresh on each view rather than captured
/// once, so toggling completions in Tools > Options takes effect on the next
/// file opened instead of requiring a restart.
/// </summary>
[Export(typeof(ProposalSourceProviderBase))]
[Name("ForgePilot Inline Completions")]
[Order(Before = "Highest Priority")]
[ContentType("text")]
internal sealed class ForgePilotProposalSourceProvider : ProposalSourceProviderBase
{
    private static readonly object Gate = new();
    private static CachingCompletionService? _service;

    public override Task<ProposalSourceBase?> GetProposalSourceAsync(
        ITextView view, CancellationToken cancel)
    {
        try
        {
            // Deliberately not gated on options.Enabled here: the source is
            // created once per view and would otherwise be absent for every
            // file already open when the user switches completions on.
            var service = GetOrCreateService();
            if (service is null) return Task.FromResult<ProposalSourceBase?>(null);

            // One source per view, kept alive with the view so the debounce
            // timer and cancellation token are not rebuilt on every keystroke.
            var source = view.Properties.GetOrCreateSingletonProperty(
                typeof(ForgePilotProposalSource),
                () => new ForgePilotProposalSource(
                    view, service, ForgePilotOptionsPage.ReadCompletionOptions));

            return Task.FromResult<ProposalSourceBase?>(source);
        }
        catch (Exception ex)
        {
            // A throw here disables inline completions for the whole session,
            // so failures degrade to "no suggestions" instead.
            System.Diagnostics.Debug.WriteLine($"ForgePilot: proposal source unavailable: {ex}");
            return Task.FromResult<ProposalSourceBase?>(null);
        }
    }

    /// <summary>
    /// Builds the completion stack once per IDE session. The cache lives here
    /// so it is shared across every open file — the same helper typed in two
    /// files should cost one request, not two.
    /// </summary>
    private static CachingCompletionService? GetOrCreateService()
    {
        lock (Gate)
        {
            if (_service is null)
            {
                var provider = new ClaudeCliCompletionProvider(
                    ForgePilotOptionsPage.ReadCliOptionsForCompletions(),
                    ForgePilotOptionsPage.ReadCompletionOptions());
                _service = new CachingCompletionService(provider);
            }

            return _service;
        }
    }

    /// <summary>Session totals, surfaced in Tools > Options for cost visibility.</summary>
    internal static (int Requests, int CacheHits) GetUsage()
    {
        lock (Gate)
        {
            return _service is null ? (0, 0) : (_service.RequestCount, _service.CacheHitCount);
        }
    }
}
