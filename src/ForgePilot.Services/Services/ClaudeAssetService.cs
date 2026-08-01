using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ForgePilot.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgePilot.Services.Services;

/// <summary>
/// Discovers the slash commands, skills, plugins and MCP connectors that the
/// Claude Code CLI will load for a given workspace.
///
/// Read-only by design. The CLI owns these features outright — it resolves them
/// at process start, applies its own precedence rules, and executes them.
/// Claude Deck's job is to show the user what is available and let them invoke
/// it; anything that changes state (installing a plugin, adding an MCP server)
/// belongs in a `claude` CLI call, not in a hand-rolled reimplementation of the
/// config format, which would drift the moment upstream changes it.
/// </summary>
public interface IClaudeAssetService
{
    Task<IReadOnlyList<ClaudeAsset>> DiscoverAsync(CancellationToken ct = default);
}

public sealed class ClaudeAssetService(string workingDirectory, ILogger<ClaudeAssetService>? logger = null)
    : IClaudeAssetService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    private string UserClaudeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private string ProjectClaudeDir => Path.Combine(workingDirectory, ".claude");

    public Task<IReadOnlyList<ClaudeAsset>> DiscoverAsync(CancellationToken ct = default)
    {
        // Pure file IO over a handful of small files — run it off the UI thread
        // rather than making every reader await individual File operations.
        return Task.Run<IReadOnlyList<ClaudeAsset>>(() =>
        {
            var assets = new List<ClaudeAsset>();

            // Project scope is listed after user scope so that, when both define
            // the same name, the project entry is the one a Last() wins lookup
            // picks — matching the CLI's project-overrides-user precedence.
            CollectCommands(UserClaudeDir, ClaudeAssetScope.User, assets, ct);
            CollectCommands(ProjectClaudeDir, ClaudeAssetScope.Project, assets, ct);

            CollectSkills(UserClaudeDir, ClaudeAssetScope.User, assets, ct);
            CollectSkills(ProjectClaudeDir, ClaudeAssetScope.Project, assets, ct);

            CollectPlugins(assets, ct);
            CollectConnectors(assets, ct);

            return assets;
        }, ct);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    private void CollectCommands(string claudeDir, ClaudeAssetScope scope, List<ClaudeAsset> into, CancellationToken ct)
    {
        var dir = Path.Combine(claudeDir, "commands");
        if (!Directory.Exists(dir)) return;

        foreach (var file in SafeEnumerateFiles(dir, "*.md"))
        {
            ct.ThrowIfCancellationRequested();

            // Nested directories namespace the command: commands/db/migrate.md
            // is invoked as /db:migrate.
            var relative = file.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.ChangeExtension(relative, null)!
                .Replace(Path.DirectorySeparatorChar, ':')
                .Replace(Path.AltDirectorySeparatorChar, ':');

            var (_, description) = ReadFrontmatter(file);

            into.Add(new ClaudeAsset
            {
                Kind = ClaudeAssetKind.Command,
                Scope = scope,
                Name = name,
                Description = description,
                SourcePath = file
            });
        }
    }

    // ── Skills ──────────────────────────────────────────────────────────────

    private void CollectSkills(string claudeDir, ClaudeAssetScope scope, List<ClaudeAsset> into, CancellationToken ct)
    {
        var dir = Path.Combine(claudeDir, "skills");
        if (!Directory.Exists(dir)) return;

        foreach (var skillDir in SafeEnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();

            var manifest = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(manifest)) continue;

            var (frontName, description) = ReadFrontmatter(manifest);

            into.Add(new ClaudeAsset
            {
                Kind = ClaudeAssetKind.Skill,
                Scope = scope,
                // The frontmatter name is authoritative; the directory is the fallback.
                Name = string.IsNullOrWhiteSpace(frontName) ? Path.GetFileName(skillDir) : frontName,
                Description = description,
                SourcePath = manifest
            });
        }
    }

    // ── Plugins ─────────────────────────────────────────────────────────────

    private void CollectPlugins(List<ClaudeAsset> into, CancellationToken ct)
    {
        var pluginRoot = Path.Combine(UserClaudeDir, "plugins");
        if (!Directory.Exists(pluginRoot)) return;

        var enabled = ReadEnabledPlugins();

        foreach (var pluginDir in SafeEnumerateDirectories(pluginRoot))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(pluginDir);
            // The marketplace cache lives alongside installed plugins but isn't one.
            if (name.Equals("repos", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("marketplaces", StringComparison.OrdinalIgnoreCase))
                continue;

            var description = "";
            var manifest = Path.Combine(pluginDir, ".claude-plugin", "plugin.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("description", out var d))
                        description = d.GetString() ?? "";
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Assets] Unreadable plugin manifest at {Path}", manifest);
                }
            }

            into.Add(new ClaudeAsset
            {
                Kind = ClaudeAssetKind.Plugin,
                Scope = ClaudeAssetScope.Plugin,
                Name = name,
                Description = description,
                SourcePath = pluginDir,
                // No enabledPlugins entry at all means the file didn't list any;
                // treat that as "on" rather than reporting everything disabled.
                IsEnabled = enabled.Count == 0 || enabled.Contains(name)
            });

            // A plugin's own commands and skills are what the user actually
            // invokes, so surface them as first-class entries too.
            CollectCommands(pluginDir, ClaudeAssetScope.Plugin, into, ct);
            CollectSkills(pluginDir, ClaudeAssetScope.Plugin, into, ct);
        }
    }

    private HashSet<string> ReadEnabledPlugins()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var settings in new[]
                 {
                     Path.Combine(ProjectClaudeDir, "settings.json"),
                     Path.Combine(ProjectClaudeDir, "settings.local.json"),
                     Path.Combine(UserClaudeDir, "settings.json")
                 })
        {
            if (!File.Exists(settings)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (!doc.RootElement.TryGetProperty("enabledPlugins", out var plugins)) continue;

                if (plugins.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in plugins.EnumerateArray())
                        if (p.GetString() is { } s) result.Add(StripMarketplace(s));
                }
                else if (plugins.ValueKind == JsonValueKind.Object)
                {
                    // Object form maps "plugin@marketplace" -> bool.
                    foreach (var p in plugins.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.True) result.Add(StripMarketplace(p.Name));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Assets] Unreadable settings at {Path}", settings);
            }
        }

        return result;

        static string StripMarketplace(string entry)
        {
            var at = entry.IndexOf('@');
            return at > 0 ? entry.Substring(0, at) : entry;
        }
    }

    // ── Connectors (MCP servers) ────────────────────────────────────────────

    private void CollectConnectors(List<ClaudeAsset> into, CancellationToken ct)
    {
        var sources = new[]
        {
            Path.Combine(workingDirectory, ".mcp.json"),
            Path.Combine(ProjectClaudeDir, "settings.json"),
            Path.Combine(UserClaudeDir, "settings.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                    servers.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var server in servers.EnumerateObject())
                {
                    if (!seen.Add(server.Name)) continue;

                    // Describe by transport so the list distinguishes a local
                    // stdio server from a remote HTTP one at a glance.
                    var description = "";
                    if (server.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (server.Value.TryGetProperty("command", out var cmd))
                            description = cmd.GetString() ?? "";
                        else if (server.Value.TryGetProperty("url", out var url))
                            description = url.GetString() ?? "";
                        if (server.Value.TryGetProperty("type", out var type) && type.GetString() is { } t)
                            description = string.IsNullOrEmpty(description) ? t : $"{t} · {description}";
                    }

                    into.Add(new ClaudeAsset
                    {
                        Kind = ClaudeAssetKind.Connector,
                        Scope = path.StartsWith(workingDirectory, StringComparison.OrdinalIgnoreCase)
                            ? ClaudeAssetScope.Project
                            : ClaudeAssetScope.User,
                        Name = server.Name,
                        Description = description,
                        SourcePath = path
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Assets] Unreadable MCP config at {Path}", path);
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls <c>name</c> and <c>description</c> out of YAML frontmatter without
    /// taking a YAML dependency — these two scalar fields are all we need, and
    /// a malformed file should degrade to an empty description rather than
    /// throw during a UI refresh.
    /// </summary>
    private (string Name, string Description) ReadFrontmatter(string path)
    {
        try
        {
            var name = "";
            var description = "";
            var inFrontmatter = false;
            var lineCount = 0;

            foreach (var raw in File.ReadLines(path))
            {
                // Frontmatter lives at the top; stop rather than scanning a long file.
                if (++lineCount > 40) break;

                var line = raw.TrimEnd();

                if (line == "---")
                {
                    if (!inFrontmatter && lineCount == 1) { inFrontmatter = true; continue; }
                    if (inFrontmatter) break;
                    break;
                }

                if (!inFrontmatter) break;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim().Trim('"', '\'');

                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            }

            return (name, Truncate(description, 160));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Assets] Unreadable frontmatter at {Path}", path);
            return ("", "");
        }

        static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    private IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Assets] Cannot enumerate {Dir}", dir);
            return Enumerable.Empty<string>();
        }
    }

    private IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Assets] Cannot enumerate {Dir}", dir);
            return Enumerable.Empty<string>();
        }
    }
}
