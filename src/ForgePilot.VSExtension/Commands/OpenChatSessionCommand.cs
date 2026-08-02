using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace ForgePilot.VSExtension.Commands;

/// <summary>
/// The View → Other Windows → Forge Pilot menu command.
///
/// A classic VSSDK <see cref="OleMenuCommand"/> placed by
/// ForgePilotPackage.vsct. The VisualStudio.Extensibility command model was
/// dropped along with its packaging: it forced
/// <c>ExtensionType="VSSDK+VisualStudio.Extensibility"</c> into the manifest and
/// shipped the new-model catalog files, and VS 2026 then registered the
/// extension as Disabled so it never listed under Manage Extensions.
///
/// The GUID and id here must match guidForgePilotCmdSet / cmdidOpenChatSession
/// in the .vsct — changing one means changing both.
/// </summary>
internal static class OpenChatSessionCommand
{
    public static readonly Guid CommandSet = new("7b3f1a52-9d84-4c07-b6ae-1f2c5d90a743");

    public const int CommandId = 0x0100;

    public static void Register(OleMenuCommandService commandService)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var id = new CommandID(CommandSet, CommandId);
        commandService.AddCommand(new OleMenuCommand(Execute, id));
    }

    private static void Execute(object sender, EventArgs e)
    {
        // No force-load needed: the package owns the command service this was
        // registered on, so it is loaded by the time this runs.
        _ = ForgePilotPackage.ShowChatSessionWindowAsync();
    }
}
