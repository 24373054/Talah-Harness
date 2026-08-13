# Architecture

Talah Harness is a host-owned desktop shell around kernel-specific adapters.
The host owns canonical events, durable UI state, credentials, approvals,
workspace identity, process supervision, diagnostics, and release lifecycle.

```text
WinUI App
  -> Conversation coordinator
     -> Kernel registry / capability negotiation
        -> Codex App Server adapter (stdio JSONL)
        -> OpenCode Server adapter (loopback HTTP + SSE)
        -> TLAH native adapter (in-process TLAH runtime boundary)
  -> Canonical SQLite event store
  -> Windows process supervisor / Job Objects
  -> Per-kernel credential profiles
```

## Contract rules

1. IDs are always namespaced by adapter and profile.
2. The host normalizes enough data to render and recover, while retaining the
   original vendor payload for diagnostics and forward compatibility.
3. Capability checks are explicit; unsupported operations do not masquerade as
   empty results.
4. Approval UI is shared, but the choices and persistence scope are supplied by
   the adapter.
5. Security is a descriptor, not an `IsSandboxed` boolean.
6. Shipping adapters use pinned, tested protocol schemas and version gates.
7. External kernel processes are children of a host-owned Windows Job Object.

The TLAH Studio submodule remains read-only. New host code may reference its
Core/Data projects through the native adapter but never writes into or commits
changes to the original repository.

