namespace ForgePilot.Services.Models;

public enum ClaudeAssetKind
{
    /// <summary>A slash command — <c>.claude/commands/*.md</c>.</summary>
    Command,

    /// <summary>A skill — <c>.claude/skills/&lt;name&gt;/SKILL.md</c>.</summary>
    Skill,

    /// <summary>An installed plugin, which may itself contribute commands and skills.</summary>
    Plugin,

    /// <summary>An MCP server ("connector") from .mcp.json or the user config.</summary>
    Connector
}

public enum ClaudeAssetScope
{
    /// <summary>Defined in the open workspace — <c>&lt;cwd&gt;/.claude/</c>.</summary>
    Project,

    /// <summary>Defined for the user — <c>~/.claude/</c>.</summary>
    User,

    /// <summary>Contributed by an installed plugin.</summary>
    Plugin
}

/// <summary>
/// One discovered Claude Code asset. Deliberately flat and inert: this layer
/// only reports what is on disk. Enabling, installing, and invoking are the
/// CLI's job — see <c>IClaudeAssetService</c> for why.
/// </summary>
public class ClaudeAsset
{
    public ClaudeAssetKind Kind { get; set; }
    public ClaudeAssetScope Scope { get; set; }

    /// <summary>Invocation name. For commands this excludes the leading slash.</summary>
    public string Name { get; set; } = "";

    /// <summary>First line of the description, or empty when the file has no frontmatter.</summary>
    public string Description { get; set; } = "";

    /// <summary>Source file, or the config file an entry came from. Empty for entries with no backing file.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Owning plugin, when <see cref="Scope"/> is Plugin.</summary>
    public string? PluginName { get; set; }

    /// <summary>
    /// Connectors and plugins can be present but switched off. Commands and
    /// skills are always available once discovered, so this is true for them.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>What to type to invoke this. Null for assets with no direct invocation.</summary>
    public string? Invocation => Kind switch
    {
        ClaudeAssetKind.Command => "/" + Name,
        ClaudeAssetKind.Skill => "/" + Name,
        _ => null
    };
}
