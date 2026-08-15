# Third-party notices

This file is the human-readable index for third-party code and protocols used
by Talah Harness. The release pipeline also produces a machine-readable SBOM
and copies the exact license texts distributed with each binary dependency.

| Component | Integration | License / terms | Source |
|---|---|---|---|
| TLAH Studio | Read-only Git submodule pinned at `3ff42e06dca0359ce499c490cc7879934d439150`; native-runtime behavior and authorized design reuse | TLAH Studio Proprietary Source License; authorized for this product by the common rightsholder and recorded in `docs/RIGHTSHOLDER_AUTHORIZATION.md`; this notice does not relicense it | https://github.com/24373054/TLAH-Studio |
| OpenAI Codex CLI 0.147.0 | Bundled win32-x64 `codex.exe` from the official `@openai/codex` npm package (SHA-256 of npm package and executable recorded in the release SBOM); generated protocol schemas are pinned under `schemas/codex/0.147.0` | Apache-2.0 for the open-source Codex CLI; OpenAI service terms apply to service use | https://github.com/openai/codex |
| OpenCode 1.18.9 | Bundled windows-x64 `opencode.exe` from the official GitHub release; release asset SHA-256 is `1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2` and the OpenAPI schema is pinned under `schemas/opencode` | MIT | https://github.com/anomalyco/opencode/releases/tag/v1.18.9 |
| Agent Client Protocol | Optional protocol compatibility layer | Apache-2.0 | https://github.com/agentclientprotocol/agent-client-protocol |
| Microsoft .NET 8 and Windows App SDK | Self-contained runtime and UI framework redistributed inside the MSIX | MIT and component-specific Microsoft notices represented in the generated CycloneDX SBOM | https://github.com/dotnet/runtime |
| NuGet dependencies | Direct and transitive managed/native libraries copied by publish | Package-specific licenses recorded by NuGet metadata in `metadata/sbom.cdx.json`; exact resolved versions are in `metadata/nuget-dependencies.json` | https://www.nuget.org/ |
| Talah Harness brand assets | Generated first-party images under `src/Talah.Harness.App/Assets`; no third-party stock art is embedded | Covered by the repository's proprietary license; generation does not grant third-party rights | repository source |

Every release copies this file and the proprietary `LICENSE`, emits a CycloneDX
SBOM with license metadata, and records the exact resolved NuGet dependency
inventory. The release owner must review packages whose registry metadata omits
a license before publishing. The pinned Codex and OpenCode executables are
verified by SHA-256 before they are copied into the MSIX payload.

