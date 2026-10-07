using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed record PerformanceDraft
{
    private int acMaxFrequencyMhz = 4_500;
    private int dcMaxFrequencyMhz = 4_500;

    public PerformanceMode Mode { get; set; } = PerformanceMode.Balanced;
    public bool UseOfficialCpuPolicy { get; set; }
    public int TemperatureLimitC { get; set; } = 95;
    public int SplWatts { get; set; } = 45;
    public int SpptWatts { get; set; } = 65;
    public int AcMaxFrequencyMhz
    {
        get => acMaxFrequencyMhz;
        set => acMaxFrequencyMhz = value;
    }

    public int DcMaxFrequencyMhz
    {
        get => dcMaxFrequencyMhz;
        set => dcMaxFrequencyMhz = value;
    }

    public int MaxFrequencyMhz
    {
        get => AcMaxFrequencyMhz;
        set
        {
            AcMaxFrequencyMhz = value;
            DcMaxFrequencyMhz = value;
        }
    }
    public bool IsBoostEnabled { get; set; }
    public Guid WindowsPowerSchemeId { get; set; }
    public int? AcMinActiveCoresPercent { get; set; }
    public int? DcMinActiveCoresPercent { get; set; }
    public int? NegativeCurveOptimizer { get; set; } = -15;
    public AdvancedCpuTuningDraft? AdvancedCpuTuning { get; set; }
}

public enum CpuCurveOptimizerMode { Bios, AllCore, PerCore }

public sealed record AdvancedCpuTuningDraft
{
    public CpuCurveOptimizerMode? CurveOptimizerMode { get; init; }

    public AdvancedCpuTuningDraft WithoutUnchangedHardwareFields(AdvancedCpuTuningPlan? limits) => this with
    {
        StapmWatts = StapmWatts == limits?.StapmWatts ? null : StapmWatts,
        FastPptWatts = FastPptWatts == limits?.FastPptWatts ? null : FastPptWatts,
        SlowPptWatts = SlowPptWatts == limits?.SlowPptWatts ? null : SlowPptWatts,
        PptWatts = PptWatts == limits?.PptWatts ? null : PptWatts,
        VrmCurrentMilliamps = VrmCurrentMilliamps == limits?.VrmCurrentMilliamps ? null : VrmCurrentMilliamps,
        TdcCurrentMilliamps = TdcCurrentMilliamps == limits?.TdcCurrentMilliamps ? null : TdcCurrentMilliamps,
        EdcCurrentMilliamps = EdcCurrentMilliamps == limits?.EdcCurrentMilliamps ? null : EdcCurrentMilliamps,
        Mp1TemperatureC = Mp1TemperatureC == limits?.Mp1TemperatureC ? null : Mp1TemperatureC,
        RsmuTemperatureC = RsmuTemperatureC == limits?.RsmuTemperatureC ? null : RsmuTemperatureC
    };

    public static AdvancedCpuTuningDraft FromCurveReadback(CpuTuningState? state, CpuCurveOptimizerMode? preferredMode)
    {
        if (state?.CurveOptimizerVerification != "hardwareReadback" || !state.HasCompleteCurveValues() ||
            state.PerCoreCurveOptimizer!.Values.Any(value => value > 0))
            return new() { CurveOptimizerMode = preferredMode ?? CpuCurveOptimizerMode.Bios };
        var cores = state.PerCoreCurveOptimizer!;
        int first = cores[0];
        bool uniform = cores.Values.All(value => value == first);
        var mode = preferredMode ?? (uniform
            ? first == 0 ? CpuCurveOptimizerMode.Bios : CpuCurveOptimizerMode.AllCore
            : CpuCurveOptimizerMode.PerCore);
        if (mode == CpuCurveOptimizerMode.AllCore && !uniform) mode = CpuCurveOptimizerMode.PerCore;
        return new()
        {
            CurveOptimizerMode = mode,
            CurveOptimizerAll = uniform ? first : null,
            PerCoreCurveOptimizer = new Dictionary<int, int>(cores)
        };
    }

