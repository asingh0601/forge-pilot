using ForgePilot.UI.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace ForgePilot.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(ChatSessionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

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

        // Permission / question / login prompts arrive through the view model's
        // ActiveBanner property, which the BannerHost ContentControl in XAML
        // binds to. Upstream's Desktop host still called the older
        // ShowPermissionBanner / ShowQuestionCard / ShowLoginBanner methods,
        // which were removed when banners moved to the ActiveBanner +
        // DataTemplate model — that's why this project stopped compiling and
        // was dropped from the solution. The banner DataTemplates land here
        // once the banner views move into ForgePilot.UI.

        Loaded += (_, _) => InputTextBox.Focus();
    }

    /// <summary>
    /// Enter sends, Shift+Enter inserts a newline, Esc interrupts.
    ///
    /// This must be the tunnelling PreviewKeyDown, not KeyDown. With
    /// AcceptsReturn="True" the TextBox class handler consumes Enter to insert
    /// a newline and marks it handled, and class handlers run before instance
    /// handlers on the bubbling event — so a KeyDown handler for Enter is never
    /// invoked at all.
    /// </summary>
    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            DataContext is ChatSessionViewModel busyVm &&
            busyVm.StopCommand.CanExecute(null))
        {
            busyVm.StopCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;

        if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }

        // Handled either way, so Enter on an empty or busy composer does
        // nothing rather than inserting a stray newline.
        e.Handled = true;
    }
}
