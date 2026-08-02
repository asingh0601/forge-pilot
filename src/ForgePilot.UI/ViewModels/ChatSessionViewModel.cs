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

    // Claude's four-pointed star, cycled through decreasing weights. This drives
    // the composer status line only — never a window caption. An animated title
    // flickers in the dock on every frame and makes the window harder to find,
    // so the tab stays fixed at "Forge Pilot".
    private static readonly string SpinnerFrames = "✻✽✢·✢✽";
    private int _pendingUserPrompts;
    private int _spinnerFrame;
    private DateTime? _busySince;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    /// <summary>Model chip text — "Default" when no explicit model is pinned.</summary>
    [ObservableProperty]
    private string _modelLabel = "Default";

    /// <summary>Effort chip text, derived from the thinking-token budget.</summary>
    [ObservableProperty]
    private string _effortLabel = "Auto";

    /// <summary>Mode chip text: "Plan", "Act", "Auto-edit" or "Bypass".</summary>
    [ObservableProperty]
    private string _modeLabel = "Act";

    /// <summary>True while the session is in plan mode, for the toggle's visual state.</summary>
    [ObservableProperty]
    private bool _isPlanMode;

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

    /// <summary>
    /// Session totals — "4.3k tokens · $0.0123" — shown on their own line below
    /// the status. Empty until the first turn completes.
    /// </summary>
    [ObservableProperty]
    private string _usageLine = "";

    /// <summary>
    /// Verb for the status line while a named command runs ("Compacting"), or
    /// null for an ordinary turn. Cleared when the turn ends.
    /// </summary>
    private string? _runningCommandVerb;

    /// <summary>
    /// Commands worth narrating: they take time and print little or nothing, so
    /// without this the panel looks idle while they work and unchanged when they
    /// finish. Value is (status verb, confirmation shown on success).
    /// </summary>
    private static readonly Dictionary<string, (string Verb, string Done)> NarratedCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/compact"] = ("Compacting", "Conversation compacted — earlier turns are now a summary."),
        };

    public SessionActivity Activity =>
        _pendingUserPrompts > 0 ? SessionActivity.AwaitingUser :
        IsBusy ? SessionActivity.Busy :
        SessionActivity.Idle;

    partial void OnIsBusyChanged(bool value) => UpdateActivityIndicator();

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

                // Name the operation when it is a command rather than a
                // conversation turn: /compact can run for a while and prints
                // nothing when it lands, so "Working…" leaves no way to tell a
                // slow compaction from a stalled one.
                var verb = _runningCommandVerb ?? "Working";
                StatusLine = $"{verb}… ({FormatElapsed(elapsed)} · esc to interrupt)";
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

        UpdateUsageLine();
    }

    /// <summary>
    /// Session totals, kept on their own line rather than inside the working
    /// message.
    ///
    /// The two change on completely different rhythms: the elapsed timer ticks
    /// eight times a second, while usage only moves when a turn completes.
    /// Sharing a line made the token count jitter sideways on every frame as
    /// the elapsed text grew and shrank, and it vanished entirely the moment
    /// the turn finished — which is exactly when it is worth reading.
    /// </summary>
    private void UpdateUsageLine()
    {
        // Tokens and cost only arrive on the CLI's result event, so during the
        // first turn there is nothing to show — stay empty rather than display
        // a misleading zero.
        // Tokens only. Cost belongs in /usage, where there is room to say what
        // it covers; on one line beside the composer it reads as a running
        // charge, which it is not on a subscription.
        var tokens = _chatService?.GetSessionTokens();

        // "context", not "tokens": this is the size of the conversation as of the
        // last turn, not a running total of everything ever sent. Labelling a
        // size as a total is what made 168k look plausible.
        UsageLine = tokens.HasValue ? $"{FormatTokens(tokens.Value)} context" : "";
    }

    // ── Session settings (model / effort / mode) ───────────────────────────

    /// <summary>Effort presets, in the order the picker shows them.</summary>
    public static readonly (string Label, int Tokens)[] EffortLevels =
    {
        ("Auto", 0),
        ("Low", 4_000),
        ("Medium", 10_000),
        ("High", 32_000),
        ("Max", 64_000),
    };

    /// <summary>
    /// Selectable models, labelled with their version so the chip always names
    /// exactly what the session is running.
    ///
    /// Full ids rather than the <c>opus</c>/<c>sonnet</c> aliases: an alias
    /// resolves to whatever currently sits behind it, which would make the
    /// version in the label a guess. Pinning costs an edit here when a new
    /// model ships, and buys a label that is always true.
    /// </summary>
    public static readonly (string Label, string Value)[] Models =
    {
        // Empty value omits --model, leaving the CLI on the account default.
        // Offered so a session can be handed back to that default once a model
        // has been pinned — otherwise the choice is one-way.
        ("Default", ""),
        ("Opus 5", "claude-opus-5"),
        ("Sonnet 5", "claude-sonnet-5"),
        ("Haiku 4.5", "claude-haiku-4-5-20251001"),
    };

    /// <summary>
    /// Used when a session has no model pinned. Sonnet is the balance point for
    /// a coding assistant — Opus costs materially more per turn, and switching
    /// up is one click away.
    /// </summary>
    public const string DefaultModel = "claude-sonnet-5";

    /// <summary>
    /// Pushes settings into the chat service and refreshes the chips.
    /// Returns the error to surface, or null on success — the caller decides
    /// how to show it, since the view models have no dialog of their own.
    /// </summary>
    public string? ApplySessionSettings(SessionSettings settings)
    {
        if (_chatService is null) return null;

        try
        {
            _chatService.ApplySettings(settings);
        }
        catch (InvalidOperationException ex)
        {
            // Raised when a turn is in flight; the CLI process must not be
            // killed out from under a response in progress.
            return ex.Message;
        }

        RefreshSettingLabels();
        return null;
    }

    /// <summary>The live CLI settings, or null when no chat service is attached.</summary>
    public SessionSettings? GetSessionSettings() => _chatService?.GetSettings();

    /// <summary>Reads the live settings back into the chip labels.</summary>
    public void RefreshSettingLabels()
    {
        var s = _chatService?.GetSettings();
        if (s is null) return;

        // When nothing is pinned the --model flag is omitted and the CLI runs the
        // account default, which may be any model. Reporting our own preferred
        // default here was a lie the user could see through: the chip said
        // "Sonnet 5" while the CLI answered as Haiku.
        //
        // Prefer the pinned model; otherwise use whatever the CLI reported on
        // its init event; and until that arrives say "Default" rather than name
        // a model we have not verified is running.
        var effectiveModel = string.IsNullOrWhiteSpace(s.Model)
            ? _chatService?.ActiveModel
            : s.Model;

        ModelLabel = string.IsNullOrWhiteSpace(effectiveModel)
            ? "Default"
            : Models.FirstOrDefault(m =>
                  string.Equals(m.Value, effectiveModel, StringComparison.OrdinalIgnoreCase)).Label
              // A model set by hand in Options, or one newer than this build
              // knows about, won't match a preset; show it verbatim.
              ?? effectiveModel!;

        EffortLabel = EffortLevels.FirstOrDefault(e => e.Tokens == s.MaxThinkingTokens).Label
            ?? $"{s.MaxThinkingTokens / 1000}k";

        IsPlanMode = s.PermissionMode == CliPermissionMode.Plan;
        ModeLabel = LabelFor(s.PermissionMode);
    }

    /// <summary>
    /// The permission modes offered in the mode menu, in the CLI's own order and
    /// wording so the two read the same.
    ///
    /// <see cref="CliPermissionMode.BypassPermissions"/> is last and deliberately
    /// unabbreviated: it hands the agent unprompted shell access, and a menu row
    /// reading "Bypass" understates that.
    /// </summary>
    public static readonly (string Label, CliPermissionMode Mode)[] PermissionModes =
    {
        ("Manual", CliPermissionMode.Default),
        ("Accept edits", CliPermissionMode.AcceptEdits),
        ("Plan", CliPermissionMode.Plan),
        ("Auto", CliPermissionMode.Auto),
        ("Bypass permissions", CliPermissionMode.BypassPermissions),
    };

    private static string LabelFor(CliPermissionMode mode) =>
        PermissionModes.FirstOrDefault(m => m.Mode == mode).Label ?? "Manual";

    /// <summary>Switches permission mode, leaving the other settings alone.</summary>
    public string? SetPermissionMode(CliPermissionMode mode)
    {
        var s = _chatService?.GetSettings();
        if (s is null) return null;

        return ApplySessionSettings(s with { PermissionMode = mode });
    }

    /// <summary>"45s" under a minute, "1m 29s" beyond it.</summary>
    private static string FormatElapsed(int seconds) =>
        seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";

    /// <summary>"2.5k" rather than "2513" — the digits past the first two are noise.</summary>
    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString()
    };

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
            UpdateStatusLine();
        };
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

        // The chip can only guess at the model until the CLI says which one it
        // resolved; correct it as soon as it does.
        chatService.ModelReported += _ => Dispatch(RefreshSettingLabels);
    }

    private void OnChatServiceLoginRequired(string? errorMessage)
    {
        Dispatch(() =>
        {
            ActiveBanner = new LoginBannerViewModel(errorMessage, () =>
            {
                ActiveBanner = null;

                // The CLI opens the browser for OAuth and writes the credentials
                // to its own store, then the current process is stopped — so the
                // next message starts a fresh one that picks them up. Nothing
                // needs to be handed back here.
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
    /// Intercepts the one slash command that addresses the window rather than
    /// the conversation.
    ///
    /// Everything else — the CLI's built-ins and any project command from
    /// <c>.claude/commands</c> — is forwarded to Claude Code, which answers them
    /// in print mode.
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

            // /clear is the only command handled here, because it is the only
            // one that has to touch the window: it empties the transcript as
            // well as the CLI's history, and the CLI's own version cannot reach
            // the UI.
            //
            // Everything else is forwarded. The CLI answers its built-ins in
            // print mode, and it answers them better than this extension can —
            // /usage reports real subscription limits, which are not derivable
            // from the event stream at all. Intercepting them here only shadowed
            // the real answers with worse local guesses.

            default:
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

        // Narrate slow, quiet commands. Set before IsBusy so the very first
        // status tick already carries the right verb.
        NarratedCommands.TryGetValue(message.Trim(), out var narration);
        _runningCommandVerb = narration.Verb;

        IsBusy = true;
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;

        // Anything the turn renders itself is the real answer; the confirmation
        // below is only for the case where nothing was rendered at all.
        var itemsBefore = Items.Count;

        try
        {
            await foreach (var _ in _chatService.SendMessageAsync(message, token))
            {
                // Output is handled by listener callbacks
            }

            if (narration.Done is not null && Items.Count == itemsBefore)
                EmitLocalMessage($"_{narration.Done}_");

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

            // Cleared before IsBusy, so the status line's final update already
            // has it gone and a cancelled /compact cannot leave the next turn
            // claiming to be compacting.
            _runningCommandVerb = null;
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