    public CpuCurveOptimizerMode ResolveCurveOptimizerMode(int? legacyAllCore = null)
    {
        if (CurveOptimizerMode is { } mode)
            return Enum.IsDefined(mode) ? mode : CpuCurveOptimizerMode.Bios;
        // Old presets may contain both paths. Preserve their values, but never guess a hardware write.
        if (PerCoreCurveOptimizer is { Count: > 0 })
            return CurveOptimizerAll.HasValue ? CpuCurveOptimizerMode.Bios : CpuCurveOptimizerMode.PerCore;
        return (CurveOptimizerAll ?? legacyAllCore).HasValue ? CpuCurveOptimizerMode.AllCore : CpuCurveOptimizerMode.Bios;
    }

    public double? StapmWatts { get; init; }
    public double? FastPptWatts { get; init; }
    public double? SlowPptWatts { get; init; }
    public double? PptWatts { get; init; }
    public int? VrmCurrentMilliamps { get; init; }
    public int? TdcCurrentMilliamps { get; init; }
    public int? EdcCurrentMilliamps { get; init; }
    public int? Mp1TemperatureC { get; init; }
    public int? RsmuTemperatureC { get; init; }
    public int? PboScalar { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? OverclockEnabled { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int? OcClockMhz { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int? OcVoltageMillivolts { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<int, int>? PerCoreOcClockMhz { get; init; }
    public int? CurveOptimizerAll { get; init; }
    public IReadOnlyDictionary<int, int>? PerCoreCurveOptimizer { get; init; }
}

public sealed record RiskAcknowledgement(bool CpuPowerAndTemperatureConfirmed, bool CurveOptimizerConfirmed)
{
    public static RiskAcknowledgement Accepted { get; } = new(true, true);
}

public interface IHardwareCommandSender
{
    Task<CommandResult> SendAsync(HardwareCommand command, CancellationToken cancellationToken);
}

public sealed class PerformanceViewModel : ObservableObject
{
    private readonly IHardwareCommandSender? client;
    private readonly RiskAcknowledgement defaultAcknowledgement;
    private bool isPending;

    public PerformanceViewModel(IHardwareCommandSender? client = null, RiskAcknowledgement? defaultAcknowledgement = null)
    {
        this.client = client;
        this.defaultAcknowledgement = defaultAcknowledgement ?? new RiskAcknowledgement(false, false);
        Draft = new PerformanceDraft();
        ApplyCommand = new AsyncRelayCommand(ApplyCurrentAsync, () => !IsPending);
        ValidateCommand = new AsyncRelayCommand(ValidateCurrentAsync);
    }

    public PerformanceDraft Draft { get; }

    public IReadOnlyList<PerformanceProfile> EditableProfiles => PerformanceCapabilities.EditableProfiles;

    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ValidateCommand { get; }

    public ValidationSummary? LastValidation { get; private set; }
    public CommandResult? LastResult { get; private set; }

    public bool IsPending
    {
        get => isPending;
        private set
        {
            if (SetProperty(ref isPending, value)) ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public Task<ValidationSummary> ValidateAsync(PerformanceDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = PerformanceDraftValidator.Validate(draft);
        LastValidation = summary;
        OnPropertyChanged(nameof(LastValidation));
        return Task.FromResult(summary);
    }

    public async Task<CommandResult> ApplyAsync(PerformanceDraft draft, RiskAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(draft, cancellationToken);
        if (!validation.IsValid) return Reject(draft, ErrorCode.ValidationFailed);
        if (!acknowledgement.CpuPowerAndTemperatureConfirmed)
        {
            return Reject(draft, ErrorCode.ValidationFailed);
        }

        if (client is null) return Reject(draft, ErrorCode.ServiceUnavailable);
        IsPending = true;
        try
        {
            var command = PerformanceCommandFactory.Create(draft, riskConfirmed: true);
            LastResult = await client.SendAsync(command, cancellationToken);
            OnPropertyChanged(nameof(LastResult));
            return LastResult;
        }
        finally
        {
            IsPending = false;
        }
    }

    private Task ValidateCurrentAsync() => ValidateAsync(Draft, CancellationToken.None);

    private async Task ApplyCurrentAsync() =>
        await ApplyAsync(Draft, defaultAcknowledgement, CancellationToken.None);

    private static CommandResult Reject(PerformanceDraft draft, ErrorCode code)
    {
        var operationId = Guid.NewGuid();
        return new CommandResult(
            operationId,
            CommandState.Rejected,
            null,
            RequiredUserAction.None,
            ServiceError.Create(code, operationId, false),
            false);
    }
}
