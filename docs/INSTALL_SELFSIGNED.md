# Installing the self-signed 1.0.0 MSIX

The v1.0.0 installer is a **self-signed development-grade release**. It is
timestamped with Authenticode, but it is not signed by a publicly trusted
certificate authority and it has no production HTTPS update channel.

## Download and verify

1. Download `Talah-Harness_1.0.0.0_x64.msix`.
2. Download `Talah-Harness-1.0.0-self-signed.cer`.
3. Download `SHA256SUMS` and verify the MSIX and certificate before running
   anything:

   ```powershell
   Get-FileHash .\Talah-Harness_1.0.0.0_x64.msix -Algorithm SHA256
   Get-Content .\SHA256SUMS
   ```

4. Compare the MSIX hash to the `packages/Talah-Harness_1.0.0.0_x64.msix`
   line in `SHA256SUMS`.

## Trust the development certificate

Open an elevated PowerShell and run:

```powershell
certutil -addstore Root .\Talah-Harness-1.0.0-self-signed.cer
Add-AppxPackage .\Talah-Harness_1.0.0.0_x64.msix
```

Then launch **Talah Harness** from Start. If Windows SmartScreen appears,
choose **More info -> Run anyway**. This is the normal workflow for an
unsigned/self-signed desktop app. Never disable Microsoft Defender,
SmartScreen, or any other security feature.

## First run and DeepSeek setup

1. Choose a workspace.
2. Select a kernel runway item (CODEX, OPENCODE, or TLAH).
3. Choose **Kernel setup** from the diagnostics/settings area.
4. Keep provider `deepseek`, leave the base URI empty, and enter the DeepSeek
   API key.
5. Save the key. Codex and OpenCode restart automatically; TLAH re-reads the
   protected setting.

The key is stored with Windows DPAPI and is injected only into kernel process
environments. It is not written to app settings, `config.toml`, OpenCode
`auth.json`, logs, or release artifacts.

## Uninstall

```powershell
Get-AppxPackage BeijingKeEntropy.TalahHarness | Remove-AppxPackage
certutil -delstore Root 'Talah Harness Development'
```

## Limitations

- No production code-signing certificate is available for this build.
- No production HTTPS update channel is configured.
- The clean-VM install qualification and Defender scan must still be run on a
  machine where Microsoft Defender is enabled.
