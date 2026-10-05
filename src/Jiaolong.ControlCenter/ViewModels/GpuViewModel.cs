using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed record GpuDraft(MuxMode MuxMode, int? CoreFrequencyLimitMhz);

public sealed class GpuViewModel
{
    private readonly IHardwareCommandSender? client;
    private readonly int? officialMinimumMhz;
    private readonly int? officialMaximumMhz;

    public GpuViewModel(IHardwareCommandSender? client = null, int? officialMinimumMhz = null, int? officialMaximumMhz = null)
    {
        this.client = client;
        this.officialMinimumMhz = officialMinimumMhz;
        this.officialMaximumMhz = officialMaximumMhz;
        Draft = new GpuDraft(MuxMode.Hybrid, null);
    }

    public GpuDraft Draft { get; private set; }
    public MuxMode CurrentMuxMode { get; private set; } = MuxMode.Hybrid;
    public MuxMode? TargetMuxMode { get; private set; }
    public string MuxStatusText { get; private set; } = "当前状态已回读";
    public bool HasVerifiedFrequencyRange => officialMinimumMhz.HasValue && officialMaximumMhz.HasValue;

    public async Task<CommandResult> ApplyMuxAsync(MuxMode mode, bool impactConfirmed, CancellationToken cancellationToken)
    {
        if (!impactConfirmed) return Reject(ErrorCode.ValidationFailed);
        if (mode == CurrentMuxMode) return Reject(ErrorCode.ValidationFailed);
        if (client is null) return Reject(ErrorCode.ServiceUnavailable);

        var result = await client.SendAsync(
            new SetMuxModeCommand(Guid.NewGuid(), mode, UserConfirmedRestartImpact: true),
            cancellationToken);
        if (result.Error is null)
        {
            TargetMuxMode = mode;
            MuxStatusText = result.RequiredAction == RequiredUserAction.Restart ? "需要重启" : "等待硬件回读";
        }

        return result;
    }

    public async Task<CommandResult> ApplyFrequencyLimitAsync(
        int? megahertz,
        RiskAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        if (!HasVerifiedFrequencyRange || !GpuDraftValidator.ValidateFrequencyLimit(megahertz, officialMaximumMhz!.Value, officialMinimumMhz!.Value).IsValid)
        {
            return Reject(ErrorCode.ValidationFailed);
        }

        if (!acknowledgement.CpuPowerAndTemperatureConfirmed) return Reject(ErrorCode.ValidationFailed);
        if (client is null) return Reject(ErrorCode.ServiceUnavailable);
        return await client.SendAsync(
            new SetGpuFrequencyLimitCommand(Guid.NewGuid(), megahertz, RiskConfirmed: true),
            cancellationToken);
    }

    public void OpenWindowsRestartOptions() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ms-settings:windowsupdate-options",
            UseShellExecute = true
        });

    private static CommandResult Reject(ErrorCode code)
    {
        var operationId = Guid.NewGuid();
        return new CommandResult(operationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, operationId, false), false);
    }
}
