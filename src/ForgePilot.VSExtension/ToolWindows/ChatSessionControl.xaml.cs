using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
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
            menu.Items.Add(new Separator());

        var newItem = new MenuItem { Header = "New session" };
        newItem.Click += (_, _) => _sessionListViewModel.NewSessionCommand.Execute(null);
        menu.Items.Add(newItem);

        menu.IsOpen = true;
    }

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
            var chosen = ChatSessionViewModel.Models.First(m => m.Label == label);
            var s = vm.GetSessionSettings();
            if (s is null) return;
            ReportIfBlocked(vm.ApplySessionSettings(s with { Model = chosen.Value }));
        });
    }

    private void EffortChip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatSessionViewModel vm) return;
        var current = vm.EffortLabel;

        ShowChipMenu(EffortChip, ChatSessionViewModel.EffortLevels.Select(l => l.Label), current, label =>
        {
            var chosen = ChatSessionViewModel.EffortLevels.First(l => l.Label == label);
            var s = vm.GetSessionSettings();
            if (s is null) return;
            ReportIfBlocked(vm.ApplySessionSettings(s with { MaxThinkingTokens = chosen.Tokens }));
        });
    }

    private void ModeChip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatSessionViewModel vm)
            ReportIfBlocked(vm.TogglePlanMode());
    }

    /// <summary>
    /// Builds a themed picker anchored above a chip. Uses the shared Fp menu
    /// styles rather than a default ContextMenu, which renders as a grey
    /// Windows menu with a blue system checkmark and reads as another
    /// application intruding on the panel.
    /// </summary>
    private static void ShowChipMenu(
        Button anchor, IEnumerable<string> options, string current, Action<string> onPick)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            VerticalOffset = -6,
            HorizontalOffset = 0,
            Style = Application.Current?.TryFindResource("FpContextMenuStyle") as Style
        };

        foreach (var option in options)
        {
            var captured = option;
            var item = new MenuItem
            {
                Header = captured,
                IsCheckable = true,
                IsChecked = captured == current
            };
            item.Click += (_, _) =>
            {
                if (captured != current) onPick(captured);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Applies the themed style to a menu built elsewhere.</summary>
    private static ContextMenu ThemedMenu(UIElement anchor,
        System.Windows.Controls.Primitives.PlacementMode placement) => new()
    {
        PlacementTarget = anchor,
        Placement = placement,
        Style = Application.Current?.TryFindResource("FpContextMenuStyle") as Style
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

    public void Initialize(ChatSessionViewModel viewModel)
    {
        DataContext = viewModel;

        // Seed the footer chips from whatever the session actually launched
        // with, rather than leaving them on their declared defaults.
        viewModel.RefreshSettingLabels();

        viewModel.MessageAdded += (id, type, data) =>
            _ = ChatWebView.AddMessageAsync(id, type, data);

        viewModel.MessageContentUpdated += (id, content) =>
            _ = ChatWebView.UpdateContentAsync(id, content);

        viewModel.MessageStatusUpdated += (id, status, expanderTitle) =>
            _ = ChatWebView.UpdateStatusAsync(id, status, expanderTitle);

        viewModel.MessageBodySet += (id, body, mode) =>
            _ = ChatWebView.SetBodyAsync(id, body, mode);

        viewModel.MessageCompleted += (id) =>
            _ = ChatWebView.CompleteMessageAsync(id);

        viewModel.AllCleared += () =>
            _ = ChatWebView.ClearAllAsync();

        viewModel.MessagesRestored += (messages) =>
            _ = ChatWebView.LoadMessagesAsync(messages);

        // Banner mounting is now driven by the ActiveBanner property on the VM —
        // the BannerHost ContentControl in XAML binds to it and DataTemplates
        // pick the right UserControl by VM type.

        ApplyTheme();

        // Re-apply when VS theme changes
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

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
                case Key.Tab:
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
        if (MentionList.SelectedItem is MentionEntry entry)
        {
            CommitMention(entry);
            return true;
        }
        return false;
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

        CloseMentionPopup();
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

    /// <summary>Commands handled in ChatSessionViewModel rather than by the CLI.</summary>
    private static readonly (string Name, string Description)[] LocalCommands =
    {
        ("clear", "Clear this conversation"),
        ("cost", "Show tokens and cost for this session"),
        ("usage", "Show tokens and cost for this session"),
        ("help", "What works here, and what doesn't"),
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
