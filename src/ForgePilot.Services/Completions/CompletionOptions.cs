namespace ForgePilot.Services.Completions;

/// <summary>
/// Settings for inline editor completions.
///
/// Separate from <c>ForgePilotOptions</c> because the two run different CLI
/// processes with different arguments: the chat's process carries the user's
/// conversation, tools and permission mode, while the completion process is
/// tool-free, stateless in intent, and pinned to a fast model.
/// </summary>
public class CompletionOptions
{
    /// <summary>
    /// Off unless explicitly enabled. Completions spawn a second CLI process
    /// and consume subscription usage, so this must never switch itself on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A CLI model alias (haiku, sonnet, opus) or a full model id. Haiku by
    /// default: inline completion is a latency problem before it is a quality
    /// one, and a slow suggestion is worse than none because the user has
    /// already typed past it.
    /// </summary>
    public string Model { get; set; } = "haiku";

    /// <summary>
    /// Idle time before a request is issued. Without a debounce the request
    /// rate scales with typing speed.
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
