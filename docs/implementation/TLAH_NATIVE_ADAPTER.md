# TLAH native adapter

## Purpose and provenance

`Talah.Harness.Adapters.Tlah` is the Windows-only, in-process adapter for the
TLAH kernel. It targets .NET 8 Windows and has direct project references to the
pinned, read-only `TLAHStudio.Core` and `TLAHStudio.Data` sources at upstream
commit `3ff42e06dca0359ce499c490cc7879934d439150`. It does not invoke a TLAH CLI,
copy upstream source, initialize WinUI, or reimplement the agent loop.

The adapter identifies itself as `tlah`, protocol `native-dotnet`, native
version `4.16.0`.

## Headless composition

`TlahNativeRuntime` creates one dependency graph per Harness profile. The graph
contains:

- `TlahDbContext` backed by `<profile-data-root>/tlah.db`;
- `DbContext`, `ChatService`, and `SettingsService` for native chats, messages,
  provider settings, models, archive metadata, and history;
- the named `LLM` `HttpClient` used by upstream providers;
- `SandboxCommandService` rooted in the profile sandbox directory;
- `LlmService`, the upstream orchestration service used by TLAH's native agent
  path.

The `LlmService` constructor builds the remainder of its native headless graph
when optional services are absent. That graph includes TLAH's agent tool
registry, provider stream adapter, event stream and checkpoint store, run
engines, tool scheduler, context manager, project memory, tool-result
persistence, execution backend router, network policy, MCP client, task tools,
and code/file/terminal/web tools. This is the same Core runtime path used by
Studio, with no App/ViewModel/View registration.

The native `WorkspaceRootService` is used only to associate a validated root
with the newly allocated chat GUID. Each adapter has a separate event channel,
active-run registry, permission registry, profile database, and DI container,
so sessions and events do not cross adapter instances. Upstream's workspace
root registry is process-wide and keyed by collision-resistant chat GUID; the
adapter validates containment before writing it.

## Contract mapping

| Harness operation | Native TLAH operation | Notes |
| --- | --- | --- |
| Health | Resolve native provider catalog from the profile graph | No network request. |
| Authentication state | `ISettingsService.IsConfiguredAsync` and masked settings | API-key configuration only. |
| Configure/logout | `UpdateGlobalSettingsAsync` | `ProtectedSecret` protects the key with Windows DPAPI before SQLite persistence. |
| Models | `ProviderModelCatalog.FallbackModels` for the configured provider | Stable native catalog; no paid/network discovery during listing. |
| Create/list/get/archive | `IChatService` | Native chat GUID is the native session id. |
| Resume session | `GetChatOrThrowAsync`, then `GetLatestAgentRunAsync` | Reconciles durable run status. An unanswered native approval is rebuilt with its original run, turn, and invocation GUIDs; reopening alone never starts or resumes model execution. |
| Session rename | `IChatService.UpdateChatAsync` exists in the runtime boundary | Harness contract 1 has no rename method, so the adapter cannot expose it yet. |
| History | `IChatService.GetChatMessagesAsync` | Chronological native messages, cursor paged by the adapter. |
| Start turn | `ILlmService.RunAgentTaskAsync` | Uses TLAH's native agent engine and tool graph. |
| Resume after approval | `SetAgentToolApprovalAsync`, then `ResumeAgentTaskAsync` | Supports deny, once, chat/session policy, and amended JSON arguments. |
| Cancel | linked cancellation token plus `CancelAgentRunAsync` | Cancellation reaches provider/tool execution and durable run state. |
| Agent progress | `AgentProgressUpdate` | Tool lifecycle, errors, and approvals become canonical events/items. |
| Streaming output | `LlmStreamUpdate` | Text and reasoning deltas keep their channel. |
| Usage | `RawResponse.TokenUsageJson` | Known OpenAI/Anthropic-style token fields map when present; no estimates are invented. |
| Diff retrieval | none | Returns `null`; capability is false. Native code tools can emit results, but upstream exposes no atomic session-diff query. |
| Fork | none | Throws `NotSupportedException`; capability is false. |
| Active steering | none | Throws `NotSupportedException`; capability is false. |

TLAH activity data that does not have a lossless canonical representation is
retained as redacted `VendorData`. The adapter never fabricates tool success,
token counts, costs, diffs, or completion state.

## Authentication limitations

