using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ForgePilot.UI.Themes;

public enum ClaudeThemeVariant
{
    Light,
    Dark
}

/// <summary>
/// Owns the single merged <see cref="ResourceDictionary"/> that carries the
/// <c>Cd*</c> brushes, and swaps it when the host's theme changes.
///
/// Both hosts drive this: the VS extension calls <see cref="Apply"/> from its
/// <c>VSColorTheme.ThemeChanged</c> handler, the Desktop app calls it once at
/// startup. Controls bind with <c>DynamicResource</c> so the swap repaints
/// them without a reload.
/// </summary>
public static class ClaudeThemeManager
{
    private const string LightUri = "pack://application:,,,/ForgePilot.UI;component/Themes/ClaudeTheme.Light.xaml";
    private const string DarkUri = "pack://application:,,,/ForgePilot.UI;component/Themes/ClaudeTheme.Dark.xaml";

    private static ResourceDictionary? _current;

    /// <summary>The variant currently applied. Defaults to Dark before the first Apply.</summary>
    public static ClaudeThemeVariant Current { get; private set; } = ClaudeThemeVariant.Dark;

    /// <summary>Raised after a swap so hosts can push the matching theme into the WebView.</summary>
    public static event Action<ClaudeThemeVariant>? ThemeChanged;

    public static void Apply(ClaudeThemeVariant variant)
    {
        var app = Application.Current;
        if (app is null) return;

        var next = new ResourceDictionary
        {
            Source = new Uri(variant == ClaudeThemeVariant.Dark ? DarkUri : LightUri, UriKind.Absolute)
        };

        // Remove the previous dictionary before adding the new one. Merged
        // dictionaries resolve last-wins, so leaving stale ones stacked would
        // work by accident but leak a dictionary per theme switch.
        if (_current is not null)
            app.Resources.MergedDictionaries.Remove(_current);

        app.Resources.MergedDictionaries.Add(next);
        _current = next;
        Current = variant;

        ThemeChanged?.Invoke(variant);
    }

    /// <summary>
    /// Picks a variant from a background colour's perceived luminance. Hosts
    /// that expose a theme enum should use it directly; this is for hosts (like
    /// the VS shell) that only hand out colours.
    /// </summary>
    public static ClaudeThemeVariant VariantFor(Color background)
    {
        // Rec. 601 luma — close enough for a light/dark decision and cheaper
        // than a full relative-luminance conversion.
        var luma = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luma < 0.5 ? ClaudeThemeVariant.Dark : ClaudeThemeVariant.Light;
    }

    /// <summary>
    /// The palette as CSS custom properties, for pushing into the WebView so the
    /// transcript matches the WPF chrome exactly rather than approximately.
    /// Keys are the CSS variable names used in chat-template.html.
    /// </summary>
    public static Dictionary<string, string> ToCssVariables()
    {
        var app = Application.Current;
        if (app is null) return new Dictionary<string, string>();

        var map = new (string Css, string Key)[]
        {
            ("--bg",             "FpBackground"),
            ("--surface",        "FpSurface"),
            ("--surface-subtle", "FpSurfaceSubtle"),
            ("--text",           "FpForeground"),
            ("--muted",          "FpMuted"),
            ("--faint",          "FpFaint"),
            ("--border",         "FpBorder"),
            ("--border-strong",  "FpBorderStrong"),
            ("--accent",         "FpAccent"),
            ("--accent-hover",   "FpAccentHover"),
            ("--accent-fg",      "FpAccentForeground"),
            ("--accent-subtle",  "FpAccentSubtle"),
            ("--success",        "FpSuccess"),
            ("--error",          "FpError"),
            ("--selection",      "FpSelection"),
            ("--code-bg",        "FpCodeBackground"),
        };

        return map
            .Select(m => (m.Css, Brush: app.TryFindResource(m.Key) as SolidColorBrush))
            .Where(x => x.Brush is not null)
            .ToDictionary(
                x => x.Css,
                x => $"#{x.Brush!.Color.R:X2}{x.Brush.Color.G:X2}{x.Brush.Color.B:X2}");
    }
}
