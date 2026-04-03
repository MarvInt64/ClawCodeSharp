# CodeSharp

CodeSharp is a terminal-first coding assistant for .NET developers who want fast answers, clean repo navigation, and a CLI that feels built for real work instead of demos.

Built as a modern C#/.NET 10 take on the original Claw Code, it brings Anthropic, NVIDIA, OpenAI, and xAI models into a sharper REPL with live streaming, readable markdown rendering, colored diff previews, clearer tool feedback, and smoother code-reading and editing workflows.

It also adds practical features that matter in daily use: slash-command suggestions, guided config management, pinned full-screen REPL layout, parallel read-only repo analysis, smarter search output with context, and better handling for queued prompts, retries, and interrupt flow.

## Showcase

<p>
  <img src="assets/showcase/screen0.png" alt="CodeSharp REPL screenshot 1" width="49%" />
  <img src="assets/showcase/screen1.png" alt="CodeSharp REPL screenshot 2" width="49%" />
</p>

## Improvements

The current C# CLI has already been improved in several practical areas:

- Assistant responses are no longer dumped as raw markdown. The CLI now renders headings, lists, quotes, inline code, links, fenced code blocks, tables, and horizontal rules in a terminal-friendly format.
- Long assistant output and status lines wrap to the current console width instead of overflowing awkwardly.
- Fenced `diff` and `patch` blocks are easier to read, and file edits can surface a colored inline diff preview with green additions and red removals.
- The REPL uses a clearer full-screen layout: the banner stays pinned at the top, while the working area below shows the latest user input, assistant plans, tool activity, and prompt state.
- Busy-state feedback is much clearer. The spinner sits near the prompt, queued follow-up prompts are visible, and empty waiting phases are labeled instead of feeling like a hang.
- Interrupt handling is more usable: `Ctrl+C` cancels the active turn first, and repeated interrupt handling supports a cleaner exit flow.
- Internal activity messages are cleaner. Tool calls such as plan updates are shown as short human-readable status lines instead of raw JSON payloads like `TodoWrite {...}`.
- The assistant now says what it is about to inspect, search, or change before using tools, and that intent is highlighted clearly in the REPL.
- Assistant text now streams into the UI while the model is still thinking, which reduces the dead time between tool calls and the next visible step.
- Slash commands now have live suggestions while typing. Typing `/` immediately shows matching commands and narrows them as you continue typing.
- Global provider, model, and API key defaults can be stored in `~/.codesharp/settings.json`, with an interactive config flow in the CLI.
- The config menu itself is more guided: it keeps the header visible and supports arrow-key navigation for provider, model, and API key management.
- Read-only repo analysis is faster because `read_file`, `glob_search`, and `grep_search` can run in parallel within the same assistant step.
- Repository search is more reliable: `glob_search` and `grep_search` return counts plus capped samples instead of only raw lists, respect the root `.gitignore`, and skip noisy folders like `.git`, `bin`, `obj`, `.idea`, and `node_modules`.
- Broad negative claims are handled more carefully: the runtime and prompt now push the model to verify search results before saying something does not exist in the codebase.


## Project Structure

```
CodeSharp.sln
src/
├── CodeSharp.Core/                  # Core runtime, session, permissions
├── CodeSharp.Api/                   # HTTP client, API providers
├── CodeSharp.Tools/                 # Tool registry and execution
├── CodeSharp.Plugins/               # Plugin management
├── CodeSharp.Lsp/                   # LSP integration
├── CodeSharp.Commands/              # Slash commands
├── CodeSharp.Cli/                   # Main CLI application
└── CodeSharp.Server/                # HTTP server for sessions
```

## Build

Requirements:
- .NET 10 SDK

`global.json` currently pins SDK `10.0.100`.

```bash
dotnet restore CodeSharp.sln
dotnet build CodeSharp.sln
```

## Run

### Interactive REPL Mode

```bash
dotnet run --project src/CodeSharp.Cli
```

### Single Prompt Mode

```bash
dotnet run --project src/CodeSharp.Cli -- "Explain this codebase"
```

### With Specific Provider

```bash
dotnet run --project src/CodeSharp.Cli -- --provider nvidia -p "What does this function do?"
```

### Default Model

The default model is currently `moonshotai/kimi-k2.5`, routed through the NVIDIA provider unless you override it.

