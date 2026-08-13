# Update, repair, uninstall, and recovery

Talah Harness uses Windows MSIX deployment. Version `1.0.0.0` upgrades an older
package with the same `BeijingKeEntropy.TalahHarness` identity and certificate;
Windows replaces package files transactionally. Reinstalling the same signed
MSIX provides repair semantics. A newer version is not downgraded implicitly.
An authorized downgrade must be a deliberate support action:

```powershell
Add-AppxPackage -Path .\Talah-Harness_1.0.0.0_x64.msix -ForceUpdateFromAnyVersion
```

The application stores product state under `%LOCALAPPDATA%\Talah Harness`, outside
the MSIX install directory. Upgrade, repair, rollback, and uninstall procedures
must preserve that directory. User-selected workspaces may be anywhere on disk
and are never installer-owned. The original TLAH Studio has a different package/
application identity and data roots; Talah Harness install and removal procedures
must not modify it.

## Install and repair

For a production certificate trusted by Windows, open the signed `.msix`, or run:

```powershell
Add-AppxPackage -Path .\Talah-Harness_1.0.0.0_x64.msix
```

For a development build, first trust its `.cer` in the current user's
`TrustedPeople` store. Do not bypass signature checks and do not trust a
development certificate on production machines.

To repair, close Talah Harness and reinstall the exact trusted MSIX. Windows
Settings > Apps > Installed apps > Talah Harness > Advanced options > Repair is
also acceptable. Do not choose Reset: Reset can clear package-managed state.
The release does not provide a custom cleanup action and never deletes
`%LOCALAPPDATA%\Talah Harness`.

## Updates

A signed release may include `Talah-Harness.appinstaller`. Its URIs must point to
the actual HTTPS-hosted metadata and MSIX. Windows App Installer checks on launch
at most once per 24 hours and shows a user prompt; activation is not blocked.
There is no silent in-app updater, no background service, and no claim that a raw
MSIX self-updates. If App Installer is unavailable or policy-disabled, download
the new signed MSIX and explicitly install it.

The companion `update-manifest.json` records identity, publisher, version,
architecture, minimum OS, byte length, HTTPS package URI, SHA-256, and signing
state. A support workflow can stage and verify a download without installing it:

```powershell
./tools/release/Test-UpdatePackage.ps1 `
  -ManifestUri 'https://downloads.example.invalid/talah-harness/1.0.0/update-manifest.json' `
  -DownloadDirectory "$env:TEMP\TalahHarnessUpdate"
```

Replace the URI with the real release origin. The verifier rejects non-HTTPS
URLs, unsigned declarations, malformed hashes, hash mismatch, invalid
Authenticode, and publisher mismatch. It downloads to `.partial`, deletes a
failed partial file, and atomically renames only after verification. Installation
remains an explicit user step.

## Failed update or rollback

MSIX deployment is transactional; if deployment fails, keep the installed
version, preserve the error text, and verify free disk space, OS build, certificate
trust, package architecture, and `SHA256SUMS`. Delete only the failed `.partial`
download, never application data or a workspace. Retry with the same verified
package or obtain a fresh copy over HTTPS.

If a new version launches but data migration fails:

1. Exit Talah Harness and make a normal file backup of
   `%LOCALAPPDATA%\Talah Harness` to a user-chosen safe directory.
2. Preserve any SQLite migration backup files already created by the app.
3. Reinstall the last known-good signed MSIX with
   `-ForceUpdateFromAnyVersion` only after support confirms schema compatibility.
4. If the old app rejects a newer database, restore the pre-migration database
   backup while the app is closed. Never overwrite the only copy.

Do not roll back by deleting files from `C:\Program Files\WindowsApps`, changing
package ownership, or removing TLAH Studio.

## Uninstall

Use Windows Settings or:

```powershell
Get-AppxPackage -Name BeijingKeEntropy.TalahHarness | Remove-AppxPackage
```

This removes the package for the current user. It does not request deletion of
`%LOCALAPPDATA%\Talah Harness`, user workspaces, external Codex/OpenCode state, or
the original TLAH Studio. If the user later requests a full data purge, treat it
as a separate manual operation: back up first, resolve the exact path, confirm it
is `%LOCALAPPDATA%\Talah Harness`, check for reparse points, and obtain explicit
confirmation. No release script automates that destructive action.
