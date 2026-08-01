using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgePilot.Services.Abstractions;
using ForgePilot.Services.ClaudeCli.Permissions;
using ForgePilot.Services.ClaudeCli.Questions;
using ForgePilot.Services.Configuration;
using ForgePilot.Services.Models;
using ForgePilot.UI.ViewModels.Banners;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgePilot.UI.ViewModels;

public partial class ChatSessionViewModel : ObservableObject, IDisposable
{
    private readonly IChatService? _chatService;
    private IDisposable? _serviceScope;
    private readonly ConcurrentDictionary<string, ChatItemViewModel> _activeItems = new();
    private int _userMsgCounter;

    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    private CancellationTokenSource? _sendCts;

    [ObservableProperty]
    private string _sessionTitle = "New Session";

    // Realtime activity indicator: a braille spinner prefix while the AI is
    // working, a steady "? " prefix while awaiting user input (permission /
    // question banner), and no prefix while idle. Host bindings (e.g. the VS
    // tool window caption) should use DisplayTitle; SessionTitle stays plain
    // for the session list entry so the sidebar doesn't flicker.
    // Claude's four-pointed star, cycled through decreasing weights. One timer
    // drives both the window-caption prefix and the composer status line.
    private static readonly string SpinnerFrames = "✻✽✢·✢✽";
    private const string AwaitingPrefix = "? ";
    private int _pendingUserPrompts;
    private int _spinnerFrame;
    private DateTime? _busySince;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    [ObservableProperty]
    private string _displayTitle = "New Session";

    /// <summary>Animated glyph shown beside <see cref="StatusLine"/> while busy.</summary>
    [ObservableProperty]
    private string _activityGlyph = "✻";

    /// <summary>
    /// Composer status text — "Working… (12s · esc to interrupt)" while the CLI
    /// is running, or a prompt to answer the open banner. Empty when idle; the
    /// view swaps in its own hint text then.
    /// </summary>
    [ObservableProperty]
    private string _statusLine = "";

    public SessionActivity Activity =>
        _pendingUserPrompts > 0 ? SessionActivity.AwaitingUser :
        IsBusy ? SessionActivity.Busy :
        SessionActivity.Idle;

    partial void OnIsBusyChanged(bool value) => UpdateActivityIndicator();

    partial void OnSessionTitleChanged(string value) => UpdateDisplayTitle();

    private void UpdateActivityIndicator()
    {
        if (Activity == SessionActivity.Busy)
        {
            _busySince ??= DateTime.UtcNow;
            EnsureActivityTimer();
            if (!_activityTimer!.IsEnabled) _activityTimer.Start();
        }
        else
        {
            _activityTimer?.Stop();
            _busySince = null;
        }
        UpdateDisplayTitle();
        UpdateStatusLine();
    }

    private void UpdateStatusLine()
    {
        switch (Activity)
        {
            case SessionActivity.Busy:
                var elapsed = _busySince.HasValue
                    ? (int)(DateTime.UtcNow - _busySince.Value).TotalSeconds
                    : 0;
                ActivityGlyph = SpinnerFrames[_spinnerFrame].ToString();
                StatusLine = $"Working… ({elapsed}s · esc to interrupt)";
                break;

            case SessionActivity.AwaitingUser:
                ActivityGlyph = "?";
                StatusLine = "Waiting for your response";
                break;

            default:
                ActivityGlyph = "✻";
                StatusLine = "";
                break;
        }
    }

