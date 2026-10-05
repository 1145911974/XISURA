using System.Text.Json;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class DiagnosticExportIpcTests
{
    [TestMethod]
    public async Task Export_and_delete_diagnostics_use_authenticated_sid_and_retained_telemetry()
    {
        var sid = "S-1-5-21-1-2-3-4";
        var exportId = Guid.NewGuid();
        var snapshot = new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 71, 35, 2400, 52);
        var state = new HomeHardwareState(
            new CapabilitySnapshot([new CapabilityDescriptor("monitoring", CapabilityState.Available, null)]),
            DeviceSupportState.Ready,
            null,
            new HardwareIdentity("board", "bios", "cpu", "gpu"),
            snapshot);
        var buffer = new TelemetryRingBuffer();
        buffer.Add(snapshot);
        var builder = new RecordingBundleBuilder(exportId);
        var dispatcher = new RequestDispatcher(
            homeRuntime: new HomeServiceRuntime(new StaticProvider(state)),
            diagnosticBundleBuilder: builder,
            telemetryRingBuffer: buffer,
            diagnosticEventReader: new StaticDiagnosticEventReader("{\"eventName\":\"service.started\"}"));
        var client = ClientIdentity.LocalInteractive(sid, 1);

        var exported = await dispatcher.DispatchAsync(Request("exportDiagnostics", exportId,
            JsonSerializer.SerializeToElement(new { operationId = exportId })), client, CancellationToken.None);

        Assert.AreEqual(ResponseStatus.Success, exported.Status);
        Assert.AreEqual(sid, builder.Request?.RequestingSid);
        StringAssert.Contains(builder.Request!.Content.TelemetryJson, "71");
        StringAssert.Contains(builder.Request.Content.LogsNdjson, "service.started");
        var bundle = exported.Payload!.Value.Deserialize<DiagnosticExport>();
        Assert.AreEqual(exportId.ToString("D") + ".zip", Path.GetFileName(bundle!.FilePath));

        var deleted = await dispatcher.DispatchAsync(Request("deleteDiagnosticExport", exportId,
            JsonSerializer.SerializeToElement(new { exportId })), client, CancellationToken.None);

        Assert.AreEqual(ResponseStatus.Success, deleted.Status);
        Assert.AreEqual(exportId, builder.DeletedExportId);

        var builds = builder.BuildCount;
        var unauthorized = await dispatcher.DispatchAsync(Request("exportDiagnostics", Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { operationId = Guid.NewGuid() })),
            ClientIdentity.Network(sid), CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Error, unauthorized.Status);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, unauthorized.Error?.Code);
        Assert.AreEqual(builds, builder.BuildCount);

        var deletes = builder.DeleteCount;
        var mismatchedDelete = await dispatcher.DispatchAsync(Request("deleteDiagnosticExport", exportId,
            JsonSerializer.SerializeToElement(new { exportId = Guid.NewGuid() })), client, CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Error, mismatchedDelete.Status);
        Assert.AreEqual(deletes, builder.DeleteCount);
    }

    private static RequestEnvelope Request(string operation, Guid operationId, JsonElement payload) => new(
        new ProtocolVersion(1, 0), Guid.NewGuid(), DateTimeOffset.UtcNow,
        operationId, operation, DateTimeOffset.UtcNow.AddMinutes(1), payload);

    private sealed class RecordingBundleBuilder(Guid exportId) : IDiagnosticBundleBuilder
    {
        public DiagnosticExportRequest? Request { get; private set; }
        public Guid? DeletedExportId { get; private set; }
        public int BuildCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<DiagnosticExport> BuildAsync(DiagnosticExportRequest request, CancellationToken cancellationToken)
        {
            BuildCount++;
            Request = request;
            return Task.FromResult(new DiagnosticExport(exportId.ToString("D") + ".zip", [], string.Empty));
        }

        public Task DeleteAsync(Guid requestedId, CancellationToken cancellationToken)
        {
            DeleteCount++;
            DeletedExportId = requestedId;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticDiagnosticEventReader(string content) : IDiagnosticEventReader
    {
        public Task<string> ReadRecentAsync(int maximumEntries, CancellationToken cancellationToken) => Task.FromResult(content);
    }

    private sealed class StaticProvider(HomeHardwareState state) : IHomeHardwareProvider
    {
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken) => Task.FromResult(state.Telemetry!);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken) => Task.FromResult(state.Controls);
        public Task<Jiaolong.Contracts.Commands.CommandResult> ExecuteAsync(
            Jiaolong.Contracts.Commands.HardwareCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
