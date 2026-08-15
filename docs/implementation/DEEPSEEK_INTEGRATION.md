# DeepSeek integration

Version 1.0.0 connects all three kernels to the official DeepSeek API with one
external API key. This document records the verified provider settings, the
credential boundary, and the exact tests used to prove each path.

## Official DeepSeek settings

Source: DeepSeek API documentation captured on 2026-08-15
(https://api-docs.deepseek.com/).

| Item | Value |
| --- | --- |
| OpenAI-compatible base URL | `https://api.deepseek.com` |
| Anthropic-compatible base URL | `https://api.deepseek.com/anthropic` |
| Models | `deepseek-v4-flash`, `deepseek-v4-pro` |
| Authentication | `Authorization: Bearer <api-key>` |
| Responses API | `POST /responses` is supported and is used by Codex |
| Chat Completions API | `POST /chat/completions` is supported and is used by OpenCode/TLAH |

## Credential boundary

- The API key is accepted by the desktop Kernel setup dialog.
- For Codex and OpenCode the key is encrypted with Windows DPAPI under the
  profile's `.credentials` directory. It is never written to app settings,
  `config.toml`, OpenCode `auth.json`, logs, diagnostics, test snapshots, or
  release artifacts.
- The key is injected into the kernel process only through the
  `DEEPSEEK_API_KEY` environment variable. Codex reads it through its
  first-party `model_providers.deepseek.env_key` setting; OpenCode reads it
  through its built-in DeepSeek provider `DEEPSEEK_API_KEY` environment
  binding.
- TLAH uses the upstream `ProtectedSecret` DPAPI boundary in its SQLite
  settings database.
- After Codex or OpenCode credentials change, the Harness controller stops and
  restarts that profile so the kernel process starts with the new environment.
  TLAH re-reads its protected settings without a process restart.

## Codex

The adapter now prepares an isolated host-owned `CODEX_HOME` under the profile
data directory. When a DeepSeek credential exists it writes Codex's
first-party `config.toml` model-provider configuration:

```toml
model_provider = "deepseek"
model = "deepseek-v4-flash"
preferred_auth_method = "apikey"
forced_login_method = "api"
model_reasoning_effort = "high"
model_catalog_json = '<profile-root>/codex-home/models.json'

[model_providers.deepseek]
name = "deepseek"
base_url = "https://api.deepseek.com/"
wire_api = "responses"
env_key = "DEEPSEEK_API_KEY"
requires_openai_auth = false
```

The official DeepSeek model catalog for Codex is pinned at
`src/Talah.Harness.Adapters.Codex/DeepSeek/deepseek-models-2026-08-15.json`
(embedded resource; SHA-256
`6c5b2706afe01987dd2ec5a74a3be03adddf3cd430ca7b01670ba58fa74ecc7e`).
The bundled Codex binary is pinned to 0.147.0, the exact version that
generated `schemas/codex/0.147.0`.

## OpenCode

OpenCode 1.18.9 already contains a first-party `deepseek` provider with
`deepseek-v4-flash` and `deepseek-v4-pro`. Talah Harness now starts the server
with the DPAPI-protected key in `DEEPSEEK_API_KEY` instead of using the
plaintext `PUT /auth/deepseek` JSON file. Because OpenCode snapshots provider
state when the server starts, the profile is restarted after the credential is
saved.

The adapter also runs one SSE watcher per workspace directory. This is
required because the OpenCode event endpoint is directory-scoped; before this
change the host missed all session events.

## TLAH

The read-only TLAH Studio 4.16.0 graph already contains the official DeepSeek
provider:

- provider `deepseek`
- base URL `https://api.deepseek.com`
- models `deepseek-v4-flash` and `deepseek-v4-pro`

TLAH stores the key with upstream `ProtectedSecret` (Windows DPAPI) before
SQLite persistence. No change was made inside `third_party/TLAH-Studio`.

## Verification tests

The billable live tests are opt-in and never run in the normal release test
suite without credentials:

```powershell
$env:DEEPSEEK_API_KEY='<key>'
$env:TALAH_DEEPSEEK_LIVE_TEST='1'
$env:TALAH_CODEX_PATH='<codex-0.147.0.exe>'
$env:OPENCODE_EXECUTABLE='<opencode-1.18.9.exe>'

dotnet test tests/Talah.Harness.Adapters.Codex.Tests/Talah.Harness.Adapters.Codex.Tests.csproj -c Release --filter FullyQualifiedName~DeepSeekLiveTests
dotnet test tests/Talah.Harness.Adapters.OpenCode.Tests/Talah.Harness.Adapters.OpenCode.Tests.csproj -c Release --filter FullyQualifiedName~DeepSeekLiveTests
dotnet test tests/Talah.Harness.Adapters.Tlah.Tests/Talah.Harness.Adapters.Tlah.Tests.csproj -c Release --filter FullyQualifiedName~DeepSeekLiveTests
```

Each test creates an isolated temporary workspace, configures the credential,
starts a real session, waits for a real model-generated `DEEPSEEK_OK`, cancels
the session, and checks that no kernel process leaked.

## Not tested this time

- DeepSeek Anthropic-format endpoint through OpenCode/Codex.
- Multi-turn tool use, approval flows, diff generation, and subagents against a
  billable DeepSeek account.
- OAuth/browser provider flows.
- Peak/off-peak billing and rate-limit edge cases.
