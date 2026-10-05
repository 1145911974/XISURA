# Jiaolong hardware evidence register

Status at Task 0, 2026-08-21: research assets are preserved; no live WMI/EC probe or hardware write was executed by this implementation session.

| Capability/evidence | Classification | Product rule |
|---|---|---|
| V39 本机已验证：MICommonInterface 读取；模式/MUX/Fn/触控板/双风扇/Logo/键盘/CPU 功耗语义映射。 | V39 direct evidence | May define read-only capability metadata and simulator fixtures. Any write still requires the simulator gate and user real-device gate. |
| 仅同系原理证据：`mech-forza-control` 与 `REPORT.md` 的 14XA EC 地址；禁止进入 MRID6-23 清单。 | Same-family principle evidence | Research-only. Do not copy addresses, infer V39 support, or issue EC/WMI calls. |
| 官方当前包证据：仅用于页面语义、状态流和依赖识别；不得执行反编译二进制。 | Official package evidence | May inform UI wording, state flow, and dependency notes only. |
| 未知：没有 V39 回读证据的动作；实现状态必须为 `Unavailable`。 | Unknown | No interface call, address guess, capability claim, or write operation. |

## Task 18 V39 semantic binding record

The following research files are preserved as evidence only; this task did not execute their WMI worker or copy their raw payloads into the signed manifest:

| Source | SHA-256 | Observed semantic binding | Implementation state |
|---|---|---|---|
| `app/backend/ps_worker.ps1` | `44FB81666CFD28D3E83753768DF9DCC551268A81632946977E7C2A2135E11F0F` | `MICommonInterface` / `MiInterface`; read type `250`; fixed 32-byte request with payload limit 28; read method names 8, 9, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23 | `VerificationRequired`; no runtime WMI call |
| `app/backend/server.py` | `5D94F226792B475F7C44D9535D316BD8126764836CBEE7C068FCD3A9BFEB4E70` | semantic setters use write type `251`: performance 8, MUX 9, Fn 11, touchpad 12, A-face logo 15, keyboard mode/color/brightness 16/17/18, CPU power 23 | `VerificationRequired`; no capability enabled |

The research names `MICommonInterface`, `root\\WMI`, `ACPI\\PNP0C14\\MIFS_0`, and types 250/251 are not sufficient to unlock a device. Only a signed manifest instance with exact fingerprint, dependency, readback, and acceptance evidence may authorize a write. The committed MRID6-23 V39 manifest remains empty-capability `ReadOnlySafeMode`.

## Task 3 signed-manifest record

- Public signing certificate DER SHA-256: `4B6CE0846DBD2BD2DF97E507A81CF0ADEAF8E765A37A156E5B9C35814C3386B9`.
- V39 manifest SHA-256: `440A493360E22043EADBC5A6FD3D62D86C019A976C28495594C51DCF7C231F57`.
- The committed V39 manifest deliberately has an empty exact GPU PNP-ID list and no verified dependency tuple; its signed result is `ReadOnlySafeMode`.
- The `.cer` is public DER only. The RSA-3072 CurrentUser CNG private key is non-exportable and is not present in the repository.

## Forbidden unbounded 14XA table

| Source or operation | Status | Required handling |
|---|---|---|
| Arbitrary 14XA EC address copied from same-family research | Forbidden | Keep outside the V39 capability matrix and `MRID6-23` list. |
| Unbounded EC address scan | Forbidden | Do not implement or run it. |
| Guessed WMI class, method, or write payload | Forbidden | Keep the capability `Unavailable`; expose only a read-only explanation. |
| Real-device MUX, fan, CPU/GPU tuning, reboot, install/uninstall, or 24-hour acceptance | User gate | Luna/user must execute the exact gate commands after simulator and software checks pass. |

## Task 20 tuning evidence boundary

No additional V39 tuning binding was found or promoted. The manifest remains empty-capability and `ReadOnlySafeMode`: CPU power/frequency, core-count, CO, Windows power writes, and GPU limit writes remain `Unavailable`. The software layer only validates DTO ranges, uses transport abstractions for future verified bindings, and performs snapshot/readback/rollback in simulator tests. No NVIDIA executable, powrprof call, WMI method, EC address, memory-clock, power-limit, or VBIOS operation is enabled by this task.

No migration from an unavailable capability to a writable capability is allowed without a new signed manifest and exact acceptance evidence.
