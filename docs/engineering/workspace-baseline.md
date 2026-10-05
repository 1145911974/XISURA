# Jiaolong Control Center workspace baseline

As of 2026-08-21, Task 0 baseline:

| Item | Recorded value |
|---|---|
| Branch | `codex/jiaolong-control-center` |
| Baseline commit | `1ce3b9f45fe99bddc6cb3842a7717fdc1843e871` (`docs: resolve implementation plan blockers`) |
| Plan reference | `8219200 docs: define Jiaolong control center design` was the plan's original expected parent; the plan was repaired before implementation because its protocol, rollback, and signing sections were incomplete. |
| .NET SDKs | `10.0.302` and `10.0.400` installed side by side; Task 1 must create `global.json` selecting `10.0.302` with `rollForward: latestPatch`. |
| Visual Studio | Community 2026 `18.9.1`, complete and launchable; managed desktop, Universal Windows, and Windows App SDK C# components configured. |
| WinUI templates | Official `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` `0.0.6-alpha`; `dotnet new list winui` lists the C# `winui` template. |
| Project state | No solution or product project was generated before this record. |
| WiX 7 OSMF EULA | Reviewed official WiX OSMF terms; user explicitly authorized acceptance on 2026-08-21. Project-local `<AcceptEula>wix7</AcceptEula>` is required; no machine-global EULA state is changed. |

## Dirty-workspace protection

- Existing untracked research files are user-owned evidence and remain untouched.
- Do not clean, move, copy, overwrite, or bulk-stage `app/`, `mechrevo-controlcenter-analysis/`, `ControlCenterX_5.56.60.26_Mechrevo/`, or any existing ZIP, EXE, or PNG.
- Every commit must stage exact intended paths only; never use `git add -A`, `git add .`, or a broad directory stage.
- Before each commit, verify the protected research paths with `git diff -- app mechrevo-controlcenter-analysis ControlCenterX_5.56.60.26_Mechrevo`.
- This document records the environment only; it does not authorize real-device writes, WMI/EC probing, MUX changes, fan changes, CPU/GPU tuning, reboot, install/uninstall, or acceptance testing.

## Evidence-strength policy

| Evidence class | Strength | Allowed implementation use | Prohibited use |
|---|---|---|---|
| V39 local evidence | Direct read evidence for `MICommonInterface`, mode/MUX/Fn/touchpad/dual-fan/Logo/keyboard/CPU-power semantics | Read-only capability mapping, simulator fixtures, and UI states | Treating an unverified write or vendor interface as available |
| Same-family principle evidence | `mech-forza-control` and `REPORT.md` 14XA EC addresses | Research annotation only; no V39 capability claim | Copying addresses into an `MRID6-23` list or issuing EC/WMI calls |
| Official current-package evidence | Page semantics, state flow, and dependency identification | UI copy, state modelling, and dependency notes | Decompiling or executing reverse-engineered binaries |
| Unknown action | No V39 read-back evidence | `Unavailable` and read-only UI state | Any call, guessed address, guessed WMI class, or write path |

## Hardware safety boundary

The implementation must not scan, guess, or call unknown WMI/EC interfaces. Any capability without V39 read-back evidence is `Unavailable`. Real-device gates remain user-executed and are outside this implementation session.
