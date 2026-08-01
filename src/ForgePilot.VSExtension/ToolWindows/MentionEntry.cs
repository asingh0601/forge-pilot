using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace ForgePilot.VSExtension.ToolWindows;

/// <summary>
/// One row in the composer's completion popup. Serves both triggers — '@' for
/// files and folders, '/' for Claude Code commands and skills — so the popup,
/// key handling and commit path stay single-implementation.
/// </summary>
public sealed class MentionEntry
{
    /// <summary>Primary text shown in the row.</summary>
    public string RelativePath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>
    /// Text substituted into the composer, replacing the trigger character and
    /// everything typed after it. Defaults to <see cref="RelativePath"/>, which
    /// is what file mentions want.
    /// </summary>
    public string InsertText { get; }

    /// <summary>Dim secondary text, e.g. a command's description. May be empty.</summary>
    public string Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    private readonly ImageMoniker _moniker;

    public ImageMoniker Moniker => _moniker;

    /// <summary>File or folder entry for the '@' picker.</summary>
    public MentionEntry(string relativePath, string name, bool isDirectory)
    {
        RelativePath = relativePath;
        Name = name;
        IsDirectory = isDirectory;
        InsertText = relativePath;
        Description = "";
        _moniker = isDirectory ? KnownMonikers.FolderClosed : KnownMonikers.Document;
    }

    /// <summary>Command, skill or other invocable for the '/' picker.</summary>
    public MentionEntry(string display, string insertText, string description, ImageMoniker moniker)
    {
        RelativePath = display;
        Name = display;
        IsDirectory = false;
        InsertText = insertText;
        Description = description ?? "";
        _moniker = moniker;
    }
}
