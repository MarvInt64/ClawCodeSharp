# CodeSharp - C#/.NET 10 Implementation

This is the C#/.NET 10 port of the original Rust implementation of Claw Code, now consistently branded as CodeSharp, with support for Anthropic, NVIDIA, OpenAI, and xAI providers plus an improved terminal UX.

## Showcase

<p>
  <img src="assets/showcase/screen0.png" alt="CodeSharp REPL screenshot 1" width="49%" />
  <img src="assets/showcase/screen1.png" alt="CodeSharp REPL screenshot 2" width="49%" />
</p>

## Improvements

The current C# CLI has already been improved in several practical areas:

- Assistant responses in the terminal are no longer shown as raw markdown only. The CLI now renders headings, lists, quotes, inline code, links, fenced code blocks, and horizontal rules in a terminal-friendly format.
- Assistant output now wraps to the current console width instead of overflowing awkwardly inside the bordered output blocks.
- Fenced `diff` and `patch` code blocks are rendered more clearly for terminal reading.
- Markdown tables are rendered as aligned terminal tables when they fit, with a compact fallback when they are too wide.
- The REPL now has a clearer busy state with spinner-based feedback while a turn is running.
- Queued follow-up prompts are surfaced in the REPL instead of disappearing into the background, making multi-step interaction easier to follow.
- Interrupt handling is more usable: `Ctrl+C` cancels the active turn first, and repeated interrupt handling supports a cleaner exit flow.
- Internal activity messages are cleaner. Tool calls such as plan updates are shown as short human-readable status lines instead of raw JSON payloads like `TodoWrite {...}`.
- The assistant now announces what it is about to inspect, search, or change, and this intent is highlighted more clearly in the REPL so the next step is easy to spot.
- Slash commands now have interactive suggestions while typing, so entering `/` immediately shows matching commands and filters them live as you type.
- Global provider, model, and API key defaults can be stored in `~/.codesharp/settings.json`, with a guided config flow in the CLI.
- Repository search is more reliable: `glob_search` and `grep_search` now return counts plus capped samples instead of only raw lists, making broad codebase queries easier to verify.
- Search tools now skip ignored build/editor directories and also respect the root `.gitignore`, reducing noisy matches from irrelevant files.
- Broad negative claims are handled more carefully: the runtime and prompt now push the model to verify search results before saying something does not exist in the codebase.
- Overall REPL output is easier to scan because user-facing status and assistant output now prioritize readability over raw protocol detail.


## Project Structure

```
csharp/
├── CodeSharp.sln                    # Solution file
└── src/
    ├── CodeSharp.Core/              # Core runtime, session, permissions
    ├── CodeSharp.Api/               # HTTP client, API providers
    ├── CodeSharp.Tools/             # Tool registry and execution
    ├── CodeSharp.Plugins/           # Plugin management
    ├── CodeSharp.Lsp/               # LSP integration
    ├── CodeSharp.Commands/          # Slash commands
    ├── CodeSharp.Cli/               # Main CLI application
    └── CodeSharp.Server/            # HTTP server for sessions
```

## Build

Requirements:
- .NET 10 SDK (preview)

```bash
cd csharp
dotnet restore
dotnet build
```

## Run

### Interactive REPL Mode

```bash
cd csharp
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

## Available Options

| Flag | Description |
|------|-------------|
| `-p` | Run a single prompt and exit |
| `--model` | Model to use (default: claude-opus-4-6) |
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
