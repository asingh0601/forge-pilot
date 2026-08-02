using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ForgePilot.VSExtension.Commands;

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

        // Force-load the VSSDK package on first invocation — the command runs
        // in the Extensibility host, which does not load it for us.
        //
        // This GUID must match [Guid] on ForgePilotPackage. It is the fork's
        // own, not upstream's: sharing upstream's meant both extensions
        // registered the same VSPackage and neither loaded.
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
