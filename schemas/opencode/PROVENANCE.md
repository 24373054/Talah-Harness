# OpenCode 1.18.9 schema provenance

- Upstream: `anomalyco/opencode`
- Release tag: `v1.18.9`, published 2026-07-28
- Release page: <https://github.com/anomalyco/opencode/releases/tag/v1.18.9>
- License: MIT, <https://github.com/anomalyco/opencode/blob/v1.18.9/LICENSE>
- Server documentation: <https://dev.opencode.ai/docs/server/>
- Captured endpoint: authenticated `GET /doc`
- Captured OpenAPI version: 3.1.0
- Captured file: `openapi-1.18.9.json`
- Captured file SHA-256: `f5cb443f0d160fc4b17190f64c2401f199160eb2137ce4e00ca319b99aa34005`

Windows x64 release artifacts published by the same release:

| Asset | CPU intent | SHA-256 |
|---|---|---|
| `opencode-windows-x64.zip` | Normal x64 build; preferred where its CPU requirements are available | `1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2` |
| `opencode-windows-x64-baseline.zip` | Baseline x64 build for older CPUs (no AVX2 assumption) | `0c85dc2d296417ac04dd51561985e5715f174a5ec38ae785d1f5233c3ebcc519` |

The adapter does not download `latest` and does not redistribute these binaries. The
hash constants are release-integrity metadata for a future installer owned by the host.
The schema was captured by launching the baseline binary with isolated XDG data/config/
cache roots, random Basic Auth, `--hostname 127.0.0.1 --port 0 --pure`, checking
`/global/health` reported `1.18.9`, and then saving `/doc`. Temporary binary and state
used for capture were deleted after verification.
