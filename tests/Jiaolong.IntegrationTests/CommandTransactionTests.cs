using Jiaolong.Contracts.Commands;
using Jiaolong.Service.Commands;
using Jiaolong.Service.Storage;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class CommandTransactionTests
{
    [TestMethod]
    public async Task Simulator_command_transaction_produces_a_terminal_result()
    {
        var scenarioPath = FindScenario("transaction-success.json");
        var adapter = await Jiaolong.Simulator.SimulatorHardwareAdapter.LoadAsync(scenarioPath, CancellationToken.None);
        var result = await new CommandOrchestrator(
            adapter,
            new CommandJournal(Path.Combine(Path.GetTempPath(), "JiaolongIntegration", Guid.NewGuid().ToString("N"))),
            new CircuitBreaker(),
            new LastKnownGoodStore(Path.Combine(Path.GetTempPath(), "JiaolongIntegration", Guid.NewGuid().ToString("N"))))
            .ExecuteAsync(
                new SetCpuTuningCommand(
                    Guid.NewGuid(),
                    new CpuTuningPlan(null, 100, null, null, null, null, null, null),
                    true),
                CommandContext.DevelopmentWritable,
                CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
    }

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
