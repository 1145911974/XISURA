using System.Reflection;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype.Controls;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AuditRemediationTests
{
    [TestMethod]
    public void Parameter_row_flushes_preload_readback_without_the_equal_value_shortcut()
    {
        var code = Read("Controls/PerformanceParameterRowV2.xaml.cs");
        StringAssert.Contains(code, "SetValueCore(hasPendingValue ? pendingValue : null);");
        Assert.IsFalse(code.Contains("SetValue(hasPendingValue ? pendingValue : null);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Fan_live_editor_tracks_capability_downgrade_but_offline_drafts_remain_editable()
    {
        var type = typeof(FanWorkspaceV2);
        var method = type.GetMethod("CanEditFanDraft", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var capabilities = new CapabilitySnapshot(
            [new("fanControl", CapabilityState.Available, null)])
        {
            Fields = [new("fixedRpm", CapabilityState.Available, true, true,
                1200, 6000, 100, "rpm", "test", "none", null, 0)]
        };
        bool CanEdit(HomeSessionStatus? status, CapabilitySnapshot? caps, bool draft) =>
            (bool)method.Invoke(null, [status, caps, draft])!;
        var connected = HomeSessionStatus.Connected;
        Assert.IsTrue(CanEdit(connected, capabilities, false));
        foreach (var state in new[] { CapabilityState.ReadOnly, CapabilityState.Unavailable })
        {
            var downgraded = capabilities with { Items = [new("fanControl", state, "dependencyUnavailable")] };
            Assert.IsFalse(CanEdit(connected, downgraded, false));
            Assert.IsTrue(CanEdit(connected, downgraded, true));
        }
        Assert.IsFalse(CanEdit(null, capabilities, false));
        Assert.IsFalse(CanEdit(connected, null, false));
        Assert.IsFalse(CanEdit(null, null, true));
        Assert.IsTrue(CanEdit(null, capabilities, true));
        foreach (var unsupported in new[]
        {
            capabilities with { Fields = [] },
            capabilities with { Fields = [capabilities.Fields[0] with { Availability = CapabilityState.Unavailable, Writable = false }] },
            capabilities with { Fields = [capabilities.Fields[0] with { Writable = false }] }
        })
        {
            Assert.IsFalse(CanEdit(connected, unsupported, false));
            Assert.IsFalse(CanEdit(null, unsupported, true));
        }
        Assert.IsTrue(CanEdit(connected, capabilities, false));
    }

    [TestMethod]
    public void Gpu_offset_editor_requires_current_values_and_a_usable_driver_range()
    {
        var type = typeof(GpuWorkspaceV2);
        var method = type.GetMethod("HasOffsetReadback", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var nodes = Enumerable.Range(0, 127).Select(index => new GpuVfNode(700 + index, 1700, 0)).ToArray();
        var hardware = new GpuVfState(nodes, -200000, 200000, null)
        { CoreOffsetKhz = 0, CoreMinimumOffsetKhz = -100000, CoreMaximumOffsetKhz = 100000,
          MemoryOffsetKhz = 0, MemoryMinimumOffsetKhz = -100000, MemoryMaximumOffsetKhz = 100000 };
        bool Ready(GpuVfState? value, bool memory) => (bool)method.Invoke(null, [value, memory])!;
        foreach (bool memory in new[] { false, true })
        {
            Assert.IsTrue(Ready(hardware, memory));
            Assert.IsFalse(Ready(hardware with { Nodes = [] }, memory));
            Assert.IsFalse(Ready(null, memory));
            Assert.IsFalse(Ready(hardware with { Error = "readFailed" }, memory));
            Assert.IsFalse(Ready(hardware with { CoreOffsetKhz = null, MemoryOffsetKhz = null }, memory));
            Assert.IsFalse(Ready(hardware with { CoreMaximumOffsetKhz = null, MemoryMaximumOffsetKhz = null }, memory));
            Assert.IsFalse(Ready(hardware with { CoreMinimumOffsetKhz = 300000, MemoryMinimumOffsetKhz = 300000 }, memory));
            Assert.IsTrue(Ready(hardware, memory));
        }
    }

    [TestMethod]
    public void Unknown_gpu_readback_keeps_a_target_editor_without_claiming_a_live_value()
    {
        string code = Read("Prototype/Controls/GpuWorkspaceV2.xaml.cs");
        string markup = Read("Prototype/Controls/GpuWorkspaceV2.xaml");
        int applyState = code.IndexOf("public void ApplyState(HomeStateSnapshot snapshot)", StringComparison.Ordinal);
        int nextMethod = code.IndexOf("private void ApplyGpuVfState", applyState, StringComparison.Ordinal);
        string apply = code[applyState..nextMethod];
        StringAssert.Contains(markup, "目标值 · 当前限制未读回");
        StringAssert.Contains(apply, "ApplyClockLimitState(snapshot.Controls.GpuClockLimit, tuningAvailable)");
        StringAssert.Contains(apply, "当前上限未知");
        StringAssert.Contains(apply, "CoreLiveUnknown.Visibility = tuningAvailable && clockDirty ? Visibility.Collapsed : Visibility.Visible");
    }

    [TestMethod]
    public void Saved_performance_presets_leave_active_automatic_configuration_frozen()
    {
        string code = Read("Prototype/PrototypeWindow.Branding.cs");
        string handler = code[code.IndexOf("private void InvalidateSavedPerformancePreset", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("private (string Name", StringComparison.Ordinal)];
        StringAssert.Contains(handler, "savedPerformanceDrafts.Remove(key)");
        StringAssert.Contains(handler, "RefreshTrayPresetCatalogAsync()");
        Assert.IsFalse(handler.Contains("SetAutomaticPresetReader", StringComparison.Ordinal),
            "Autosave must leave the active automatic configuration frozen until explicitly applied.");
    }

    [TestMethod]
    public void Fan_editing_and_help_remain_available_when_hardware_application_is_unavailable()
    {
        string code = Read("Prototype/Controls/FanWorkspaceV2.xaml.cs");
        string live = Read("Prototype/Controls/FanWorkspaceV2.FollowPreset.cs");
        string curve = Read("Prototype/Controls/FanCurveWorkspaceV2.xaml.cs");
        StringAssert.Contains(code, "UpdateFanPresetAvailability()");
        StringAssert.Contains(code, "HomeSessionStatus.Connected");
        Assert.IsFalse(code.Contains("SetActionAvailability(true, true)", StringComparison.Ordinal));
        Assert.IsFalse(live.Contains("FanCurveWorkspace.IsEnabled = curve", StringComparison.Ordinal));
        StringAssert.Contains(live, "FanCurveWorkspace.SetEditingEnabled(curve");
        StringAssert.Contains(curve, "TargetBox.IsEnabled = enabled");
        Assert.IsFalse(curve.Contains("CurveHelp.IsEnabled", StringComparison.Ordinal));
    }

    private static string Read(string relativePath)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "src", "Jiaolong.ControlCenter", relativePath);
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new FileNotFoundException(relativePath);
    }
}
