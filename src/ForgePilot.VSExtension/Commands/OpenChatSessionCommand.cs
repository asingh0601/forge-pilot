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
        Log("menu command invoked");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Force-load the VSSDK package on first invocation — the command runs
        // in the Extensibility host, which does not load it for us.
        //
        // This GUID must match [Guid] on ForgePilotPackage. It is the fork's
        // own, not upstream's: sharing upstream's meant both extensions
        // registered the same VSPackage and neither loaded.
        if (!ForgePilotPackage.IsLoaded)
        {
            Log("package not loaded - forcing LoadPackage");
            var shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
            if (shell != null)
            {
                var guid = new System.Guid("d7a41f38-2c96-4e5b-8b31-9f60c2ae4d17");
                var hr = shell.LoadPackage(ref guid, out _);
                Log($"LoadPackage hr=0x{hr:X8} loaded={ForgePilotPackage.IsLoaded}");
            }
            else
            {
                Log("ERROR: SVsShell unavailable");
            }
        }
        else
        {
            Log("package already loaded");
        }

        try
        {
            await ForgePilotPackage.ShowChatSessionWindowAsync();
            Log("ShowChatSessionWindowAsync returned");
        }
        catch (System.Exception ex)
        {
            // An exception here is swallowed by the Extensibility host, which is
            // why a failing open looked like the menu doing nothing at all.
            Log($"ERROR: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Appends to %AppData%\ForgePilot\logs\window-*.log, the same file the
    /// package writes, so the whole open path reads as one sequence.
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "ForgePilot", "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, $"window-{System.DateTime.Now:yyyyMMdd}.log"),
                $"{System.DateTime.Now:HH:mm:ss.fff} [command] {message}{System.Environment.NewLine}");
        }
        catch
        {
            // Never let diagnostics break the command.
        }
    }
}
