using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using ForgePilot.Services.Configuration;

namespace ForgePilot.VSExtension.Options;

/// <summary>
/// Options page shown under Tools → Options → ForgePilot → General.
/// Settings are automatically persisted to the VS registry by DialogPage.
/// </summary>
[ComVisible(true)]
[Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")]
public class ForgePilotOptionsPage : DialogPage
{
    // No [DefaultValue]: the real default is resolved at runtime and the
    // attribute only takes a compile-time constant. Declaring "claude" here
    // would make the property grid show the wrong value as the default and
    // reset to it.
    [Category("Claude CLI")]
    [DisplayName("Claude CLI Path")]
    [Description(@"Path to the Claude Code CLI executable. Defaults to the npm global shim at %AppData%\npm\claude.cmd when it exists, otherwise 'claude' from PATH.")]
    public string ClaudeCliPath { get; set; } = ForgePilotOptions.DefaultCliPath;

    [Category("Claude CLI")]
    [DisplayName("Model")]
    [Description("Model passed to the CLI: an alias (sonnet, opus, haiku) or a full model id. Leave empty to use your account default — recommended, since the CLI knows about new models before this extension does. Takes effect on the next session.")]
    [DefaultValue("")]
    public string Model { get; set; } = "";

    [Category("Claude CLI")]
    [DisplayName("Extended thinking budget (tokens)")]
    [Description("Sets MAX_THINKING_TOKENS for the CLI process. 0 leaves the CLI's own default in place. Higher values buy more reasoning on hard problems at the cost of latency. Takes effect on the next session.")]
    [DefaultValue(0)]
    public int MaxThinkingTokens { get; set; }

    [Category("Claude CLI")]
    [DisplayName("CLI Permission Mode")]
    [Description("Controls how the CLI handles tool permissions. Default: every gated tool call surfaces an Allow/Deny banner in the chat (safest). Plan: Claude explores and proposes an approach without editing files or running commands. AcceptEdits: file edits (Edit, Write, NotebookEdit) auto-accept; everything else still prompts. BypassPermissions: auto-accept every tool call without prompting (use only in trusted environments).")]
    [DefaultValue(CliPermissionMode.Default)]
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.Default;

    [Category("Sessions")]
    [DisplayName("Keep days of activity")]
    [Description("When the extension starts, sessions whose last activity is older than this many days are deleted. Default: 30. Set to 0 to disable cleanup.")]
    [DefaultValue(30)]
    public int KeepActivityDays { get; set; } = 30;
}
