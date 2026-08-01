using ForgePilot.Services.Abstractions;
using ForgePilot.Services.DependencyInjection;
using ForgePilot.UI;
using ForgePilot.UI.Themes;
using ForgePilot.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO;
using System.Windows;

namespace ForgePilot.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must run before the first window is constructed — the Cd* brushes are
        // resolved with DynamicResource, but a window built against an empty
        // resource dictionary renders unstyled for a frame.
        ClaudeThemeManager.Apply(
            IsSystemInDarkMode() ? ClaudeThemeVariant.Dark : ClaudeThemeVariant.Light);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File("logs/ForgePilot-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var workingDir = e.Args.Length > 0 ? e.Args[0] : Environment.CurrentDirectory;
            workingDir = Path.GetFullPath(workingDir);

            if (!Directory.Exists(workingDir))
            {
                MessageBox.Show($"Directory does not exist: {workingDir}", "ForgePilot", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            _host = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog()
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<OutputListener>();
                    services.AddSingleton<IOutputListener>(sp => sp.GetRequiredService<OutputListener>());
                    services.AddSingleton<ChatSessionViewModel>();
                    services.AddForgePilotServices(options =>
                    {
                        options.WorkingDirectory = workingDir;
                    });
                    services.AddTransient<MainWindow>();
                })
                .Build();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            MessageBox.Show($"Failed to start: {ex.Message}", "ForgePilot", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Reads the Windows "apps use light theme" preference. Missing key (older
    /// Windows, or a locked-down profile) is treated as dark, which matches the
    /// default palette in chat-template.html.
    /// </summary>
    private static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