The pinned TLAH source supports provider API keys for OpenAI, DeepSeek,
OpenAI-compatible endpoints, and Anthropic. It does not expose a vendor
subscription login, OAuth browser flow, or device-code flow. Accordingly,
`SupportedMethods` contains only `ApiKey`; interactive login and login
cancellation throw `NotSupportedException`.

Secrets are passed directly to `SettingsService`, which uses upstream
`ProtectedSecret`/Windows DPAPI. Masked settings are used everywhere outside an
actual provider call. Configuration failures are rethrown without their inner
exception after `SecretRedactor` processing so a provider error cannot expose
the submitted key through exception formatting. Native persisted agent events
also use upstream `SecretRedactor` before storage.

## Security boundary

Workspace paths are expanded, converted to absolute canonical paths, and
trimmed. A session requires `IsTrusted=true`. Every additional root must equal
or be below the primary root using a separator-aware, case-insensitive Windows
comparison. Profile IDs must be safe single path segments. Profile state is
placed below `<DataRoot>/profiles/<ProfileId>`.

The descriptor intentionally reports `PermissionGate`, owned by TLAH's native
tool authorization policy, and `IsVerifiedByHost=false`. TLAH validates command
and file effects, applies approval policy, restricts tool network destinations,
and routes process execution through its sandbox command/backend services. This
is not an operating-system security boundary. Provider traffic is required,
and allowed tool traffic can leave the machine. Callers should not interpret
the descriptor as an AppContainer or VM guarantee.

## Lifecycle and concurrency

Initialization is one-shot. Each active turn receives a linked cancellation
source and an adapter-local native turn id. Native run and invocation GUIDs are
captured from progress callbacks. Permission ids are the native invocation
GUIDs, preventing approval from being applied to a different call. Events have
a monotonically increasing adapter sequence and include profile/session/turn
identity. Terminal events remove active-run state. Disposal cancels all turns,
waits for their tasks, clears permission state, completes the channel, and
disposes the native service provider.

### Restart recovery contract

`ResumeSessionAsync` reads the latest durable native run after it validates and
loads the chat. The returned Harness session status reflects the native run
status (running, waiting for approval, paused, completed, or failed), with an
archived chat always remaining archived. Cancelled runs map back to idle because
contract 1 has no cancelled session status.

Only an `awaiting_approval` run with an invocation that is still explicitly
`awaiting_approval` is reconstructed as an active Harness interaction. The
adapter uses the checkpoint's native run GUID, turn GUID, invocation GUID,
arguments, and safety metadata, publishes a waiting turn plus one permission
request, and registers it with the existing approve/deny path. Repeating session
resume on the same adapter instance is idempotent because the native turn and
invocation registrations are accepted only once.

Recovery is deliberately passive. Session reopen never calls
`RunAgentTaskAsync` or `ResumeAgentTaskAsync`. The latter is called only after a
caller explicitly approves or denies the recovered invocation through
`RespondToPermissionAsync`, after native TLAH has durably recorded that decision.
Running or paused checkpoints are surfaced but not automatically continued, and
completed, cancelled, or failed runs are never registered or resumed. This is
the safety boundary that prevents a stale checkpoint from replaying a tool whose
side effects may already have occurred.

## Tests

The focused unit suite uses the injectable `ITlahNativeRuntime` boundary to
exercise mapping and lifecycle without a paid request: capability honesty,
secret redaction, session/history paging, ordering and stream mapping, approval
round trip, crash/restart approval recovery (allow and deny), terminal checkpoint
non-replay, cancellation, archive, workspace containment, disposal, and
unsupported operations.

`TlahNativeRuntimeIntegrationTests` composes the real upstream Core/Data graph,
creates and renames/archives a native chat, configures a provider without
calling it, then queries SQLite to prove the submitted key is not plaintext and
is recognized by upstream `ProtectedSecret` as DPAPI-protected.

## Truthful limitations

- Harness contract 1 has no `GetSessionAsync` or `RenameSessionAsync` member.
  Resume performs the get/validation behavior; native rename remains available
  internally but cannot be surfaced through `IKernelAdapter`.
- The contract has no generic provider-settings operation beyond API-key
  configuration. A custom base URI can be supplied through `ApiKeyCredential`.
- MCP configuration is not surfaced, although the native agent graph can use
  profile database MCP records created by TLAH itself; the capability is false.
- Ask-user elicitation cannot be resumed through a stable upstream response API;
  it is not advertised as supported by this adapter.
- Historical event replay is not exposed. Live adapter events start when an
  adapter instance runs a turn, so `CanReplayEvents` is false.
