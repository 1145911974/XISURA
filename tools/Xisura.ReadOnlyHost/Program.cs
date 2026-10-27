using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

if (args.Length >= 1 && args[0] is "--capture-baseline" or "--capture-current-state" or "--capture-oem-current-state")
{
    if (args.Length is not (3 or 4)) throw new ArgumentException("Expected --capture-baseline|--capture-current-state|--capture-oem-current-state journal-directory output.json [scheme-guids].");
    bool captureWithHistory = args[0] != "--capture-baseline";
    string source = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
    if (!source.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) || !output.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
        !File.Exists(Path.Combine(source, "control-execution.json")) || File.Exists(output))
        throw new IOException("Capture requires the original C: journal and a new C: output file.");
    var captureJournal = new ControlExecutionJournal(source);
    var originalCapture = await captureJournal.ReadAsync(CancellationToken.None);
    if (!captureWithHistory && (originalCapture.Unresolved.Length != 0 || originalCapture.Current?.Fields.Count > 0 ||
        File.Exists(Path.Combine(source, "client-control-session.json"))))
        throw new IOException("Existing recovery authority prevents baseline capture.");
    using var captureProvider = new WindowsHomeHardwareProvider(controlJournal: captureJournal);
    var captureReader = new ReadOnlyHomeHardwareProvider(captureProvider);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    // This one-shot getter records raw admission; it never creates a command endpoint.
    var hardware = await captureProvider.DiagnoseAsync(deadline.Token);
    var deviceKey = await captureReader.ReadDeviceRecoveryKeyAsync(hardware.Identity, deadline.Token);
    var controls = await captureReader.ReadControlBaselineAsync(deadline.Token, includeCpuAdvanced: true);
    var mode = await captureReader.ReadNativeModeEvidenceAsync(deadline.Token);
    OemCpuConfigurationSnapshot? oemConfiguration = null;
    string? oemConfigurationError = null;
    if (args[0] == "--capture-oem-current-state")
    {
        try { oemConfiguration = await captureProvider.ReadOemCpuConfigurationAsync(deadline.Token); }
        catch (IOException error) { oemConfigurationError = error.Message; }
    }
    var schemes = new Dictionary<Guid, object?>();
    foreach (var scheme in args.Length == 4 ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse) : [])
        schemes.Add(scheme, await captureReader.ReadWindowsPowerSchemeAsync(scheme, deadline.Token));
    var keyAfter = await captureReader.ReadDeviceRecoveryKeyAsync(hardware.Identity, deadline.Token);
    if (!DeviceRecoveryKey.IsValid(deviceKey) || deviceKey != keyAfter) throw new IOException("Device key missing or unstable.");
    using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(stream, new { CapturedAtUtc = DateTimeOffset.UtcNow, DeviceKey = deviceKey,
        Identity = hardware.Identity, Hardware = hardware, Controls = controls, NativeMode = mode, WindowsSchemes = schemes,
        OemConfiguration = oemConfiguration, OemConfigurationError = oemConfigurationError,
        CapabilityObservation = "Raw provider capabilities; observation grants no command authority in this capture process",
        Scope = "Read-only snapshot; unknown fields remain unknown; no session or control command dispatched; does not certify recovery",
        CaptureWithHistory = captureWithHistory,
        UnresolvedOperationIds = originalCapture.Unresolved.Select(item => item.OperationId).ToArray() },
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "baseline-captured", output, readOnly = true }));
    return;
}

if (args.Length is not (2 or 3) || args.Length == 3 && args[2] != "--pending-session-probe")
    throw new ArgumentException("Expected original journal directory, stop-file path, and optional --pending-session-probe.");
bool pendingSessionProbe = args.Length == 3;
string journalRoot = Path.GetFullPath(args[0]), stopFile = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(journalRoot, "control-execution.json"))) throw new IOException("Original control journal is required.");
if (!journalRoot.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) || !stopFile.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Live verification artifacts must stay on C:.");
var journal = new ControlExecutionJournal(journalRoot);
var original = await journal.ReadAsync(CancellationToken.None);
if (original.Unresolved.Length != 0 || original.Current?.Fields.Count > 0 || original.Current?.NativeModeOwnerClientSessionId is not null)
    throw new IOException("Original journal contains unresolved authority; live host refused.");
using var physical = new WindowsHomeHardwareProvider(controlJournal: journal);
var readOnly = new ReadOnlyHomeHardwareProvider(physical);
var runtime = new HomeServiceRuntime(readOnly, journal: journal, requireClientSessions: true);
await runtime.InitializeAsync(CancellationToken.None);
var dispatcher = new AuditReadDispatcher(new RequestDispatcher(homeRuntime: runtime), pendingSessionProbe);
await using var pipes = new NamedPipeServiceHost(dispatcher: dispatcher, homeRuntime: runtime);
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var watch = Task.Run(async () =>
{
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(lifetime.Token))
            if (File.Exists(stopFile)) { lifetime.Cancel(); break; }
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
});
Console.WriteLine(JsonSerializer.Serialize(new { kind = "ready", pid = Environment.ProcessId, readOnly = true, pendingSessionProbe }));
try { await pipes.RunAsync(lifetime.Token); }
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally { lifetime.Cancel(); await watch; }

sealed class AuditReadDispatcher(IRequestDispatcher inner, bool pendingSessionProbe) : IRequestDispatcher
{
    public async Task<ResponseEnvelope> DispatchAsync(RequestEnvelope request, ClientIdentity client, CancellationToken token)
    {
        bool allowed = request.Operation is "getCapabilities" or "getDeviceState" or "getSnapshot" or "getDiagnostics" or "subscribeTelemetry";
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "request", pid = client.ProcessId, operation = request.Operation, allowed }));
        // The visual probe simulates an unavailable lease; it never dispatches Begin or any control operation.
        if (pendingSessionProbe && request.Operation == "beginClientSession")
        {
            var pending = new CommandResult(request.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, request.OperationId, false), false) { Mutation = HardwareMutation.NoMutation };
            return new(request.ProtocolVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, request.MessageId, request.OperationId,
                ResponseStatus.Success, JsonSerializer.SerializeToElement(pending, ProtocolJsonContext.Default.CommandResult), null);
        }
        if (!allowed) return new ResponseEnvelope(
            request.ProtocolVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, request.MessageId, request.OperationId,
            ResponseStatus.Error, null, Jiaolong.Contracts.Errors.ServiceError.Create(
                Jiaolong.Contracts.Errors.ErrorCode.ReadOnlySafeMode, request.OperationId, false));
        var response = await inner.DispatchAsync(request, client, token);
        if (pendingSessionProbe && request.Operation == "getDeviceState" && response.Payload is { } payload)
        {
            var home = payload.Deserialize(ProtocolJsonContext.Default.HomeStateSnapshot)!;
            // Individual capabilities remain read-only. Only the lease precondition is simulated as Ready.
            response = response with { Payload = JsonSerializer.SerializeToElement(home with
                { Capabilities = home.Capabilities with { SupportState = DeviceSupportState.Ready } }, ProtocolJsonContext.Default.HomeStateSnapshot) };
        }
        return response;
    }
}
