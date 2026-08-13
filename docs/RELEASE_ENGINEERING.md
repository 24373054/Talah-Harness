# Talah Harness 1.0 release engineering

Talah Harness 1.0.0 ships as an x64 MSIX with identity
`BeijingKeEntropy.TalahHarness`, package version `1.0.0.0`, and minimum Windows
version `10.0.19041.0`. The package wraps the self-contained WinUI publish output;
it does not change or claim the original TLAH Studio identity. Windows therefore
allows the two products to coexist.

## Clean-machine prerequisites

- Windows 10 version 2004 (build 19041) or later, x64.
- Git for Windows with long paths enabled and submodule access to
  `https://github.com/24373054/TLAH-Studio.git`.
- .NET SDK `8.0.407` or a later .NET 8 feature band allowed by `global.json`.
- Windows 10/11 SDK containing x64 `makeappx.exe` and `signtool.exe`. The GitHub
  `windows-latest` image supplies these tools.
- Internet access to NuGet.org for restore and the pinned CycloneDX .NET tool
  `6.2.0` on the first build.
- An interactive Windows user session for the bounded WinUI launch smoke test.
- For a public build, a code-signing certificate whose subject exactly equals the
  MSIX Publisher, its password, and access to an RFC 3161 timestamp service.

From a fresh checkout:

```powershell
git clone --recurse-submodules https://github.com/24373054/Talah-Harness.git
Set-Location Talah-Harness
git submodule update --init --recursive
pwsh -NoProfile -File ./tools/release/Build-Release.ps1
```

That command restores, builds with warnings as errors, tests, publishes a
self-contained `win-x64` app, launches it in a bounded smoke test that requires a
real `Talah Harness` main window and graceful shutdown, packages an unsigned
development MSIX, produces a CycloneDX SBOM and NuGet inventory, writes
update/provenance JSON, computes
SHA-256 checksums, and revalidates every artifact. It only cleans the validated
repository-local `build/release` and `build/release-staging` directories.

The unsigned file is deliberately named
`Talah-Harness_1.0.0.0_x64_unsigned-development.msix`. It is test evidence, not a
public release. For a local developer certificate, create the key outside the
repository and trust only the exported public `.cer` on the test machine:

```powershell
$password = Read-Host 'PFX password' -AsSecureString
./tools/release/New-DeveloperCertificate.ps1 `
  -OutputPath "$env:USERPROFILE\TalahHarnessDev\talah-harness-dev.pfx" `
  -Password $password
Import-Certificate `
  -FilePath "$env:USERPROFILE\TalahHarnessDev\talah-harness-dev.cer" `
  -CertStoreLocation Cert:\CurrentUser\TrustedPeople
./tools/release/Build-Release.ps1 `
  -Publisher 'CN=Talah Harness Development' `
  -CertificatePath "$env:USERPROFILE\TalahHarnessDev\talah-harness-dev.pfx" `
  -CertificatePassword $password
```

Never distribute or commit that developer PFX. A production build uses the real
publisher subject and certificate:

```powershell
$password = Read-Host 'Production PFX password' -AsSecureString
./tools/release/Build-Release.ps1 `
  -Publisher 'CN=<certificate subject>' `
  -CertificatePath 'D:\secure\talah-harness-release.pfx' `
  -CertificatePassword $password `
  -TimestampUrl 'http://timestamp.digicert.com' `
  -UpdateBaseUri 'https://downloads.example.invalid/talah-harness/1.0.0'
```

Replace the final URI with the actual HTTPS distribution origin. The script
refuses remote update metadata for an unsigned package. It also opens the PFX
ephemerally and verifies that its subject exactly matches the manifest Publisher.

## Release artifacts

The reproducible layout under `build/release` is:

```text
build/release/
  packages/Talah-Harness_1.0.0.0_x64[_unsigned-development].msix
  metadata/sbom.cdx.json
  metadata/nuget-dependencies.json
  metadata/THIRD-PARTY-NOTICES.md
  metadata/LICENSE
  update-manifest.json
  provenance.json
  Talah-Harness.appinstaller       # signed build with UpdateBaseUri only
  SHA256SUMS
```

Per-assembly TRX test evidence is kept under
`build/release-staging/test-results`; it is CI evidence rather than a distributed
release payload, because durations and machine details are inherently
environment-specific.

`provenance.json` binds the source commit, source-derived timestamp, TLAH Studio
submodule pin, runtime identifier, package hash, SBOM, and signing state.
`SHA256SUMS` covers every release artifact other than itself. The CycloneDX tool
version is pinned in `Release.Common.ps1`; non-deterministic SBOM serial numbers
are removed and its timestamp is derived from the source commit. MSIX inputs have
their timestamps normalized to the same source date before packaging, and the
MSIX ZIP headers are normalized after `makeappx`. Identical unsigned inputs are
therefore byte-reproducible. Production Authenticode/RFC 3161 timestamp bytes are
necessarily unique; the signed result is instead bound by the published checksum
and provenance record.

No Codex CLI or OpenCode executable is included. Codex 0.147.0 protocol schemas
and OpenCode 1.18.9 schema/hash provenance remain source inputs. The read-only
TLAH Studio submodule must stay at
`3ff42e06dca0359ce499c490cc7879934d439150` and clean. The generated brand assets
are first-party material under the repository's existing proprietary license;
the release process never changes that license assumption.

## GitHub release configuration

Pull requests run the complete unsigned package pipeline. The workflow checks
out submodules recursively and uploads the clearly labeled development artifact.
A tag exactly equal to `v1.0.0` invokes the signed release workflow. Configure:

- secret `MSIX_SIGNING_PFX_BASE64`: Base64 of the production PFX;
- secret `MSIX_SIGNING_PASSWORD`: production PFX password;
- secret `MSIX_PUBLISHER`: exact X.500 certificate subject;
- variable `RELEASE_BASE_URI`: final HTTPS directory containing the MSIX and
  `Talah-Harness.appinstaller`;
- optional variable `MSIX_TIMESTAMP_URL`: RFC 3161 timestamp URL (defaults to
  DigiCert when absent).

The tag job fails closed if signing inputs or the HTTPS release URI are absent.
Secrets are placed in the runner environment only for the signing job, are never
printed by repository scripts, and the temporary PFX lives under `RUNNER_TEMP`.
All third-party Actions are pinned by full commit SHA. Release workflow permission
is limited to `contents: write`; PR CI has only `contents: read`.

Before publishing, compare the tag to `src/Directory.Build.props`, review the
generated SBOM/license inventory, verify `SHA256SUMS`, install on a clean x64 VM,
exercise launch/repair/upgrade/uninstall, and confirm TLAH Studio plus user
workspaces remain untouched. Code-signing keys, publisher identity registration,
HTTPS hosting, and timestamp availability are external release-owner duties.
