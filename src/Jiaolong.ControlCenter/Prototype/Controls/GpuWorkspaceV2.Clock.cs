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

        if (!available || state is null)
        {
            SetGpuHelpState(CoreFrequencyHelp, "驱动限频不可用");
            return;
        }
        if (CoreRail.Minimum != state.MinimumMhz || CoreRail.Maximum != state.MaximumMhz || !clockInitialized)
        {
            clockSynchronizing = true;
            CoreRail.Minimum = CoreValueBox.Minimum = state.MinimumMhz;
            CoreRail.Maximum = CoreValueBox.Maximum = state.MaximumMhz;
            CoreRail.LimitValue = state.MaximumMhz;
            if (!clockInitialized && !clockDirty)
            {
                CoreRail.SetValue(state.SubmittedMhz ?? state.MaximumMhz);
                CoreValueBox.Value = state.SubmittedMhz ?? state.MaximumMhz;
            }
            clockInitialized = true;
            clockSynchronizing = false;
        }
        if (!clockDirty && !clockPending)
            SetGpuHelpState(CoreFrequencyHelp, !state.HasSubmission ? "范围已读取 · 当前限制未读回" :
                state.SubmittedMhz is int mhz ? $"已提交 {mhz} MHz · 未读回" : "驱动已接受恢复默认");
    }

}
