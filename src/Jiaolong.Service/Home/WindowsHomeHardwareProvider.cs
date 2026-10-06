using System.Management;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Mechrevo.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Telemetry;
using Jiaolong.Hardware.Mechrevo.Wmi;
using LibreHardwareMonitor.Hardware;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider : IHomeHardwareProvider, IDisposable
{
    private const double BytesPerGb = 1024d * 1024 * 1024;
    private readonly object gate = new();
    private readonly object commandGate = new();
    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = false
    };
    private readonly MiCommonInterfaceClient miInterface = new(new WindowsMiReadTransport(), new WindowsMiWriteTransport());
    private readonly MiFanTelemetryReader miFanTelemetry;
    private readonly MiCpuTelemetryReader miCpuTelemetry;
    private readonly PawnIoCurveOptimizerTransport packagePowerReader = new();
    private readonly NvidiaSmiReader nvidiaSmiReader = new(
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
            ? Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
    private readonly ManifestResolver resolver = new();
    private readonly IHomeRadioStateReader radioController;
    private readonly ILogger<WindowsHomeHardwareProvider>? logger;
    private readonly object sensorGate = new();
    private Task? sensorSamplingTask;
    private SensorSnapshot sensorSnapshot = new([], null, null, DateTimeOffset.MinValue);
    private HardwareIdentity identity = UnknownIdentity;
    private WmiProviderEvidence? telemetryProvider;
    private WmiProviderEvidence? observedMiProvider;
    private CompatibilityDecision? compatibilityDecision;
    private WindowsPowerSettingsTransport? powerSettings;
    private PawnIoCurveOptimizerTransport? curveOptimizer;
    private PawnIoCurveOptimizerTransport? pboScalarTransport;
    private CpuSmuLimitSnapshot? smuLimitCache;
    private DateTimeOffset smuLimitCacheAt;
    private WindowsCpuTuningTransport? cpuTuningTransport;
    private PerformanceController? performanceController;
    private MiMuxTransport? muxTransport;
    private GpuController? gpuController;
    private bool curveProbeCompleted;
    private bool pboProbeCompleted;
    private GpuVfState? gpuVfCache;
    private DateTimeOffset gpuVfCacheAt;
    private HardwareSnapshot? telemetryCache;
    private bool gpuMemoryRecoveryRequired;
    private bool gpuCoreRecoveryRequired;
    private bool opened;
    private volatile bool disposed;

    public WindowsHomeHardwareProvider(IHomeRadioStateReader? radioController = null, ILogger<WindowsHomeHardwareProvider>? logger = null)
    {
        this.logger = logger;
        this.radioController = radioController ?? new WindowsRadioController();
        miFanTelemetry = new MiFanTelemetryReader(miInterface);
        miCpuTelemetry = new MiCpuTelemetryReader(miInterface);
    }

    public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            telemetryCache = null;
            identity = ReadIdentity();
            var fingerprint = ReadFingerprint(identity, cancellationToken);
            var decision = resolver.ResolveAsync(fingerprint, cancellationToken).GetAwaiter().GetResult();
            compatibilityDecision = decision;
            telemetryProvider = MatchesTelemetryIdentity(decision.Manifest, identity)
                ? decision.Manifest!.WmiProvider
                : null;
            ConfigureCpuTuning(decision);
            ConfigureMux(decision);
            var reason = decision.Reasons.FirstOrDefault()?.Code;
            var telemetry = ReadTelemetryLocked(cancellationToken);
            var cpuTuningState = ReadCpuTuningState(cancellationToken);
            var muxMode = ReadMuxMode(telemetryProvider ?? observedMiProvider, cancellationToken);
            var gpuVf = ReadGpuVfLocked(cancellationToken);
            var radios = radioController.ReadAsync(cancellationToken).GetAwaiter().GetResult();
            var supportState = decision.Mode == Jiaolong.Hardware.Abstractions.Compatibility.CompatibilityMode.Writable
                ? DeviceSupportState.Ready
                : DeviceSupportState.ReadOnly;
            var items = decision.Capabilities.Items
                .Where(item => item.Key is not ("gpuVoltageBoost" or "gpuPowerLimit" or "cpuTuning:manualOc"))
                .Concat([new CapabilityDescriptor("monitoring", CapabilityState.Available, null)])
                .Concat(supportState == DeviceSupportState.ReadOnly
                    ? HomeCapabilityCatalog.ReadOnlyControls(reason ?? "verifiedWritableEvidenceMissing")
                    : Array.Empty<CapabilityDescriptor>())
                .Select(item => item.Key switch
                {
                    "cpuTuning" when !HasReadableCpuTuning(cpuTuningState) => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "cpuTuningReadFailed"
                    },
                    "cpuTuning:smu" when cpuTuningState?.AdvancedLimits is null => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "advancedCpuProtocolUnavailable"
                    },
                    "cpuTuning:curveOptimizer" when curveOptimizer is null => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "curveOptimizerUnavailable"
                    },
                    "cpuTuning:pbo" when cpuTuningState?.PboScalar is null => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "pboScalarReadFailed"
                    },
                    "gpuVfCurve" when gpuMemoryRecoveryRequired || gpuCoreRecoveryRequired => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "gpuPstateRecoveryRequired"
                    },
                    "gpuVfCurve" when gpuVf.Nodes.Length != 127 => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = gpuVf.Error ?? "gpuVfReadFailed"
                    },
                    "gpuMemoryOffset" when gpuMemoryRecoveryRequired || gpuCoreRecoveryRequired => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "gpuPstateRecoveryRequired"
                    },
                    "gpuMemoryOffset" when gpuVf.Nodes.Length != 127 ||
                        gpuVf.MemoryOffsetKhz is null || gpuVf.MemoryMinimumOffsetKhz is null ||
                        gpuVf.MemoryMaximumOffsetKhz is null => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "gpuMemoryOffsetReadFailed"
                    },
                    "gpuCoreOffset" when gpuCoreRecoveryRequired => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "gpuCoreRecoveryRequired"
                    },
                    "gpuCoreOffset" when gpuVf.Nodes.Length != 127 || gpuVf.CoreOffsetKhz is null ||
                        gpuVf.CoreMinimumOffsetKhz is null || gpuVf.CoreMaximumOffsetKhz is null => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "gpuCoreOffsetReadFailed"
                    },
                    "fanControl" when !BldingFanEcTransport.HasVerifiedFiles() => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "bldingDependencyMissing"
                    },
                    "quickSetting:wifi" when radios.WifiEnabled.HasValue => item with
                    {
                        State = CapabilityState.Available,
                        Reason = null
                    },
                    "quickSetting:bluetooth" when radios.BluetoothEnabled.HasValue => item with
                    {
                        State = CapabilityState.Available,
                        Reason = null
                    },
                    _ => item
                })
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var capabilities = new CapabilitySnapshot(items)
                {
                    Identity = identity,
                    SupportState = supportState,
                    Reason = reason
                };
            return Task.FromResult(new HomeHardwareState(
                capabilities,
                supportState,
                reason,
                identity,
                telemetry)
            {
                Controls = new HomeControlState(
                    ReadPerformanceMode(telemetryProvider ?? observedMiProvider, cancellationToken),
                    ReadStrongCoolingStateLocked(ReadBooleanControl(MiHomeControlKind.StrongCooling, telemetryProvider ?? observedMiProvider, cancellationToken)),
                    [
                        new QuickSettingStatus(QuickSettingKind.Wifi, radios.WifiEnabled, CapabilityReason(capabilities, "quickSetting:wifi")),
                        new QuickSettingStatus(QuickSettingKind.Bluetooth, radios.BluetoothEnabled, CapabilityReason(capabilities, "quickSetting:bluetooth")),
                        new QuickSettingStatus(QuickSettingKind.Touchpad, ReadBooleanControl(MiHomeControlKind.Touchpad, telemetryProvider ?? observedMiProvider, cancellationToken), CapabilityReason(capabilities, "quickSetting:touchpad")),
                        new QuickSettingStatus(QuickSettingKind.FnLock, ReadBooleanControl(MiHomeControlKind.FnLock, telemetryProvider ?? observedMiProvider, cancellationToken), CapabilityReason(capabilities, "quickSetting:fnLock")),
                         new QuickSettingStatus(QuickSettingKind.LidLogo, ReadBooleanControl(MiHomeControlKind.LidLogo, telemetryProvider ?? observedMiProvider, cancellationToken), CapabilityReason(capabilities, "quickSetting:lidLogo"))
                     ])
                {
                    MuxMode = muxMode,
                    CpuTuning = cpuTuningState,
                    KeyboardLighting = ReadLightingLocked(cancellationToken),
                    KeyboardLightingPreviewAvailable = true,
                KeyboardLightingNativeCycleAvailable = true,
                    KeyboardLightingPreviewActive = lightingPreview.Original is not null,
                    FanTargetCeilingAvailable = true,
                    FanAutomaticCeilingAvailable = true,
                    GpuCurveFactoryResetAvailable = true,
                    FanAutomaticCeilingActive = activeFanPlan?.Strategy == "Auto",
                    ActiveFanControlPlan = activeFanPlan,
                    KeyboardLightingError = lightingError,
                    KeyboardLightingElapsedSeconds = appliedLighting is null ? null : lightingClock.Elapsed.TotalSeconds,
                    GpuVf = gpuVf,
                    GpuClockLimit = ReadGpuClockLimitLocked(cancellationToken),
                    FanEcControl = ReadFanEcControlLocked()
                }
            });
        }
    }

    public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            try
            {
                if (opened) computer.Close();
                computer.Open();
                opened = true;
                return DiagnoseAsync(cancellationToken);
            }
            catch
            {
                return Task.FromResult(new HomeHardwareState(
                    new CapabilitySnapshot([new CapabilityDescriptor("monitoring", CapabilityState.Unavailable, "hardwareUnavailable")])
                    {
                        Identity = identity,
                        SupportState = DeviceSupportState.RepairRequired,
                        Reason = "hardwareUnavailable"
                    },
                    DeviceSupportState.RepairRequired,
                    "hardwareUnavailable",
                    identity,
                    null));
            }
        }
    }

    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            return Task.FromResult(ReadTelemetryLocked(cancellationToken));
        }
    }

    public Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            var provider = telemetryProvider ?? observedMiProvider;
            var radios = radioController.ReadAsync(cancellationToken).GetAwaiter().GetResult();
            var controls = new HomeControlState(
                ReadPerformanceMode(provider, cancellationToken),
                ReadStrongCoolingStateLocked(ReadBooleanControl(MiHomeControlKind.StrongCooling, provider, cancellationToken)),
                [
                    new QuickSettingStatus(QuickSettingKind.Wifi, radios.WifiEnabled, null),
                    new QuickSettingStatus(QuickSettingKind.Bluetooth, radios.BluetoothEnabled, null),
                    new QuickSettingStatus(QuickSettingKind.Touchpad, ReadBooleanControl(MiHomeControlKind.Touchpad, provider, cancellationToken), null),
                    new QuickSettingStatus(QuickSettingKind.FnLock, ReadBooleanControl(MiHomeControlKind.FnLock, provider, cancellationToken), null),
                     new QuickSettingStatus(QuickSettingKind.LidLogo, ReadBooleanControl(MiHomeControlKind.LidLogo, provider, cancellationToken), null)
                 ])
            {
                MuxMode = ReadMuxMode(provider, cancellationToken),
                CpuTuning = ReadCpuTuningState(cancellationToken),
                KeyboardLighting = ReadLightingLocked(cancellationToken),
                KeyboardLightingPreviewAvailable = true,
                KeyboardLightingNativeCycleAvailable = true,
                KeyboardLightingPreviewActive = lightingPreview.Original is not null,
                FanTargetCeilingAvailable = true,
                FanAutomaticCeilingAvailable = true,
                GpuCurveFactoryResetAvailable = true,
                FanAutomaticCeilingActive = activeFanPlan?.Strategy == "Auto",
                ActiveFanControlPlan = activeFanPlan,
                KeyboardLightingError = lightingError,
                KeyboardLightingElapsedSeconds = appliedLighting is null ? null : lightingClock.Elapsed.TotalSeconds,
                GpuVf = ReadGpuVfLocked(cancellationToken),
                GpuClockLimit = ReadGpuClockLimitLocked(cancellationToken),
                FanEcControl = ReadFanEcControlLocked()
            };
            return Task.FromResult(controls);
        }
    }

    public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (commandGate)
        {
            if (command is SetPerformanceModeCommand)
            {
                ThrowIfDisposed();
                return Task.FromResult(ExecuteLocked(command, cancellationToken));
            }
            lock (gate)
            {
                ThrowIfDisposed();
                return Task.FromResult(ExecuteLocked(command, cancellationToken));
            }
        }
    }

    private CommandResult ExecuteLocked(HardwareCommand command, CancellationToken cancellationToken)
    {
        if (command is SetFanControlCommand or ReleaseFanControlCommand or SetStrongCoolingCommand)
            return ExecuteFanLocked(command, cancellationToken);

        if (compatibilityDecision is null)
            return Rejected(command.OperationId, ErrorCode.ReadOnlySafeMode);

        if (command is SetMuxModeCommand mux)
            return ExecuteMuxLocked(mux, cancellationToken);
        if (command is SetCpuTuningCommand cpu)
            return ExecuteCpuTuningLocked(cpu, cancellationToken);
        if (command is SetCpuTuningBatchCommand batch)
            return ExecuteCpuTuningBatchLocked(batch, cancellationToken);
        if (command is SetKeyboardLightingCommand lighting)
            return ExecuteLightingLocked(lighting, cancellationToken);
        if (command is RestoreKeyboardLightingPreviewCommand restoreLighting)
            return RestoreLightingPreviewLocked(restoreLighting.OperationId, cancellationToken);
        if (command is SetGpuVfCurveCommand vf)
            return ExecuteGpuVfLocked(vf, cancellationToken);
        if (command is SetGpuMemoryOffsetCommand memory)
            return ExecuteGpuMemoryOffsetLocked(memory, cancellationToken);
        if (command is SetGpuCoreOffsetCommand core)
            return ExecuteGpuCoreOffsetLocked(core, cancellationToken);
        if (command is SetGpuFrequencyLimitCommand clock)
            return ExecuteGpuClockLocked(clock, cancellationToken);
        if (command is SetGpuVoltageBoostCommand voltage)
            return Rejected(voltage.OperationId, ErrorCode.CapabilityUnavailable);
        if (command is SetGpuPowerLimitCommand power)
            return Rejected(power.OperationId, ErrorCode.CapabilityUnavailable);
        if (command is SetGpuPowerPolicyCommand policy)
            return Rejected(policy.OperationId, ErrorCode.CapabilityUnavailable);

        var control = command switch
        {
            SetPerformanceModeCommand => (MiHomeControlKind?)MiHomeControlKind.PerformanceMode,
            SetQuickSettingCommand { Setting: QuickSettingKind.Touchpad } => MiHomeControlKind.Touchpad,
            SetQuickSettingCommand { Setting: QuickSettingKind.FnLock } => MiHomeControlKind.FnLock,
            SetQuickSettingCommand { Setting: QuickSettingKind.LidLogo } => MiHomeControlKind.LidLogo,
            SetLidLogoCommand => MiHomeControlKind.LidLogo,
            _ => null
        };
        if (control is null || !MiHomeControlBindingFactory.TryCreate(compatibilityDecision, control.Value, out var binding))
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        var payload = command switch
        {
            SetPerformanceModeCommand mode when MiPerformanceModeCodec.TryEncode(mode.Mode, out var wireMode) => new[] { wireMode },
            SetLidLogoCommand logo => new[] { logo.Enabled ? (byte)1 : (byte)0 },
            SetQuickSettingCommand quick => new[] { quick.Enabled ? (byte)1 : (byte)0 },
            _ => Array.Empty<byte>()
        };
        if (payload.Length == 0)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        var before = (byte)0;
        if (!TryReadControlByte(binding!, out before, cancellationToken))
            return Rejected(command.OperationId, ErrorCode.HardwareReadFailed);
        var write = miInterface.WriteOnceAsync(binding!, payload, cancellationToken).GetAwaiter().GetResult();
        if (write.Quality != DataQuality.Good)
            return Rejected(command.OperationId, ErrorCode.HardwareWriteFailed);

        if (TryReadControlByte(binding!, out var after, cancellationToken) &&
            after == payload[0])
        {
            return new CommandResult(command.OperationId, CommandState.Applied,
                command is SetPerformanceModeCommand ? null : ReadTelemetryLocked(cancellationToken),
                RequiredUserAction.None, null, false)
            { VerifiedPerformanceMode = command is SetPerformanceModeCommand ? MiPerformanceModeCodec.Decode(after) : null };
        }

        var rollback = miInterface.WriteOnceAsync(binding!, new[] { before }, cancellationToken).GetAwaiter().GetResult();
        var restored = rollback.Quality == DataQuality.Good &&
                       TryReadControlByte(binding!, out var restoredValue, cancellationToken) &&
                       restoredValue == before;
        return new CommandResult(
            command.OperationId,
            restored ? CommandState.RolledBack : CommandState.RecoveryRequired,
            null,
            RequiredUserAction.None,
            ServiceError.Create(restored ? ErrorCode.ReadBackMismatch : ErrorCode.RollbackFailed, command.OperationId, false),
            false);
    }

    private CommandResult ExecuteCpuTuningLocked(
        SetCpuTuningCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.RiskConfirmed || cpuTuningTransport is null || performanceController is null)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        if (command.Plan.TemperatureLimitC.HasValue || command.Plan.SplWatts.HasValue || command.Plan.SpptWatts.HasValue)
            return ExecuteCpuTuningBatchLocked(new SetCpuTuningBatchCommand(command.OperationId, [command.Plan], command.RiskConfirmed), cancellationToken);

        if (command.Plan.Advanced?.PerCoreCurveOptimizer is not null)
            return ExecutePerCoreCurveLocked(command, cancellationToken);

        if (command.Plan.Advanced is { } smu &&
            (smu.StapmWatts is not null || smu.FastPptWatts is not null || smu.SlowPptWatts is not null ||
             smu.PptWatts is not null || smu.VrmCurrentMilliamps is not null || smu.TdcCurrentMilliamps is not null || smu.EdcCurrentMilliamps is not null ||
             smu.Mp1TemperatureC is not null || smu.RsmuTemperatureC is not null ||
             smu.OverclockEnabled is not null || smu.OcClockMhz is not null || smu.OcVoltageMillivolts is not null || smu.PerCoreOcClockMhz is { Count: > 0 }))
            return ExecuteAdvancedCpuLimitLocked(command, cancellationToken);

        bool hasCurveOptimizer = command.Plan.NegativeCurveOptimizer is not null ||
            command.Plan.Advanced?.CurveOptimizerAll is not null ||
            command.Plan.Advanced?.PerCoreCurveOptimizer is not null;
        if (hasCurveOptimizer)
        {
            int? target = command.Plan.NegativeCurveOptimizer ?? command.Plan.Advanced?.CurveOptimizerAll;
            var otherFields = command.Plan with { NegativeCurveOptimizer = null, Advanced = null };
            // Keep curve transactions separate from OEM limits with no reliable original-value getter.
            if (target is null or < -30 or > 0 ||
                command.Plan.Advanced?.PerCoreCurveOptimizer is not null ||
                (command.Plan.NegativeCurveOptimizer is not null && command.Plan.Advanced?.CurveOptimizerAll is not null) ||
                otherFields != new CpuTuningPlan(null, null, null, null, null, null, null, null) ||
                (command.Plan.Advanced is { } advanced &&
                 (advanced with { CurveOptimizerAll = null }) != new AdvancedCpuTuningPlan()))
                return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        }

        if (hasCurveOptimizer &&
            compatibilityDecision?.Capabilities.Items.Any(item =>
                item.Key == "cpuTuning:curveOptimizer" && item.State == CapabilityState.Available) != true)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        if (command.Plan.Advanced?.PboScalar is int newPbo)
        {
            if (compatibilityDecision?.Capabilities.Items.Any(item =>
                    item.Key == "cpuTuning:pbo" && item.State == CapabilityState.Available) != true ||
                ReadOptionalCpuField(CpuTuningField.PboScalar, cancellationToken) is not int currentPbo)
                return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

            if (newPbo > currentPbo)
            {
                var telemetry = ReadTelemetryLocked(cancellationToken);
                if (SystemPowerStatusReader.Read().AcPowerConnected is not true ||
                    telemetry.CpuTemperatureC is not double temperature || temperature >= 80)
                    return Rejected(command.OperationId, ErrorCode.ValidationFailed);
            }
        }

        if (SystemPowerStatusReader.Read().AcPowerConnected is not true && HasHighPowerField(command.Plan))
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);

        var result = performanceController.ApplyCpuTuningAsync(
                command.Plan,
                isAcConnected: true,
                cancellationToken)
            .GetAwaiter().GetResult();
        var error = result.Error is null
            ? null
            : result.Error with { CorrelationId = command.OperationId };
        return new CommandResult(
            command.OperationId,
            hasCurveOptimizer && error?.Code == ErrorCode.RollbackFailed ? CommandState.RecoveryRequired : result.State,
            hasCurveOptimizer ? null : ReadTelemetryLocked(cancellationToken),
            RequiredUserAction.None,
            error,
            false);
    }

    private CommandResult ExecuteMuxLocked(SetMuxModeCommand command, CancellationToken cancellationToken)
    {
        if (!command.UserConfirmedRestartImpact)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (gpuController is null)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);

        var result = gpuController.ApplyMuxAsync(command.Mode, cancellationToken).GetAwaiter().GetResult();
        var error = result.Error is null
            ? null
            : result.Error with { CorrelationId = command.OperationId };
        return new CommandResult(
            command.OperationId,
            result.State,
            null,
            result.RequiredAction,
            error,
            false);
    }

    private void ConfigureMux(CompatibilityDecision decision)
    {
        muxTransport = null;
        gpuController = null;
        if (!VerifiedWmiBinding.TryCreate(decision, new ControlKey(ControlKeys.MuxMode), out var binding)) return;

        muxTransport = new MiMuxTransport(miInterface, binding!);
        gpuController = new GpuController(muxTransport);
    }

    private MuxMode? ReadMuxMode(WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        if (provider is null ||
            provider.ReadType != 250 ||
            !string.Equals(provider.Namespace, "root\\WMI", StringComparison.Ordinal) ||
            !string.Equals(provider.Class, "MICommonInterface", StringComparison.Ordinal) ||
            !string.Equals(provider.InstanceName, "ACPI\\PNP0C14\\MIFS_0", StringComparison.Ordinal))
            return null;

        try
        {
            var result = miInterface.ReadAsync(
                    new MiReadBinding(provider.Namespace, provider.Class, provider.InstanceName, provider.ReadType, 9),
                    cancellationToken)
                .GetAwaiter().GetResult();
            return result.Quality == DataQuality.Good &&
                   result.Payload is { } payload &&
                   MiMuxModeCodec.TryDecode(payload, out var mode)
                ? mode
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureCpuTuning(CompatibilityDecision decision)
    {
        if (!MiCpuPowerBindingFactory.TryCreate(decision, out var binding))
        {
            DisposeCpuTuning();
            return;
        }

        if (!curveProbeCompleted && decision.Capabilities.Items.Any(item =>
                item.Key == "cpuTuning:curveOptimizer" && item.State == CapabilityState.Available))
        {
            curveProbeCompleted = true;
            var probe = new PawnIoCurveOptimizerTransport();
            try
            {
                if (identity.CpuModel.Contains("7745HX", StringComparison.OrdinalIgnoreCase) &&
                    probe.TryInitialize() && probe.ReadPerCore(CancellationToken.None).Count == 8)
                {

                    curveOptimizer = probe;
                }
                else probe.Dispose();
            }
            catch (Exception error)
            {
                probe.Dispose();
                logger?.LogDebug(error, "Curve optimizer hardware readback unavailable");
            }
        }

        if (!pboProbeCompleted && decision.Capabilities.Items.Any(item =>
                item.Key == "cpuTuning:pbo" && item.State == CapabilityState.Available))
        {
            pboProbeCompleted = true;
            var probe = new PawnIoCurveOptimizerTransport();
            try
            {
                if (probe.TryInitialize() && probe.Read(CancellationToken.None) is >= 1 and <= 10)
                    pboScalarTransport = probe;
                else
                    probe.Dispose();
            }
            catch
            {
                probe.Dispose();
            }
        }

        if (cpuTuningTransport is not null)
            return;

        powerSettings = new WindowsPowerSettingsTransport();
        cpuTuningTransport = new WindowsCpuTuningTransport(
            miInterface,
            binding!,
            powerSettings,
            curveOptimizer,
            pboScalarTransport,
            curveOptimizer);
        performanceController = new PerformanceController(cpuTransport: cpuTuningTransport);
    }

    private CpuTuningState? ReadCpuTuningState(CancellationToken cancellationToken)
    {
        if (cpuTuningTransport is null) return null;

        try
        {
            if (!cpuTuningTransport.IsMiCpuPowerProtocolAvailable(cancellationToken))
                return null;

            var limitSnapshot = ReadSmuLimits(cancellationToken);
            var temperatureValue = limitSnapshot?.Limits.Mp1TemperatureC;
            var splValue = limitSnapshot?.OemSplWatts;
            var fast = limitSnapshot?.Limits.FastPptWatts;
            int? spptValue = fast is double watts && watts == Math.Round(watts) ? (int)watts : null;
            var temperatureLimit = temperatureValue is >= 40 and <= 100 ? temperatureValue : null;
            var splWatts = splValue is > 0 and <= 200 ? splValue : null;
            var spptWatts = spptValue is > 0 and <= 250 ? spptValue : null;
            var windows = cpuTuningTransport.ReadWindowsPowerSettingsAsync(cancellationToken).GetAwaiter().GetResult();
            var parking = windows.AcMinActiveCoresPercent is int acParking && windows.DcMinActiveCoresPercent is int dcParking
                ? new WindowsCoreParkingValue(acParking, dcParking) : null;
            var cores = ReadOptionalCpuField(CpuTuningField.EnabledCoreCount, cancellationToken);
            var curveSnapshot = ReadOptionalCpuField(CpuTuningField.NegativeCurveOptimizer, cancellationToken);
            var curveCores = curveSnapshot as IReadOnlyDictionary<int, int>;
            int? curve = curveSnapshot as int?;
            if (curveCores?.Count == 8 && curveCores.Values.Distinct().Count() == 1)
                curve = curveCores.Values.First();
            return new CpuTuningState(
                temperatureLimit,
                splWatts,
                spptWatts,
                windows.AcMaxFrequencyMhz,
                new WindowsPowerBoostValue(windows.AcBoostMode, windows.DcBoostMode).IsEnabled,
                cores as int?,
                windows.ActiveSchemeId,
                curve,
                curveCores?.Count == 8 ? "hardwareReadback" : null)
            {
                AcMaxFrequencyMhz = windows.AcMaxFrequencyMhz,
                DcMaxFrequencyMhz = windows.DcMaxFrequencyMhz,
                AcMinActiveCoresPercent = parking?.Ac is >= 0 and <= 100 ? parking.Ac : null,
                DcMinActiveCoresPercent = parking?.Dc is >= 0 and <= 100 ? parking.Dc : null,
                WindowsPowerSchemeName = cpuTuningTransport.ActivePowerSchemeName,
                PboScalar = ReadOptionalCpuField(CpuTuningField.PboScalar, cancellationToken) as int?,
                PerCoreCurveOptimizer = curveCores,
                AdvancedLimits = limitSnapshot?.Limits,
                OemCustomPowerMode = cpuTuningTransport.OemCustomPowerMode
            };
        }
        catch
        {
            return null;
        }
    }

    private object? ReadCpuField(CpuTuningField field, CancellationToken cancellationToken) =>
        cpuTuningTransport!.ReadFieldAsync(field, cancellationToken).GetAwaiter().GetResult();

    private CpuSmuLimitSnapshot? ReadSmuLimits(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - smuLimitCacheAt < TimeSpan.FromSeconds(1)) return smuLimitCache;
        try { smuLimitCache = curveOptimizer?.ReadLimitSnapshot(cancellationToken); }
        catch { smuLimitCache = null; }
        smuLimitCacheAt = DateTimeOffset.UtcNow;
        return smuLimitCache;
    }

    private object? ReadOptionalCpuField(CpuTuningField field, CancellationToken cancellationToken)
    {
        try
        {
            return ReadCpuField(field, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasReadableCpuTuning(CpuTuningState? state) =>
        state is not null &&
        state.MaxFrequencyMhz is not null &&
        state.BoostEnabled is not null &&
        state.WindowsPowerSchemeId is not null;

    private static bool HasHighPowerField(CpuTuningPlan plan) =>
        plan.SplWatts is not null ||
        plan.SpptWatts is not null ||
        plan.MaxFrequencyMhz is not null ||
        plan.BoostEnabled is true ||
        plan.WindowsPowerSchemeId is not null;

    private void DisposeCpuTuning()
    {
        curveOptimizer?.Dispose();
        curveOptimizer = null;
        pboScalarTransport?.Dispose();
        pboScalarTransport = null;
        cpuTuningTransport = null;
        performanceController = null;
        powerSettings = null;
        curveProbeCompleted = false;
        pboProbeCompleted = false;
    }

    private PerformanceMode? ReadPerformanceMode(WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        var value = ReadControlByte(MiHomeControlKind.PerformanceMode, provider, cancellationToken);
        return value is byte wireValue ? MiPerformanceModeCodec.Decode(wireValue) : null;
    }

    private bool? ReadBooleanControl(MiHomeControlKind control, WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        var value = ReadControlByte(control, provider, cancellationToken);
        return value switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }

    private byte? ReadControlByte(MiHomeControlKind control, WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        if (provider is null) return null;
        var result = miInterface.ReadAsync(
                new MiReadBinding(
                    provider.Namespace,
                    provider.Class,
                    provider.InstanceName,
                    provider.ReadType,
                    MiHomeControlBindingFactory.MethodName(control)),
                cancellationToken)
            .GetAwaiter().GetResult();
        return WmiResponseParser.ReadByte(
            result.Payload ?? [],
            MiHomeControlBindingFactory.ResponseIndex(control)).Value;
    }

    private bool TryReadControlByte(VerifiedWmiBinding binding, out byte value, CancellationToken cancellationToken)
    {
        var result = miInterface.ReadAsync(binding, cancellationToken).GetAwaiter().GetResult();
        var parsed = WmiResponseParser.ReadByte(
            result.Payload ?? [],
            MiHomeControlBindingFactory.ResponseIndex(binding.MethodName));
        if (parsed.Value is not byte current || parsed.Quality != DataQuality.Good)
        {
            value = 0;
            return false;
        }

        value = current;
        return true;
    }

    private static CommandResult Rejected(Guid operationId, ErrorCode code) => new(
        operationId,
        CommandState.Rejected,
        null,
        RequiredUserAction.None,
        ServiceError.Create(code, operationId, false),
        false);

    public void Dispose()
    {
        lock (commandGate)
        lock (gate)
        {
            if (disposed) return;
            if (lightingPreview.Original is not null)
                RestoreLightingPreviewLocked(Guid.NewGuid(), CancellationToken.None);
            disposed = true;
            try { ReleaseFanLocked(); }
            catch (Exception ex) { logger?.LogError(ex, "Fan EC shutdown release failed"); }
            fanTimer?.Dispose();
            fanTimer = null;
            lightingTimer?.Dispose();
            lightingTimer = null;
            lock (sensorGate)
            {
                if (opened) computer.Close();
                opened = false;
            }
            DisposeCpuTuning();
            packagePowerReader.Dispose();
        }
    }

    private HardwareIdentity ReadIdentity() => new(
        ReadManagementValue("Win32_BaseBoard", "Product") ?? "未知",
        ReadManagementValue("Win32_BIOS", "SMBIOSBIOSVersion") ?? "未知",
        ReadManagementValue("Win32_Processor", "Name")?.Trim() ?? "未知",
        ReadManagementValue("Win32_VideoController", "Name", value => value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "未知");

    private HardwareFingerprint ReadFingerprint(HardwareIdentity value, CancellationToken cancellationToken)
    {
        var pnp = ReadManagementValues("Win32_VideoController", "PNPDeviceID", item =>
            item.Contains("PCI\\VEN_10DE", StringComparison.OrdinalIgnoreCase));
        observedMiProvider = ReadObservedMiProvider(cancellationToken);
        var dependency = ReadPawnIoDependency();
        return new HardwareFingerprint(value.BoardProduct, value.BiosVersion, value.CpuModel, value.GpuName,
            observedMiProvider is not null && dependency is not null && pnp.Length > 0)
        {
            CpuVendor = ReadManagementValue("Win32_Processor", "Manufacturer") ?? string.Empty,
            GpuVendorId = pnp.FirstOrDefault() is { } deviceId ? ExtractVendorId(deviceId) : string.Empty,
            GpuPnpDeviceIdsExact = pnp,
            OemProvider = observedMiProvider is null ? null : new VerifiedOemProvider(
                observedMiProvider.Namespace,
                observedMiProvider.Class,
                observedMiProvider.InstanceName,
                observedMiProvider.ReadType,
                observedMiProvider.WriteType),
            Dependencies = dependency is null ? Array.Empty<VerifiedDependency>() : [dependency]
        };
    }

    private WmiProviderEvidence? ReadObservedMiProvider(CancellationToken cancellationToken)
    {
        var provider = new WmiProviderEvidence
        {
            Namespace = "root\\WMI",
            Class = "MICommonInterface",
            InstanceName = "ACPI\\PNP0C14\\MIFS_0",
            ReadType = 250,
            WriteType = 251
        };
        var response = miInterface.ReadAsync(
                new MiReadBinding(provider.Namespace, provider.Class, provider.InstanceName, provider.ReadType, 8),
                cancellationToken)
            .GetAwaiter().GetResult();
        var instanceReadSucceeded =
            response.Quality == DataQuality.Good &&
            WmiResponseParser.ReadByte(response.Payload ?? [], 4).Quality == DataQuality.Good;
        if (!MiProviderEvidencePolicy.CanAttemptControlledWrite(
                instanceReadSucceeded,
                HasMiProviderContract(provider)))
        {
            observedMiProvider = null;
            return null;
        }

        observedMiProvider = provider;
        return provider;
    }

    private static bool HasMiProviderContract(WmiProviderEvidence provider)
    {
        try
        {
            using var managementClass = new System.Management.ManagementClass(
                provider.Namespace,
                provider.Class,
                null);
            var method = managementClass.Methods["MiInterface"];
            return method?.InParameters?.Properties["InData"] is not null &&
                   method.OutParameters?.Properties["OutData"] is not null;
        }
        catch
        {
            return false;
        }
    }

    private static VerifiedDependency? ReadPawnIoDependency() => PawnIoDependencyProbe.ReadInstalled();

    private static string ExtractVendorId(string deviceId)
    {
        var marker = "VEN_";
        var index = deviceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 || deviceId.Length < index + marker.Length + 4
            ? string.Empty
            : deviceId.Substring(index + marker.Length, 4).ToUpperInvariant();
    }

    private static string? ReadManagementValue(string className, string propertyName, Func<string, bool>? predicate = null)
    {
        foreach (var value in ReadManagementValues(className, propertyName, predicate)) return value;
        return null;
    }

    private static string[] ReadManagementValues(string className, string propertyName, Func<string, bool>? predicate = null)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
            return searcher.Get()
                .Cast<ManagementObject>()
                .Select(item => item[propertyName]?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Where(value => predicate is null || predicate(value!))
                .Select(value => value!.Trim())
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed record SensorReading(IHardware Hardware, SensorType SensorType, string Name, float? Value);
    private sealed record SensorSnapshot(SensorReading[] Sensors, double? CpuUsagePercent, double? CpuFrequencyMhz, DateTimeOffset CapturedAtUtc);

    private static void Collect(IHardware hardware, ICollection<SensorReading> sensors)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors) sensors.Add(new(hardware, sensor.SensorType, sensor.Name, sensor.Value));
        foreach (var child in hardware.SubHardware) Collect(child, sensors);
    }

    private SensorSnapshot ReadSensorSnapshotLocked()
    {
        // Sensor drivers can take seconds. Their single sampler must never own the control gate.
        if ((sensorSamplingTask is null || sensorSamplingTask.IsCompleted) &&
            DateTimeOffset.UtcNow - Volatile.Read(ref sensorSnapshot).CapturedAtUtc >= TimeSpan.FromMilliseconds(500))
            sensorSamplingTask = Task.Run(() =>
            {
                lock (sensorGate)
                {
                    if (disposed) return;
                    try
                    {
                        if (!opened) { computer.Open(); opened = true; }
                        var sensors = new List<SensorReading>();
                        foreach (var hardware in computer.Hardware) Collect(hardware, sensors);
                        var cpu = sensors.Where(sensor => sensor.Hardware.HardwareType == HardwareType.Cpu).ToArray();
                        Volatile.Write(ref sensorSnapshot, new(sensors.ToArray(),
                            Preferred(cpu, SensorType.Load, "CPU Total", "Total") ?? ReadCpuUsagePercent(),
                            Average(cpu, SensorType.Clock, "Core") ?? ReadCpuFrequencyMhz(), DateTimeOffset.UtcNow));
                    }
                    catch (Exception error) { logger?.LogDebug(error, "Sensor sampling unavailable"); }
                }
            });
        return Volatile.Read(ref sensorSnapshot);
    }

    private static bool IsGpu(HardwareType type) => type is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;

    private HardwareSnapshot ReadTelemetryLocked(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (telemetryCache is { } cached && DateTimeOffset.UtcNow - cached.CapturedAtUtc is { } age &&
            age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(250)) return cached;
        return telemetryCache = SampleTelemetryLocked(cancellationToken);
    }

    private HardwareSnapshot SampleTelemetryLocked(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var powerStatus = SystemPowerStatusReader.Read();
        try
        {
            var packagePower = (curveOptimizer ?? packagePowerReader).ReadPackagePower(cancellationToken);
            var sensorSample = ReadSensorSnapshotLocked();
            var sensors = sensorSample.Sensors;
            var cpu = sensors.Where(item => item.Hardware.HardwareType == HardwareType.Cpu).ToArray();
            var gpuHardware = sensors.Select(item => item.Hardware).Distinct()
                .Where(item => IsGpu(item.HardwareType))
                .OrderBy(item => item.HardwareType == HardwareType.GpuNvidia ? 0 : item.HardwareType == HardwareType.GpuAmd ? 1 : 2)
                .FirstOrDefault();
            var gpu = sensors.Where(item => ReferenceEquals(item.Hardware, gpuHardware)).ToArray();
            var memory = sensors.Where(item => item.Hardware.HardwareType == HardwareType.Memory).ToArray();
            var fans = sensors.Where(item => item.SensorType == SensorType.Fan).ToArray();
            var nvidiaReading = nvidiaSmiReader.ReadAsync(cancellationToken).GetAwaiter().GetResult();
            var drives = ReadDrives();
            var memoryUsed = Named(memory, SensorType.Data, "Memory Used");
            var memoryAvailable = Named(memory, SensorType.Data, "Memory Available");
            var memoryTotal = memoryUsed.HasValue && memoryAvailable.HasValue ? memoryUsed + memoryAvailable : null;
            var gpuUsed = Named(gpu, SensorType.SmallData, "GPU Memory Used") ?? Named(gpu, SensorType.Data, "GPU Memory Used");
            var gpuTotal = Named(gpu, SensorType.SmallData, "GPU Memory Total") ?? Named(gpu, SensorType.Data, "GPU Memory Total");
            var cpuTemperature = Preferred(cpu, SensorType.Temperature, "Package", "Tctl", "Core");
            var cpuVoltage = Preferred(cpu, SensorType.Voltage, "Core", "CPU", "VID");
            var gpuTemperature = Preferred(gpu, SensorType.Temperature, "GPU Core", "Core") ?? nvidiaReading.TemperatureC;
            // An unnamed fan cannot establish CPU/GPU ownership; prefer the model-specific WMI pair.
            var cpuFanRpm = Named(fans, SensorType.Fan, "CPU");
            var gpuFanRpm = Named(gpu, SensorType.Fan, "GPU") ?? Named(fans, SensorType.Fan, "GPU");
            var readProvider = HomeTelemetryProviderSelection.ForRead(telemetryProvider, observedMiProvider);
            // Thermal guards use a fresh firmware reading, independently of the background sensor sample.
            var miCpuTemperature = ReadMiCpuTemperature(readProvider, cancellationToken) ??
                (DateTimeOffset.UtcNow - sensorSample.CapturedAtUtc <= TimeSpan.FromSeconds(3) ? cpuTemperature : null);
            var cpuPowerWatts = RoundInt(Preferred(cpu, SensorType.Power, "Package", "CPU")) ??
                RoundInt(packagePower);
            (double? CpuRpm, double? GpuRpm) miFans = cpuFanRpm.HasValue && gpuFanRpm.HasValue
                ? (null, null)
                : ReadMiFanTelemetry(readProvider, cancellationToken);
            var snapshot = new HardwareSnapshot(
                DateTimeOffset.UtcNow,
                miCpuTemperature.HasValue || gpuTemperature.HasValue ? "normal" : "unknown",
                miCpuTemperature,
                gpuTemperature,
                cpuPowerWatts,
                RoundInt(Preferred(gpu, SensorType.Power, "GPU Package", "Total", "GPU")) ?? nvidiaReading.PowerWatts)
            {
                CpuUsagePercent = sensorSample.CpuUsagePercent,
                CpuFrequencyMhz = sensorSample.CpuFrequencyMhz,
                CpuVoltageVolts = cpuVoltage,
                GpuUsagePercent = Preferred(gpu, SensorType.Load, "GPU Core", "Core", "D3D 3D") ?? nvidiaReading.UtilizationPercent,
                GpuFrequencyMhz = Preferred(gpu, SensorType.Clock, "Core", "Graphics"),
                GpuCoreVoltageVolts = Preferred(gpu, SensorType.Voltage, "GPU Core", "Core"),
                GpuPerformanceState = nvidiaReading.PerformanceState,
                GpuPerformanceLimitReason = nvidiaReading.PerformanceLimitReason,
                GpuEnforcedPowerLimitWatts = nvidiaReading.EnforcedPowerLimitWatts,
                GpuMaximumPowerLimitWatts = nvidiaReading.MaximumPowerLimitWatts,
                GpuMemoryUsedGb = ToGb(gpuUsed),
                GpuMemoryTotalGb = ToGb(gpuTotal),
                MemoryUsedGb = memoryUsed,
                MemoryTotalGb = memoryTotal,
                SystemDriveUsedGb = drives.SystemUsed,
                SystemDriveTotalGb = drives.SystemTotal,
                AllDrivesUsedGb = drives.AllUsed,
                AllDrivesTotalGb = drives.AllTotal,
                CpuFanRpm = cpuFanRpm ?? miFans.CpuRpm,
                GpuFanRpm = gpuFanRpm ?? miFans.GpuRpm,
                BiosVersion = identity.BiosVersion,
                AcPowerConnected = powerStatus.AcPowerConnected,
                BatteryPercent = powerStatus.BatteryPercent
            };
            return snapshot;
        }
        catch
        {
            return new HardwareSnapshot(DateTimeOffset.UtcNow, "unknown", null, null, null, null)
            {
                BiosVersion = identity.BiosVersion,
                AcPowerConnected = powerStatus.AcPowerConnected,
                BatteryPercent = powerStatus.BatteryPercent
            };
        }
    }

    private (double? CpuRpm, double? GpuRpm) ReadMiFanTelemetry(
        WmiProviderEvidence? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null ||
            string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName) ||
            provider.ReadType <= 0)
        {
            return (null, null);
        }

        var binding = new MiReadBinding(
            provider.Namespace,
            provider.Class,
            provider.InstanceName,
            provider.ReadType);
        var pair = miFanTelemetry.ReadAsync(binding, cancellationToken).GetAwaiter().GetResult();
        // MRID6-23 method 13 reports GPU at bytes 4–5 and CPU at 6–7 (working F: console).
        return identity.BoardProduct == "MRID6-23" ? (pair.GpuRpm, pair.CpuRpm) : pair;
    }

    private double? ReadMiCpuTemperature(WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        if (provider is null ||
            string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName) ||
            provider.ReadType <= 0)
        {
            return null;
        }

        var binding = new MiReadBinding(
            provider.Namespace,
            provider.Class,
            provider.InstanceName,
            provider.ReadType);
        return miCpuTelemetry.ReadAsync(binding, cancellationToken).GetAwaiter().GetResult();
    }

    private static bool MatchesTelemetryIdentity(CompatibilityManifest? manifest, HardwareIdentity value) =>
        manifest is not null &&
        manifest.BoardProductsExact.Contains(value.BoardProduct, StringComparer.Ordinal) &&
        manifest.BiosVersionsExact.Contains(value.BiosVersion, StringComparer.Ordinal) &&
        value.CpuModel.Contains(manifest.Cpu.ModelContains, StringComparison.Ordinal) &&
        manifest.Gpus.Any(gpu => value.GpuName.Contains(gpu.NameContains, StringComparison.Ordinal));

    private static double? ReadCpuFrequencyMhz()
    {
        var current = ReadManagementValue("Win32_Processor", "CurrentClockSpeed");
        if (double.TryParse(current, out var currentMhz) && currentMhz > 0) return currentMhz;

        var total = ReadManagementValue(
            "Win32_PerfFormattedData_Counters_ProcessorInformation",
            "ProcessorFrequency",
            value => value.Length > 0);
        return double.TryParse(total, out var frequencyMhz) && frequencyMhz > 0 ? frequencyMhz : null;
    }

    private static double? ReadCpuUsagePercent()
    {
        var value = ReadManagementValue(
            "Win32_PerfFormattedData_Counters_ProcessorInformation",
            "PercentProcessorTime",
            value => value.Length > 0);
        return double.TryParse(value, out var usage) && usage is >= 0 and <= 100 ? usage : null;
    }

    private static double? Preferred(IEnumerable<SensorReading> sensors, SensorType type, params string[] names)
    {
        var values = sensors.Where(sensor => sensor.SensorType == type && sensor.Value.HasValue && IsPlausible(type, sensor.Value.Value)).ToArray();
        foreach (var name in names)
        {
            var match = values.FirstOrDefault(sensor => sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is float value) return value;
        }

        return values.FirstOrDefault()?.Value;
    }

    private static double? Named(IEnumerable<SensorReading> sensors, SensorType type, string name) =>
        sensors.FirstOrDefault(sensor => sensor.SensorType == type && sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static double? Average(IEnumerable<SensorReading> sensors, SensorType type, string name)
    {
        var values = sensors.Where(sensor => sensor.SensorType == type && sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && sensor.Value.HasValue && IsPlausible(type, sensor.Value.Value))
            .Select(sensor => (double)sensor.Value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static bool IsPlausible(SensorType type, float value) => type switch
    {
        SensorType.Temperature => value > 0 && value <= 150,
        SensorType.Clock => value > 0,
        SensorType.Power => value > 0,
        _ => float.IsFinite(value)
    };

    private static double? ToGb(double? megabytes) => megabytes / 1024d;

    private static int? RoundInt(double? value) => value is double number && double.IsFinite(number) ? (int)Math.Round(number) : null;

    private static (double? SystemUsed, double? SystemTotal, double? AllUsed, double? AllTotal) ReadDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed).ToArray();
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var system = drives.FirstOrDefault(drive => string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase));
            var allTotal = drives.Sum(drive => (double)drive.TotalSize) / BytesPerGb;
            var allFree = drives.Sum(drive => (double)drive.AvailableFreeSpace) / BytesPerGb;
            return (
                system is null ? null : (system.TotalSize - system.AvailableFreeSpace) / BytesPerGb,
                system?.TotalSize / BytesPerGb,
                drives.Length == 0 ? null : allTotal - allFree,
                drives.Length == 0 ? null : allTotal);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static readonly HardwareIdentity UnknownIdentity = new("未知", "未知", "未知", "未知");

    private static string? CapabilityReason(CapabilitySnapshot snapshot, string key)
    {
        var capability = snapshot.Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return capability is null ? snapshot.Reason : capability.Reason;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsHomeHardwareProvider));
    }
}
