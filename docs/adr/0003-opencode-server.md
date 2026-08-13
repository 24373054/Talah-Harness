# ADR 0003: OpenCode uses its Server API and SSE

- Status: Accepted
- Date: 2026-08-13

## Decision

The OpenCode adapter launches a pinned native OpenCode server bound only to
`127.0.0.1`, uses a per-launch random Basic Auth secret, consumes its OpenAPI
HTTP surface, and listens to SSE events. ACP remains a separate compatibility
adapter and never concurrently owns the same OpenCode session.

## Rationale

OpenCode's own TUI uses the Server boundary, which includes provider login,
configuration, sessions, permissions, diffs, MCP, agents, and events that a
lowest-common-denominator ACP adapter cannot fully represent.

