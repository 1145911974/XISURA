using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Service.Commands;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CommandOrchestratorTests
{
    [TestMethod]
    public async Task Success_scenario_finishes_applied()
    {
        var fixture = await CommandFixture.CreateAsync("transaction-success.json");
        var result = await fixture.ExecuteAsync();

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.IsTrue(fixture.LastKnownGoodWasUpdated);
    }

    [TestMethod]
    public async Task Write_failure_scenario_finishes_rolled_back()
    {
        var fixture = await CommandFixture.CreateAsync("write-failure.json");
        var result = await fixture.ExecuteAsync();

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(ErrorCode.HardwareWriteFailed, result.Error?.Code);
    }

    [TestMethod]
    public async Task Readback_mismatch_scenario_finishes_rolled_back()
    {
        var fixture = await CommandFixture.CreateAsync("readback-mismatch.json");
        var result = await fixture.ExecuteAsync();

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(ErrorCode.ReadBackMismatch, result.Error?.Code);
    }

    [TestMethod]
    public async Task Rollback_failure_requires_recovery_and_opens_circuit()
    {
        var fixture = await CommandFixture.CreateAsync("write-failure.json", ControlKeys.GpuFrequencyLimit);
        var result = await fixture.ExecuteAsync();

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(ErrorCode.RollbackFailed, result.Error?.Code);
        Assert.IsTrue(fixture.CircuitIsOpen);
        Assert.IsFalse(fixture.LastKnownGoodWasUpdated);
    }

    [TestMethod]
    public async Task Same_operation_and_hash_replays_but_changed_hash_is_rejected()
    {
        var fixture = await CommandFixture.CreateAsync("baseline.json");
        var first = await fixture.ExecuteWithOperationAsync(CommandFixture.OperationId, CommandFixture.PayloadA);
        var replay = await fixture.ExecuteWithOperationAsync(CommandFixture.OperationId, CommandFixture.PayloadA);
        var conflict = await fixture.ExecuteWithOperationAsync(CommandFixture.OperationId, CommandFixture.PayloadB);
        var replayAfterConflict = await fixture.ExecuteWithOperationAsync(CommandFixture.OperationId, CommandFixture.PayloadA);

        Assert.IsFalse(first.IsReplay);
        Assert.IsTrue(replay.IsReplay);
        Assert.AreEqual(ErrorCode.IdempotencyConflict, conflict.Error?.Code);
        Assert.IsTrue(replayAfterConflict.IsReplay);
    }
}

internal sealed class CommandFixture
{
    public static readonly Guid OperationId = Guid.Parse("2d0e3d31-5f82-4dc1-9ee4-f5a2d12cc8d7");
    public const int PayloadA = 100;
    public const int PayloadB = 101;

    private readonly CommandOrchestrator orchestrator;
    private readonly CircuitBreaker circuitBreaker;
    private readonly LastKnownGoodStore lastKnownGood;

    private CommandFixture(CommandOrchestrator orchestrator, CircuitBreaker circuitBreaker, LastKnownGoodStore lastKnownGood)
    {
        this.orchestrator = orchestrator;
        this.circuitBreaker = circuitBreaker;
        this.lastKnownGood = lastKnownGood;
    }

    public bool CircuitIsOpen => circuitBreaker.IsOpen;
    public bool LastKnownGoodWasUpdated => lastKnownGood.WasUpdated;

    public static async Task<CommandFixture> CreateAsync(string scenario, string key = ControlKeys.CpuTuning)
    {
        var scenarioPath = FindScenario(scenario);
        var adapter = await Jiaolong.Simulator.SimulatorHardwareAdapter.LoadAsync(scenarioPath, CancellationToken.None);
        var circuitBreaker = new CircuitBreaker();
        var lastKnownGood = new LastKnownGoodStore(Path.Combine(Path.GetTempPath(), "JiaolongCommandTests", Guid.NewGuid().ToString("N")));
        var orchestrator = new CommandOrchestrator(adapter, new CommandJournal(Path.Combine(Path.GetTempPath(), "JiaolongCommandTests", Guid.NewGuid().ToString("N"))), circuitBreaker, lastKnownGood);
        return new CommandFixture(orchestrator, circuitBreaker, lastKnownGood) { key = key };
    }

    private string key = ControlKeys.CpuTuning;

    public Task<CommandResult> ExecuteAsync() => ExecuteWithOperationAsync(Guid.NewGuid(), key == ControlKeys.GpuFrequencyLimit ? 1200 : PayloadA);

    public Task<CommandResult> ExecuteWithOperationAsync(Guid operationId, int value) =>
        orchestrator.ExecuteAsync(
            key == ControlKeys.GpuFrequencyLimit
                ? new SetGpuFrequencyLimitCommand(operationId, value, true)
                : new SetCpuTuningCommand(
                operationId,
                new CpuTuningPlan(null, value, null, null, null, null, null, null),
                true),
            CommandContext.DevelopmentWritable,
            CancellationToken.None);

    private static string FindScenario(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Jiaolong.Simulator", "Scenarios", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(name);
    }
}
