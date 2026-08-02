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

Everything else — how sessions work, how the CLI is driven, how permissions are brokered — is unchanged from upstream.

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

Building the VSIX needs **Visual Studio with the "Visual Studio extension development" workload**, because the VSSDK targets that produce the package ship with it:

```
pwsh build-vsix.ps1
```

Then install it, with Visual Studio closed:

```
pwsh install-vsix.ps1
```

That uninstalls any previous copy, installs the new package, and rebuilds Visual Studio's extension and menu caches.

Open it from **View → Other Windows → Forge Pilot**.

> **Why the install script rebuilds caches.** On some Visual Studio 2026 installs the shell does not consume the pending extension configuration change on the next launch: `extensions.configurationchanged` is left in place and `ExtensionMetadataCache.mpack` is never rebuilt, so a newly installed extension contributes no menu commands and does not appear under Manage Extensions. `devenv /setup` and `/updateconfiguration` force the rebuild. This is a host-side quirk rather than something the package controls — the VSIX registers its package, menus and options pages through an ordinary pkgdef, and upstream VsAgentic shows the same behaviour on the same machine.

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

---

## 📄 License

MIT — see [LICENSE](LICENSE). Original copyright belongs to the upstream VsAgentic authors; that notice is retained in full.

## 🙏 Credits

- [VsAgentic](https://github.com/adospace/vs-agentic) by Adolfo Marinucci — the extension this is forked from.
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) by Anthropic — the CLI that does the actual work, and the UI this fork imitates.
