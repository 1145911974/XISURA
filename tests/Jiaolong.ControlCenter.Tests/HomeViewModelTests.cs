using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HomeViewModelTests
{
    [TestMethod]
    public async Task Unknown_values_render_as_unknown_and_never_as_zero()
    {
        var vm = await HomeFixture.CreateAsync(DataQuality.Unknown);

        Assert.AreEqual("未知", vm.CpuTemperature.Value);
        Assert.AreNotEqual("0", vm.CpuTemperature.Value);
    }

    [TestMethod]
    public async Task Read_only_mode_disables_write_controls_with_reason()
    {
        var vm = await HomeFixture.CreateReadOnlyAsync("biosUnsupported");

        Assert.IsFalse(vm.SelectModeCommand.CanExecute(PerformanceMode.Balanced));
        Assert.AreEqual("当前 BIOS 未验证，仅提供只读监控", vm.WriteDisabledReason);
    }

    [TestMethod]
    public void Valid_snapshot_exposes_raw_cpu_and_gpu_temperatures()
    {
        var vm = new HomeViewModel();
        vm.ApplySnapshot(new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 56.4, 48.2, 45, 62), 1);

        Assert.AreEqual(56.4, vm.CpuTemperatureValue);
        Assert.AreEqual(48.2, vm.GpuTemperatureValue);
    }

    [TestMethod]
    public void Unknown_snapshot_clears_raw_temperatures_instead_of_retaining_stale_values()
    {
        var vm = new HomeViewModel();
        vm.ApplySnapshot(new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 56, 48, 45, 62), 1);
        vm.ApplySnapshot(new HardwareSnapshot(DateTimeOffset.UtcNow, "unknown", null, null, null, null), 2, DataQuality.Unknown);

        Assert.IsNull(vm.CpuTemperatureValue);
        Assert.IsNull(vm.GpuTemperatureValue);
    }
}

internal static class HomeFixture
{
    public static Task<HomeViewModel> CreateAsync(DataQuality quality) =>
        Task.FromResult(new HomeViewModel(quality));

    public static Task<HomeViewModel> CreateReadOnlyAsync(string reason) =>
        Task.FromResult(new HomeViewModel(
            DataQuality.Unknown,
            ConnectionState.Connected,
            CompatibilityMode.ReadOnly,
            reason));

    public static TelemetryPoint Sample(int sequence) =>
        new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), sequence);
}
