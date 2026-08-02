# ForgePilot

**An agentic coding assistant for Visual Studio 2026** — chat with Claude to explore, understand, and modify your codebase, in a tool window styled after the Claude Desktop app.

> ### Fork notice
>
> ForgePilot is a fork of **[VsAgentic](https://github.com/adospace/vs-agentic)** by Adolfo Marinucci ([@adospace](https://github.com/adospace)), used under the MIT License. All of the CLI integration, session persistence, permission pipe, and agentic plumbing is upstream's work.
>
> This fork replaces the user interface with the Claude Desktop look, and gives the extension its own identity so it installs side-by-side with the original.
>
> It is **not** published to the Visual Studio Marketplace and is not affiliated with Anthropic. If you want the upstream extension, install [VsAgentic from the Marketplace](https://marketplace.visualstudio.com/items?itemName=adospace.VsAgentic).

---

## ✨ What this fork changes

- **Claude Desktop transcript** — a centred reading column, user turns in right-aligned rounded bubbles, assistant replies as plain prose with no bubble or avatar gutter, and proportional type throughout instead of monospace.
- **Claude palette** — warm cream in light, warm charcoal in dark, terracotta accent. Which variant you get follows the Visual Studio theme; the panel keeps its own colours rather than adopting VS's.
- **Rounded composer** — a single input card with a circular accent send button that turns into a stop button while Claude is working, plus one status line (`Working… (12s · esc to interrupt)`) that replaces the old separate "Thinking…" strip.
- **Structured tool cards** — tool calls collapse into a card showing a status dot, the tool name and its argument (`Read  src/Foo.cs`), expanding to the result. This is the one deliberate departure from a pure chat UI: a coding assistant produces tool output that a chat app has no equivalent for.
- **Diff and todo rendering** — edits show tinted `+`/`−` rows with line-number gutters; `TodoWrite` renders as a `☐` / `◐` / `☑` checklist rather than raw markdown checkboxes.
- **Session controls in the composer** — model, thinking budget and permission mode are chips under the input rather than settings buried in a dialog. Each change restarts the CLI child process (all three are launch-time properties of it) but resumes the same conversation, so nothing is lost.
- **Permission mode menu** — Manual, Accept edits, Plan, Auto, Bypass permissions, matching the CLI's own set. Bypass is spelled out and has no digit shortcut on purpose: it grants unprompted shell access.
- **Slash commands** — a `/` picker that lists the workspace's own commands alongside the built-ins, with the typed command styled so it reads as a command rather than as the first word of a message.
- **`/usage` as meters** — the CLI's quota report renders as labelled bars that tint amber past 70% and red past 90%, with the trailing detail kept below.
- **Context size in the composer** — the conversation's token size, updated per turn.

Everything else — how sessions work, how the CLI is driven, how permissions are brokered — is unchanged from upstream.

### Slash commands

The `/` picker offers three:

| Command | Handled by | What it does |
|---|---|---|
| `/clear` | ForgePilot | Empties the transcript and the CLI's history |
| `/usage` | Claude Code | Subscription limits, rendered as meters |
| `/compact` | Claude Code | Summarises earlier turns to free up context |

`/clear` is the only one intercepted, because it is the only one that has to touch the window as well as the conversation.

Anything else you type is forwarded to Claude Code, so the CLI's other built-ins still work even though they are not listed — `/context` and `/model` both answer normally. Project and personal commands from `.claude/commands` work too, and the ⚡ menu lists the ones available in the current workspace. A few are specific to the CLI's interactive terminal and report themselves as unavailable here (`/status`, `/help`).

### Inline completions (optional, off by default)

Editor ghost text, generated through the same subscription login the chat uses — no API key, nothing billed per request. Enable it in the session menu or **Tools → Options → Forge Pilot → Inline completions**.

**This is explicit-invoke only, and that is a deliberate limit rather than an oversight.** Measured on a normal machine, a one-shot `claude -p` costs 7.5–11s end to end, and a trivial prompt costs almost as much as a real one — the overhead is per-invocation CLI setup, not generation, so prompt trimming does not touch it. Keeping a process warm brings it to roughly **2.7–11s**, still far past the window in which a suggestion is useful while typing. Copilot-style automatic ghost text is not achievable on subscription auth; the completion path therefore never fires on a keystroke.

### Claude Code assets

ForgePilot discovers the slash commands, skills, plugins and MCP connectors that the Claude Code CLI will load for the open workspace, from `.claude/commands`, `.claude/skills`, `~/.claude/plugins`, and `.mcp.json`.

This layer is read-only by design. The CLI owns these features outright — it resolves them at startup, applies its own precedence rules, and executes them. ForgePilot shows you what is available and lets you invoke it; anything that changes state (installing a plugin, adding a connector) belongs in a `claude` CLI call rather than a reimplementation of the config format that would drift the moment upstream changes it.

---

## 📋 Requirements

- **Visual Studio 2026** version **17.14 or later** (Community, Professional, or Enterprise)
- **Windows x64** (amd64)
- **[Node.js](https://nodejs.org/)** (v18 or later) — required to install the Claude Code CLI
- **An active [Claude Pro or Max subscription](https://claude.ai/pricing)** — the chat window uses the Claude Code CLI, which requires a paid Claude subscription. API keys are **not** supported for chat.

---

## 🚀 Installation

ForgePilot has no Marketplace listing — build and install it yourself.

Building the VSIX needs **Visual Studio with the "Visual Studio extension development" workload**, because the VSSDK targets that produce the package ship with it. Build with MSBuild from that install (not `dotnet build` — see below):

```
"C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" src\ForgePilot.slnx -t:Rebuild -p:Configuration=Release
```

The package lands at:

```
src\ForgePilot.VSExtension\bin\Release\net472\ForgePilot.VSExtension.vsix
```

Close Visual Studio and double-click it to install.

Open it from **View → Other Windows → Forge Pilot**. On first install it also opens itself once, docked beside Solution Explorer, so you don't have to find the menu.

> **Bump the version when reinstalling.** `Version` in `source.extension.vsixmanifest` gates whether an install replaces an existing copy. Rebuilding without changing it produces a package Visual Studio may decline to install over the old one, and you end up testing a stale build while assuming otherwise. Check **Extensions → Installed** to confirm which version is live.

> **If the extension installs but never appears.** On some Visual Studio 2026 installs the shell does not consume the pending extension configuration change on the next launch: `extensions.configurationchanged` is left in place and `ExtensionMetadataCache.mpack` is never rebuilt, so a newly installed extension contributes no menu commands and does not show under Manage Extensions. Running `devenv /setup` and `devenv /updateconfiguration` from an elevated prompt forces the rebuild. This is a host-side quirk rather than something the package controls — the VSIX registers its package, menus and options pages through an ordinary pkgdef, and upstream VsAgentic behaves identically on the same machine. Extensions installed from the Marketplace are unaffected.

> **`dotnet build` will not produce a .vsix.** It compiles every assembly and reports success, but the VSSDK import is skipped. Use `build-vsix.ps1`, which fails loudly instead. The other projects (`ForgePilot.Desktop`, `ForgePilot.Console`) build fine under `dotnet build`.

To develop against it, open `src/ForgePilot.slnx` in Visual Studio 2026 with the **Visual Studio extension development** workload, set `ForgePilot.VSExtension` as the startup project, and press **F5** — that launches the VS experimental instance.

Because ForgePilot carries its own extension identity, it installs alongside upstream VsAgentic rather than replacing it, and keeps its sessions in a separate directory. You can run both and compare.

---

## ⚙️ Setup

### 1. Install the Claude Code CLI

```
npm install -g @anthropic-ai/claude-code
```

### 2. Log in with your Claude subscription

```
claude login
```

This opens your browser to authenticate. You need an active **Pro** or **Max** subscription.

### 3. Verify the CLI works

```
claude -p "hello"
```

### 4. Configure ForgePilot (optional)

If `claude` is not on your PATH, go to **Tools → Options → ForgePilot → General** and set **Claude CLI Path** to the full path (e.g. `C:\Users\<you>\AppData\Roaming\npm\claude.cmd`).

### Open ForgePilot

**View → Other Windows → ForgePilot** opens a new chat session. A **ForgePilot Sessions** panel appears alongside Solution Explorer for managing conversations.

### Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| **"Not logged in · Please run /login"** | The CLI is not authenticated | Run `claude login` from a terminal and complete the browser login |
| **"Invalid API key"** | An `ANTHROPIC_API_KEY` environment variable is interfering | The chat window uses subscription auth, not API keys. Remove it with `[System.Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", $null, "User")` and restart Visual Studio |
| **"Failed to start Claude CLI"** | The `claude` command was not found | `npm install -g @anthropic-ai/claude-code`, or set the full path in **Tools → Options → ForgePilot** |

---

## 🗂️ Project Structure

```
src/ForgePilot.slnx
├── ForgePilot.VSExtension/   # VSIX entry point — commands, tool windows, package bootstrap
├── ForgePilot.UI/            # Shared WPF controls, ViewModels, WebView2 transcript renderer
├── ForgePilot.Services/      # Core service layer — CLI integration, session store
├── ForgePilot.Desktop/       # Standalone WPF app — the fast loop for UI work
└── ForgePilot.Console/       # Console host (for development & testing)
```

`ForgePilot.Desktop` hosts the same transcript renderer, composer and banners as the extension, so UI changes can be checked without launching a VS experimental instance:

```
src\ForgePilot.Desktop\bin\Debug\net472\ForgePilot.Desktop.exe D:\path\to\some\repo
```

Both hosts share everything visual: `ForgePilot.UI` owns the WebView2 transcript, the theme dictionaries, and the permission / question / login banner views. The VS extension adds only the tool-window plumbing, the `@`-mention file picker, and the session list.

---

## 🔒 Privacy & Security

- Your code is sent to **Anthropic** via the Claude Code CLI to fulfill requests. Review [Anthropic's privacy policy](https://www.anthropic.com/privacy) before use on sensitive or proprietary codebases.
- **No API keys are stored or required** for chat. Authentication is handled by the Claude Code CLI via your Claude subscription.
- Session history is stored **locally** in `%AppData%\ForgePilot\` and never leaves your machine. This is a separate directory from upstream's `%AppData%\VsAgentic\`, so the two extensions never share state.

---

## 🐛 Known Limitations

- **Visual Studio 2026 (17.14+)** only — VS Code and Rider are not supported.
- **x64 Windows** only.
- **Inline completions are explicit-invoke, not automatic.** A CLI round trip is 2.7–11s even with the process kept warm; see [Inline completions](#inline-completions-optional-off-by-default).
- **Subscription limits are only visible through `/usage`.** They come from an endpoint the CLI calls directly and are not carried on the print-mode event stream, so the composer shows context size rather than quota.
- **`/model` reads reliably but does not update the chips.** The model chip is driven by the CLI's `init` event, which a mid-session `/model` change does not re-emit — use the chip to change models.
- **A few CLI built-ins are unavailable in print mode** (`/status`, the CLI's own `/help`). They report this themselves when run.

---

## 📄 License

MIT — see [LICENSE](LICENSE). Original copyright belongs to the upstream VsAgentic authors; that notice is retained in full.

## 🙏 Credits

- [VsAgentic](https://github.com/adospace/vs-agentic) by Adolfo Marinucci — the extension this is forked from.
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) by Anthropic — the CLI that does the actual work, and the UI this fork imitates.
