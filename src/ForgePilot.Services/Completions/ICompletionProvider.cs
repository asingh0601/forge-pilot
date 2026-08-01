using System.Threading;
using System.Threading.Tasks;

namespace ForgePilot.Services.Completions;

/// <summary>
/// Produces a single inline completion for the code at a caret.
///
/// Deliberately separate from <c>IChatService</c>: the chat drives the Claude
/// Code CLI, which is an agent loop with multi-second turns and a per-session
/// child process. Inline completion needs a sub-500ms round trip on every
/// typing pause, so it takes its own path to the API.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>True when the provider is configured enough to be called at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the text to insert at the caret, or null when there is nothing
    /// useful to suggest. Implementations must honour cancellation promptly —
    /// the caller cancels on every keystroke.
    /// </summary>
    Task<string?> CompleteAsync(CompletionContext context, CancellationToken cancellationToken);
}
