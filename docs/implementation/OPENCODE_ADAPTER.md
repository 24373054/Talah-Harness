# OpenCode Server adapter

## Release decision

The Talah Harness 1.0.0 adapter pins OpenCode **1.18.9**. It talks directly to the
native executable over authenticated HTTP/OpenAPI and SSE. It does not use the
JavaScript SDK, Node, Bun, or ACP. Runtime version compatibility is deliberately
limited to `>= 1.18.9` and `< 1.19.0`; an absent binary reports `NotInstalled`, while
another version reports a distinct incompatible-version failure with remediation.

The authoritative captured OpenAPI 3.1 document and its provenance are in
`schemas/opencode/openapi-1.18.9.json` and `schemas/opencode/PROVENANCE.md`. The
standard Windows x64 release SHA-256 is
`1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2`; the baseline
x64 asset for CPUs without AVX2 has SHA-256
`0c85dc2d296417ac04dd51561985e5715f174a5ec38ae785d1f5233c3ebcc519`.

## Host and transport boundary

`OpenCodeExecutableDiscovery` resolves an explicit profile `OPENCODE_EXECUTABLE`
first and then PATH, invokes `--version`, and never follows a moving release URL.
`OpenCodeServerLauncher` starts exactly:

```text
opencode serve --hostname 127.0.0.1 --port 0 --pure
```

OpenCode 1.18.9 documents/defaults port `0`, mDNS `false`, and an empty CORS list.
The adapter therefore requests the server-managed ephemeral port, accepts only the
announced `127.0.0.1` URI, does not enable mDNS, and supplies no public CORS origin.
It generates 48 random bytes for every launch and passes the Base64 secret only in
`OPENCODE_SERVER_PASSWORD` (with username `talah`)—never on the command line or in
diagnostics. Readiness is an authenticated `/global/health` loop with exponential
backoff and a 20-second bound. The injectable process supervisor permits deterministic
tests; production cleanup escalates to `Kill(entireProcessTree: true)` after a bounded
graceful attempt.

The HTTP client rejects non-loopback base URIs, applies Basic Auth, bounds JSON bodies
at 8 MiB, maps non-success responses to typed/redacted exceptions, honors cancellation,
and ignores forward-compatible unknown fields. Event vendor JSON is retained. The SSE
client uses a bounded channel (capacity 256, writer waits), a 1 MiB event limit,
multi-line `data:` parsing, cancellation, exponential reconnect, `Last-Event-ID`, and
deduplication by SSE ID or vendor event ID. Because `/event` offers no historical replay
contract, reconnect is best-effort and `CanReplayEvents` is false.

## Capability matrix

| Host capability | Advertised | OpenCode 1.18.9 mapping / boundary |
|---|---:|---|
| Authenticate | Yes | `/provider/auth`, provider OAuth authorize/callback, `/auth/{providerID}` |
| API key | Yes | `PUT`/`DELETE /auth/{providerID}`; secrets only in request bodies |
| Provider/model listing | Yes | `GET /provider`; flattened as `provider/model` |
| List/resume sessions | Yes | `GET /session`, `GET /session/{id}` |
| Create/update | Yes | `POST /session`; public adapter extension maps `PATCH /session/{id}` |
| Fork/children | Yes | host fork plus public `GetChildrenAsync` extension |
| Archive/delete | Yes | host archive is `PATCH time.archived`; public delete extension maps `DELETE` |
| Async turn | Yes | `POST /session/{id}/prompt_async`; completion is event-driven |
| Active-turn steering | **No** | no explicit 1.18.9 server endpoint; throws `NotSupportedException` |
| Abort/cancel | Yes | `POST /session/{id}/abort` |
| History | Yes | `GET /session/{id}/message` with bounded paging |
| Diff | Yes | `GET /session/{id}/diff` |
| Revert/unrevert | Yes, extension | public methods map the native endpoints; absent from `IKernelAdapter` v1 |
| Permission gate | Yes | `permission.v2.asked` and `/permission/{requestID}/reply` |
| Amend tool input | **No** | reply schema accepts only once/always/reject; throws clearly |
| Questions/elicitation | Yes | question asked/reply/reject |
| MCP configuration | **No** | status is reported as metadata; no host-v1 configuration method is claimed |
| Agents/subagents | Yes | create/prompt agent selection and normalized subtask/agent events |
| Event replay | **No** | reconnect/dedup exists, but the server does not promise replay |

Security is accurately described as an OpenCode-owned **permission gate**. The adapter
does not claim an operating-system sandbox, network restriction, process restriction,
or host verification. Workspace roots become descriptive writable roots only after
the adapter has observed or created sessions.

## Event normalization

The normalizer preserves the complete vendor event and maps session lifecycle/status,
text and reasoning deltas, text/reasoning/tool/file/patch/subtask parts, usage/cost,
errors, permission requests, questions, and file edits into host events/items. Unknown
future event and part types become trace diagnostics/notices instead of breaking the
stream. Tool state maps pending/running/completed/error without manufacturing results.

## Integration gaps

- Add both project files to `Talah.Harness.sln` and register
  `OpenCodeAdapterFactory`; solution/runtime ownership intentionally remained with the
  parent integration task.
- `IKernelAdapter` v1 has `BeginLoginAsync` but no OAuth completion member. The adapter
  exposes `CompleteOAuthAsync(loginId, code)` as a concrete extension; the host needs a
  narrow dispatch hook for callback completion.
- `IKernelAdapter` v1 lacks session update/delete/children and revert/unrevert members.
  Concrete adapter methods expose the verified server operations without overstating
  the common contract.
- OpenCode MCP/agent status is included in health vendor metadata. MCP mutation is not
  advertised until a common host contract and product policy exist.
- The opt-in smoke test is enabled with `TALAH_OPENCODE_LIVE=1`; it performs only local
  version/schema/health-style checks and never starts a billable model turn. It returns
  cleanly when the pinned executable is absent.

## Verification

Run:

```powershell
dotnet build src\Talah.Harness.Adapters.OpenCode\Talah.Harness.Adapters.OpenCode.csproj -warnaserror
dotnet test tests\Talah.Harness.Adapters.OpenCode.Tests\Talah.Harness.Adapters.OpenCode.Tests.csproj -warnaserror
```

Tests cover launch argument and secret boundaries, readiness timeout and cleanup,
NotInstalled/incompatible/crash health, typed HTTP mappings, OAuth/API key, session
lifecycle/prompt/abort/diff/permission routes, errors and redaction, response/event size
limits, cancellation, SSE multi-line parsing/reconnect/Last-Event-ID/dedup/bounded
backpressure, normalized prompt/tool/usage/error/permission events, and the opt-in live
schema check.
