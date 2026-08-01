using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ForgePilot.VSExtension.ToolWindows;

[Guid("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e")]
public class ChatSessionToolWindow : ToolWindowPane
{
    /// <summary>
    /// The tab title. Stays constant: with a single window, the session name
    /// belongs in the header picker, not the tab — a caption that renamed
    /// itself per session would make the tool window hard to find again.
    /// </summary>
    public const string BaseCaption = "Forge Pilot";

    public ChatSessionControl ChatControl { get; }

    /// <summary>
    /// Raised when the tool window frame is closed by the user (e.g. clicking X).
    /// </summary>
    public event Action? Closed;

    public ChatSessionToolWindow() : base(null)
    {
        Caption = BaseCaption;
        ChatControl = new ChatSessionControl();
        Content = ChatControl;
    }

    protected override void OnClose()
    {
        Closed?.Invoke();
        base.OnClose();
    }
}
