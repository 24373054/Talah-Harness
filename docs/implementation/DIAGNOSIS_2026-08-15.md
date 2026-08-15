# 1.0.0 connectivity diagnosis (2026-08-15)

This is the baseline diagnosis performed before implementation.

## Repository state

- Branch `agent/talah-harness-1-0` tracks `origin/agent/talah-harness-1-0`.
- TLAH Studio submodule remains pinned at `3ff42e06dca0359ce499c490cc7879934d439150`.
- The working tree contained 463 EOL-only modified files (index LF, working
  tree CRLF). `git diff --ignore-space-at-eol` showed zero semantic changes.
  Those files were not reverted or content-edited; adding `.gitattributes`
  with `* text=auto` made the checkout clean without discarding content.
- The submodule checkout has the same EOL-only dirty state. No file inside
  `third_party/TLAH-Studio` was edited. Release validation now checks the
  submodule with `core.autocrlf=true` and still rejects semantic changes.

## Baseline build

`dotnet build Talah.Harness.sln -c Release --no-restore` succeeded with 0
warnings and 0 errors.

## Codex before fix

- Discovery and `initialize` passed with npm-installed Codex 0.147.0
  (`codex-cli 0.147.0`).
- `CodexKernelAdapter.ConfigureApiKeyAsync` rejected any provider other than
  `openai` with:
  `Pinned Codex schema supports host API-key configuration only for provider 'openai'.`
- A protocol probe showed Codex 0.147.0 accepts first-party DeepSeek provider
  config only when `model_providers.deepseek` is defined with
  `wire_api = "responses"`; `wire_api = "chat"` is rejected.
- Live path after fix reached `https://api.deepseek.com/responses`.

## OpenCode before fix

- No OpenCode binary was installed or bundled, so clean machines reported
  `OpenCode.NotInstalled`.
- Pinned GitHub asset v1.18.9 downloaded and SHA-256 matched
  `1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2`.
- `PUT /auth/deepseek` stored the key as plaintext in `auth.json`.
- The provider state is snapshotted at server start. A key submitted through
  the HTTP API after startup was not used; the server emitted
  `ProviderModelNotFoundError`/`Authentication Fails (governor)`.
- The adapter watched SSE without a directory, so session events were not
  received.

## TLAH before fix

- The adapter started correctly in-process.
- Upstream TLAH Studio 4.16.0 already supports provider `deepseek`, base URL
  `https://api.deepseek.com`, and models `deepseek-v4-flash`/`deepseek-v4-pro`.
- Credential persistence already uses DPAPI `ProtectedSecret`.
- No real DeepSeek connectivity record existed.

## DeepSeek key evidence

The provided key `sk-...af5` was used only in process-local environment
variables for live tests. The official API rejected it on every endpoint with
HTTP 401:

```json
{"error":{"message":"Authentication Fails, Your api key: ****baf5 is invalid","type":"authentication_error","param":null,"code":"invalid_request_error"}}
```

All three kernel paths were proven to reach DeepSeek. A follow-up replacement
key supplied by the rightsholder was accepted by the official API, and the
opt-in live tests then passed real model-content assertions for Codex,
OpenCode, and TLAH. No key value is recorded in this repository.
