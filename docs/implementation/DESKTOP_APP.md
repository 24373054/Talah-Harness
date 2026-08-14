# Talah Harness desktop application

## Release shape

Talah Harness 1.0.0 is an unpackaged, self-contained Windows x64 WinUI 3
application. It is a single desktop control room over three independent kernel
profiles: Codex, OpenCode, and native TLAH. Each session belongs to exactly one
profile and one adapter. The application does not translate a turn between
kernels or coordinate kernels with one another.

The main window follows the Trace Ledger design system:

- the left ledger aggregates real sessions returned by every running profile;
- the center ledger renders canonical history and durable events along an
  ordered Trace Rail;
- the right inspector shows the selected profile, session, workspace, security
  boundary, capabilities, diff, diagnostics, and reported usage;
- the top state strip reports profile availability without equating the three
  adapters' security guarantees.

All colors used by structural XAML are theme resources. Light, dark, and high
contrast dictionaries define the canvas, surfaces, text, borders, rail, and
modal overlay. The interaction layer uses Windows controls, visible platform
focus, keyboard navigation, text wrapping, selectable technical output, and
44-pixel primary targets. Trace scrolling and updates do not depend on
decorative motion.

Adaptive states preserve the trace as the primary working surface. At widths
below 1180 effective pixels the inspector collapses; below 760 the session pane
also collapses while kernel setup and diagnostics remain in the title strip.
The center column can contract to 320 pixels, and onboarding content becomes a
single scrollable column with its decorative image removed. At 200% text or
display scaling this prevents fixed side columns, onboarding copy, or the
composer from clipping instead of shrinking type or interaction targets.

## Boot and shutdown

`App` creates one `HarnessController`, and the controller creates one
`KernelHost`. Window loading performs these operations in order:

1. create the product log and schema directories;
2. read nonsecret UI preferences;
3. initialize the schema-v2 canonical SQLite store, repair event projections,
   and sweep interrupted turns and interactions into terminal recovery states;
4. start the Codex, OpenCode, and TLAH default profiles independently;
5. begin one durable host-event reader;
6. refresh health, session summaries, onboarding, and workspace state.

A failure to start one profile is converted into that profile's unavailable
state and does not abort the loop. In particular, the OpenCode factory can
return a hosted `NotInstalled` descriptor. `KernelHost` intentionally does not
start an adapter event pump for that availability, so there is no false event
stream failure. The other profiles remain usable. The state strip and setup
dialog display the adapter's installation or compatibility remediation.

Window close unsubscribes UI delivery, cancels the durable reader, asks
`KernelHost` to stop and dispose every hosted adapter, and disposes the
controller. Adapter processes therefore leave through their normal supervised
shutdown paths.

When `LastRecoverySweep.HadInterruptedWork` is true, the app reports exact
counts for failed turns, paused sessions, abandoned permission requests, and
abandoned questions in both the live status bar and diagnostics. The operator
is directed to refresh or resume affected sessions; the app does not claim the
interrupted native action succeeded.

## Data paths

The default root is `%LOCALAPPDATA%\Talah Harness`:

| Path | Purpose |
|---|---|
| `data\harness.db` | Canonical sessions, workspaces, turns, items, events, checkpoints, and approvals |
| `data\backups` | Required SQLite pre-migration backups |
| `state\settings.json` | Onboarding completion, approved workspace path/trust, selected kernel, and selected model IDs |
| `profiles\codex\default` | Default Codex profile data root |
| `profiles\opencode\default` | Default OpenCode profile data root |
| `profiles\tlah\default` | Default native TLAH profile data root |
| `logs` | Redacted host and adapter diagnostics |
| `schemas` | Adapter schema root supplied by `KernelHost` |

The crash ledger is created under a directory whose inherited access rules are
removed and whose full-control rule is restricted to the current Windows user.
It records timestamp, redacted source, exception type, and a bounded redacted
message for at most twelve nested or aggregate exceptions. Authorization and
proxy-authorization values, bearer/basic material, cookies, named keys/tokens/
passwords/secrets/credentials, URL user information, sensitive query values,
and JWT-shaped values are replaced before writing. The active file rotates at
512 KiB and retains four prior files. UI failures use a bounded, type-oriented
explanation instead of exposing arbitrary exception text.

`settings.json` has no credential field. API keys exist only in a local
`PasswordBox` long enough to call `KernelHost.ConfigureApiKeyAsync`; the box is
cleared before the asynchronous operation. Keys are not placed in profile
environment dictionaries, command arguments, diagnostic strings, or UI
preferences. Trace JSON and text redact values whose names or labels indicate
keys, tokens, passwords, secrets, or credentials before display.

## Default profiles

The application creates one durable default profile per adapter:

| Adapter | Profile ID | Display name | Profile environment |
|---|---|---|---|
| `codex` | `default` | Codex | Empty |
| `opencode` | `default` | OpenCode | Empty |
| `tlah` | `default` | TLAH | Empty |

No provider secret is inferred from the process environment or copied into a
profile. Runtime executable overrides remain adapter-level deployment options;
the normal desktop defaults use adapter discovery.

## First run and workspace policy

The first-run guide appears until setup completes. It shows current health for
all three real profiles and requires an existing workspace folder. Completing
setup persists only the completion flag and the normalized workspace
preference. The guide remains available from kernel setup but does not reappear
automatically afterward.

The selected folder becomes a canonical `WorkspaceDescriptor`. Session creation
passes it to `KernelHost`, which persists the binding and normalizes the path.
The user explicitly chooses whether the workspace is trusted. File attachments
are selected with the Windows picker, checked for existence, resolved through
`WorkspacePolicy`, and rejected if they resolve outside every approved root.
The application never opens the file to manufacture prompt content; it passes
the validated path to the selected adapter.

