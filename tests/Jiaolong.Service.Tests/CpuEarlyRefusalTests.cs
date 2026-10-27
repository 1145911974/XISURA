using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CpuEarlyRefusalTests
{
    [TestMethod]
    public void Single_and_batch_cpu_preflight_refusals_are_known_no_mutation()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (string unavailable in new[] { "risk", "transport", "controller" })
        foreach (bool batch in new[] { false, true })
        {
            int hardwareCalls = 0;
            using var provider = new WindowsHomeHardwareProvider(() => hardwareCalls++, () => { }, () => []);
            var transport = new WindowsCpuTuningTransport(null!, null!, null!,
                hardwareAccessAllowed: () => { hardwareCalls++; return false; });
            typeof(WindowsHomeHardwareProvider).GetField("cpuTuningTransport", flags)!.SetValue(provider,
                unavailable == "transport" ? null : transport);
            typeof(WindowsHomeHardwareProvider).GetField("performanceController", flags)!.SetValue(provider,
                unavailable == "controller" ? null : new PerformanceController(cpuTransport: transport,
                    hardwareAccessAllowed: () => { hardwareCalls++; return false; }));
            var plan = new CpuTuningPlan(null, 55, null, null, null, null, null, null);
            var id = Guid.NewGuid();
            HardwareCommand command = batch ? new SetCpuTuningBatchCommand(id, [plan], unavailable != "risk")
                : new SetCpuTuningCommand(id, plan, unavailable != "risk");
            var result = (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod(
                batch ? "ExecuteCpuTuningBatchLocked" : "ExecuteCpuTuningLocked", flags)!
                .Invoke(provider, [command, CancellationToken.None])!;
            Assert.AreEqual(id, result.OperationId);
            Assert.AreEqual(CommandState.Rejected, result.State, $"{unavailable}, batch={batch}");
            Assert.IsNotNull(result.Error);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, $"{unavailable}, batch={batch}");
            Assert.AreEqual(0, hardwareCalls, "No hardware admission or dispatch belongs before this refusal.");
        }
    }
}
