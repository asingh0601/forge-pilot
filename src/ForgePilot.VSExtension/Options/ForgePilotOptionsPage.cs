using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using ForgePilot.Services.Completions;
using ForgePilot.Services.Configuration;

namespace ForgePilot.VSExtension.Options;

/// <summary>
/// Options page shown under Tools → Options → ForgePilot → General.
/// Settings are automatically persisted to the VS registry by DialogPage.
/// </summary>
[ComVisible(true)]
[Guid("b4e829c1-53fa-4d7e-9c02-6a8bd15f3e40")]
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

    // ── Inline completions ──────────────────────────────────────────────────
    //
    // Driven by the same Claude Code CLI login the chat uses, through a second
    // dedicated CLI process. No API key, and nothing billed per request.
    //
    // The trade-off is latency. A one-shot `claude -p` measures 7.5-11s here
    // regardless of prompt size, so the process is kept alive and reused; even
    // then a completion is far slower than an API call and too slow to fire on
    // every typing pause. It is therefore bound to explicit invocation rather
    // than automatic ghost text.

    [Category("Inline completions")]
    [DisplayName("Enable inline completions")]
    [Description("Offer ghost-text suggestions in the editor, generated through the Claude Code CLI using your existing subscription login. No API key and no per-request billing. Suggestions are requested on explicit invocation, not automatically as you type — a CLI round trip is too slow to keep up with typing. Off by default.")]
    [DefaultValue(false)]
    public bool CompletionsEnabled { get; set; }

    [Category("Inline completions")]
    [DisplayName("Model")]
    [Description("Model alias passed to the CLI for completions: haiku, sonnet or opus. Haiku by default — inline completion is a latency problem before it is a quality one, and a slow suggestion is worse than none because you have already typed past it.")]
    [DefaultValue("haiku")]
    public string CompletionsModel { get; set; } = "haiku";

    [Category("Inline completions")]
    [DisplayName("Debounce (ms)")]
    [Description("Idle time before a suggestion is requested. Default: 300.")]
    [DefaultValue(300)]
    public int CompletionsDebounceMs { get; set; } = 300;

    [Category("Inline completions")]
    [DisplayName("Disabled languages")]
    [Description("Comma-separated editor content types to skip, e.g. \"Markdown, plaintext\". Empty means suggest everywhere.")]
    [DefaultValue("")]
    public string CompletionsDisabledLanguages { get; set; } = "";

    /// <summary>
    /// Snapshot of the completion settings for the MEF provider, which cannot
    /// reach a DialogPage directly.
    /// </summary>
    internal static CompletionOptions ReadCompletionOptions()
    {
        var page = TryGetPage();

        if (page is null)
        {
            return new CompletionOptions { Enabled = false };
        }

        return new CompletionOptions
        {
            Enabled = page.CompletionsEnabled,
            Model = string.IsNullOrWhiteSpace(page.CompletionsModel)
                ? "haiku"
                : page.CompletionsModel.Trim(),
            DebounceMilliseconds = page.CompletionsDebounceMs >= 0 ? page.CompletionsDebounceMs : 300,
            DisabledLanguages = page.CompletionsDisabledLanguages ?? "",
        };
    }

    /// <summary>
    /// The CLI settings the completion process needs — path and working
    /// directory. Model comes from <see cref="ReadCompletionOptions"/>; the
    /// chat's model and permission mode deliberately do not apply.
    /// </summary>
    internal static ForgePilotOptions ReadCliOptionsForCompletions()
    {
        var page = TryGetPage();

        return new ForgePilotOptions
        {
            ClaudeCliPath = string.IsNullOrWhiteSpace(page?.ClaudeCliPath)
                ? ForgePilotOptions.DefaultCliPath
                : page!.ClaudeCliPath,
        };
    }

    /// <summary>
    /// GetDialogPage lives on the package, so MEF components reach the settings
    /// through the loaded package instance. Null before the package loads,
    /// which callers treat as "completions off".
    /// </summary>
    private static ForgePilotOptionsPage? TryGetPage()
    {
        try
        {
            return ForgePilotPackage.OptionsPage;
        }
        catch
        {
            return null;
        }
    }
}
