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

    public override CommandConfiguration CommandConfiguration => new("ForgePilot")
    {
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
        Icon = new(ImageMoniker.KnownValues.WindowsForm, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Force-load the VSSDK package on first invocation
        if (!ForgePilotPackage.IsLoaded)
        {
            var shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
            if (shell != null)
            {
                var guid = new System.Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f");
                shell.LoadPackage(ref guid, out _);
            }
        }

        await ForgePilotPackage.ShowChatSessionWindowAsync();
    }
}
