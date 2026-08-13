# Codex App Server adapter

The Codex adapter is a native .NET 8 client for the official `codex app-server
--stdio` rich-client protocol. It does not use `codex exec`, MCP as an adapter
bridge, ACP, WebSockets, a sidecar, or simulated production output.

Protocol references: [OpenAI App Server documentation](https://developers.openai.com/codex/app-server/)
and the [official Codex App Server source](https://github.com/openai/codex/tree/main/codex-rs/app-server).

## Pinned protocol

The checked-in schema is generated from `codex-cli 0.147.0`:

```powershell
codex --version
codex app-server generate-json-schema --experimental --out schemas/codex/0.147.0
```

`schemas/codex/0.147.0/manifest.json` records the command, version, file count,
and SHA-256 hashes of the two aggregate bundles. Experimental members are
included so the exact approval and elicitation shapes exposed by this version
are preserved. Production behavior remains capability-gated.

The stdio wire format is one UTF-8 JSON object per line. Per the App Server
protocol, the JSON-RPC 2.0 `jsonrpc` header is omitted. The adapter sends numeric
request IDs, correlates responses, handles notifications and server-initiated
requests concurrently, and writes correlated server-request responses with the
original JSON ID.

## Capability matrix

| Host capability | Advertised | Pinned App Server operation |
| --- | --- | --- |
| Health/version | Yes | successful `initialize`; response `userAgent` |
| Account status | Yes | `account/read` |
| Browser login | Yes | `account/login/start` with `type: chatgpt` |
| Device-code login | Yes | `account/login/start` with `type: chatgptDeviceCode` |
| OpenAI API-key login | Yes | `account/login/start` with `type: apiKey` |
| Cancel login/logout | Yes | `account/login/cancel`, `account/logout` |
| Model listing | Yes | paged `model/list` |
| List/start/resume/fork/archive thread | Yes | `thread/list`, `thread/start`, `thread/resume`, `thread/fork`, `thread/archive` |
| Read item history | Yes | paged `thread/items/list` |
| Start/steer/interrupt turn | Yes | `turn/start`, `turn/steer`, `turn/interrupt` |
| Command/file/permission approval | Yes | correlated server requests under `item/*/requestApproval` |
| Tool user input/MCP elicitation | Yes | `item/tool/requestUserInput`, `mcpServer/elicitation/request` |
| Text/reasoning/plan/command/file/diff/subagent/usage events | Yes | v2 notifications, with sanitized vendor JSON retained |
| Read diff | Yes | latest `turn/diff/updated` state; `null` when App Server has not emitted a diff |
| Amend approval input | No | Host amendment is generic, while Codex 0.147.0 uses operation-specific amendment unions |
| Configure arbitrary providers | No | The adapter accepts only the schema-defined OpenAI API-key login shape |
| Configure MCP servers | No | Configuration is intentionally outside the adapter contract implementation |
| Replay past events | No | The protocol stream is live; persisted replay is a host responsibility |

Unsupported login methods, custom provider/base-URI API-key settings, unknown
approval/sandbox modes, and unsupported input content throw
`NotSupportedException` instead of returning synthetic results.

## Security and resilience

- Secrets are never placed in process arguments. The process command line is
  fixed to `app-server --stdio`; credentials travel only inside the stdio
  protocol request that defines them.
- API keys, access/refresh tokens, bearer values, and secret-like vendor fields
  are redacted before diagnostics or vendor JSON reach host events.
- Stdout and stderr are consumed independently. Stderr becomes a redacted
  diagnostic event and never participates in protocol framing.
- Incoming and outgoing messages default to a 16 MiB limit. Malformed or
  forward-compatible unknown messages produce diagnostics; unknown fields on
  known messages are tolerated.
- Requests have cancellation and a 30-second default timeout. Process exit or
  stdout closure faults every pending request. Disposal closes stdin, waits
  briefly, then terminates only the owned App Server process if required.
- `initialize` and the required `initialized` notification are emitted exactly
  once per client instance. Host event sequence numbers are atomic, unique, and
  monotonically increasing. Event replay is not advertised.

## Tests

Run the deterministic suite (it uses only an in-memory test transport):

```powershell
dotnet test tests/Talah.Harness.Adapters.Codex.Tests/Talah.Harness.Adapters.Codex.Tests.csproj -c Release
```

The suite covers line framing, exactly-once initialization, concurrent response
correlation, notifications and unknown messages, auth methods, models and thread
lifecycle, turn streams and interrupt, approvals for allow-once/session/deny,
elicitation, process exit, timeout, oversized messages, redaction, vendor-data
retention, and stable event sequencing.

The live smoke path performs only initialize, account read, and (when signed in)
one-page model listing. It never starts a turn and therefore never sends a
billable prompt. It is opt-in:

```powershell
$env:TALAH_CODEX_LIVE_TEST = '1'
dotnet test tests/Talah.Harness.Adapters.Codex.Tests/Talah.Harness.Adapters.Codex.Tests.csproj -c Release --filter Category=Live
```

Without `TALAH_CODEX_LIVE_TEST=1`, the test is reported as skipped without launching Codex. If
the CLI is signed out, initialize and account-read are still validated and the
credential-dependent model-list step is reported as skipped safely.

## Integration

The parent solution must add the production and test projects; this isolated
adapter change intentionally does not edit the root solution. A profile can set
`TALAH_CODEX_PATH` to an absolute CLI path. Other profile environment entries
are inherited by the child process without being copied to logs.
