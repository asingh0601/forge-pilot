namespace ForgePilot.Services.Configuration;

/// <summary>
/// Permission mode passed to the Claude CLI via --permission-mode. Controls
/// which gated tool calls the CLI forwards to our in-process MCP permission
/// helper (which raises the chat banner) versus auto-accepting silently.
/// </summary>
public enum CliPermissionMode
{
    /// <summary>
    /// Auto-accept file-edit tools (Edit, Write, NotebookEdit, etc.) without
    /// prompting. All other gated tools (Bash, WebFetch, …) still surface a
    /// banner via the MCP permission helper.
    /// </summary>
    AcceptEdits,

    /// <summary>
    /// Auto-accept every gated tool call. The MCP permission helper is never
    /// invoked, so no banners appear. Only use in trusted/sandboxed
    /// environments — the agent can run arbitrary commands without confirmation.
    /// </summary>
    BypassPermissions,

    /// <summary>
    /// Prompt for every gated tool call. Each request is forwarded to the
    /// in-process MCP permission helper and surfaced to the user as an
    /// Allow / Deny banner in the chat. This is the safest mode and the
    /// default for the extension.
    /// </summary>
    Default,

    /// <summary>
    /// Plan mode. The CLI explores and proposes an approach but does not edit
    /// files or run commands until the user approves. Maps to
    /// <c>--permission-mode plan</c>.
    ///
    /// This is a mode of the CLI itself, not a permission filter layered on
    /// top: it changes how the agent behaves, so nothing gated reaches the
    /// permission helper in the first place.
    /// </summary>
    Plan
}
