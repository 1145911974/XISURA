# Jiaolong Control Center security model

## Trust boundaries

| Boundary | Threat | Control |
|---|---|---|
| UI → service pipe | Forged client, oversized frame, replay | Authenticated local IPC, bounded frames, protocol versioning, unique `OperationId`, replay payload equality check, fail closed. |
| Manifest → service | Tampering, duplicate signer, expired signer, unpinned signer | Strict UTF-8/schema validation; detached CMS; exactly one signer; SHA-256 or stronger; current validity; signer DER must equal the embedded certificate; embedded DER hash must equal the pinned trust anchor; no machine-root validation. |
| Signing key → manifest | Key loss, misuse, repudiation, rotation | Current-user CNG RSA-3072 key, non-exportable; repository contains only DER public certificate; signing tool compares exact DER and writes atomically. Rotation requires a separately reviewed overlap commit. |
| WMI/OEM response → adapter | Malicious or malformed response, guessed interface | No raw WMI/EC method/address/payload crosses the abstraction; unknown evidence is `Unavailable`; no scan or write is issued without an exact signed manifest and validated write. |
| Config/log filesystem | Symlink/reparse escape, disclosure, corruption | Fixed paths, reparse-point rejection, bounded input, atomic writes, no secrets or private keys in logs. |
| Diagnostic export | Personal or machine data disclosure | Explicit user action, allowlisted fields, redaction, bounded size, no raw payloads or private key material. |
| Foreground-app advisory input | Spoofing or unsafe automation | Advisory-only input; allowlist and debounce; it cannot grant hardware capability or bypass confirmation. |

## Required abuse cases

- Forged UI client: pipe authentication and service-side authorization reject it.
- Oversized frame: bounded decoder rejects before allocation or dispatch.
- Reused `OperationId` with changed payload: replay cache rejects the conflicting payload.
- Tampered, invalid, unsigned, duplicate-signer, expired, or unpinned manifest: resolver returns `ReadOnlySafeMode`.
- Signing-key loss or rotation: no fallback certificate is trusted; a reviewed overlap commit must add the new public anchor and signatures before removing the old one.
- Reparse-point configuration path: reject rather than follow the path.
- Malicious WMI/OEM output: validate typed values and bounds; malformed or unknown data becomes `Unavailable`.
- Conflict process: do not initialize a writer while a conflicting owner is present.
- Cancellation during apply: stop at the next safe boundary, verify readback, and enter recovery-required state if the result is uncertain.

## Reachability review

- No TCP listeners, firewall rules, or network service endpoints are created by the service or installer.
- Diagnostics are local, explicitly user initiated, bounded, and redacted; they contain no private keys, raw WMI/EC payloads, or unredacted machine identifiers.
- The official OEM dependency is not bundled, launched, downloaded, or granted capability by the installer.

## Hardware safety rule

The compatibility manifest is an allowlist, not a discovery mechanism. `MRID6-23` plus `MRID6_23_P_V39` alone is insufficient. Writable mode additionally requires exact CPU/GPU evidence, a non-empty exact GPU PNP ID, a verified OEM provider tuple, verified dependency identities, and a valid signature from the pinned certificate. Missing or changed evidence remains read-only. No real-device MUX, fan, CPU/GPU tuning, reboot, or unknown WMI/EC operation is performed by this layer.
