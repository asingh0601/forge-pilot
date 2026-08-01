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

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            !Keyboard.IsKeyDown(Key.LeftShift) &&
            !Keyboard.IsKeyDown(Key.RightShift))
        {
            if (DataContext is ChatSessionViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
