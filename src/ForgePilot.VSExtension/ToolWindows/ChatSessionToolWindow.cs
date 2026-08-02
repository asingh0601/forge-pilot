using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ForgePilot.VSExtension.ToolWindows;

// New GUID on purpose. VS persists a tool window's dock placement against its
// GUID, and a remembered layout beats the placement hints in ProvideToolWindow.
// The inherited GUID already had "MDI document tab" saved against it from the
// upstream registration, so the window kept opening in the editor well no
// matter what the attributes asked for. A fresh GUID has no saved layout, so
// the right-dock hint actually applies.
[Guid("5e9a2f14-7c63-4bd8-a1e7-0c9d4f6b83a2")]
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

    /// <summary>
    /// Loads a session when VS restores this window from a saved layout.
    ///
    /// Restoration constructs the pane directly — the package's
    /// OpenOrActivateSessionAsync never runs, so nothing sets a DataContext and
    /// the panel comes back as an empty black transcript with dead chips and a
    /// disabled send button. That only started happening once the window became
    /// non-Transient (so the dock position would persist), which is exactly what
    /// makes VS restore it in the first place.
    ///
    /// A no-op when a session is already loading: the package sets
    /// _activeSessionId before it asks for the window, so the normal open path
    /// is already covered by the time this runs.
    /// </summary>
    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        ForgePilotPackage.EnsureSessionLoaded();
    }

    protected override void OnClose()
    {
        Closed?.Invoke();
        base.OnClose();
    }
}
