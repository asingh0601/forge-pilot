using ForgePilot.Services.Models;

namespace ForgePilot.Services.Abstractions;

public interface IChatService
{
    /// <summary>The CLI settings this session is currently running under.</summary>
    SessionSettings GetSettings();

    /// <summary>
    /// Applies new CLI settings to this session.
    ///
    /// Model, thinking budget and permission mode are all launch-time
    /// properties of the CLI child process, so this stops it; the next message
    /// starts it again with the new arguments. Conversation continuity is
    /// preserved by the CLI itself — the relaunch passes <c>--resume</c> with
    /// the same session id, so the model still has the full history.
    ///
    /// Throws <see cref="InvalidOperationException"/> if a turn is in flight:
    /// killing the process mid-response would discard it silently.
    /// </summary>
    void ApplySettings(SessionSettings settings);
    IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default);
    void ClearHistory();

    /// <summary>
    /// Serializes the current conversation history to a JSON string for persistence.
    /// </summary>
    string SerializeHistory();

    /// <summary>
    /// Restores conversation history from a previously serialized JSON string.
    /// </summary>
    void RestoreHistory(string serializedHistory);

    /// <summary>
    /// Returns the cumulative USD cost for this session based on CLI cost reporting.
    /// Returns null when no messages have been sent yet.
    /// </summary>
    decimal? GetSessionCost();

    /// <summary>
    /// Total tokens the CLI reported for this session, including cache reads
    /// and writes. Null until a turn has completed — the CLI only reports usage
    /// on the result event, not while streaming.
    /// </summary>
    long? GetSessionTokens();


    /// <summary>
    /// The model the CLI reported on its last <c>init</c> event, or null before
    /// the first turn.
    ///
    /// This is the only reliable answer to "which model is running". When no
    /// model is pinned the <c>--model</c> flag is omitted and the CLI falls back
    /// to the account default, which cannot be inferred from settings — and
    /// asking the model itself does not work, since models routinely
    /// misidentify themselves.
    /// </summary>
    string? ActiveModel { get; }

    /// <summary>
    /// Raised when the CLI reports a different model than before, so the UI can
    /// correct a label it could only guess at until now.
    /// </summary>
    event Action<string>? ModelReported;

    /// <summary>
    /// Raised when the underlying CLI returned an authentication / login-required
    /// error. The string argument is the original error text from the CLI so the
    /// host can surface it to the user. Hosts should respond by showing a login
    /// banner and calling <see cref="LaunchLogin"/> when the user opts in.
    /// </summary>
    event Action<string?>? LoginRequired;

    /// <summary>
    /// Launches an interactive Claude CLI window so the user can complete the
    /// OAuth / login flow, and tears down the current long-running CLI process
    /// so the next <see cref="SendMessageAsync"/> call starts fresh against the
    /// new credentials.
    /// </summary>
    void LaunchLogin();
}