## Standalone Binary

You can build a standalone single-file binary with `dotnet publish`.

### macOS (Apple Silicon)

```bash
dotnet publish src/CodeSharp.Cli/CodeSharp.Cli.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  /p:PublishSingleFile=true
```

### macOS (Intel)

```bash
dotnet publish src/CodeSharp.Cli/CodeSharp.Cli.csproj \
  -c Release \
  -r osx-x64 \
  --self-contained true \
  /p:PublishSingleFile=true
```

### Linux x64

```bash
dotnet publish src/CodeSharp.Cli/CodeSharp.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  /p:PublishSingleFile=true
```

### Windows x64

```bash
dotnet publish src/CodeSharp.Cli/CodeSharp.Cli.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true
```

The published binary ends up under:

```text
src/CodeSharp.Cli/bin/Release/net10.0/<RID>/publish/
```

If you prefer a framework-dependent build instead of bundling the runtime, replace `--self-contained true` with `--self-contained false`.

## Config Files

CodeSharp now uses a few distinct config and state files. They serve different purposes:

- `~/.codesharp/settings.json`
  Global defaults for the CLI. This is where `codesharp config` stores your preferred provider, model, and provider-specific API keys.
- `./.codesharp/settings.json`
  Project-local config created by `codesharp init`. This is the repo-local config file CodeSharp looks for in the current workspace, including plugin/config loading for that repo.
- `./CODESHARP.md`
  Project instructions for the agent. If present, its contents are included in the generated system prompt so you can define coding conventions, repo context, or workflow notes.
- `./.codesharp/sessions/session-*.json`
  Saved conversation/session files for prompt and REPL runs.
- `./.codesharp-todos.json`
  Todo state written by the internal plan/todo tool.

CLI flags still win over stored defaults. For example, `--model` and `--provider` override values from `~/.codesharp/settings.json` for that run.

### Global Config Example

```json
{
  "Model": "moonshotai/kimi-k2.5",
  "Provider": "nvidia",
  "ApiKeys": {
    "Nvidia": "nvapi-..."
  }
}
```

### Project Bootstrap

Run this once inside a repo to create the local project files:

```bash
codesharp init
```

That creates:

- `.codesharp/settings.json`
- `CODESHARP.md`

### Managing Global Defaults

Interactive menu:

```bash
codesharp config
```

Non-interactive examples:

```bash
codesharp config show
codesharp config set provider nvidia
codesharp config set model moonshotai/kimi-k2.5
codesharp config set api-key nvidia
codesharp config unset model
```

## Available Options

| Flag | Description |
|------|-------------|
| `-p` | Run a single prompt and exit |
| `--model` | Model to use (default: `moonshotai/kimi-k2.5`) |
| `--provider` | API provider: anthropic, nvidia, openai, xai |
| `--permission-mode` | Permission mode: read-only, workspace-write, danger-full-access |
| `--allowedTools` | Comma-separated list of allowed tools |
| `--output` | Output format: text, json |
| `--version` | Show version |
| `--help` | Show help |


## Architecture

The C# implementation follows the same architecture as the Rust version:

- **CodeSharp.Core**: Core types (Session, ContentBlock, TokenUsage), runtime (ConversationRuntime), permissions (PermissionPolicy), and usage tracking.
- **CodeSharp.Api**: API client abstraction, provider implementations (CodeSharpApi/Anthropic, OpenAI, xAI, NVIDIA).
- **CodeSharp.Tools**: Built-in tool definitions (bash, read_file, write_file, etc.) and execution logic.
- **CodeSharp.Plugins**: Plugin manifest parsing and tool aggregation.
- **CodeSharp.Lsp**: Language Server Protocol client for code intelligence.
- **CodeSharp.Commands**: Slash command parsing and handlers.
- **CodeSharp.Cli**: Main entry point, argument parsing, REPL loop.
- **CodeSharp.Server**: HTTP server for session management via REST API.

## Differences from Rust

- Uses `System.Text.Json` for serialization instead of `serde_json`
- Uses `HttpClient` for HTTP requests instead of `reqwest`
- Uses `HttpListener` for the HTTP server instead of `axum`
- Async/await pattern throughout using `Task` and `CancellationToken`
- Record types for immutable data structures
- Extension methods for enums
