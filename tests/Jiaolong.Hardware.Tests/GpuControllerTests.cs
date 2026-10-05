using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class GpuControllerTests
{
    [TestMethod]
    public void Gpu_controller_public_surface_has_no_power_memory_or_vbios_operation()
    {
        var publicNames = typeof(GpuController)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .ToArray();

        Assert.IsFalse(publicNames.Any(name =>
            name.Contains("Power", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Vbios", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Gpu_limit_timeout_is_rejected_without_shell_or_memory_operation()
    {
        var controller = new GpuController(limitTransport: new TimeoutGpuTransport());
        var result = await controller.ApplyFrequencyLimitAsync(
            new GpuLimitPlan(1800), CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(ErrorCode.DeadlineExceeded, result.Error!.Code);
    }

    private sealed class TimeoutGpuTransport : IGpuLimitTransport
    {
        public Task<GpuFrequencyRange> ReadRangeAsync(CancellationToken cancellationToken) =>
            Task.FromException<GpuFrequencyRange>(new TimeoutException());

        public Task ApplyCoreLimitAsync(int minimumMhz, int maximumMhz, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ResetCoreLimitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int?> ReadCoreLimitAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(null);
    }
}
