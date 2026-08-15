# Talah Harness 1.0.0 release notes (development-grade self-signed)

## What changed

- Codex 0.147.0 is bundled and configured with the first-party Codex
  `model_providers.deepseek` format (`wire_api = "responses"`,
  `env_key = "DEEPSEEK_API_KEY"`). The official DeepSeek Codex model catalog
  is pinned and embedded.
- OpenCode 1.18.9 is bundled. DeepSeek uses OpenCode's built-in provider and
  the `DEEPSEEK_API_KEY` environment binding; the previous plaintext
  `auth.json` path is no longer used for DeepSeek.
- OpenCode now keeps one SSE watcher per workspace directory so session events
  reach the host event ledger.
- TLAH continues to use the read-only TLAH Studio DeepSeek provider and its
  DPAPI-protected `ProtectedSecret` settings store.
- Codex and OpenCode profiles restart after credential changes.
- The release pipeline downloads and SHA-256 verifies both kernel binaries,
  stages them in the MSIX, emits an SBOM including those components, and
  generates SHA-256 checksums and provenance.

## DeepSeek configuration

| Kernel | Endpoint | Model | Authentication |
| --- | --- | --- | --- |
| Codex | `https://api.deepseek.com/responses` | `deepseek-v4-flash` or `deepseek-v4-pro` | `Authorization: Bearer <key>` through `DEEPSEEK_API_KEY` |
| OpenCode | `https://api.deepseek.com/chat/completions` | `deepseek/deepseek-v4-flash` or `deepseek/deepseek-v4-pro` | OpenCode built-in `deepseek` provider with `DEEPSEEK_API_KEY` |
| TLAH | `https://api.deepseek.com/v1/chat/completions` | `deepseek-v4-flash` or `deepseek-v4-pro` | Upstream DeepSeek provider with DPAPI `ProtectedSecret` |

## Verification status

- Release build, full test suite, published app smoke test, MSIX packaging,
  self-signing, timestamping, SBOM, checksums, and a host install/uninstall
  launch test passed.
- The DeepSeek key supplied for this work was rejected by the official API
  with HTTP 401 (`Authentication Fails, Your api key: ****baf5 is invalid`).
  All three live tests were run and failed only at the real model-content
  assertion. They must be rerun with a valid key before this package is
  published as final v1.0.0.
- A clean-Windows-VM test and a Microsoft Defender scan remain pending; this
  build is therefore explicitly labeled **self-signed development-grade**.

## Install

See [`docs/INSTALL_SELFSIGNED.md`](INSTALL_SELFSIGNED.md).