## Kernel setup, authentication, and models

The state-strip buttons and Settings button open the same kernel setup surface.
It reports the adapter health summary and truthful availability before enabling
profile operations.

- Browser or device-code login uses the adapter-advertised authentication
  methods and opens the adapter-provided verification URI with Windows
  `Launcher`.
- OpenCode additionally requires a provider ID and accepts the returned OAuth
  callback code through `CompleteLoginAsync`.
- API-key setup requires a provider ID, accepts an optional absolute HTTPS base
  URI (or HTTP only for a loopback development endpoint), rejects embedded URI
  credentials, and calls the adapter protected-credential flow directly.
- Logout delegates to the active adapter; for OpenCode it removes connected
  provider authentication, and for TLAH it clears the protected native
  credential.
- Model refresh calls `ListModelsAsync`. The selected real model ID is persisted
  as a nonsecret preference and supplied to new sessions and turns.

The setup surface disables authentication and model operations for profiles
that are stopped, absent, incompatible, or failed and retains the health
remediation instead of offering a nonfunctional action.

## Sessions and turns

Session refresh pages through each operational profile and orders the combined
ledger by native update time. There are no seeded rows. Local filtering only
filters the already fetched real summaries.

Creating a session requires a workspace and an operational kernel. The dialog
selects kernel, title, workspace trust, approval policy, sandbox request, and
the previously selected real model. Selecting a row calls resume before reading
history. Session actions call the host rename, fork, and archive operations;
fork and archive are exposed only when the descriptor advertises support.
Archive requires confirmation.

The composer rejects blank prompts. A new turn carries text, validated
attachment paths, model, approval mode, and sandbox mode. During a live turn it
becomes a steering composer only when the adapter reports
`CanSteerActiveTurn`; otherwise it stays unavailable until the turn finishes or
is cancelled. Cancel appears only when the adapter supports it. `Ctrl+Enter`
runs or steers and `Escape` cancels an active cancellable turn.

## Trace and inspector projection

History is read from the selected adapter through `KernelHost` and projected by
item kind. The display covers user and assistant text, reasoning, plans, tool
calls and results, commands, file changes, unified diffs, approvals, questions,
subagents, notices, and errors.

The controller reads `DurableKernelEvent` values sequentially. It discards no
event with a host sequence greater than the current cursor. UI work is enqueued
on the WinUI dispatcher in that order. Content deltas append to the matching
item identity; item snapshots replace the current projection; all other events
append an ordered rail node. Native event identities are retained by the host,
so reconnect replay is deduplicated before UI projection. Turn terminal events release the composer, refresh
the real session summary, and request the real diff. Usage events update both
the rail and the screen-reader-friendly inspector total. Diagnostic events add
only the adapter-provided canonical diagnostic fields, never vendor payloads.

The inspector derives security and feature descriptions from the selected
`KernelDescriptor`. It names the enforcement kind and owner, writable roots,
network/process restrictions, and whether the host verified the boundary. The
diff control calls `ReadDiffAsync`; an empty result is reported as such rather
than populated with invented file changes.

## Permissions and elicitation

All adapter permission requests use one shared dialog. It displays the native
reason, every structured resource impact, and only the adapter-provided choices.
Closing the dialog selects the provided denial choice. Amended JSON appears only
when the selected choice advertises `AllowsAmendedInput`; invalid JSON is not
submitted.

Before any dialog opens, the app queries `GetPendingPermissionRequestsAsync`
inside the serialized interaction gate and requires the request ID to remain
pending. Therefore historical durable events and duplicate delivery cannot
reopen a resolved or recovery-abandoned decision. Responses use the host's
durable two-phase transition (`pending` to `responding` to `resolved`, or
`indeterminate` when native delivery fails).

All adapter questions use one shared elicitation dialog. The top-level schema
type chooses a string field, number field, boolean switch, or JSON editor for
objects and arrays. Submission is serialized to a `JsonElement`; cancellation
sends an explicit cancelled response. Interaction dialogs are serialized so
concurrent kernel requests cannot overlap.
The same pending-ledger recheck uses `GetPendingElicitationRequestsAsync`, and
elicitation responses use the corresponding durable two-phase transition.

## Truthful adapter limitations

Capabilities come exclusively from each descriptor and determine whether an
action is available.

- Codex supplies its native App Server authentication, session, turn,
  permission, history, diff, and sandbox behavior. The inspector reports the
  descriptor's current enforcement result rather than assuming one.
- OpenCode has no server event replay and does not support steering an active
  turn. Its permission layer is not presented as operating-system isolation.
  Provider OAuth completion requires the user to paste the provider callback
  code. An absent or incompatible executable keeps the profile unavailable
  while the desktop application and other profiles continue.
- Native TLAH supports provider API keys but has no browser, device-code, or
  subscription login. It does not support session fork or active-turn steer.
  Its workspace and tool policy is not described as a hardened virtual machine.

Provider configuration, MCP configuration, subagents, amended tool input, event
replay, and other features remain hidden or disabled whenever the selected
adapter reports the corresponding capability as false. The desktop application
does not substitute another kernel's behavior.

## Verification

The release build gate is:

```powershell
dotnet build src\Talah.Harness.App\Talah.Harness.App.csproj -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true
```

Focused state and degraded-start tests run with:

```powershell
dotnet test tests\Talah.Harness.App.Tests\Talah.Harness.App.Tests.csproj -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true
```

The tests cover display-time secret redaction, authorization/cookie/credential
URL and nested-exception diagnostic redaction, safe error projection,
credential-free settings serialization, and OpenCode `NotInstalled` hosting
without an event-stream failure.
