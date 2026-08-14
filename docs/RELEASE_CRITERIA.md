# Talah Harness 1.0.0 release criteria

Version 1.0.0 is a product release, not a protocol spike or partial MVP. Every
item below is a blocking gate unless explicitly removed from the 1.0 product
promise by the rightsholder.

## Functional gates

- [ ] Codex: discover/pin runtime, initialize App Server, official login and API
      key, list/create/resume/fork/archive sessions, stream items, approve/deny,
      steer/cancel, history, models, diff, logout, and crash recovery.
- [ ] OpenCode: discover/pin runtime, authenticated loopback server, provider
      login/API key configuration, list/create/resume/fork/archive sessions,
      SSE events, permission response, cancellation, history, models, diff,
      MCP state, logout, and crash recovery.
- [ ] TLAH: create/resume/cancel native runs, stream content and activity,
      preserve native tool approvals, history, checkpoint recovery, provider
      settings, and workspace restrictions.
- [ ] The user can configure the isolated Codex, OpenCode, and TLAH profiles,
      select one kernel per conversation, select a workspace, attach paths,
      inspect changes, and recover interrupted work.
- [ ] No production path returns simulated model output or silently falls back
      to a test double.

## Product-quality gates

- [ ] First-run setup, authentication, empty, loading, offline, incompatible,
      permission, failure, recovery, and update states are designed and tested.
- [ ] Light/dark/high-contrast themes, keyboard-only use, screen readers, reduced
      motion, and 200% text scaling pass review.
- [ ] Credentials, logs, raw payloads, and crash reports pass redaction tests.
- [ ] Process-tree cleanup, bounded output, timeout, cancellation, and restart
      behavior pass Windows integration tests.
- [ ] Database migrations are forward-tested and a failed migration preserves a
      recoverable backup.
- [ ] Installer install/repair/upgrade/uninstall and side-by-side TLAH Studio
      coexistence pass on a clean Windows x64 VM.
- [ ] Release binaries, installer, manifest, SBOM, notices, hashes, and signatures
      are produced from a clean checkout.
- [ ] All unit, contract, adapter integration, UI smoke, and packaging checks pass.
- [ ] No P0/P1 defects and no known credential-loss or destructive replay defect.
