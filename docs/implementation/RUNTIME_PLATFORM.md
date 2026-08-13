# Runtime platform implementation

This implementation supplies the Windows sidecar runtime and canonical SQLite
store for Talah Harness 1.0.0.

## Runtime

- `ProcessSupervisor` launches an explicit executable with
  `ProcessStartInfo.ArgumentList`, an absolute working directory, a caller-owned
  minimal environment, redirected input/error, optional protocol output, and no
  visible window. Each launch is assigned to a Windows Job Object configured
  with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
- Shutdown may write a caller-supplied protocol message, closes standard input,
  waits for the bounded shutdown interval, and terminates the Job Object when
  the child does not exit. Async disposal completes stream pumps and closes the
  Job Object deterministically.
- Protocol lines and stderr are bounded. Oversize lines and discarded stderr
  characters are explicitly counted; protocol queue overflow is counted on the
  next delivered message. Startup, initialization, idle, turn, and shutdown
  operations share typed timeout helpers. Health, exit/crash, and restart state
  are observable.
- `ProfilePathProvider` validates adapter/profile identifiers and resolves them
  beneath a configurable product root. Windows directory ACL hardening grants
  the current user and Local System full control where the filesystem permits.
- `DpapiCredentialStore` uses DPAPI CurrentUser protection and atomic encrypted
  file replacement. `ProfileMetadataStore` deliberately has no secret or
  environment field, so profile JSON cannot serialize credentials.
  `SecretRedactor` removes registered secret values from diagnostic text.

## Persistence

- `HarnessDatabase` creates and migrates SQLite databases to schema v1. The
  connection policy enables foreign keys, WAL, a busy timeout, and normal WAL
  synchronization. Initialization runs integrity checks before and after
  migration and rejects databases from newer schema versions.
- A consistent SQLite backup is created before migrating any existing v0
  database. Migration runs transactionally and reports source/target versions
  plus backup availability on failure.
- Schema v1 contains adapter profiles, sessions, turns, items, canonical events,
  checkpoints/recovery markers, approvals, and an explicit schema-version
  ledger. Repository APIs cover session/item/turn writes, profile/checkpoint/
  approval writes, and paged session/history/event reads.
- Canonical event insertion is transactional. SQLite assigns the monotonic host
  sequence, while a unique adapter/profile/native-event identity makes replay
  idempotent. A duplicate returns the original host sequence without adding an
  event.

## Validation and limitations

The owned test projects exercise Windows process-tree termination, bounded
buffers, cancellation, DPAPI ciphertext and round-trip behavior, redaction,
profile path validation and metadata CRUD, database creation/integrity/WAL,
pre-migration backup, duplicate replay, paging, canonical entity writes, and
parallel writers.

The runtime target is intentionally `net8.0-windows`; Job Objects and DPAPI are
not emulated on other operating systems. ACL hardening is best effort because a
filesystem or host policy may deny ACL replacement; path containment and DPAPI
protection remain enforced. The stable `KernelEvent` contract has no native
event identity separate from its adapter sequence, so `AppendEventAsync`
accepts that identity as an explicit parameter rather than changing contracts.
