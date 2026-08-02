using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ForgePilot.Services.Completions;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace ForgePilot.VSExtension.Completions;

/// <summary>
/// Produces the inline (ghost text) proposal for one text view.
///
/// The editor calls <see cref="RequestProposalsAsync"/> on every typing pause,
/// so this method is where cost is controlled: it debounces, cancels the
/// previous request, skips contexts that cannot produce anything useful, and
/// answers from cache whenever it can. Without that, one billable API call
/// happens per keystroke.
/// </summary>
internal sealed class ForgePilotProposalSource : ProposalSourceBase
{
    /// <summary>Shown in the editor's inline-completion UI as the provider name.</summary>
    private const string SourceName = "ForgePilot";

    private readonly ITextView _view;
    private readonly CachingCompletionService _completions;

    /// <summary>
    /// Read on every request rather than captured once. The source outlives any
    /// single setting change, so a snapshot would leave already-open files
    /// suggesting after the user turned completions off.
    /// </summary>
    private readonly Func<CompletionOptions> _readOptions;

    /// <summary>
    /// Cancels the in-flight request when a newer one arrives. Per view, so
    /// typing in one file never cancels a request for another.
    /// </summary>
    private CancellationTokenSource? _pending;

    private readonly object _gate = new();

    internal ForgePilotProposalSource(
        ITextView view, CachingCompletionService completions, Func<CompletionOptions> readOptions)
    {
        _view = view;
        _completions = completions;
        _readOptions = readOptions;
    }

    public override async Task<ProposalCollectionBase?> RequestProposalsAsync(
        VirtualSnapshotPoint caret,
        CompletionState? completionState,
        ProposalScenario scenario,
        char triggeringCharacter,
        CancellationToken cancel)
    {
        var options = _readOptions();
        if (!options.Enabled || !_completions.IsConfigured) return null;

        // Explicit invocation only.
        //
        // A completion goes through the Claude Code CLI, and even with the
        // process kept warm that round trip cannot land inside a typing pause —
        // a suggestion that arrives after the user has typed past it is worse
        // than none. TypeChar (the automatic per-keystroke scenario) is
        // therefore deliberately not handled; the user asks for a suggestion
        // and waits a beat for it.
        if (scenario != ProposalScenario.ExplicitInvocation) return null;

        var snapshot = caret.Position.Snapshot;
        if (snapshot.Length > options.MaxFileSizeBytes) return null;
        if (IsLanguageDisabled(snapshot, options)) return null;

        // Replace any request still running for this view.
        CancellationTokenSource linked;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            linked = _pending;
        }

        try
        {
            // Debounce. A cancellation here means the user kept typing, which
            // is the common case and not an error.
            await Task.Delay(options.DebounceMilliseconds, linked.Token).ConfigureAwait(false);

            var context = BuildContext(caret);
            if (context is null) return null;

            var completion = await _completions
                .CompleteAsync(context, linked.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(completion) || linked.IsCancellationRequested) return null;

            // The snapshot may have moved on while the request was in flight;
            // applying an edit against a stale snapshot throws.
            if (caret.Position.Snapshot != _view.TextSnapshot) return null;

            return BuildProposal(caret, completionState, completion!);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps the completion as a zero-length insertion at the caret. Ghost text
    /// only ever adds; it never rewrites what is already there.
    /// </summary>
    private static ProposalCollection BuildProposal(
        VirtualSnapshotPoint caret, CompletionState? completionState, string completion)
    {
        var span = new SnapshotSpan(caret.Position, 0);
        var edits = new List<ProposedEdit> { new(span, completion) };

        var proposal = new Proposal(
            description: "ForgePilot",
            edits: edits,
            caret: caret,
            completionState: completionState,
            flags: ProposalFlags.SingleTabToAccept | ProposalFlags.FormatAfterCommit,
            commitAction: null,
            proposalId: null,
            acceptText: null,
            previewText: null,
            nextText: null,
            undoDescription: "ForgePilot completion",
            scope: null);

        return new ProposalCollection(SourceName, new[] { proposal });
    }

    /// <summary>
    /// Extracts the code around the caret. Returns null when there is nothing
    /// worth sending — an empty buffer, or a caret in leading whitespace.
    /// </summary>
    private CompletionContext? BuildContext(VirtualSnapshotPoint caret)
    {
        var snapshot = caret.Position.Snapshot;
        var offset = caret.Position.Position;

        var context = CompletionContext.FromDocument(
            snapshot.GetText(),
            offset,
            GetFilePath(),
            snapshot.ContentType?.TypeName ?? "");

        return context.IsTriviallyEmpty ? null : context;
    }

    private string GetFilePath()
    {
        try
        {
            if (_view.TextBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument document))
            {
                return document.FilePath ?? "";
            }
        }
        catch
        {
            // Not every buffer is backed by a file.
        }
        return "";
    }

    private static bool IsLanguageDisabled(ITextSnapshot snapshot, CompletionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DisabledLanguages)) return false;

        var contentType = snapshot.ContentType?.TypeName;
        if (string.IsNullOrEmpty(contentType)) return false;

        foreach (var entry in options.DisabledLanguages.Split(','))
        {
            if (string.Equals(entry.Trim(), contentType, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public override Task DisposeAsync()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
        return Task.CompletedTask;
    }
}
