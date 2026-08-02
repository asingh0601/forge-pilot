using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ForgePilot.VSExtension.ToolWindows;

[Guid("9c05e7b2-1d48-4a63-bf59-3e7c0a2d86f5")]
public class SessionListToolWindow : ToolWindowPane
{
    public SessionListControl SessionListControl { get; }

    public SessionListToolWindow() : base(null)
    {
        Caption = "Forge Pilot Sessions";
        SessionListControl = new SessionListControl();
        Content = SessionListControl;
    }
}
