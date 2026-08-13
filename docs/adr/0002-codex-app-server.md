# ADR 0002: Codex uses App Server over stdio

- Status: Accepted
- Date: 2026-08-13

## Decision

The Codex adapter launches the pinned native Codex binary as
`codex app-server --stdio` and implements the official bidirectional JSONL
protocol. It does not use `codex exec --json`, the TypeScript SDK, MCP Server,
WebSocket transport, or a third-party ACP bridge as its primary boundary.

## Rationale

App Server is the first-party rich-client interface and exposes authentication,
conversation history, approvals, streamed events, and Windows setup semantics.
Stdio avoids a local network listener and an unnecessary Node sidecar.

