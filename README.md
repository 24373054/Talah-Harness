# Talah Harness

Talah Harness is a Windows-native desktop host for running and supervising
coding agents through one coherent workspace. Version 1.0 supports three real
kernels:

- OpenAI Codex through the official App Server protocol.
- OpenCode through its first-party Server/OpenAPI and SSE interfaces.
- The TLAH native runtime through the pinned TLAH Studio source dependency.

The application owns the desktop experience, durable event projection,
credential profiles, approvals, diagnostics, process lifecycle, and release
identity. Each kernel keeps its native session, authentication, security, and
tool semantics instead of being reduced to a lowest-common-denominator chatbot.

## Product status

The repository implements the complete `1.0.0` Windows x64 product and its
fail-closed release pipeline. Unsigned local builds are development evidence,
not public releases; the `v1.0.0` workflow requires the production signing
identity, HTTPS release origin, and final clean-machine qualification. The gate
is defined in [`docs/RELEASE_CRITERIA.md`](docs/RELEASE_CRITERIA.md). Test doubles
are allowed only inside test assemblies and are never a shipping fallback.

## Repository layout

```text
src/                       Product code
tests/                     Unit, contract, integration, and UI tests
docs/                      Architecture, security, design, and release records
schemas/                   Pinned external protocol schemas
installer/                 Windows x64 installer and release packaging
third_party/TLAH-Studio/   Read-only, commit-pinned Git submodule
```

## Supported platform

- Windows 10 build 19041 or newer; Windows 11 recommended.
- x64 processor.
- .NET 8 SDK for development.
- Visual Studio 2022 with the WinUI workload for desktop UI development.

## Licensing

Talah Harness is publicly visible source under the repository's proprietary
source license. Third-party components retain their own licenses; see
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The common-rightsholder
grant for the TLAH Studio-derived work is recorded in
[`docs/RIGHTSHOLDER_AUTHORIZATION.md`](docs/RIGHTSHOLDER_AUTHORIZATION.md).
