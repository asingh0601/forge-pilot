# Forge Pilot

**Claude Code, docked in Visual Studio.**

Forge Pilot puts an agentic coding assistant in a tool window beside Solution Explorer. Ask a question about your codebase, hand over a refactor, or have it run and fix a failing test — it reads, edits and runs commands with your approval, and shows you every step.

It drives the official [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code), so the agent behaviour is Anthropic's, not a reimplementation. Forge Pilot is the host: the window, the transcript, the permission prompts and the session management.

---

## What it looks like

A conversation, not a log. Your turns sit in rounded bubbles on the right; Claude's replies are plain prose. Everything the agent *does* is folded into collapsible cards so a long task reads as a summary you can open, rather than a wall of output.

- **Collapsed tool runs** — "Used 9 tools", "Ran 2 commands", expandable to the detail
- **Inline diffs** — edits render with line-number gutters and tinted `+`/`−` rows
- **Todo lists** — multi-step plans as a live `☐` / `◐` / `☑` checklist
- **Permission prompts** — numbered, keyboard-first: `1` allow, `2` allow for this tool, `3` deny with a note
- **Follows your theme** — light and dark variants that track Visual Studio

---

## Controls where you're already looking

Model, thinking effort and permission mode are chips under the composer, not settings buried in a dialog.

| Chip | Choices |
|---|---|
| **Model** | Default, Opus 5, Sonnet 5, Haiku 4.5 |
| **Effort** | Auto, Low, Medium, High, Max thinking budget |
| **Mode** | Manual · Accept edits · Plan · Auto · Bypass permissions |

**Plan** mode is the one to reach for on unfamiliar code: Claude explores and proposes an approach without touching anything, and you decide whether to let it proceed.

Changing any of these restarts the CLI process but **resumes the same conversation**, so you can switch models mid-task without losing context.

---

## Sessions

Multiple conversations per workspace, switchable from the header, persisted across restarts. Each one keeps its own history, model and mode. Delete one and the panel moves to the next rather than closing.

---

## Slash commands

Type `/` in the composer for a picker.

| Command | What it does |
|---|---|
| `/usage` | Your subscription limits — session and weekly — rendered as meters |
| `/compact` | Summarises earlier turns to free up context |
| `/clear` | Empties the conversation |

Other Claude Code commands work when typed, including `/context` and `/model`. Project commands from `.claude/commands` work too, and the ⚡ menu lists the skills, plugins and MCP connectors available in the current workspace.

---

## Requirements

Forge Pilot is a front end for the Claude Code CLI. You need all four:

1. **Visual Studio 2026** (17.14 or later) on **Windows x64**
2. **[Node.js](https://nodejs.org/)** 18 or later
3. **The Claude Code CLI** — `npm install -g @anthropic-ai/claude-code`
4. **An active [Claude Pro or Max subscription](https://claude.ai/pricing)**

Then run `claude login` once from a terminal and open **View → Other Windows → Forge Pilot**.

**A subscription is required and an API key will not substitute for it.** Authentication is handled entirely by the CLI; Forge Pilot never asks for, stores or transmits a key.

---

## Privacy

Your code is sent to Anthropic through the Claude Code CLI in order to answer requests — read [Anthropic's privacy policy](https://www.anthropic.com/privacy) before using it on proprietary or regulated codebases. Conversations are stored locally under `%AppData%\ForgePilot\` and are not sent anywhere else.

---

## Known limitations

Worth knowing before you install:

- **Windows x64 and Visual Studio 2026 only.** No VS Code, no Rider, no VS 2022.
- **Inline completions are opt-in and on-demand, not Copilot-style ghost text.** They run through the CLI on your subscription rather than a billed API key, and a CLI round trip measures 3–10 seconds — too slow to keep up with typing, so they never fire on a keystroke. Off by default.
- **Subscription limits are only visible via `/usage`.** They come from an endpoint the CLI calls directly and aren't available to the extension otherwise.
- **A few CLI commands are terminal-only** (`/status`, `/help`) and say so when run.

---

## Credits and licence

Forge Pilot is a fork of **[VsAgentic](https://github.com/adospace/vs-agentic)** by Adolfo Marinucci ([@adospace](https://github.com/adospace)), used under the MIT Licence. The CLI transport, session persistence and permission plumbing are upstream's work; this fork contributes the interface, the session controls and the completion path. If you'd prefer the original, [VsAgentic is on the Marketplace](https://marketplace.visualstudio.com/items?itemName=adospace.VsAgentic).

**Not affiliated with, endorsed by, or supported by Anthropic.** Claude and Claude Code are products of Anthropic.

MIT licensed · [Source](https://github.com/asingh0601/forge-pilot) · [Report an issue](https://github.com/asingh0601/forge-pilot/issues)
