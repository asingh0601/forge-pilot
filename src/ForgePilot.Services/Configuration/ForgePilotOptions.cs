using System.IO;

namespace ForgePilot.Services.Configuration;

public class ForgePilotOptions
{
    /// <summary>
    /// Where <c>npm install -g @anthropic-ai/claude-code</c> puts the shim on
    /// Windows. Preferred over a bare "claude" because Visual Studio does not
    /// always inherit the PATH a terminal has — the npm global directory is a
    /// common casualty, and the failure looks like "Failed to start Claude CLI"
    /// even though the CLI is installed and works in a shell.
    /// </summary>
    public static string DefaultCliPath
    {
        get
        {
            try
            {
                var npmShim = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "claude.cmd");

                // Fall back to PATH resolution when npm installed elsewhere
                // (nvm, a custom prefix, or a non-npm install method).
                return File.Exists(npmShim) ? npmShim : "claude";
            }
            catch
            {
                return "claude";
            }
        }
    }

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Path to the Claude CLI executable. Defaults to the npm global shim at
    /// <c>%AppData%\npm\claude.cmd</c> when present, otherwise "claude" on PATH.
    /// </summary>
    public string ClaudeCliPath { get; set; } = DefaultCliPath;

    /// <summary>
    /// Permission mode for the Claude CLI. Controls how tool permissions are handled.
    /// Defaults to <see cref="CliPermissionMode.Default"/>: every gated tool call is
    /// surfaced to the user via the in-process MCP permission helper, and the user
    /// approves/denies it through the chat banner. Use <see cref="CliPermissionMode.AcceptEdits"/>
    /// or <see cref="CliPermissionMode.BypassPermissions"/> as escape hatches.
    /// </summary>
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.Default;

    /// <summary>
    /// Model passed to the CLI via <c>--model</c>: an alias (<c>sonnet</c>,
    /// <c>opus</c>, <c>haiku</c>) or a full model id. Empty means "don't pass
    /// the flag", leaving the CLI on whatever the account default is — which is
    /// the right default here, since the CLI tracks new models before this
    /// extension does.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Extended-thinking budget, passed to the CLI process as the
    /// <c>MAX_THINKING_TOKENS</c> environment variable. Zero leaves it unset so
    /// the CLI applies its own default.
    /// </summary>
    public int MaxThinkingTokens { get; set; }
}
