using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace ForgePilot.VSExtension.Commands;

/// <summary>
/// The View → Other Windows → Forge Pilot menu command.
///
/// A classic VSSDK <see cref="OleMenuCommand"/> rather than a
/// VisualStudio.Extensibility <c>Command</c>. The latter forced
/// <c>ExtensionType="VSSDK+VisualStudio.Extensibility"</c> into the manifest,
/// and on VS 2026 the shell then discovered the extension but marked it
/// Disabled — "Missing entry in per-user enabled extensions cache" — so it
/// never showed under Manage Extensions until <c>devenv /setup</c> rebuilt the
/// cache. The command IDs here mirror ForgePilotPackage.vsct.
/// </summary>
internal static class OpenChatSessionCommand
{
    /// <summary>Must match guidForgePilotCmdSet in the .vsct.</summary>
    public static readonly Guid CommandSet = new("7b3f1a52-9d84-4c07-b6ae-1f2c5d90a743");

    /// <summary>Must match cmdidOpenChatSession in the .vsct.</summary>
    public const int CommandId = 0x0100;

    public static void Register(OleMenuCommandService commandService)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var id = new CommandID(CommandSet, CommandId);
        commandService.AddCommand(new OleMenuCommand(Execute, id));
    }

    private static void Execute(object sender, EventArgs e)
    {
        // The package is already loaded — it owns the command service this was
        // registered on — so unlike the old implementation there is no need to
        // force-load it first.
        _ = ForgePilotPackage.ShowChatSessionWindowAsync();
    }
}
