using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using ForgePilot.Services.Abstractions;
using ForgePilot.Services.DependencyInjection;
using ForgePilot.Services.Services;
using ForgePilot.UI;
using ForgePilot.UI.Controls;
using ForgePilot.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Serilog;
using ForgePilot.Services.Configuration;
using ForgePilot.VSExtension.Options;
using ForgePilot.VSExtension.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace ForgePilot.VSExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(ForgePilotOptionsPage), "Forge Pilot", "General", 0, 0, true)]
[ProvideToolWindow(typeof(SessionListToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
// Chat docks into the right-hand well alongside Solution Explorer, the way
// Copilot Chat does, instead of opening as an MDI document tab in the editor
// area. Note these placement hints only apply the first time a window is
// created on a given VS profile — after that the shell restores the user's own
// layout, so an existing install keeps whatever position it already had.
// Single instance: sessions are switched inside this one window via the header
// picker, so MultiInstances would just let stray duplicates accumulate.
[ProvideToolWindow(typeof(ChatSessionToolWindow),
    Style = VsDockStyle.Tabbed,
    Window = EnvDTE.Constants.vsWindowKindSolutionExplorer,
    Orientation = ToolWindowOrientation.Right,
    Transient = true)]
[Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f")]
public sealed class ForgePilotPackage : AsyncPackage, IVsSolutionEvents
{
    private static ForgePilotPackage? _instance;

    // Exposed so tool windows can bind when VS restores them
    internal static SessionListViewModel? SessionListVM => _instance?._sessionListViewModel;

    /// <summary>
    /// Discovers the CLI's commands / skills / connectors / plugins for the
    /// current workspace. Built per-workspace rather than resolved from the
    /// chat session's DI scope, because the assets menu must work even when no
    /// session is loaded.
    /// </summary>
    internal static IClaudeAssetService? AssetService =>
        _instance?._solutionDirectory is { } dir ? new ClaudeAssetService(dir) : null;

    private SessionListViewModel? _sessionListViewModel;
    private ISessionStore? _sessionStore;
    private string? _solutionDirectory;
    private static readonly SemaphoreSlim _openSessionGate = new(1, 1);
    private uint _solutionEventsCookie;

    // Single chat window. Switching sessions swaps the view model inside this
    // one window rather than opening another, so there is exactly one place to
    // chat and the session picker in its header decides what's loaded.
    private const int ChatWindowId = 0;
    private string? _activeSessionId;
    private ChatSessionViewModel? _activeViewModel;

    public static bool IsLoaded => _instance is not null;

    /// <summary>Raised on the UI thread after the package has fully initialized.</summary>
    internal static event Action? Initialized;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        _instance = this;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        _solutionDirectory = GetSolutionDirectory()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Initialize session store
        _sessionStore = new JsonSessionStore();

        _sessionListViewModel = new SessionListViewModel();

        // Initialize persistence and load saved sessions
        await InitializeSessionPersistenceAsync();

        _sessionListViewModel.SessionOpenRequested += session =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await OpenOrActivateSessionAsync(session);
            });
        };

        _sessionListViewModel.SessionRemoved += session =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await CloseSessionWindowAsync(session);
            });
        };

        // Listen for file-open requests from rendered markdown
        ChatWebView.FileOpenRequested += OnFileOpenRequested;

        // Listen for solution open/close/switch events
        if (await GetServiceAsync(typeof(SVsSolution)) is IVsSolution solutionService)
        {
            solutionService.AdviseSolutionEvents(this, out _solutionEventsCookie);
        }

        Initialized?.Invoke();

        // Check the Marketplace for a newer published version and surface an InfoBar
        // if one is available. Fire-and-forget on a background task with a small delay
        // so we don't compete with VS startup work.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), DisposalToken);
                await new UpdateChecker(this).CheckAsync(DisposalToken);
            }
            catch (OperationCanceledException) { }
        }, cancellationToken);
    }

    private async Task InitializeSessionPersistenceAsync()
    {
        if (_sessionStore is null || _solutionDirectory is null || _sessionListViewModel is null) return;

        try
        {
            await _sessionStore.EnsureWorkspaceAsync(_solutionDirectory);
            await PurgeOldSessionsAsync();
            _sessionListViewModel.Initialize(_sessionStore, _solutionDirectory);
            await _sessionListViewModel.LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to initialize session persistence: {ex}");
        }
    }

    private async Task PurgeOldSessionsAsync()
    {
        if (_sessionStore is null || _solutionDirectory is null) return;

        try
        {
            var optionsPage = (ForgePilotOptionsPage?)GetDialogPage(typeof(ForgePilotOptionsPage));
            var keepDays = optionsPage?.KeepActivityDays ?? 30;
            await _sessionStore.DeleteSessionsOlderThanAsync(_solutionDirectory, keepDays);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to purge old sessions: {ex}");
        }
    }

    private ChatSessionViewModel CreateChatViewModel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var workingDir = _solutionDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var services = new ServiceCollection();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            // Directory name stays unspaced and must match the session store's
            // root (%AppData%\ForgePilot) — the display name having a space is
            // a UI decision, not a storage one.
            "ForgePilot", "logs", "ForgePilot-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(logPath, rollingInterval: Serilog.RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Read persisted settings from Tools → Options → ForgePilot
        var optionsPage = (ForgePilotOptionsPage?)GetDialogPage(typeof(ForgePilotOptionsPage));

        var outputListener = new OutputListener();
        services.AddSingleton(outputListener);
        services.AddSingleton<IOutputListener>(outputListener);
        services.AddForgePilotServices(options =>
        {
            options.WorkingDirectory = workingDir;

            if (optionsPage is not null)
            {
                options.ClaudeCliPath = optionsPage.ClaudeCliPath;
                options.CliPermissionMode = optionsPage.CliPermissionMode;
            }
        });

        var provider = services.BuildServiceProvider();
        var chatService = provider.GetRequiredService<IChatService>();
        var optionsAccessor = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForgePilot.Services.Configuration.ForgePilotOptions>>();
        var permissionBroker = provider.GetRequiredService<ForgePilot.Services.ClaudeCli.Permissions.IPermissionBroker>();
        var questionBroker = provider.GetRequiredService<ForgePilot.Services.ClaudeCli.Questions.IUserQuestionBroker>();
        var vmLogger = provider.GetService<Microsoft.Extensions.Logging.ILogger<ChatSessionViewModel>>();

        var vm = new ChatSessionViewModel(chatService, outputListener, optionsAccessor, permissionBroker, questionBroker, vmLogger);
        vm.SetServiceScope(provider);
        return vm;
    }

    private string? GetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is IVsSolution solution)
        {
            solution.GetSolutionInfo(out string solutionDir, out _, out _);
            if (!string.IsNullOrEmpty(solutionDir))
                return solutionDir;
        }
        return null;
    }

    public static async Task ShowSessionListWindowAsync()
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();
        var window = await _instance.ShowToolWindowAsync(
            typeof(SessionListToolWindow), 0, true, _instance.DisposalToken);

        // Initialize in case the window was just created
        if (window is SessionListToolWindow slw)
        {
            slw.SessionListControl.BindIfNeeded();
        }
    }

    /// <summary>
    /// Opens the single chat window, resuming the most recently used session.
    /// Only creates a session when there are none — otherwise every reload
    /// would leave behind another empty one.
    /// </summary>
    public static async Task ShowChatSessionWindowAsync()
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        var vm = _instance._sessionListViewModel;
        if (vm is null) return;

        if (vm.Sessions.Count == 0)
        {
            // NewSessionCommand raises SessionOpenRequested, which loads it.
            vm.NewSessionCommand.Execute(null);
            return;
        }

        // Resume where the user left off. LastActivity is maintained by the
        // view model on every completed exchange.
        var mostRecent = vm.Sessions
            .OrderByDescending(s => s.LastActivity)
            .First();

        await OpenOrActivateSessionAsync(mostRecent);
    }

    private static async Task OpenOrActivateSessionAsync(SessionInfo session)
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Already showing this session — just focus the window.
        if (_instance._activeSessionId == session.Id)
        {
            try
            {
                var existing = await _instance.ShowToolWindowAsync(
                    typeof(ChatSessionToolWindow), ChatWindowId, true, _instance.DisposalToken);

                if (existing?.Frame is IVsWindowFrame existingFrame)
                {
                    existingFrame.Show();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to activate session window: {ex}");
            }
            return;
        }

        // Loading a session builds a WebView2 and replays the transcript.
        // Serialize with a gate — concurrent WebView2 init deadlocks the UI
        // thread. WaitAsync(0) drops rapid clicks rather than queueing them.
        if (!await _openSessionGate.WaitAsync(0))
            return;

        try
        {
            // Tear down whatever session was loaded: disposing the view model
            // kills its CLI process and pipe server. Without this, switching
            // sessions would leak a `claude` process per switch.
            if (_instance._activeViewModel is not null)
            {
                var previousId = _instance._activeSessionId;
                if (previousId is not null &&
                    _instance._sessionListViewModel?.Sessions.FirstOrDefault(s => s.Id == previousId) is { } previous)
                {
                    previous.IsActive = false;
                }

                try { _instance._activeViewModel.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to dispose previous session: {ex}"); }

                _instance._activeViewModel = null;
                _instance._activeSessionId = null;
            }

            _instance._activeSessionId = session.Id;

            var window = await _instance.ShowToolWindowAsync(
                typeof(ChatSessionToolWindow), ChatWindowId, true, _instance.DisposalToken);

            if (window is not null)
            {
                window.Caption = ChatSessionToolWindow.BaseCaption;

                if (window is ChatSessionToolWindow chatWindow)
                {
                    ChatSessionViewModel viewModel;
                    try
                    {
                        viewModel = _instance.CreateChatViewModel();
                    }
                    catch (Exception ex)
                    {
                        viewModel = new ChatSessionViewModel(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                        System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to create chat service: {ex}");
                        MessageBox.Show($"Forge Pilot service init failed:\n\n{ex.Message}\n\n{ex.InnerException?.Message}", "Forge Pilot Debug", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    // Wire up the UI event handlers BEFORE restoring messages,
                    // so the MessagesRestored event is received by the WebView.
                    chatWindow.ChatControl.Initialize(viewModel);

                    // Enable persistence on the view model
                    if (session.PersistedId.HasValue && _instance._sessionStore is not null && _instance._solutionDirectory is not null)
                    {
                        viewModel.EnablePersistence(_instance._sessionStore, _instance._solutionDirectory, session.PersistedId.Value);

                        // Restore messages from a previously saved session
                        if (!session.IsActive)
                        {
                            await viewModel.RestoreFromStoreAsync();
                            session.IsActive = true;
                            if (!string.IsNullOrEmpty(viewModel.SessionTitle) && viewModel.SessionTitle != "New Session")
                            {
                                // Keep the persisted title
                            }
                            else
                            {
                                viewModel.SessionTitle = session.Name;
                            }
                        }
                    }

                    // Link the view model to its session entry so cost updates flow back to the list
                    viewModel.SessionInfo = session;
                    _instance._activeViewModel = viewModel;

                    // Let the header's session picker show and switch sessions.
                    chatWindow.ChatControl.BindSessions(_instance._sessionListViewModel, session);

                    // The caption stays "Forge Pilot" — with one window, the
                    // session name belongs in the header picker, not the tab,
                    // where it would make the tool window hard to find. Only the
                    // busy indicator is worth surfacing on the tab.
                    viewModel.PropertyChanged += (_, e) =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();

                        if (e.PropertyName == nameof(ChatSessionViewModel.SessionTitle))
                        {
                            session.Name = viewModel.SessionTitle;
                        }
                        else if (e.PropertyName == nameof(ChatSessionViewModel.DisplayTitle))
                        {
                            window.Caption = viewModel.IsBusy
                                ? viewModel.DisplayTitle.Substring(0, 2) + ChatSessionToolWindow.BaseCaption
                                : ChatSessionToolWindow.BaseCaption;
                        }
                    };

                    // Closing the window ends the session: dispose the view model
                    // so the CLI process and pipe server go with it.
                    chatWindow.Closed += () =>
                    {
                        if (_instance is not null && _instance._activeSessionId == session.Id)
                        {
                            _instance._activeSessionId = null;
                            _instance._activeViewModel = null;
                        }
                        session.IsActive = false;
                        viewModel.Dispose();
                    };
                }

                if (window.Frame is IVsWindowFrame frame)
                {
                    // Belt and braces alongside the ProvideToolWindow hints: if
                    // a previously saved layout put this window in the document
                    // well, force it back to a dock. Without it the panel opens
                    // as an editor tab, which is not where a chat sidebar
                    // belongs. Best effort — a failure here must not stop the
                    // session from opening.
                    try
                    {
                        frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_Dock);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ForgePilot: could not force dock mode: {ex.Message}");
                    }

                    frame.Show();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Forge Pilot error: {ex.Message}", "Forge Pilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _openSessionGate.Release();
        }
    }

    private static async Task CloseSessionWindowAsync(SessionInfo session)
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Only the loaded session owns the window. Deleting any other session
        // is purely a list operation and must not close what's on screen.
        if (_instance._activeSessionId != session.Id) return;

        var window = _instance.FindToolWindow(typeof(ChatSessionToolWindow), ChatWindowId, false);
        if (window?.Frame is IVsWindowFrame frame)
        {
            frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        _instance._activeSessionId = null;
        _instance._activeViewModel = null;
        session.IsActive = false;
    }

    /// <summary>
    /// Switches the session list to a new workspace directory.
    /// Closes idle chat windows and keeps busy (waiting for AI) ones open.
    /// </summary>
    private async Task SwitchWorkspaceAsync(string newSolutionDirectory)
    {
        if (_sessionStore is null || _sessionListViewModel is null) return;
        if (string.Equals(_solutionDirectory, newSolutionDirectory, StringComparison.OrdinalIgnoreCase)) return;

        await JoinableTaskFactory.SwitchToMainThreadAsync();

        // Close the chat window unless it's mid-turn. Sessions are scoped to a
        // workspace, so the loaded one no longer belongs here — but yanking it
        // away while the CLI is still answering would lose the response.
        if (_activeSessionId is not null)
        {
            var window = FindToolWindow(typeof(ChatSessionToolWindow), ChatWindowId, false);
            var isBusy = window is ChatSessionToolWindow chatWindow
                         && chatWindow.ChatControl.DataContext is ChatSessionViewModel vm
                         && vm.IsBusy;

            if (!isBusy)
            {
                if (window?.Frame is IVsWindowFrame frame)
                {
                    frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                }
                _activeSessionId = null;
                _activeViewModel = null;
            }
        }

        // Switch to the new workspace
        _solutionDirectory = newSolutionDirectory;

        try
        {
            await _sessionStore.EnsureWorkspaceAsync(_solutionDirectory);
            _sessionListViewModel.Initialize(_sessionStore, _solutionDirectory);
            await _sessionListViewModel.LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to switch workspace: {ex}");
        }
    }

    // --- IVsSolutionEvents implementation ---

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var newDir = GetSolutionDirectory()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await SwitchWorkspaceAsync(newDir);
        });
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await SwitchWorkspaceAsync(fallback);
        });
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;

    private void OnFileOpenRequested(string rawPath)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            // Parse optional :line suffix (e.g. "file.cs:42" or "file.cs:42-51")
            int line = 0;
            var lineMatch = Regex.Match(rawPath, @":(\d+)(?:-\d+)?$");
            var filePath = lineMatch.Success ? rawPath.Substring(0, lineMatch.Index) : rawPath;

            // Convert MSYS/Git-Bash style paths ("/c/foo/bar") to Windows form ("c:\foo\bar")
            var msysMatch = Regex.Match(filePath, @"^/([A-Za-z])/");
            if (msysMatch.Success)
                filePath = msysMatch.Groups[1].Value + ":/" + filePath.Substring(3);

            // Normalize forward slashes
            filePath = filePath.Replace('/', '\\');

            // Resolve relative paths against the solution directory
            if (!Path.IsPathRooted(filePath) && _solutionDirectory is not null)
            {
                filePath = Path.GetFullPath(Path.Combine(_solutionDirectory, filePath));
            }

            if (!File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"ForgePilot: File not found: {filePath}");
                return;
            }

            if (lineMatch.Success)
                line = int.Parse(lineMatch.Groups[1].Value);

            try
            {
                VsShellUtilities.OpenDocument(this, filePath, Guid.Empty,
                    out _, out _, out IVsWindowFrame? frame);
                frame?.Show();

                if (line > 0 && frame is not null)
                {
                    // Navigate to the specific line
                    if (VsShellUtilities.GetTextView(frame) is var textView && textView is not null)
                    {
                        textView.SetCaretPos(line - 1, 0);
                        textView.CenterLines(line - 1, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ForgePilot: Failed to open file: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            ChatWebView.FileOpenRequested -= OnFileOpenRequested;

            if (_solutionEventsCookie != 0)
            {
                if (GetService(typeof(SVsSolution)) is IVsSolution solutionService)
                {
                    solutionService.UnadviseSolutionEvents(_solutionEventsCookie);
                }
                _solutionEventsCookie = 0;
            }
        }
        base.Dispose(disposing);
    }
}
