using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ForgePilot.VSExtension.Commands;

/// <summary>
/// The View → Other Windows → Forge Pilot entry.
///
/// This stays on the VisualStudio.Extensibility command model even though a
/// classic .vsct exists (ForgePilotPackage.vsct). Compiling a .vsct needs
/// Microsoft.VSSDK.BuildTools' VSCTCompile target, which does not run without
/// the "Visual Studio extension development" workload installed — so on this
/// machine the .vsct silently produces no menu resource and the entry
/// disappears entirely. Once that workload is present, switch to the .vsct and
/// drop this file along with the Extensibility packages.
/// </summary>
[VisualStudioContribution]
public class OpenChatSessionCommand : Command
{
    public OpenChatSessionCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override CommandConfiguration CommandConfiguration => new("Forge Pilot")
    {
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
        Icon = new(ImageMoniker.KnownValues.WindowsForm, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Force-load the VSSDK package on first invocation — the command lives
        // in the Extensibility host, which does not load the package for us.
        if (!ForgePilotPackage.IsLoaded)
        {
            var shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
            if (shell != null)
            {
                var guid = new System.Guid("d7a41f38-2c96-4e5b-8b31-9f60c2ae4d17");
                shell.LoadPackage(ref guid, out _);
            }
        }

        await ForgePilotPackage.ShowChatSessionWindowAsync();
    }
}
