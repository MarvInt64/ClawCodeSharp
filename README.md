# Claw Code - C#/.NET 10 Implementation

This is a C#/.NET 10 port of the Claw Code Rust implementation.

## Project Structure

```
csharp/
├── Claw.sln                    # Solution file
└── src/
    ├── Claw.Core/              # Core runtime, session, permissions
    ├── Claw.Api/               # HTTP client, API providers
    ├── Claw.Tools/             # Tool registry and execution
    ├── Claw.Plugins/           # Plugin management
    ├── Claw.Lsp/               # LSP integration
    ├── Claw.Commands/          # Slash commands
    ├── Claw.Cli/               # Main CLI application
    └── Claw.Server/            # HTTP server for sessions
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
dotnet run --project src/Claw.Cli
```

### Single Prompt Mode

```bash
dotnet run --project src/Claw.Cli -- "Explain this codebase"
```

### With Specific Provider

```bash
dotnet run --project src/Claw.Cli -- --provider nvidia -p "What does this function do?"
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

- **Claw.Core**: Core types (Session, ContentBlock, TokenUsage), runtime (ConversationRuntime), permissions (PermissionPolicy), and usage tracking.
- **Claw.Api**: API client abstraction, provider implementations (ClawApi/Anthropic, OpenAI, xAI, NVIDIA).
- **Claw.Tools**: Built-in tool definitions (bash, read_file, write_file, etc.) and execution logic.
- **Claw.Plugins**: Plugin manifest parsing and tool aggregation.
- **Claw.Lsp**: Language Server Protocol client for code intelligence.
- **Claw.Commands**: Slash command parsing and handlers.
- **Claw.Cli**: Main entry point, argument parsing, REPL loop.
- **Claw.Server**: HTTP server for session management via REST API.

## Differences from Rust

- Uses `System.Text.Json` for serialization instead of `serde_json`
- Uses `HttpClient` for HTTP requests instead of `reqwest`
- Uses `HttpListener` for the HTTP server instead of `axum`
- Async/await pattern throughout using `Task` and `CancellationToken`
- Record types for immutable data structures
- Extension methods for enums
