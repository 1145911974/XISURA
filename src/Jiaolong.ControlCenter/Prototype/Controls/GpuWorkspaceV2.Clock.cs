using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private bool clockSynchronizing;
    private bool clockDirty;
    private bool clockPending => GpuWritePending;
    private bool clockReady;
    private bool clockInitialized;

    private async void OnResetClockLimitClick(object sender, RoutedEventArgs e)
    {
        if (!clockReady || clockPending || gpuEditorLoading || session?.State is not { } state ||
            !CapabilityAvailable(state, "gpuFrequencyLimit")) return;
        // Cancel queued slider submissions so they cannot reapply a ceiling after this explicit reset.
        ++liveGpuRevision;
        liveGpuQueued = false;
        try
        {
            await ApplyGpuPresetAsync(new GpuWorkspacePreset(null, null, null, null)
                { ResetCoreFrequencyLimit = true }, automatic: true);
        }
        catch (Exception)
        { await PresetToolbar.ShowStatusAsync("解除限频结果未确认，请检查设备或服务状态"); }
    }

    private void UpdateClockDraft(double? value, bool fromRail)
    {
        if (clockSynchronizing || gpuPresetLoading || value is null) return;
        gpuEditorResetClock = false;
        clockSynchronizing = true;
        if (fromRail) CoreValueBox.Value = value.Value;
        else CoreRail.SetValue(value);
        clockSynchronizing = false;
        clockDirty = true;

        SetGpuHelpState(CoreFrequencyHelp, "未应用");
        MarkPresetDirty();
    }

    private void ApplyClockLimitState(GpuClockLimitState? state, bool available)
    {
        clockReady = available;
        CoreRail.IsEnabled = CoreValueBox.IsEnabled = available && !clockPending;
        CoreRail.Visibility = CoreValueBox.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

        if (!available || state is null)
        {
            CoreRail.SetValue(null);
            SetGpuHelpState(CoreFrequencyHelp, "驱动限频不可用，请检查设备或服务状态");
            return;
        }
        if (CoreRail.Minimum != state.MinimumMhz || CoreRail.Maximum != state.MaximumMhz || !clockInitialized)
        {
            clockSynchronizing = true;
            CoreRail.Minimum = CoreValueBox.Minimum = state.MinimumMhz;
            CoreRail.Maximum = CoreValueBox.Maximum = state.MaximumMhz;
            CoreRail.LimitValue = state.MaximumMhz;
            CoreRail.Configure();
            if (!clockInitialized && !clockDirty)
            {
                CoreRail.SetValue(state.SubmittedMhz ?? state.MaximumMhz);
                CoreValueBox.Value = state.SubmittedMhz ?? state.MaximumMhz;
            }
            clockInitialized = true;
            clockSynchronizing = false;
        }
        if (!clockDirty && !clockPending)
            SetGpuHelpState(CoreFrequencyHelp, !state.HasSubmission ? "手动设置上限 · 当前限制未读回 · 退出后保留，需手动重置" :
                state.SubmittedMhz is int mhz ? $"驱动已接受 {mhz} MHz · 未独立读回 · 退出后保留" : "驱动已接受解除限频 · 未独立读回");
    }

}
