using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using ForgePilot.Services.Abstractions;
using ForgePilot.Services.Models;
using ForgePilot.Services.Services;
using Microsoft.VisualStudio.Imaging;
using ForgePilot.UI.Controls;
using ForgePilot.UI.Themes;
using ForgePilot.UI.ViewModels;

namespace ForgePilot.VSExtension.ToolWindows;

public partial class ChatSessionControl : UserControl
{
    private const double InputMinHeight = 36;

    private bool _isResizing;
    private double _resizeStartScreenY;
    private double _resizeStartHeight;

    // -1 when no completion is active. Otherwise, the caret position right
    // after the trigger character — text from this index up to the caret is
    // the filter.
    private int _mentionStart = -1;

    // Which trigger opened the popup: '@' for files, '/' for commands. Both
    // share the popup, key handling and commit path; only the source list and
    // the trigger test differ.
    private char _triggerChar = '@';

    private List<MentionEntry>? _mentionCache;
    private List<MentionEntry>? _commandCache;
    private bool _suppressTextChanged;

    private SessionListViewModel? _sessionListViewModel;
    private SessionInfo? _currentSession;

    public ChatSessionControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Points the header picker at the session list. Called by the package each
    /// time a session is loaded into the window.
    /// </summary>
    public void BindSessions(SessionListViewModel? sessions, SessionInfo current)
    {
        _sessionListViewModel = sessions;
        _currentSession = current;
        SessionNameText.Text = current.Name;

        // The session is often renamed a moment later, once a title has been
        // generated from the first message.
        if (DataContext is ChatSessionViewModel vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatSessionViewModel.SessionTitle))
                    SessionNameText.Text = vm.SessionTitle;
            };
        }
    }

    private void SessionMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionListViewModel is null) return;

        var menu = ThemedMenu(SessionMenuButton, System.Windows.Controls.Primitives.PlacementMode.Bottom);

        // Most recent first — the ordering the picker is actually used in.
        foreach (var session in _sessionListViewModel.Sessions.OrderByDescending(s => s.LastActivity))
        {
            var captured = session;
            var item = new MenuItem
            {
                Header = session.Name,
                IsCheckable = true,
                IsChecked = _currentSession is not null && captured.Id == _currentSession.Id
            };
            item.Click += (_, _) =>
            {
                if (_currentSession is not null && captured.Id == _currentSession.Id) return;
                _sessionListViewModel.OpenSessionCommand.Execute(captured);
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            // ItemContainerStyle targets MenuItem, so a Separator would keep the
            // native look unless styled explicitly.
            menu.Items.Add(new Separator { Style = TryFindResource("FpSeparatorStyle") as Style });
        }

        var newItem = new MenuItem { Header = "New session" };
        newItem.Click += (_, _) => _sessionListViewModel.NewSessionCommand.Execute(null);
        menu.Items.Add(newItem);

        // Actions on the session that is currently loaded.
        if (_currentSession is not null)
        {
            menu.Items.Add(new Separator { Style = TryFindResource("FpSeparatorStyle") as Style });

            // Empties the transcript and the CLI's history but keeps the
            // session, so the window stays on the same entry.
            var clearItem = new MenuItem
            {
                Header = "Clear conversation",
                IsEnabled = DataContext is ChatSessionViewModel { IsBusy: false }
            };
            clearItem.Click += (_, _) =>
            {
                if (DataContext is ChatSessionViewModel vm) vm.ClearCommand.Execute(null);
            };
            menu.Items.Add(clearItem);

            // Deletes the session outright. Confirmed first: it removes the
            // stored transcript, which nothing else can undo.
            var deleteTarget = _currentSession;
            var deleteItem = new MenuItem { Header = $"Delete \"{Truncate(deleteTarget.Name, 32)}\"" };
            deleteItem.Click += (_, _) =>
            {
                var answer = MessageBox.Show(
                    $"Delete the session \"{deleteTarget.Name}\"?\n\nIts transcript is removed permanently.",
                    "Forge Pilot",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);

                if (answer == MessageBoxResult.OK)
                    _sessionListViewModel.RemoveSessionCommand.Execute(deleteTarget);
            };
            menu.Items.Add(deleteItem);
        }

        // Editor-wide, not session-scoped — but this is the only menu the panel
        // has, and burying an on/off switch in Tools → Options makes it awkward
        // to flip while working.
        menu.Items.Add(new Separator { Style = TryFindResource("FpSeparatorStyle") as Style });

        var completionsItem = new MenuItem
        {
            Header = "Inline completions",
            InputGestureText = "Editor",
            IsCheckable = true,
            IsChecked = ForgePilotPackage.CompletionsEnabled
        };
        completionsItem.Click += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ForgePilotPackage.SetCompletionsEnabled(completionsItem.IsChecked);

            // Read back rather than echo what was asked for: the options page is
            // null until the package finishes loading, and a silent no-op there
            // looks exactly like a toggle that does not work.
            var actual = ForgePilotPackage.CompletionsEnabled;
            StatusInfoText.Text = actual == completionsItem.IsChecked
                ? (actual
                    ? "Inline completions on — invoke them in the editor, they do not fire as you type"
                    : "Inline completions off")
                : "Could not change inline completions — settings are not available yet";
        };
        menu.Items.Add(completionsItem);

        menu.IsOpen = true;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max - 1) + "…";

    private void NewSessionButton_Click(object sender, RoutedEventArgs e)
        => _sessionListViewModel?.NewSessionCommand.Execute(null);

    // ── Composer footer chips ───────────────────────────────────────────────
    //
    // Each change restarts the CLI child process, because model, thinking
    // budget and permission mode are all launch-time properties of it. The
    // conversation survives — the relaunch resumes the same CLI session — but
    // it cannot happen mid-response, so ApplySessionSettings returns a message
    // to show instead of throwing.

    private void ModelChip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatSessionViewModel vm) return;
        var current = vm.ModelLabel;

        ShowChipMenu(ModelChip, ChatSessionViewModel.Models.Select(m => m.Label), current, label =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chosen = ChatSessionViewModel.Models.First(m => m.Label == label);
            var s = vm.GetSessionSettings();
            if (s is null) return;
            ApplyAndPersist(vm, s with { Model = chosen.Value });
        });
    }

    private void EffortChip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatSessionViewModel vm) return;
        var current = vm.EffortLabel;

        ShowChipMenu(EffortChip, ChatSessionViewModel.EffortLevels.Select(l => l.Label), current, label =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chosen = ChatSessionViewModel.EffortLevels.First(l => l.Label == label);
            var s = vm.GetSessionSettings();
            if (s is null) return;
            ApplyAndPersist(vm, s with { MaxThinkingTokens = chosen.Tokens });
        });
    }

    /// <summary>
    /// Opens the permission-mode picker. Previously a two-state plan/act
    /// toggle, which could not reach acceptEdits, auto or bypassPermissions —
    /// three of the five modes the CLI supports were unreachable from the UI.
    /// </summary>
    private void ModeChip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatSessionViewModel vm) return;
        var current = vm.ModeLabel;

        // Digits 1-3 for the everyday modes, matching the CLI's own shortcuts.
        // Bypass gets "Enable" rather than a digit on purpose: it grants
        // unprompted shell access, and a number key makes that one mistyped
        // keystroke away.
        var options = ChatSessionViewModel.PermissionModes
            .Select((m, i) => (m.Label, Hint: i < 3 ? (i + 1).ToString() : ""))
            .ToList();
        // Index-from-end (^1) is unavailable on net472 — System.Index is missing.
        var last = options.Count - 1;
        options[last] = (options[last].Label, "Enable");

        ShowChipMenu(ModeChip, options, current, "Mode", label =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chosen = ChatSessionViewModel.PermissionModes.First(m => m.Label == label);
            var s = vm.GetSessionSettings();
            if (s is null) return;
            ApplyAndPersist(vm, s with { PermissionMode = chosen.Mode });
        });
    }

    /// <summary>
    /// Applies a chip choice and, when it took, writes it back to Tools →
    /// Options.
    ///
    /// Without the write-back the choice lived only in the in-memory options
    /// instance: correct for the running session, but every new session is
    /// built from the Options page, so picking a model appeared to do nothing
    /// as soon as the user opened another session or restarted VS.
    /// </summary>
    private void ApplyAndPersist(ChatSessionViewModel vm, SessionSettings settings)
    {
        // Only ever reached from a menu Click, which is already on the UI
        // thread; stated so the threading analyzer can see it too.
        ThreadHelper.ThrowIfNotOnUIThread();

        var error = vm.ApplySessionSettings(settings);
        ReportIfBlocked(error);

        // Only persist what actually took — a mid-turn rejection must not
        // become the new default.
        if (error is null) ForgePilotPackage.PersistSessionSettings(settings);
    }

    /// <summary>
    /// Builds a themed picker anchored above a chip. Uses the shared Fp menu
    /// styles rather than a default ContextMenu, which renders as a grey
    /// Windows menu with a blue system checkmark and reads as another
    /// application intruding on the panel.
    /// </summary>
    private void ShowChipMenu(
        Button anchor, IEnumerable<string> options, string current, Action<string> onPick)
        => ShowChipMenu(anchor, options.Select(o => (o, "")), current, null, onPick);

    private void ShowChipMenu(
        Button anchor,
        IEnumerable<(string Label, string Hint)> options,
        string current,
        string? title,
        Action<string> onPick)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            VerticalOffset = -6,
            HorizontalOffset = 0,
            Style = MenuStyle
        };

        if (!string.IsNullOrEmpty(title))
        {
            menu.Items.Add(new TextBlock
            {
                Text = title,
                Style = TryFindResource("FpMenuHeaderStyle") as Style
            });
        }

        // Digit shortcuts, in the order the rows were added. Held here rather
        // than read off the menu items so the caption TextBlock never shifts
        // the numbering.
        var byDigit = new List<Action>();

        foreach (var (label, hint) in options)
        {
            var captured = label;
            var item = new MenuItem
            {
                Header = captured,
                InputGestureText = hint,
                IsCheckable = true,
                IsChecked = captured == current
            };
            void Pick()
            {
                menu.IsOpen = false;
                if (captured != current) onPick(captured);
            }
            item.Click += (_, _) => Pick();
            byDigit.Add(Pick);
            menu.Items.Add(item);
        }

        // The digit hints have to actually do something — InputGestureText is
        // display-only, so WPF never binds it to a key.
        menu.PreviewKeyDown += (_, args) =>
        {
            var digit = args.Key switch
            {
                >= Key.D1 and <= Key.D9 => args.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => args.Key - Key.NumPad1,
                _ => -1
            };
            if (digit < 0 || digit >= byDigit.Count) return;
            args.Handled = true;
            byDigit[digit]();
        };

        menu.IsOpen = true;
    }

    /// <summary>
    /// The themed menu style.
    ///
    /// Resolved from THIS control, not Application.Current. BannerStyles.xaml is
    /// merged into the control's own resources, never into the application's, so
    /// an Application-level lookup returned null and every menu silently fell
    /// back to the native Windows one.
    /// </summary>
    private Style? MenuStyle => TryFindResource("FpContextMenuStyle") as Style;

    /// <summary>Applies the themed style to a menu built elsewhere.</summary>
    private ContextMenu ThemedMenu(UIElement anchor,
        System.Windows.Controls.Primitives.PlacementMode placement) => new()
    {
        PlacementTarget = anchor,
        Placement = placement,
        Style = MenuStyle
    };

    /// <summary>
    /// Surfaces the "can't change settings mid-response" case in the status
    /// line rather than a modal — it's a wait-a-moment condition, not an error
    /// worth interrupting for. Cleared on the next status update.
    /// </summary>
    private void ReportIfBlocked(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        StatusInfoText.Text = message;
    }


    private void InsertIntoComposer(string text)
    {
        if (DataContext is not ChatSessionViewModel vm) return;

        // Replace rather than append when the composer already holds a slash
        // command — picking a second one should swap it, not concatenate.
        var existing = vm.InputText ?? "";
        vm.InputText = existing.TrimStart().StartsWith("/", StringComparison.Ordinal)
            ? text
            : text + existing;

        InputTextBox.Focus();
        InputTextBox.CaretIndex = InputTextBox.Text.Length;
    }

    /// <summary>
    /// The session currently wired to this control, so its handlers can be
    /// removed before another one is attached.
    /// </summary>
    private ChatSessionViewModel? _boundViewModel;

    private bool _themeHandlerAttached;

    /// <summary>
    /// Points the control at a session.
    ///
    /// The control, and the WebView inside it, are reused for every session in
    /// this window — only the view model is swapped. So this has to undo the
    /// previous session completely: detach its handlers and wipe the
    /// transcript. Without the wipe, a new or replacement session rendered on
    /// top of the conversation that was already on screen, which is why
    /// deleting a session left its messages behind and "new session" did not
    /// start empty.
    ///
    /// Named handlers rather than lambdas, because a lambda cannot be
    /// unsubscribed — each switch would stack another live subscription.
    /// </summary>
    public void Initialize(ChatSessionViewModel viewModel)
    {
        if (ReferenceEquals(_boundViewModel, viewModel)) return;

        DetachViewModel();

        _boundViewModel = viewModel;
        DataContext = viewModel;

        // Queued through the same channel as the message calls, so it is
        // ordered ahead of any transcript this session restores.
        _ = ChatWebView.ClearAllAsync();

        // Seed the footer chips from whatever the session actually launched
        // with, rather than leaving them on their declared defaults.
        viewModel.RefreshSettingLabels();

        viewModel.MessageAdded += OnMessageAdded;
        viewModel.MessageContentUpdated += OnMessageContentUpdated;
        viewModel.MessageStatusUpdated += OnMessageStatusUpdated;
        viewModel.MessageBodySet += OnMessageBodySet;
        viewModel.MessageCompleted += OnMessageCompleted;
        viewModel.AllCleared += OnAllCleared;
        viewModel.MessagesRestored += OnMessagesRestored;

        // Banner mounting is now driven by the ActiveBanner property on the VM —
        // the BannerHost ContentControl in XAML binds to it and DataTemplates
        // pick the right UserControl by VM type.

        ApplyTheme();

        // Once per control, not once per session: this is a static event, so
        // re-subscribing on every switch would leak a handler each time and
        // repaint the theme once per session ever loaded.
        if (!_themeHandlerAttached)
        {
            VSColorTheme.ThemeChanged += OnThemeChanged;
            _themeHandlerAttached = true;
        }
    }

    private void DetachViewModel()
    {
        var vm = _boundViewModel;
        if (vm is null) return;

        vm.MessageAdded -= OnMessageAdded;
        vm.MessageContentUpdated -= OnMessageContentUpdated;
        vm.MessageStatusUpdated -= OnMessageStatusUpdated;
        vm.MessageBodySet -= OnMessageBodySet;
        vm.MessageCompleted -= OnMessageCompleted;
        vm.AllCleared -= OnAllCleared;
        vm.MessagesRestored -= OnMessagesRestored;

        _boundViewModel = null;
    }

    private void OnMessageAdded(string id, ChatItemType type, ChatMessageData data)
        => _ = ChatWebView.AddMessageAsync(id, type, data);

    private void OnMessageContentUpdated(string id, string content)
        => _ = ChatWebView.UpdateContentAsync(id, content);

    private void OnMessageStatusUpdated(string id, OutputItemStatus status, string expanderTitle)
        => _ = ChatWebView.UpdateStatusAsync(id, status, expanderTitle);

    private void OnMessageBodySet(string id, string body, OutputBodyMode mode)
        => _ = ChatWebView.SetBodyAsync(id, body, mode);

    private void OnMessageCompleted(string id)
        => _ = ChatWebView.CompleteMessageAsync(id);

    private void OnAllCleared()
        => _ = ChatWebView.ClearAllAsync();

    private void OnMessagesRestored(System.Collections.Generic.IEnumerable<ChatMessageData> messages)
        => _ = ChatWebView.LoadMessagesAsync(messages);

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyTheme()));
    }

    /// <summary>
    /// ForgePilot paints its own palette rather than adopting the VS one — the
    /// point of the fork is that the panel looks like Claude, not like a VS
    /// tool window. All the VS theme decides is which variant to use, taken
    /// from the tool-window background's luminance so the panel still reads as
    /// light in a light IDE.
    /// </summary>
    private void ApplyTheme()
    {
        var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);

        // GetThemedColor hands back a System.Drawing.Color; ClaudeThemeManager
        // works in System.Windows.Media.
        var mediaColor = System.Windows.Media.Color.FromRgb(background.R, background.G, background.B);

        ClaudeThemeManager.Apply(ClaudeThemeManager.VariantFor(mediaColor));
        _ = ChatWebView.ApplyCurrentThemeAsync();
    }

    // Send / interrupt live in InputTextBox_PreviewKeyDown — see the note there
    // on why the bubbling KeyDown never sees Enter. Kept as a no-op so the XAML
    // binding stays valid and the two paths can't drift apart.
    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IInputElement grip) return;

        _isResizing = true;
        // Use screen-space coordinates so we are not affected by the textbox
        // resizing under us during drag (which would change positions in
        // the local coordinate system).
        _resizeStartScreenY = PointToScreen(e.GetPosition(this)).Y;
        _resizeStartHeight = InputTextBox.ActualHeight > 0
            ? InputTextBox.ActualHeight
            : InputTextBox.MinHeight;

        Mouse.Capture(grip);
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        var currentScreenY = PointToScreen(e.GetPosition(this)).Y;
        // Dragging upward decreases Y, which should grow the input box.
        var delta = _resizeStartScreenY - currentScreenY;
        var requested = _resizeStartHeight + delta;

        var maxHeight = Math.Max(InputMinHeight, RootPanel.ActualHeight / 2);
        var newHeight = Math.Max(InputMinHeight, Math.Min(requested, maxHeight));

        // Pin the textbox to the dragged height so it neither grows with
        // content nor shrinks below the user's chosen size.
        InputTextBox.MinHeight = newHeight;
        InputTextBox.MaxHeight = newHeight;
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;

        _isResizing = false;
        if (Mouse.Captured is IInputElement captured && ReferenceEquals(captured, sender))
        {
            Mouse.Capture(null);
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // @-mention file/folder picker
    // ------------------------------------------------------------------

    private void AtMentionButton_Click(object sender, RoutedEventArgs e)
    {
        // Insert '@' at the caret (or after a leading space) and open the popup
        // explicitly. We suppress TextChanged because it fires before we get a
        // chance to update CaretIndex, which would confuse the trigger logic.
        InputTextBox.Focus();
        var text = InputTextBox.Text ?? "";
        var caret = Math.Min(InputTextBox.CaretIndex, text.Length);

        var needsLeadingSpace = caret > 0 && !char.IsWhiteSpace(text[caret - 1]);
        var insert = needsLeadingSpace ? " @" : "@";

        _suppressTextChanged = true;
        try
        {
            InputTextBox.Text = text.Insert(caret, insert);
            InputTextBox.CaretIndex = caret + insert.Length;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        _mentionStart = caret + insert.Length;
        ShowMentionPopup("");
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Outside the suppression guard: the composer is also cleared and
        // repopulated programmatically, and the styling has to follow that too.
        UpdateCommandHighlight();

        if (_suppressTextChanged) return;

        var text = InputTextBox.Text ?? "";
        var caret = InputTextBox.CaretIndex;

        if (_mentionStart < 0)
        {
            if (caret > 0 && caret <= text.Length)
            {
                var typed = text[caret - 1];

                // '@' triggers at the start of the message or after whitespace.
                if (typed == '@' && (caret == 1 || char.IsWhiteSpace(text[caret - 2])))
                {
                    _triggerChar = '@';
                    _mentionStart = caret;
                    ShowMentionPopup("");
                }
                // '/' triggers only as the very first character. Anywhere else
                // it is almost always a path separator or a division, and
                // popping a command list over those would be noise.
                else if (typed == '/' && caret == 1)
                {
                    _triggerChar = '/';
                    _mentionStart = caret;
                    ShowMentionPopup("");
                }
            }
            return;
        }

        // A completion is active — re-validate and refilter.
        if (caret < _mentionStart
            || _mentionStart > text.Length
            || _mentionStart == 0
            || text[_mentionStart - 1] != _triggerChar)
        {
            CloseMentionPopup();
            return;
        }

        var filter = text.Substring(_mentionStart, caret - _mentionStart);
        if (filter.Any(char.IsWhiteSpace))
        {
            CloseMentionPopup();
            return;
        }

        ApplyMentionFilter(filter);
    }

    private void InputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_mentionStart < 0) return;

        var caret = InputTextBox.CaretIndex;
        if (caret < _mentionStart) CloseMentionPopup();
    }

    private void InputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Don't close if focus moved into the popup (clicking a list item).
        if (e.NewFocus is DependencyObject d && IsDescendantOf(d, MentionPopup.Child)) return;
        CloseMentionPopup();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? root)
    {
        if (root == null) return false;
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The mention popup owns the navigation keys while it's open.
        if (MentionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveMentionSelection(+1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveMentionSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    // Take the highlight when it completes what was typed;
                    // otherwise Enter means "send exactly what I wrote". Falling
                    // through unhandled would let the TextBox insert a newline
                    // instead, which is neither.
                    if (CommitMentionSelection())
                    {
                        e.Handled = true;
                        break;
                    }

                    CloseMentionPopup();
                    if (DataContext is ChatSessionViewModel typedVm
                        && typedVm.SendCommand.CanExecute(null))
                    {
                        typedVm.SendCommand.Execute(null);
                    }
                    e.Handled = true;
                    break;

                case Key.Tab:
                    // Completion only — Tab never sends, so a rejected highlight
                    // simply does nothing and leaves the popup open.
                    if (CommitMentionSelection()) e.Handled = true;
                    break;
                case Key.Escape:
                    CloseMentionPopup();
                    e.Handled = true;
                    break;
            }
            return;
        }

        // Send/interrupt must be handled here, in the tunnelling event, not in
        // KeyDown. With AcceptsReturn="True" the TextBox class handler consumes
        // Enter to insert a newline and marks it handled, and class handlers run
        // before instance handlers on the bubbling event — so a KeyDown handler
        // for Enter is never invoked. That is why this was Ctrl+Enter upstream.
        switch (e.Key)
        {
            case Key.Escape when DataContext is ChatSessionViewModel busyVm
                                 && busyVm.StopCommand.CanExecute(null):
                busyVm.StopCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift:
                // Shift+Enter falls through to the TextBox and inserts a newline.
                if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
                {
                    vm.SendCommand.Execute(null);
                }

                // Handled either way: without this, an Enter pressed while the
                // input is empty or a turn is running would insert a stray
                // newline instead of doing nothing.
                e.Handled = true;
                break;
        }
    }

    private void MentionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!MentionPopup.IsOpen) return;
        // Handle on PreviewMouseDown so the click commits BEFORE the default
        // ListBoxItem mouse-down logic moves keyboard focus away from the
        // input textbox.
        if (e.OriginalSource is DependencyObject src)
        {
            var item = FindAncestor<ListBoxItem>(src);
            if (item?.DataContext is MentionEntry entry)
            {
                CommitMention(entry);
                InputTextBox.Focus();
                e.Handled = true;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        DependencyObject? current = node;
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MoveMentionSelection(int delta)
    {
        var count = MentionList.Items.Count;
        if (count == 0) return;

        var idx = MentionList.SelectedIndex;
        if (idx < 0) idx = delta > 0 ? -1 : 0;
        var next = ((idx + delta) % count + count) % count;
        MentionList.SelectedIndex = next;
        if (MentionList.SelectedItem is { } sel)
            MentionList.ScrollIntoView(sel);
    }

    private bool CommitMentionSelection()
    {
        if (MentionList.SelectedItem is not MentionEntry entry) return false;
        if (!SelectionMatchesTypedText(entry)) return false;

        CommitMention(entry);
        return true;
    }

    /// <summary>
    /// Whether the highlighted row is actually a completion of what has been
    /// typed.
    ///
    /// The command list is filtered asynchronously, so between keystrokes the
    /// highlight can still be sitting on an earlier candidate. Committing that
    /// used to leave the wrong text in the composer, which was visible and
    /// recoverable; now that picking a command runs it, it would silently
    /// execute something the user never chose — typing /compact and getting
    /// /usage.
    /// </summary>
    private bool SelectionMatchesTypedText(MentionEntry entry)
    {
        var text = InputTextBox.Text ?? "";
        var caret = InputTextBox.CaretIndex;

        if (_mentionStart < 0 || _mentionStart > caret || caret > text.Length) return false;

        var typed = text.Substring(_mentionStart, caret - _mentionStart);

        // Nothing typed past the trigger: the highlight is the only intent
        // expressed, so it stands.
        if (typed.Length == 0) return true;

        var candidate = (entry.InsertText ?? "").Trim().TrimStart('/', '@');
        return candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
    }

    private void CommitMention(MentionEntry entry)
    {
        if (_mentionStart < 0) return;

        var text = InputTextBox.Text ?? "";
        var caret = InputTextBox.CaretIndex;
        if (_mentionStart - 1 < 0 || _mentionStart - 1 >= text.Length)
        {
            CloseMentionPopup();
            return;
        }

        var atIndex = _mentionStart - 1;
        var endIndex = Math.Min(Math.Max(caret, _mentionStart), text.Length);

        var insert = entry.InsertText;

        // Quote paths containing spaces so the CLI sees one argument. Commands
        // already carry their own trailing space and must not be quoted.
        if (_triggerChar == '@' && insert.IndexOf(' ') >= 0)
            insert = "\"" + insert + "\"";

        _suppressTextChanged = true;
        try
        {
            InputTextBox.Text = text.Remove(atIndex, endIndex - atIndex).Insert(atIndex, insert);
            InputTextBox.CaretIndex = atIndex + insert.Length;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        var wasCommand = _triggerChar == '/';
        CloseMentionPopup();
        UpdateCommandHighlight();

        // Picking a command from the list is the whole intent — there is nothing
        // left to type, so run it rather than parking it in the composer and
        // waiting for Enter. A file mention is the opposite: it is one argument
        // in a sentence the user is still writing.
        if (wasCommand && IsBareCommand(InputTextBox.Text))
        {
            if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
        }
    }

    /// <summary>
    /// True when the composer holds a slash command and nothing else — no
    /// arguments and no prose around it.
    /// </summary>
    private static bool IsBareCommand(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length > 1
            && trimmed[0] == '/'
            && trimmed.IndexOf(' ') < 0
            && trimmed.IndexOf('\n') < 0;
    }

    /// <summary>
    /// Paints a bare slash command in the composer so it reads as a command
    /// rather than as the first word of a message.
    ///
    /// The whole box is styled rather than just the token: a WPF TextBox has one
    /// Foreground for all its text, and per-run formatting would mean swapping
    /// in a RichTextBox — which would take the caret arithmetic the @ and /
    /// pickers depend on with it. Since a bare command IS the entire input, the
    /// distinction is not visible. As soon as an argument is typed the styling
    /// drops, so arguments never masquerade as part of the command.
    /// </summary>
    private void UpdateCommandHighlight()
    {
        var isCommand = IsBareCommand(InputTextBox.Text);

        if (isCommand)
        {
            // SetResourceReference, not a plain assignment: a local value would
            // outrank the DynamicResource declared in XAML and freeze the
            // composer on one palette across a light/dark switch.
            InputTextBox.SetResourceReference(ForegroundProperty, "FpCommand");
            InputTextBox.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            // Clear rather than set back: this restores the binding the XAML
            // declared, so normal text keeps following the theme.
            InputTextBox.ClearValue(ForegroundProperty);
            InputTextBox.ClearValue(FontWeightProperty);
        }
    }

    private void ShowMentionPopup(string filter)
    {
        if (_triggerChar == '/')
        {
            // Discovery touches the filesystem, so it runs once per popup and
            // is cached. Kick it off and filter when it lands — the popup opens
            // immediately either way rather than stalling on disk IO.
            if (_commandCache is null)
            {
                _ = LoadCommandCacheAsync(filter);
                return;
            }
        }
        else
        {
            EnsureMentionCache();
        }

        ApplyMentionFilter(filter);
    }

    private async Task LoadCommandCacheAsync(string filter)
    {
        var entries = new List<MentionEntry>();
        try
        {
            var service = ForgePilotPackage.AssetService;
            if (service is not null)
            {
                var assets = await service.DiscoverAsync();
                foreach (var asset in assets
                             .Where(a => a.Invocation is not null && a.IsEnabled)
                             .OrderBy(a => a.Kind)
                             .ThenBy(a => a.Name))
                {
                    entries.Add(new MentionEntry(
                        asset.Invocation!,
                        asset.Invocation! + " ",
                        asset.Description,
                        asset.Kind == ClaudeAssetKind.Skill
                            ? KnownMonikers.IntellisenseKeyword
                            : KnownMonikers.Action));
                }
            }
        }
        catch (Exception ex)
        {
            // A malformed .claude config must not break typing.
            System.Diagnostics.Debug.WriteLine($"ForgePilot: command discovery failed: {ex}");
        }

        // Add the commands the extension answers itself — they are real and
        // usable, and would be invisible if only disk-backed ones were listed.
        foreach (var (name, description) in LocalCommands)
            entries.Add(new MentionEntry("/" + name, "/" + name, description, KnownMonikers.Action));

        _commandCache = entries;

        // The user kept typing while discovery ran; only apply if the popup is
        // still open on the same trigger.
        if (_mentionStart >= 0 && _triggerChar == '/')
            ApplyMentionFilter(filter);
    }

    /// <summary>
    /// Commands offered in the "/" picker beyond whatever the workspace defines
    /// in <c>.claude/commands</c>.
    ///
    /// Mixed on purpose: <c>clear</c> and <c>help</c> are handled in the view
    /// model, the rest are passed straight to Claude Code, which answers them
    /// in print mode. They are listed here only because the CLI does not
    /// enumerate its own built-ins for the picker to discover.
    /// </summary>
    private static readonly (string Name, string Description)[] LocalCommands =
    {
        ("clear", "Clear this conversation"),
        ("usage", "Subscription usage — session and weekly limits"),
        ("compact", "Summarise the conversation to free up context"),
    };

    private void CloseMentionPopup()
    {
        _mentionStart = -1;
        MentionPopup.IsOpen = false;
        MentionList.ItemsSource = null;
    }

    private void ApplyMentionFilter(string filter)
    {
        var source = _triggerChar == '/' ? _commandCache : _mentionCache;
        if (source == null)
        {
            MentionList.ItemsSource = Array.Empty<MentionEntry>();
            return;
        }

        IEnumerable<MentionEntry> q = source;
        if (!string.IsNullOrEmpty(filter))
        {
            q = q.Where(e =>
                e.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var results = q.Take(200).ToList();
        MentionList.ItemsSource = results;
        if (results.Count > 0) MentionList.SelectedIndex = 0;

        if (results.Count == 0) MentionPopup.IsOpen = false;
        else MentionPopup.IsOpen = true;
    }

    private void EnsureMentionCache()
    {
        if (_mentionCache != null) return;

        var root = (DataContext as ChatSessionViewModel)?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _mentionCache = new List<MentionEntry>();
            return;
        }

        _mentionCache = EnumerateEntries(root!).ToList();
    }

    private static IEnumerable<MentionEntry> EnumerateEntries(string root)
    {
        const int maxEntries = 5000;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".idea", "packages", "TestResults"
        };

        var stack = new Stack<string>();
        stack.Push(root);
        var count = 0;

        while (stack.Count > 0 && count < maxEntries)
        {
            var current = stack.Pop();

            string[] dirs;
            string[] files;
            try
            {
                dirs = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (skip.Contains(name) || name.StartsWith("."))
                    continue;
                yield return new MentionEntry(ToRelative(root, dir), name, isDirectory: true);
                if (++count >= maxEntries) yield break;
                stack.Push(dir);
            }

            foreach (var file in files)
            {
                yield return new MentionEntry(
                    ToRelative(root, file), Path.GetFileName(file), isDirectory: false);
                if (++count >= maxEntries) yield break;
            }
        }
    }

    private static string ToRelative(string root, string fullPath)
    {
        var rooted = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var rel = fullPath.StartsWith(rooted, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(rooted.Length)
            : fullPath;
        return rel.Replace('\\', '/');
    }
}
