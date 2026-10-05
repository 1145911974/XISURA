# Jiaolong Control Center release runbook

## Scope and gates

- MSI validation/build is software-only and may run in the development workspace.
- Install, repair, major upgrade, uninstall, forced-failure rollback, service start/stop, reboot, and real-device acceptance run only in a disposable Windows 11 x64 VM or an explicitly user-approved device gate.
- Keep the VM snapshot and network disabled. Do not launch `ControlCenterX`, Mechrevo Control Center, or any OEM GUI during validation.
- Do not run hardware writes, MUX changes, fan control, CPU/GPU tuning, BIOS changes, or reboots as part of MSI validation.

## Build evidence

```powershell
dotnet restore src/Jiaolong.Service/Jiaolong.Service.csproj -r win-x64 --locked-mode
dotnet restore src/Jiaolong.ControlCenter/Jiaolong.ControlCenter.csproj -r win-x64 --locked-mode
dotnet build installer/Jiaolong.Installer/Jiaolong.Installer.wixproj -c Release -p:Platform=x64
dotnet test tests/Jiaolong.Installer.Tests/Jiaolong.Installer.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~MsiTableTests|FullyQualifiedName~InstallLifecycleTests"

$msi = Resolve-Path installer/Jiaolong.Installer/bin/x64/Release/Jiaolong.ControlCenter.msi
Get-FileHash $msi -Algorithm SHA256
```

Record the MSI hash, commit, SDK version, package-lock state, test result/TRX path, and VM snapshot ID with the release artifact. The MSI must be per-machine x64, contain no `WixFirewallException` row or OEM binaries, and declare `JiaolongControlService` as LocalSystem delayed-auto with a restricted service SID.

Service recovery is configured by the deferred, non-impersonating `Jiaolong.Service.exe --configure-service-recovery` action after `InstallServices`. It calls the SCM API directly, verifies reset period 86400 seconds and restart delays 1000/5000/30000 ms, and intentionally replaces the MSI `MsiServiceConfigFailureActions` table because that table returned Error 1939 on clean Windows 11 x64.

## Task 21 execution record

- Locked restore, Release x64 build, and the full test suite passed: 128 passed, 0 failed, 0 skipped. TRX: `tests/**/TestResults/task21-final.trx`.
- Vulnerability audit passed: every solution project reported no vulnerable packages.
- Installer build passed with 0 errors and the existing `WIX1149` ServiceConfig warning. WiX uses an automatic ProductCode, so each rebuild may produce a different MSI hash; the disposable-VM candidate tested below is the exact artifact with SHA-256 `5EFD25239C20286E4A87936658B7528611CCC0D7D7B44880AE70FEB61D13D4C9`.
- Disposable Windows 11 x64 VM: clean install, same-version repair, default uninstall with ProgramData preservation, explicit `REMOVE_DATA=1` deletion, service crash recovery, restricted service SID, protected ProgramData ACL, no TCP listener, no Jiaolong firewall rule, and no OEM GUI process passed.
- The previous MSI artifact is unavailable; previous-to-candidate upgrade, downgrade refusal, and controlled failure-after-removal rollback are not evidenced. Reboot and missing/incompatible OEM dependency cases are also pending.
- UIA matrix, Accessibility Insights FastPass, WPR/WPA XAML Frame Analysis, and the 24-minute simulator soak are not release evidence yet. This environment has `wpr.exe` but no `wpa.exe` or Accessibility Insights executable. Do not mark production-ready; Task 22 user gates remain mandatory.

## Disposable VM lifecycle gate

Run from an elevated PowerShell in a clean Windows 11 x64 VM. Replace `$msi` with the exact artifact under test and keep logs outside the installation directory.

```powershell
$msi = (Resolve-Path .\Jiaolong.ControlCenter.msi).Path
$logRoot = Join-Path $env:TEMP 'Jiaolong-Msi-Logs'
New-Item -ItemType Directory -Force $logRoot | Out-Null

# Clean install.
msiexec.exe /i $msi /qn /l*v (Join-Path $logRoot 'install.log')
Get-Service JiaolongControlService
Test-Path "$env:ProgramFiles\Jiaolong Control Center\Jiaolong.Service.exe"

# Repair; verify the service remains present and ProgramData data is preserved.
msiexec.exe /fvomus $msi /qn /l*v (Join-Path $logRoot 'repair.log')

# Major upgrade: build or copy a higher-version MSI, then install it over this one.
msiexec.exe /i .\Jiaolong.ControlCenter.next.msi /qn /l*v (Join-Path $logRoot 'upgrade.log')
Get-Service JiaolongControlService

# Uninstall; verify the service is removed and generated ProgramData data is preserved by default.
msiexec.exe /x $msi /qn /l*v (Join-Path $logRoot 'uninstall.log')
Get-Service JiaolongControlService -ErrorAction SilentlyContinue
Test-Path "$env:ProgramData\JiaolongControlCenter"
```

For forced-failure rollback, restore the clean snapshot, interrupt the MSI transaction with a controlled VM-only failure, and verify the previous product version, service registration, files, and ProgramData state are restored. Do not improvise a production-device failure or alter hardware state.

## Rollback

Stop on any non-zero `msiexec` result, missing service, unexpected file, firewall row, OEM GUI launch, or loss of ProgramData data. Preserve the MSI logs and snapshot. Restore the VM snapshot rather than deleting files manually. A production rollback requires an explicit user decision after the VM evidence is reviewed.

## Lifecycle recovery evidence

The release record must include `ServiceStopping`, `SystemSuspend`, hibernate, shutdown, unlock, and SCM restart results. Each path must release volatile fan ownership to EC automatic control, preserve the last-known-good snapshot, and leave unknown hardware capabilities in read-only mode. No lifecycle test may run a real-device write.
