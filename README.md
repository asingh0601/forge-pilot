# Claude Deck

**A Claude Code-style agentic coding assistant for Visual Studio 2026** — chat with Claude to explore, understand, and modify your codebase, in a tool window that mirrors the Claude Code terminal experience.

> ### Fork notice
>
> Claude Deck is a fork of **[VsAgentic](https://github.com/adospace/vs-agentic)** by Adolfo Marinucci ([@adospace](https://github.com/adospace)), used under the MIT License. All of the CLI integration, session persistence, permission pipe, and agentic plumbing is upstream's work.
>
> This fork changes the user interface: a faithful replica of the Claude Code terminal look — monospace throughout, `⏺` / `⎿` / `✻` step glyphs, terracotta accent, numbered permission prompts — plus its own extension identity so it installs side-by-side with the original.
>
> It is **not** published to the Visual Studio Marketplace and is not affiliated with Anthropic. If you want the upstream extension, install [VsAgentic from the Marketplace](https://marketplace.visualstudio.com/items?itemName=adospace.VsAgentic).

---

## ✨ What this fork changes

- **Terminal-faithful transcript** — user prompts echo behind a `>` gutter, assistant turns lead with `⏺`, tool calls render as `⏺ Read(src/Foo.cs)` with a dim `⎿ Read 120 lines` result row, thinking collapses behind `✻ Thought for 12s`.
- **Claude Code palette** — terracotta accent on a flat terminal background, in both a dark and a light variant that follow the Visual Studio theme.
- **Box-drawn input** — a `>` prompt frame with a dim status line (`@ for files · ctrl+enter to send`, or `✻ Working… (12s · esc to interrupt)` while busy) instead of send/stop buttons.
- **Numbered permission prompts** — the familiar `❯ 1. Yes / 2. Yes, and don't ask again / 3. No, and tell Claude what to do differently`, keyboard-first.
- **Diff and todo rendering** — edits show red/green gutters; `TodoWrite` renders as `☐` / `☒` checklists.

Everything else — how sessions work, how the CLI is driven, how permissions are brokered — is unchanged from upstream.

---

## 📋 Requirements

- **Visual Studio 2026** version **17.14 or later** (Community, Professional, or Enterprise)
- **Windows x64** (amd64)
- **[Node.js](https://nodejs.org/)** (v18 or later) — required to install the Claude Code CLI
- **An active [Claude Pro or Max subscription](https://claude.ai/pricing)** — the chat window uses the Claude Code CLI, which requires a paid Claude subscription. API keys are **not** supported for chat.

---

## 🚀 Installation

Claude Deck has no Marketplace listing — build and install it yourself:

1. Open `src/ClaudeDeck.slnx` in Visual Studio 2026 with the **Visual Studio extension development** workload installed.
2. Build in `Release`; the VSIX lands in `src/ClaudeDeck.VSExtension/bin/Release/`.
3. Double-click the `.vsix` to install, then restart Visual Studio.

To develop against it, set `ClaudeDeck.VSExtension` as the startup project and press **F5** — that launches the VS experimental instance.

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

### 4. Configure Claude Deck (optional)

If `claude` is not on your PATH, go to **Tools → Options → ClaudeDeck → General** and set **Claude CLI Path** to the full path (e.g. `C:\Users\<you>\AppData\Roaming\npm\claude.cmd`).

### Open Claude Deck

**View → Other Windows → ClaudeDeck** opens a new chat session. A **ClaudeDeck Sessions** panel appears alongside Solution Explorer for managing conversations.

### Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| **"Not logged in · Please run /login"** | The CLI is not authenticated | Run `claude login` from a terminal and complete the browser login |
| **"Invalid API key"** | An `ANTHROPIC_API_KEY` environment variable is interfering | The chat window uses subscription auth, not API keys. Remove it with `[System.Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", $null, "User")` and restart Visual Studio |
| **"Failed to start Claude CLI"** | The `claude` command was not found | `npm install -g @anthropic-ai/claude-code`, or set the full path in **Tools → Options → ClaudeDeck** |

---

## 🗂️ Project Structure

```
src/ClaudeDeck.slnx
├── ClaudeDeck.VSExtension/   # VSIX entry point — commands, tool windows, package bootstrap
├── ClaudeDeck.UI/            # Shared WPF controls, ViewModels, WebView2 transcript renderer
├── ClaudeDeck.Services/      # Core service layer — CLI integration, session store
├── ClaudeDeck.Desktop/       # Standalone WPF app — the fast loop for UI work
└── ClaudeDeck.Console/       # Console host (for development & testing)
```

`ClaudeDeck.Desktop` hosts the same transcript renderer and input chrome as the extension, so UI changes can be checked without launching a VS experimental instance:

```
ClaudeDeck.Desktop.exe D:\path\to\some\repo
```

---

## 🔒 Privacy & Security

- Your code is sent to **Anthropic** via the Claude Code CLI to fulfill requests. Review [Anthropic's privacy policy](https://www.anthropic.com/privacy) before use on sensitive or proprietary codebases.
- **No API keys are stored or required** for chat. Authentication is handled by the Claude Code CLI via your Claude subscription.
- Session history is stored **locally** in `%AppData%\ClaudeDeck\` and never leaves your machine. This is a separate directory from upstream's `%AppData%\VsAgentic\`, so the two extensions never share state.

---

## 🐛 Known Limitations

- **Visual Studio 2026 (17.14+)** only — VS Code and Rider are not supported.
- **x64 Windows** only.

---

## 📄 License

MIT — see [LICENSE](LICENSE). Original copyright belongs to the upstream VsAgentic authors; that notice is retained in full.

## 🙏 Credits

- [VsAgentic](https://github.com/adospace/vs-agentic) by Adolfo Marinucci — the extension this is forked from.
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) by Anthropic — the CLI that does the actual work, and the UI this fork imitates.
