# ForgePilot — Project Details

Architecture, invariants and the reasoning behind the non-obvious decisions.
[README.md](README.md) covers installation and usage; this is the document to
read before changing anything.

---

## 1. What this is

A Visual Studio 2026 extension that drives the **Claude Code CLI** from a tool
window. The CLI does the agentic work — planning, reading, editing, running
commands. ForgePilot is a host: it launches the CLI, speaks its streaming
protocol, brokers tool permissions, renders the transcript, and persists
sessions.

Forked from [VsAgentic](https://github.com/adospace/vs-agentic) (MIT). The CLI
transport, session store and permission pipe are upstream's design; the UI, the
session controls and the completion path are this fork's.

---

## 2. Solution layout

| Project | Target | Role |
|---|---|---|
| `ForgePilot.VSExtension` | net472 | VSIX entry point: package, tool windows, options, MEF completion provider |
| `ForgePilot.UI` | net472 | Shared WPF — view models, theme dictionaries, WebView2 transcript, banner views |
| `ForgePilot.Services` | net472 | CLI process host, chat service, session store, completions |
| `ForgePilot.Services.McpPermissionServer` | net472 | Stdio MCP server the CLI spawns to ask permission |
| `ForgePilot.Desktop` | net472 | Standalone WPF host — the fast loop for UI work |
| `ForgePilot.Console` | net472 | Console host; also the completion latency harness |

`ForgePilot.Desktop` and the extension share **everything visual**. UI changes
should be checked there first — it starts in seconds, where a VS experimental
instance does not.

`net472` throughout, because that is what the VSSDK requires. This has teeth:
**`System.Index` and `System.Range` do not exist**, so `x[^1]` and `x[1..]` fail
to compile. Use `Count - 1` and `Skip`/`Take`.

---

## 3. How a turn works

```
ChatSessionViewModel.SendAsync
  └─ IChatService.SendMessageAsync
       ├─ ClaudeCliProcessHost.EnsureStartedAsync   (launch or reuse the CLI)
       ├─ EnsureDispatcherStarted                   (one reader per event channel)
       ├─ write one user message line to stdin
       └─ consume events until `result`
            ├─ system/init      → session id, model in effect, tool list
            ├─ assistant/text   → transcript prose
            ├─ assistant/tool_use → tool card; permission via the MCP pipe
            └─ result           → usage, cost, end of turn
```

The CLI runs in **print mode** with bidirectional stream-json:

```
claude -p --input-format stream-json --output-format stream-json --verbose
       --permission-mode <mode> [--model <id>]
       --permission-prompt-tool mcp__ForgePilot__approval_prompt
       --strict-mcp-config --mcp-config <temp.json>
       [--resume <session-id>]
       --append-system-prompt "<AskUserQuestion nudge>"
```

### Invariants

- **One turn at a time.** Serialised by the view model's `IsBusy` gate, so a
  single `_activeTurn` reference is sufficient.
- **The dispatcher binds to a channel, not to liveness.** See §4.
- **Settings changes stop the process; they do not restart it.** The next
  `SendMessageAsync` relaunches with the new arguments, and because
  `_cliSessionId` survives, that relaunch passes `--resume` and keeps the whole
  conversation. Restarting eagerly would spawn a process that then sits idle.

---

## 4. Traps that have already bitten

Each of these was a real defect. They are recorded because the code looks
correct without the explanation.

### The dispatcher must track the channel, not just "is it running"

Every CLI restart builds a **new** event channel. The old dispatcher loop is
still awaiting the old one — its writer is only completed from the process
`Exited` callback, which fires asynchronously *after* `Stop()` returns. A
liveness-only check (`_dispatcherTask is { IsCompleted: false }`) therefore sees
the stale loop as healthy, declines to start one for the new process, and
nothing ever reads the new channel. **Symptom: change the model, then send a
message, and it hangs forever.** `EnsureDispatcherStarted` compares
`ReferenceEquals(_dispatcherReader, _host.EventReader)`.

### Cache reads are a size, not a total

`cache_read_input_tokens` is the whole conversation prefix, re-read on **every**
turn. Summing it counts the same tokens once per exchange — a six-message chat
reported 168k. Input, output and cache writes accumulate; cache reads do not.
The composer shows *context size* (`input + cacheRead + cacheWrite + output` for
the last turn), which is what `/context` reports independently.

### The result event's cost field is `total_cost_usd`

Not `cost_usd`, which does not exist — the old lookup silently never matched and
cost stayed at zero all session. It is also **already cumulative**, so it must be
assigned, never accumulated.

### Never render the result event's `result` field

It holds the turn's closing text, which after a successful `/compact` is the
**previous** turn's reply. Rendering it resurrects the last answer under the new
command. Built-in slash commands emit ordinary `assistant` text blocks, so the
normal path already covers them; there is nothing this field adds.

### A model that names itself is not evidence

Models routinely misidentify themselves. The only reliable source is the
`model` field on the CLI's `system/init` event, surfaced as
`IChatService.ActiveModel` + the `ModelReported` event. When no model is pinned,
`--model` is omitted entirely and the CLI uses the **account default**, which
cannot be inferred from settings — so the chip shows `Default` until init says
otherwise, rather than guessing.

### Chip changes must be written back to the options page

New sessions are built from `ForgePilotOptionsPage`. A chip that only mutates the
in-memory options object works for the running session and silently reverts the
moment another session opens. `ForgePilotPackage.PersistSessionSettings` closes
this.

### A restored tool window has no view model

Making the window non-`Transient` (so the dock position persists) is exactly what
makes VS restore it on startup — and restoration constructs the pane directly,
bypassing `OpenOrActivateSessionAsync`. **Symptom: black transcript, dead chips,
disabled send button.** `OnToolWindowCreated` calls
`ForgePilotPackage.EnsureSessionLoaded()`.

### …but that hydration must not run on the creation stack

VS holds each pane in a `Lazy<T>`. Calling `ShowToolWindowAsync` from inside
`OnToolWindowCreated` re-enters that lazy initializer:
*"The value factory has called for the value on the same instance."*
`EnsureSessionLoaded` first awaits
`Dispatcher.Yield(DispatcherPriority.Background)`.

### The control outlives the session

`ChatSessionControl.Initialize` is called once per session, on the **same**
control and the **same** WebView. It must detach the previous view model (named
handlers — a lambda cannot be unsubscribed) and clear the transcript, or a new
session renders on top of the old conversation. The static
`VSColorTheme.ThemeChanged` subscription must be made once per control, not once
per session.

### Deleting a session: the package decides, not the list

`SessionListViewModel.SelectedSession` tracks list selection;
`ForgePilotPackage._activeSessionId` tracks what is on screen. They diverge.
Deciding "was this the open session?" from the former closed the whole panel.
The view model computes a replacement and raises
`SessionRemoved(removed, replacement)`; the package compares against
`_activeSessionId` and swaps the window in place.

### Enter with the completion popup open

Enter commits the **highlighted** row, and committing a command now runs it. The
list filters asynchronously, so the highlight can lag what has been typed —
typing `/compact` could execute `/usage`. `CommitMentionSelection` accepts the
highlight only when it actually completes the typed text; otherwise Enter sends
the text verbatim. Tab stays completion-only.

### `TextBox` styling and `DynamicResource`

Assigning `Foreground` in code outranks the XAML `DynamicResource` and freezes
the control on one palette across a theme switch. Use `SetResourceReference` to
set, `ClearValue` to restore.

### PowerShell 5.1

`sc` is an alias for `Set-Content` — use `sc.exe`. Redirecting a native
executable's stderr with `2>&1` wraps each line in an `ErrorRecord` and can turn
a successful run into a failure under `$ErrorActionPreference = 'Stop'`.

---

## 5. Permissions

The CLI is launched with `--permission-prompt-tool mcp__ForgePilot__approval_prompt`
and a generated MCP config pointing at `ForgePilot.Services.McpPermissionServer`,
extracted to `%LocalAppData%\ForgePilot\helpers\<hash>\`. The CLI spawns it as a
stdio MCP server; it connects back over a named pipe with a handshake secret and
relays each request to `IPermissionBroker`, which raises the in-chat banner.

`--strict-mcp-config` keeps user-level MCP configuration out of the session, so
the permission tool is the only server in play.

`CliPermissionMode` maps to `--permission-mode`:

| Enum | Flag | Behaviour |
|---|---|---|
| `Default` | `manual` | Prompt for every gated tool call |
| `AcceptEdits` | `acceptEdits` | File edits auto-accept; everything else prompts |
| `Plan` | `plan` | Propose an approach; touch nothing |
| `Auto` | `auto` | CLI decides per call |
| `BypassPermissions` | `bypassPermissions` | Never prompt |

`default` is still accepted as a legacy alias for `manual`, but `manual` is what
is sent — naming the mode meant survives the alias being dropped.

---

## 6. Rendering

The transcript is **not WPF**. It is a WebView2 rendering
`ForgePilot.UI/Assets/chat-template.html` (inline CSS + `showdown`), driven from
C# through `ExecuteScriptAsync`. That single file carries most of the look.

Structured renderers run before markdown and return `null` to fall through:

| Function | Trigger |
|---|---|
| `tryRenderTodos` | every line is a `- [ ]` / `- [x]` / `- [~]` item |
| `tryRenderDiff` | unified-diff markers |
| `tryRenderUsage` | **two or more** `<label>: N% used` lines |

`tryRenderUsage` matches the `N% used` shape rather than the surrounding
sentence, which is wording Anthropic can change. Requiring two or more lines is
what stops an ordinary answer mentioning a percentage from being hijacked.

Theming: `ClaudeThemeManager` swaps `ClaudeTheme.Dark.xaml` / `.Light.xaml`
(brush keys prefixed `Fp*`) on `VSColorTheme.ThemeChanged`, and pushes the same
palette into the WebView as CSS custom properties. The panel keeps Claude's
colours rather than adopting Visual Studio's; only the light/dark *variant*
follows the IDE.

---

## 7. Completions

`ICompletionProvider` → `ClaudeCliCompletionProvider` → `CachingCompletionService`
→ `ForgePilotProposalSource` (MEF, `ProposalSourceProviderBase`).

Authenticated by the CLI's subscription login. There is deliberately **no API key
path** — it was removed.

Measured on this machine:

| Path | Latency |
|---|---|
| `claude --version` (node boot alone) | ~1.0s |
| One-shot `claude -p`, trivial prompt | 7.6–9.8s |
| One-shot `claude -p`, real FIM prompt | 9.5–11.1s |
| Long-lived process, warm | 2.7–11.1s (median ~6.6s) |

Prompt size barely matters, so the cost is per-invocation CLI setup rather than
generation. The provider therefore keeps one process alive (`--model haiku
--tools "" --safe-mode`, recycled every 20 turns) and handles **only**
`ProposalScenario.ExplicitInvocation`. `TypeChar` is not handled: a suggestion
that lands after you have typed past it is worse than none.

`--bare` looks like the right flag and is not — it reads auth strictly from
`ANTHROPIC_API_KEY` and never the OAuth login. `--safe-mode` drops CLAUDE.md,
MCP, hooks and plugins while explicitly leaving auth intact.

Measure with:

```
ForgePilot.Console --complete <file> <line> <col> [runs]
```

It calls the provider **directly**, not the cache — a cache hit reports ~0ms and
tells you nothing.

---

## 8. Testing without Visual Studio

Most of the stack can be exercised without an experimental instance, and should
be.

| Question | How |
|---|---|
| Does the CLI behave as assumed? | Drive `claude` directly with the same flags |
| Does the service layer work? | `ForgePilot.Console` — real `ClaudeCliChatService` |
| Does the UI render? | `ForgePilot.Desktop` |
| What is completion latency? | `ForgePilot.Console --complete` |

**`ForgePilot.Console` uses `ConsoleOutputListener` and never touches
`ChatSessionViewModel`.** It validates the service layer only. A feature working
there does not mean it works in the extension — that mistake has been made
already.

Logs, which are usually faster than reasoning about the code:

```
%AppData%\ForgePilot\logs\ForgePilot-<date>_NNN.log   service + view model
%AppData%\ForgePilot\logs\window-<date>.log           window/session lifecycle
```

`window-*.log` is a plain file append rather than Serilog on purpose: `Log.Logger`
is only configured inside `CreateChatViewModel`, so nothing on the window-open
path before that point would otherwise be recorded — and that is exactly the
stretch that fails silently.

---

## 9. Storage

| Path | Contents |
|---|---|
| `%AppData%\ForgePilot\workspaces\` | Session index and transcripts, per workspace |
| `%AppData%\ForgePilot\logs\` | Serilog + window logs |
| `%AppData%\ForgePilot\.shown` | First-run marker for the auto-show |
| `%LocalAppData%\ForgePilot\helpers\` | Extracted MCP permission helper |
| `%LocalAppData%\ForgePilot\WebView2\` | WebView2 user data |

Separate from upstream's `%AppData%\VsAgentic\`, so both extensions can be
installed at once without sharing state.

---

## 10. Identity

Regenerated so this fork installs alongside upstream rather than replacing it —
a reused GUID means VS treats them as the same extension and **neither loads**:

| | |
|---|---|
| VSIX Identity | `ForgePilot.VSExtension.8f4b1c27-6d3a-4e59-9a10-b7c2e5d84f13` |
| Package | `d7a41f38-2c96-4e5b-8b31-9f60c2ae4d17` |
| Chat tool window | `5e9a2f14-7c63-4bd8-a1e7-0c9d4f6b83a2` |
| Options page | `b4e829c1-53fa-4d7e-9c02-6a8bd15f3e40` |

The chat window's GUID was itself regenerated once: VS persists dock placement
against the GUID, and the inherited one already had "MDI document tab" saved
against it, which beat the placement hints in `ProvideToolWindow`.

The update checker is disabled (early return, code retained). Upstream's
`MarketplaceItemName` would otherwise offer to "update" this fork into the
original extension.

---

## 11. Slash commands

`TryHandleLocalCommand` intercepts exactly one command — `/clear` — because it is
the only one that must touch the window as well as the conversation. Everything
else is forwarded to the CLI, which answers its built-ins in print mode.

The `/` picker advertises `/clear`, `/usage` and `/compact`. That list is
discoverability, not capability: any other command still works when typed,
including `/context`, `/model` and anything in `.claude/commands`.

`/compact` is narrated (`NarratedCommands`): it can run for a while and prints
nothing on success, so the status line reads "Compacting…" and a confirmation is
emitted afterwards — but only if the turn rendered nothing itself, so a real
reply is never duplicated.

---

## 12. Open items

- Not published to the Marketplace; needs a publisher ID.
- Inline completions have no invocation keybinding, so they may be unreachable in
  practice — see README.
- Repo/branch bar above the composer (branch, diff stat, Create PR) is designed
  but not built.
