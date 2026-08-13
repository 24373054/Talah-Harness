# ADR 0001: Host-owned kernel adapter architecture

- Status: Accepted
- Date: 2026-08-13

## Decision

The application uses a host-owned versioned contract and one adapter per coding
agent. Codex, OpenCode, and TLAH retain native extensions and raw payloads. The
UI renders canonical items and capabilities rather than vendor protocol DTOs.

## Consequences

- A kernel may expose more features than another without misleading parity.
- Protocol churn is isolated to one assembly and schema snapshot.
- The host can provide one durable event log and recovery model.
- Adapter contract design and conformance testing become release-critical.

