# Product vision

Talah Harness 1.0 is a complete Windows x64 desktop product for developers who
want to run Codex, OpenCode, or the native TLAH runtime without changing tools,
terminals, and safety vocabulary for every conversation.

The product's single job is to let a person start, observe, approve, interrupt,
recover, and audit a coding-agent session from one durable workspace.

## Version 1.0 promise

- Every shipping kernel is real. A missing or incompatible runtime is presented
  as an actionable installation or compatibility state, never replaced by a
  simulated response.
- Every conversation belongs to one selected kernel. Cross-kernel delegation is
  intentionally deferred until after 1.0 so that it can build on trustworthy
  single-kernel semantics.
- Codex, OpenCode, and TLAH keep their native sessions, authentication, model,
  permission, and security semantics.
- The host provides a coherent Windows UI, durable event projection, profile
  isolation, process supervision, diagnostics, approvals, installation, and
  updates.
- A crash must not silently lose a completed event or repeat a potentially
  destructive action.

## Not 1.0

- Automatic routing of one task across multiple kernels.
- Claiming equivalent sandbox guarantees for engines with different enforcement.
- Cloud account synchronization owned by Talah Harness.
- A web or cross-platform client.