    private void EnsureActivityTimer()
    {
        if (_activityTimer != null) return;
        var dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _activityTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _activityTimer.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            UpdateDisplayTitle();
            UpdateStatusLine();
        };
    }

    private void UpdateDisplayTitle()
    {
        var prefix = Activity switch
        {
            SessionActivity.Busy => SpinnerFrames[_spinnerFrame] + " ",
            SessionActivity.AwaitingUser => AwaitingPrefix,
            _ => ""
        };
        DisplayTitle = prefix + SessionTitle;
    }

    public string WorkingDirectory { get; }

    /// <summary>
    /// The <see cref="SessionInfo"/> entry in the session list that owns this view model.
    /// When set, cost is updated on the entry after each completed message exchange.
    /// </summary>
    public SessionInfo? SessionInfo { get; set; }

    public event Action? ScrollRequested;

    // Events for single-WebView rendering
    public event Action<string, ChatItemType, ChatMessageData>? MessageAdded;
    public event Action<string, string>? MessageContentUpdated;
    public event Action<string, OutputItemStatus, string>? MessageStatusUpdated;
    public event Action<string, string, OutputBodyMode>? MessageBodySet;
    public event Action<string>? MessageCompleted;
    public event Action? AllCleared;
    public event Action<IEnumerable<ChatMessageData>>? MessagesRestored;

    /// <summary>
    /// The banner currently shown above the input box (permission prompt,
    /// AskUserQuestion card, or login prompt). The host's ContentControl
    /// binds to this; concrete type is selected by DataTemplate. Null when
    /// no banner is active.
    /// </summary>
    [ObservableProperty]
    private IBannerViewModel? _activeBanner;

    private readonly IPermissionBroker? _permissionBroker;
    private readonly IUserQuestionBroker? _questionBroker;
    private readonly ILogger _logger;

    /// <summary>
    /// Standalone constructor for use without a chat service (e.g. before service is wired up).
    /// </summary>
    public ChatSessionViewModel(string workingDirectory = "")
    {
        WorkingDirectory = workingDirectory;
        _logger = NullLogger.Instance;
    }

    public ChatSessionViewModel(IChatService chatService, OutputListener outputListener, IOptions<ForgePilotOptions> options)
        : this(chatService, outputListener, options, permissionBroker: null, questionBroker: null, logger: null)
    {
    }

    public ChatSessionViewModel(
        IChatService chatService,
        OutputListener outputListener,
        IOptions<ForgePilotOptions> options,
        IPermissionBroker? permissionBroker,
        IUserQuestionBroker? questionBroker,
        ILogger<ChatSessionViewModel>? logger = null)
    {
        _chatService = chatService;
        WorkingDirectory = options.Value.WorkingDirectory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        outputListener.StepStarted += OnStepStarted;
        outputListener.StepUpdated += OnStepUpdated;
        outputListener.StepCompleted += OnStepCompleted;

        _permissionBroker = permissionBroker;
        _questionBroker = questionBroker;

        if (_permissionBroker is not null)
            _permissionBroker.PermissionRequested += OnPermissionBrokerRequested;
        if (_questionBroker is not null)
            _questionBroker.QuestionRequested += OnQuestionBrokerRequested;

        chatService.LoginRequired += OnChatServiceLoginRequired;
    }

    private void OnChatServiceLoginRequired(string? errorMessage)
    {
        Dispatch(() =>
        {
            ActiveBanner = new LoginBannerViewModel(errorMessage, () =>
            {
                ActiveBanner = null;
                _chatService?.LaunchLogin();
            });
        });
    }

    private void OnPermissionBrokerRequested(PermissionRequest request)
    {
        Dispatch(() =>
        {
            try
            {
                _pendingUserPrompts++;
                UpdateActivityIndicator();
                _logger.LogInformation(
                    "[VM] Permission prompt requested (id={Id}, tool={Tool})",
                    request.Id, request.ToolName);
                ActiveBanner = new PermissionBannerViewModel(
                    request,
                    decision =>
                    {
                        Dispatch(() =>
                        {
                            ActiveBanner = null;
                            if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                            UpdateActivityIndicator();
                        });
                        _permissionBroker?.Resolve(request.Id, decision);
                    },
                    onAlwaysAllow: _permissionBroker is null
                        ? null
                        : toolName => _permissionBroker.AlwaysAllowTool(toolName));
            }
            catch (Exception ex)
            {
                // Without this guard the throw escapes Dispatcher.BeginInvoke,
                // tears down the dispatcher loop, and leaves the chat hung.
                _logger.LogError(ex, "[VM] Permission prompt handler crashed (id={Id})", request.Id);
                if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                UpdateActivityIndicator();
                try { _permissionBroker?.Resolve(request.Id, PermissionDecision.Deny("Banner failed to display")); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] PermissionBroker.Resolve also failed"); }
            }
        });
    }

    private void OnQuestionBrokerRequested(UserQuestionRequest request)
    {
        Dispatch(() =>
        {
            try
            {
                _pendingUserPrompts++;
                UpdateActivityIndicator();
                _logger.LogInformation(
                    "[VM] User question requested (toolUseId={Id}, questions={Count})",
                    request.ToolUseId, request.Questions.Count);
                ActiveBanner = new QuestionCardViewModel(request, answers =>
                {
                    Dispatch(() =>
                    {
                        ActiveBanner = null;
                        if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                        UpdateActivityIndicator();
                    });
                    _questionBroker?.Resolve(request.ToolUseId, answers);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VM] User question handler crashed (toolUseId={Id})", request.ToolUseId);
                if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                UpdateActivityIndicator();
                try { _questionBroker?.Resolve(request.ToolUseId, new Dictionary<string, string>()); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] QuestionBroker.Resolve also failed"); }
            }
        });
    }

    /// <summary>
    /// Enables persistence for this chat session.
    /// </summary>
    public void EnablePersistence(ISessionStore sessionStore, string folderPath, int sessionId)
    {
        // Use reflection-free approach: store in mutable fields
        SetPersistence(sessionStore, folderPath, sessionId);
    }

    private ISessionStore? _sessionStore;
    private string? _folderPath;
    private int? _sessionId;

    private void SetPersistence(ISessionStore store, string folder, int id)
    {
        _sessionStore = store;
        _folderPath = folder;
        _sessionId = id;
    }

    private ISessionStore? ActiveStore => _sessionStore;
    private string? ActiveFolder => _folderPath;
    private int? ActiveSessionId => _sessionId;

    /// <summary>
    /// Restores previously saved messages into the Items collection and AI history.
    /// </summary>
    public async Task RestoreFromStoreAsync()
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;

        if (store is null || folder is null || !sessionId.HasValue) return;

        try
        {
            var messages = await store.GetMessagesAsync(folder, sessionId.Value);
            var restoreData = new List<ChatMessageData>();
            var msgIndex = 0;
            foreach (var msg in messages)
            {
                var type = ParseEnum<ChatItemType>(msg.ItemType);
                Items.Add(new ChatItemViewModel
                {
                    Type = type,
                    Content = msg.Content,
                    ToolName = msg.ToolName,
                    ToolArgs = msg.ToolArgs,
                    Title = msg.Title ?? "",
                    Body = msg.Body,
                    BodyMode = ParseEnum<OutputBodyMode>(msg.BodyMode ?? "Markdown"),
                    ExpanderTitle = msg.ExpanderTitle ?? "",
                    Status = ParseEnum<OutputItemStatus>(msg.StatusText),
                    IsStreaming = false
                });
                restoreData.Add(new ChatMessageData
                {
                    Id = $"restore-{msgIndex++}",
                    Type = type.ToString(),
                    Content = msg.Content,
                    ToolName = msg.ToolName,
                    ToolArgs = msg.ToolArgs,
                    Title = msg.Title ?? "",
                    Body = msg.Body,
                    BodyMode = msg.BodyMode ?? "Markdown",
                    ExpanderTitle = msg.ExpanderTitle ?? "",
                    Status = msg.StatusText,
                    IsStreaming = false
                });
            }
            if (restoreData.Count > 0)
                MessagesRestored?.Invoke(restoreData);

            var historyJson = await store.GetConversationHistoryAsync(folder, sessionId.Value);
            if (historyJson is not null && _chatService is not null)
            {
                _chatService.RestoreHistory(historyJson);
            }
        }
        catch
        {
            // Best effort — session works even if restore fails
        }
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    /// <summary>
    /// Handles the slash commands that the CLI only implements in its
    /// interactive REPL.
    ///
    /// The chat runs the CLI with <c>-p --input-format stream-json</c>, where
    /// there is no REPL to parse them: <c>/usage</c>, <c>/cost</c> and friends
    /// are passed through as ordinary text and the model just sees a message
    /// starting with a slash. Custom project commands
    /// (<c>.claude/commands/*.md</c>) <em>are</em> expanded by the CLI, so they
    /// must still be forwarded — only this fixed set is intercepted.
    /// </summary>
    /// <returns>True when the command was handled locally and must not be sent.</returns>
    private bool TryHandleLocalCommand(string message)
    {
        if (!message.StartsWith("/", StringComparison.Ordinal)) return false;

        var name = message.TrimStart('/').Split(' ')[0].ToLowerInvariant();

        switch (name)
        {
            case "clear":
                ClearCommand.Execute(null);
                return true;

            case "cost":
            case "usage":
            {
                var cost = _chatService?.GetSessionCost();
                var body = cost.HasValue
                    ? $"**Session cost:** ${cost.Value:F4}"
                    : "No cost reported yet — the CLI reports it once a turn completes.";

                EmitLocalMessage(
                    $"{body}\n\n_Reported by the CLI for this session only. `/usage` in the terminal shows " +
                    "subscription-wide limits, which the print-mode transport this window uses does not expose._");
                return true;
            }

            case "help":
                EmitLocalMessage(
                    "**Handled here:** `/clear`, `/cost`, `/usage`, `/help`\n\n" +
                    "Project and personal commands from `.claude/commands` are passed to the CLI and work " +
                    "normally — the ⚡ menu lists the ones available in this workspace.\n\n" +
                    "Other built-in commands (`/login`, `/model`, `/doctor`, …) belong to the CLI's " +
                    "interactive terminal and have no effect here; run them in a shell with `claude`.");
                return true;

            default:
                // A custom command, or one this build doesn't know — let the CLI decide.
                return false;
        }
    }

    /// <summary>
    /// Renders an assistant-styled message produced by the extension itself,
    /// without involving the CLI. Not persisted: it is a UI response to a UI
    /// command, and replaying it on restore would misrepresent the transcript.
    /// </summary>
    private void EmitLocalMessage(string markdown)
    {
        var id = $"local-{++_userMsgCounter}";
        Items.Add(new ChatItemViewModel
        {
            Type = ChatItemType.Assistant,
            Content = markdown,
            IsStreaming = false
        });
        MessageAdded?.Invoke(id, ChatItemType.Assistant, new ChatMessageData
        {
            Id = id,
            Type = "Assistant",
            Content = markdown
        });
        RequestScroll();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var message = InputText.Trim();
        InputText = "";

        // Intercept before echoing the user turn: these commands address the
        // window, not the conversation, so they shouldn't appear in the
        // transcript or reach the model.
        if (TryHandleLocalCommand(message)) return;

        var userMsgId = $"user-{++_userMsgCounter}";
        Items.Add(new ChatItemViewModel
        {
            Type = ChatItemType.User,
            Content = message,
            Title = "You"
        });
        MessageAdded?.Invoke(userMsgId, ChatItemType.User, new ChatMessageData
        {
            Id = userMsgId,
            Type = "User",
            Content = message,
            Title = "You"
        });
        RequestScroll();

        PersistMessageFireAndForget(new PersistedMessage
        {
            ItemType = ChatItemType.User.ToString(),
            Content = message,
            Title = "You",
            CreatedUtc = DateTime.UtcNow
        });

        if (_chatService is null)
        {
            var errId = $"user-err-{_userMsgCounter}";
            var errContent = "_AI service not connected yet. This will be wired up in a future update._";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = errContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(errId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = errId,
                Type = "Assistant",
                Content = errContent
            });
            RequestScroll();
            return;
        }

        // Generate a title from the first user message (fire-and-forget, non-blocking)
        var isFirstMessage = Items.Count(i => i.Type == ChatItemType.User) == 1;
        if (isFirstMessage)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await _chatService.GenerateTitleAsync(message);
                    Dispatch(() =>
                    {
                        SessionTitle = title;
                        PersistTitleUpdateFireAndForget(title);
                    });
                }
                catch { /* best effort */ }
            });
        }

        IsBusy = true;
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        try
        {
            await foreach (var _ in _chatService.SendMessageAsync(message, token))
            {
                // Output is handled by listener callbacks
            }

            // Persist conversation history after each completed exchange
            PersistConversationHistoryFireAndForget();

            // Refresh cost and last activity in the session list
            if (_chatService is not null && SessionInfo is not null)
            {
                SessionInfo.SessionCost = _chatService.GetSessionCost();
                SessionInfo.LastActivity = DateTime.Now;
            }
        }
        catch (OperationCanceledException)
        {
            var cancelId = $"cancel-{++_userMsgCounter}";
            var cancelContent = "_Processing stopped._";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = cancelContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(cancelId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = cancelId,
                Type = "Assistant",
                Content = cancelContent
            });
        }
        catch (Exception ex)
        {
            var catchErrId = $"err-{++_userMsgCounter}";
            var catchErrContent = $"**Error:** {ex.Message}";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = catchErrContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(catchErrId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = catchErrId,
                Type = "Assistant",
                Content = catchErrContent
            });
        }
        finally
        {
            _sendCts?.Dispose();
            _sendCts = null;
            IsBusy = false;
        }
        RequestScroll();
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        try { _sendCts?.Cancel(); }
        catch { /* best effort — token may already be disposed */ }

        // The dispatcher loop blocks on the broker's TCS while a permission /
        // question banner is open. SendAsync's cancellation token doesn't reach
        // those TCSs (they're created with CancellationToken.None), so without
        // explicitly resolving them here a stuck banner leaves the chat hung
        // even after the user clicks Stop.
        try { _questionBroker?.CancelAllPending(); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Stop: questionBroker.CancelAllPending failed"); }
        try { _permissionBroker?.CancelAllPending(); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Stop: permissionBroker.CancelAllPending failed"); }
    }

    [RelayCommand]
    private void Clear()
    {
        _chatService?.ClearHistory();
        Items.Clear();
        _activeItems.Clear();
        AllCleared?.Invoke();
    }

    private void OnStepStarted(OutputItem item)
    {
        Dispatch(() =>
        {
            var isAi = item.ToolName == "AI";
            var isThinking = item.ToolName == "Thinking";
            var isAgent = item.ToolName == "Agent";

            var type = isAi ? ChatItemType.Assistant
                     : isThinking ? ChatItemType.Thinking
                     : ChatItemType.ToolStep;

            var streaming = isAi || isAgent || isThinking;
            var expanderTitle = isThinking ? "Thinking..." : item.Title;
            var vm = new ChatItemViewModel
            {
                Type = type,
                ToolName = item.ToolName,
                ToolArgs = item.ToolArgs,
                Title = item.Title,
                Status = item.Status,
                IsStreaming = streaming,
                ExpanderTitle = expanderTitle
            };
            _activeItems[item.Id] = vm;
            Items.Add(vm);
            MessageAdded?.Invoke(item.Id, type, new ChatMessageData
            {
                Id = item.Id,
                Type = type.ToString(),
                Content = "",
                ToolName = item.ToolName,
                ToolArgs = item.ToolArgs,
                Title = item.Title,
                Status = item.Status.ToString(),
                ExpanderTitle = expanderTitle,
                IsStreaming = streaming
            });
            RequestScroll();
        });
    }

    private void OnStepUpdated(OutputItem item)
    {
        if (string.IsNullOrEmpty(item.Delta))
            return;

        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
                vm.Content += item.Delta;

                if (item.ToolName == "Thinking")
                {
                    vm.ExpanderTitle = item.Title;
                    MessageStatusUpdated?.Invoke(item.Id, vm.Status, item.Title);
                }

                MessageContentUpdated?.Invoke(item.Id, vm.Content);

                var index = Items.IndexOf(vm);
                if (index >= 0 && index < Items.Count - 1)
                {
                    Items.Move(index, Items.Count - 1);
                }
            }
        });
    }

    private void OnStepCompleted(OutputItem item)
    {
        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
                vm.Status = item.Status;
                vm.IsStreaming = false;

                MessageStatusUpdated?.Invoke(item.Id, item.Status,
                    item.ToolName == "Thinking" ? item.Title : vm.ExpanderTitle);

                if (item.ToolName == "Thinking")
                {
                    vm.ExpanderTitle = item.Title;
                }
                else if (!string.IsNullOrEmpty(item.Body) && item.ToolName != "AI")
                {
                    vm.Body = item.Body;
                    vm.BodyMode = item.BodyMode;
                    MessageBodySet?.Invoke(item.Id, item.Body!, item.BodyMode);
                }

                MessageCompleted?.Invoke(item.Id);

                _activeItems.TryRemove(item.Id, out _);
                RequestScroll();

                // Persist completed step
                PersistMessageFireAndForget(new PersistedMessage
                {
                    ItemType = vm.Type.ToString(),
                    Content = vm.Content,
                    ToolName = vm.ToolName,
                    ToolArgs = vm.ToolArgs,
                    Title = vm.Title,
                    Body = vm.Body,
                    BodyMode = vm.BodyMode.ToString(),
                    ExpanderTitle = vm.ExpanderTitle,
                    StatusText = vm.Status.ToString(),
                    CreatedUtc = DateTime.UtcNow
                });
            }
        });
    }

    // --- Persistence helpers (fire-and-forget) ---

    private void PersistMessageFireAndForget(PersistedMessage message)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        _ = Task.Run(async () =>
        {
            try { await store.AppendMessageAsync(folder, sessionId.Value, message); }
            catch { /* best effort */ }
        });
    }

    private void PersistConversationHistoryFireAndForget()
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue || _chatService is null) return;

        var historyJson = _chatService.SerializeHistory();
        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveConversationHistoryAsync(folder, sessionId.Value, historyJson);

                var index = await store.GetSessionIndexAsync(folder);
                var entry = index.FirstOrDefault(e => e.Id == sessionId.Value);
                if (entry is not null)
                {
                    entry.LastActivityUtc = DateTime.UtcNow;
                    await store.UpdateSessionAsync(folder, entry);
                }
            }
            catch { /* best effort */ }
        });
    }

    private void PersistTitleUpdateFireAndForget(string title)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var index = await store.GetSessionIndexAsync(folder);
                var entry = index.FirstOrDefault(e => e.Id == sessionId.Value);
                if (entry is not null)
                {
                    entry.Title = title;
                    entry.LastActivityUtc = DateTime.UtcNow;
                    await store.UpdateSessionAsync(folder, entry);
                }
            }
            catch { /* best effort */ }
        });
    }

    private void RequestScroll() => ScrollRequested?.Invoke();

    private static T ParseEnum<T>(string value) where T : struct
        => Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : default;

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Sets a disposable scope (typically the DI <c>ServiceProvider</c>) that
    /// will be disposed when this view model is disposed, cascading disposal to
    /// the <c>ClaudeCliChatService</c> → <c>ClaudeCliProcessHost</c> (kills the
    /// child process and tears down the permission pipe).
    /// </summary>
    public void SetServiceScope(IDisposable scope) => _serviceScope = scope;

    public void Dispose()
    {
        try { _activityTimer?.Stop(); } catch { }
        try { (_chatService as IDisposable)?.Dispose(); } catch { }
        try { _serviceScope?.Dispose(); } catch { }
    }
}

public enum SessionActivity
{
    Idle,
    Busy,
    AwaitingUser
}
