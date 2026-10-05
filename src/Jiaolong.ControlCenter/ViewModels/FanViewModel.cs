using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public enum FanControlMode
{
    EcAutomatic,
    OfficialPreset,
    FixedTarget,
    TemperatureCurve
}

public sealed record FanDraft(
    FanControlMode Mode,
    int? FixedTargetPercent,
    FanCurve CpuCurve,
    FanCurve GpuCurve,
    bool SynchronizeFans);

public sealed record CurveSimulation(IReadOnlyList<FanPoint> Points, string Summary);

public sealed class FanViewModel : ObservableObject
{
    private readonly IHardwareCommandSender? client;
    private readonly bool readOnly;
    private readonly bool activeOverride;

    public FanViewModel(IHardwareCommandSender? client = null, bool readOnly = false, bool activeOverride = false)
    {
        this.client = client;
        this.readOnly = readOnly;
        this.activeOverride = activeOverride;
        Draft = DefaultDraft();
        ApplyCommand = new AsyncRelayCommand(ApplyCurrentAsync, () => !this.readOnly && client is not null);
        ReleaseCommand = new AsyncRelayCommand(ReleaseCurrentAsync, () => this.activeOverride || client is not null);
    }

    public FanDraft Draft { get; private set; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ReleaseCommand { get; }

    public async Task<CurveSimulation> PreviewAsync(FanDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = FanCurveValidator.Validate(draft.CpuCurve);
        if (!validation.IsValid) return new CurveSimulation([], string.Join("；", validation.Errors));
        await Task.Yield();
        return new CurveSimulation(draft.CpuCurve.Points, "预览仅使用模拟器算法，未写入硬件");
    }

    public async Task<CommandResult> ApplyAsync(FanDraft draft, RiskAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        var cpuValidation = FanCurveValidator.Validate(draft.CpuCurve);
        var gpuValidation = FanCurveValidator.Validate(draft.GpuCurve);
        if (!cpuValidation.IsValid || !gpuValidation.IsValid || !acknowledgement.CpuPowerAndTemperatureConfirmed)
        {
            return Reject(ErrorCode.ValidationFailed);
        }

        if (client is null || readOnly) return Reject(ErrorCode.ReadOnlySafeMode);
        var points = draft.CpuCurve.Points.Select(x => new Jiaolong.Contracts.Models.FanPoint(x.TemperatureC, x.TargetPercent)).ToArray();
        return await client.SendAsync(
            new SetFanControlCommand(Guid.NewGuid(), new FanControlPlan(points), RiskConfirmed: true),
            cancellationToken);
    }

    public async Task<CommandResult> ReleaseAsync(CancellationToken cancellationToken)
    {
        if (client is null) return Reject(ErrorCode.ServiceUnavailable);
        return await client.SendAsync(
            new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested),
            cancellationToken);
    }

    private async Task ApplyCurrentAsync() => await ApplyAsync(Draft, new RiskAcknowledgement(true, true), CancellationToken.None);
    private async Task ReleaseCurrentAsync() => await ReleaseAsync(CancellationToken.None);

    private static FanDraft DefaultDraft() => new(
        FanControlMode.EcAutomatic,
        null,
        DefaultCurve(),
        DefaultCurve(),
        false);

    private static FanCurve DefaultCurve() => new(
    [
        new FanPoint(40, 20), new FanPoint(50, 30), new FanPoint(60, 40),
        new FanPoint(70, 55), new FanPoint(80, 75), new FanPoint(90, 100)
    ]);

    private static CommandResult Reject(ErrorCode code)
    {
        var operationId = Guid.NewGuid();
        return new CommandResult(operationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, operationId, false), false);
    }
}
