using ForgePilot.Services.Configuration;

namespace ForgePilot.Services.Models;

/// <summary>
/// The CLI settings a chat session runs under. These are launch-time
/// properties of the Claude CLI child process — they live in its command line
/// and environment — so changing any of them means relaunching it.
/// </summary>
/// <param name="Model">
/// Value for <c>--model</c>: an alias (<c>sonnet</c>, <c>opus</c>,
/// <c>haiku</c>) or a full model id. Empty leaves the flag off entirely, so the
/// CLI uses the account default.
/// </param>
/// <param name="MaxThinkingTokens">
/// Extended-thinking budget passed as <c>MAX_THINKING_TOKENS</c>. Zero leaves
/// it unset so the CLI applies its own default.
/// </param>
/// <param name="PermissionMode">
/// How gated tool calls are handled, including <see cref="CliPermissionMode.Plan"/>
/// for plan-then-act.
/// </param>
public record SessionSettings(
    string Model,
    int MaxThinkingTokens,
    CliPermissionMode PermissionMode);
