namespace ForgePilot.Services.Completions;

/// <summary>
/// Settings for inline editor completions.
///
/// Separate from <c>ForgePilotOptions</c> because the two features have
/// nothing in common at runtime: chat authenticates through the CLI's
/// subscription login, completions through an API key, and they bill
/// differently.
/// </summary>
public class CompletionOptions
{
    /// <summary>
    /// Off unless explicitly enabled AND a key is present. Completions are
    /// billed per request against the Anthropic API and are not covered by the
    /// Claude subscription the chat uses, so this must never switch itself on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Haiku by default: inline completion is a latency problem before it is a
    /// quality one, and a slow suggestion is worse than none because the user
    /// has already typed past it.
    /// </summary>
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Enough for a statement or a short block. Raising it mostly buys longer
    /// waits for suggestions that get dismissed.
    /// </summary>
    public int MaxTokens { get; set; } = 128;

    /// <summary>
    /// Idle time before a request is issued. Without a debounce the request
    /// rate — and the bill — scales with typing speed.
    /// </summary>
    public int DebounceMilliseconds { get; set; } = 300;

    /// <summary>
    /// Files larger than this are skipped: they are usually generated, and the
    /// context extraction cost is not repaid.
    /// </summary>
    public int MaxFileSizeBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Content types to skip, comma-separated. Empty means complete everywhere
    /// the editor offers a code content type.
    /// </summary>
    public string DisabledLanguages { get; set; } = "";
}
