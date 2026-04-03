# CodeSharp Roadmap

## Goal

Make CodeSharp feel at least as capable and trustworthy as Claude Code in day-to-day coding work.

## Phase 1: Reliability and Product Honesty

- Only surface REPL commands that actually work end to end.
- Add regression tests for session compaction, tool-call ordering, provider request translation, and interrupt handling.
- Fix server/session correctness issues, especially session identity and event delivery.
- Replace fragile prompt-only behaviors with runtime invariants where possible.

## Phase 2: Semantic Code Intelligence

- Implement real LSP lifecycle management instead of the current stub manager.
- Add symbol-aware tools for definition lookup, reference search, diagnostics, and project-wide symbol navigation.
- Feed semantic context into the agent automatically so fewer turns are wasted on regex search and file paging.
- Use diagnostics and symbol graphs to validate edits before returning control to the user.

## Phase 3: Agent Workflow Quality

- Add task-specific validation loops: build, test, lint, and targeted reruns after edits.
- Improve edit safety with transaction previews, undo support, and clearer multi-file change summaries.
- Add stronger long-context handling so large repo analysis does not degrade into context-limit failures.
- Support durable subtask execution and richer plugin-backed workflows.

## Immediate Execution Order

1. Product honesty in the REPL help/completion surface.
2. Session and provider regression tests for compaction and tool-call protocol correctness.
3. Fix `HttpServer`/`SessionStore` session ID and event-stream behavior.
4. Replace the LSP stub with a working symbol/diagnostic pipeline.
