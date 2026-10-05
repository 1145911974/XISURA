# Jiaolong Control Center protocol v1.0

```text
wire: 4-byte unsigned little-endian length + UTF-8 JSON; max frame 1,048,576 bytes.
handshake: hello/helloAck before requests; v1.0; same major is additive, unknown fields ignored; major mismatch closes after error.
message kinds: hello, helloAck, request, response, event, cancel, ping, pong.
idempotency: OperationId is created once per user intent and reused across retry; payload SHA-256 must match.
duplicate in-progress: commandInProgress; completed: replay stored CommandResult; retention: 7 days.
timeouts: connect 2 s; read-only 3 s; normal write 10 s; MUX 20 s; diagnostic export 60 s.
cancellation: effective only in Queued/Validating; Applying always finishes readback/recovery.
```

## Contract rules

- Envelope discrimination is `kind`; hardware command discrimination is `type`.
- The eight v1 envelope kinds are closed and use source-generated JSON metadata.
- `ResponseStatus.Success` requires a payload and no error; `ResponseStatus.Error` requires an error and no payload.
- Wire enums are strings. `ResponseStatus` is `success`/`error`; `CommandState` and `ErrorCode` use explicit lower-camel tokens.
- Unknown additive fields are ignored. Unknown kinds, malformed frames, and invalid shapes return `invalidFrame` or `validationFailed` with the stable `ServiceError` shape.
- `ServiceError` contains only `code`, `messageKey`, `isRetryable`, `correlationId`, and allowlisted scalar `details`; exception and stack text never crosses the boundary.
- `RecoveryRequired` means the write or rollback cannot be proven and must never be presented as restored.
