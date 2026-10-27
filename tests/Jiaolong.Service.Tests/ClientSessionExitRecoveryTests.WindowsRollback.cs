using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static async Task<Jiaolong.Contracts.Models.ClientControlSessionHandle> SeedBeforeWindowsRollback(Fixture fixture, bool owned)
    {
        var handle = await fixture.Begin(null);
        if (owned)
        {
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), SchemePendingPlan(Fixture.OtherScheme, 3800, 60), true), handle)).State);
            Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), SchemePendingPlan(Fixture.Scheme, 4400, 70), true), handle)).State);
        }
        return handle;
    }

    private static SetCpuTuningBatchCommand WindowsRollbackCommand() => new(Guid.NewGuid(),
        [SchemePendingPlan(Fixture.OtherScheme, 3700, 40), SchemePendingPlan(Fixture.Scheme, 4300, 30)], true);

    [TestMethod]
    public async Task Windows_rollback_confirmation_restores_previous_index_ownership_then_exits_to_first_baselines()
    {
        using var fixture = new Fixture();
        var first = fixture.Provider.ReadScheme(Fixture.Scheme);
        var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        var handle = await SeedBeforeWindowsRollback(fixture, true);
        fixture.Provider.RollbackNextCpuWrite = true;
        Assert.AreEqual(CommandState.RolledBack, (await fixture.Write(WindowsRollbackCommand(), handle)).State);
        Assert.AreEqual(4400, fixture.Provider.ReadScheme(Fixture.Scheme).AcMaxFrequencyMhz);
        Assert.AreEqual(3800, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz);
        var scopes = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json")))!["schemes"]!;
        Assert.AreEqual(4400, scopes[Fixture.Scheme.ToString()]!["writes"]!["AcMaxFrequencyMhz"]!["lastWritten"]!.GetValue<int>());
        Assert.AreEqual(3800, scopes[Fixture.OtherScheme.ToString()]!["writes"]!["AcMaxFrequencyMhz"]!["lastWritten"]!.GetValue<int>());
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(other, fixture.Provider.ReadScheme(Fixture.OtherScheme));
    }

    [TestMethod]
    public async Task Windows_rollback_confirmation_without_previous_ownership_keeps_exact_capture_and_exit_works()
    {
        using var fixture = new Fixture();
        var first = fixture.Provider.ReadScheme(Fixture.Scheme);
        var other = fixture.Provider.ReadScheme(Fixture.OtherScheme);
        var handle = await SeedBeforeWindowsRollback(fixture, false);
        fixture.Provider.RollbackNextCpuWrite = true;
        var command = WindowsRollbackCommand();
        Assert.AreEqual(CommandState.RolledBack, (await fixture.Write(command, handle)).State);
        var scope = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json")))!;
        Assert.IsTrue(scope["schemes"]!.AsObject().All(pair => pair.Value!["writes"]!.AsObject().Count == 0));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, "confirmed-windows-rollbacks", command.OperationId.ToString("N") + ".json")), "Retain the original attempt before rewinding metadata.");
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(first, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(other, fixture.Provider.ReadScheme(Fixture.OtherScheme));
    }

    [TestMethod]
    public async Task Windows_rollback_confirmation_rejects_bad_actual_read_or_changed_scope_without_clearing_parent()
    {
        foreach (string fault in new[] { "actual", "read", "scope" })
        {
            using var fixture = new Fixture();
            var handle = await SeedBeforeWindowsRollback(fixture, true);
            fixture.Provider.RollbackNextCpuWrite = true;
            fixture.Provider.AfterCpuRollback = () =>
            {
                if (fault == "actual") fixture.Provider.ExternalSchemeIndex(Fixture.OtherScheme, WindowsCpuSetting.AcMaxFrequencyMhz, 3512);
                if (fault == "read") fixture.Provider.FailNextWindowsSchemeRead = true;
                if (fault == "scope")
                {
                    string path = Path.Combine(fixture.Root, "client-windows-" + handle.SessionId.ToString("N") + ".json");
                    var node = JsonNode.Parse(File.ReadAllText(path))!;
                    node["pendingWrite"]!["initialScheme"] = Fixture.OtherScheme;
                    File.WriteAllText(path, node.ToJsonString());
                }
            };
            var command = WindowsRollbackCommand();
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(command, handle)).State);
            var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
            Assert.AreEqual(command.OperationId, saved.Unresolved.Single().OperationId);
            Assert.AreEqual(command.OperationId, saved.Current!.Fields["performance.acMaxFrequencyMhz"].OwnerOperationId);
            if (fault == "actual") Assert.AreEqual(3512, fixture.Provider.ReadScheme(Fixture.OtherScheme).AcMaxFrequencyMhz);
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "confirmed-windows-rollbacks")));
        }
    }
}
