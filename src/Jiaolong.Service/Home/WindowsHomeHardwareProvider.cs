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
    private readonly MiCommonInterfaceClient miInterface;
    private readonly MiFanTelemetryReader miFanTelemetry;
    private readonly MiCpuTelemetryReader miCpuTelemetry;
    private readonly PawnIoCurveOptimizerTransport packagePowerReader;
    private readonly NvidiaSmiReader nvidiaSmiReader = new(
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
            ? Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
    private readonly ManifestResolver resolver = new();
    private readonly IHomeRadioStateReader radioController;
    private readonly ILogger<WindowsHomeHardwareProvider>? logger;
    private readonly object sensorGate = new();
    private readonly Action openSensorComputer;
    private readonly Action closeSensorComputer;
    private readonly Func<IList<IHardware>> getSensorHardware;
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
    private SensorAccess? smuLimitCacheAccess;
    private WindowsCpuTuningTransport? cpuTuningTransport;
    private PerformanceController? performanceController;
    private MiMuxTransport? muxTransport;
    private GpuController? gpuController;
    private bool curveProbeCompleted;
    private bool pboProbeCompleted;
    private GpuVfState? gpuVfCache;
    private DateTimeOffset gpuVfCacheAt;
    private HardwareSnapshot? telemetryCache;
    private int? previousGpuPowerWatts;
    private int? previousGpuUsagePercent;
    private DateTimeOffset previousGpuPowerAtUtc;
    private bool gpuMemoryRecoveryRequired;
    private bool gpuCoreRecoveryRequired;
    private bool opened;
    private volatile bool disposed;
    private long backgroundWorkPaused;
    private PowerLifecycleState? powerLifecycle;

    public void SetBackgroundWorkPaused(bool paused)
    {
        long before, after;
        do
        {
            before = Volatile.Read(ref backgroundWorkPaused);
            if (((before & 1) != 0) == paused) return;
            after = ((before & ~1L) + 2) | (paused ? 1L : 0L);
        } while (Interlocked.CompareExchange(ref backgroundWorkPaused, after, before) != before);
    }
    public void SetBackgroundWorkState(PowerLifecycleState lifecycle) => Volatile.Write(ref powerLifecycle, lifecycle);
    private bool BackgroundWorkPaused => (Volatile.Read(ref backgroundWorkPaused) & 1) != 0 || Volatile.Read(ref powerLifecycle)?.IsPaused == true;
    private bool CpuHardwareReadAllowed => (Volatile.Read(ref backgroundWorkPaused) & 1) == 0 &&
        Volatile.Read(ref powerLifecycle)?.IsReadAllowed != false;

    private void EnsureProviderReadAccess()
    {
        if (!CpuHardwareReadAllowed) throw new IOException("hardwareAccessPaused");
    }

    private T ReadProviderHardware<T>(Func<T> read)
    {
        EnsureProviderReadAccess();
        try { return read(); }
        finally { EnsureProviderReadAccess(); }
    }

    public WindowsHomeHardwareProvider(IHomeRadioStateReader? radioController = null, ILogger<WindowsHomeHardwareProvider>? logger = null,
        ControlExecutionJournal? controlJournal = null)
    {
        this.controlJournal = controlJournal;
        this.logger = logger;
        this.radioController = radioController ?? new WindowsRadioController();
        openSensorComputer = () => computer.Open();
        closeSensorComputer = () => computer.Close();
        getSensorHardware = () => computer.Hardware;
        miInterface = new(new WindowsMiReadTransport(), new WindowsMiWriteTransport(),
            hardwareAccessAllowed: () => !BackgroundWorkPaused, hardwareReadAllowed: () => CpuHardwareReadAllowed);
        packagePowerReader = new(hardwareAccessAllowed: () => !BackgroundWorkPaused,
            hardwareReadAllowed: () => CpuHardwareReadAllowed);
        miFanTelemetry = new MiFanTelemetryReader(miInterface);
        miCpuTelemetry = new MiCpuTelemetryReader(miInterface);
    }

    internal WindowsHomeHardwareProvider(Action open, Action close, Func<IList<IHardware>> hardware,
        ILogger<WindowsHomeHardwareProvider>? logger = null) : this(logger: logger)
    {
        openSensorComputer = open;
        closeSensorComputer = close;
        getSensorHardware = hardware;
    }

    public Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken)
    {
        EnsureProviderReadAccess();
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureProviderReadAccess();
            ThrowIfDisposed();
            telemetryCache = null;
            identity = ReadIdentity();
            EnsureProviderReadAccess();
            var fingerprint = ReadFingerprint(identity, cancellationToken);
            EnsureProviderReadAccess();
            var decision = resolver.ResolveAsync(fingerprint, cancellationToken).GetAwaiter().GetResult();
            EnsureProviderReadAccess();
            compatibilityDecision = decision;
            telemetryProvider = observedMiProvider;
            ConfigureCpuTuning(decision);
            EnsureProviderReadAccess();
            ConfigureMux(decision);
            EnsureProviderReadAccess();
            string? reason = decision.Mode == CompatibilityMode.Writable ? null : "partialCapabilitiesAvailable";
            var telemetry = ReadTelemetryLocked(cancellationToken);
            EnsureProviderReadAccess();
            var cpuTuningState = ReadCpuTuningState(cancellationToken);
            EnsureProviderReadAccess();
            var muxMode = ReadMuxMode(telemetryProvider ?? observedMiProvider, cancellationToken);
            EnsureProviderReadAccess();
            var gpuVf = ReadGpuVfLocked(cancellationToken);
            EnsureProviderReadAccess();
            var radios = radioController.ReadAsync(cancellationToken).GetAwaiter().GetResult();
            EnsureProviderReadAccess();
            var supportState = DeviceSupportState.Ready;
            var items = decision.Capabilities.Items
                .Where(item => item.Key is not ("gpuVoltageBoost" or "gpuPowerLimit" or "cpuTuning:manualOc"))
                .Concat([new CapabilityDescriptor("monitoring", CapabilityState.Available, null)])
                .Concat(HomeCapabilityCatalog.ControlKeys.Where(key => !decision.Capabilities.Items.Any(item => item.Key == key))
                    .Select(key => new CapabilityDescriptor(key, CapabilityState.Unavailable, "hardwareInterfaceUnavailable")))
                .Select(item => item.Key switch
                {
                    "strongCooling" => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "fanAdvancedControlNotSupportedInRelease"
                    },
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
                    "fanControl" when !PawnIoLpcEcPorts.HasTrustedDependency() => item with
                    {
                        State = CapabilityState.Unavailable,
                        Reason = "pawnIoDependencyDiagnosis:" + PawnIoLpcEcPorts.DiagnoseDependency()
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
            var gpuClockLimit = ReadProviderHardware(() => ReadGpuClockLimitLocked(cancellationToken));
            var fields = CreateFieldCapabilities(items, cpuTuningState);
            items = HomeCapabilityCatalog.WithFieldAvailability(items, fields);
            supportState = items.Any(item => item.Key != "monitoring" && item.State == CapabilityState.Available)
                ? DeviceSupportState.Ready : DeviceSupportState.ReadOnly;
            if (supportState == DeviceSupportState.ReadOnly) reason = "hardwareInterfacesUnavailable";
            var capabilities = new CapabilitySnapshot(items)
                {
                    Identity = identity,
                    IsOnAcPower = ReadProviderHardware(() => SystemPowerStatusReader.Read()).AcPowerConnected,
                    Fields = fields,
                    SupportState = supportState,
                    Reason = reason
                };
            compatibilityDecision = decision with { Capabilities = capabilities };
            return Task.FromResult(new HomeHardwareState(
                capabilities,
                supportState,
                reason,
                identity,
                telemetry)
            {
                Controls = new HomeControlState(
                    ReadProviderHardware(() => ReadPerformanceMode(telemetryProvider ?? observedMiProvider, cancellationToken)),
                    ReadStrongCoolingStateLocked(ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.StrongCooling, telemetryProvider ?? observedMiProvider, cancellationToken))),
                    [
                        new QuickSettingStatus(QuickSettingKind.Wifi, radios.WifiEnabled, CapabilityReason(capabilities, "quickSetting:wifi")),
                        new QuickSettingStatus(QuickSettingKind.Bluetooth, radios.BluetoothEnabled, CapabilityReason(capabilities, "quickSetting:bluetooth")),
                        new QuickSettingStatus(QuickSettingKind.Touchpad, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.Touchpad, telemetryProvider ?? observedMiProvider, cancellationToken)), CapabilityReason(capabilities, "quickSetting:touchpad")),
                        new QuickSettingStatus(QuickSettingKind.FnLock, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.FnLock, telemetryProvider ?? observedMiProvider, cancellationToken)), CapabilityReason(capabilities, "quickSetting:fnLock")),
                         new QuickSettingStatus(QuickSettingKind.LidLogo, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.LidLogo, telemetryProvider ?? observedMiProvider, cancellationToken)), CapabilityReason(capabilities, "quickSetting:lidLogo"))
                     ])
                {
                    MuxMode = muxMode,
                    CpuTuning = cpuTuningState,
                    KeyboardLighting = ReadProviderHardware(() => ReadLightingLocked(cancellationToken)),
                    KeyboardLightingPreviewAvailable = true,
                KeyboardLightingNativeCycleAvailable = false,
                    KeyboardLightingPreviewActive = lightingPreview.Original is not null,
                    FanTargetCeilingAvailable = true,
                    FanAutomaticCeilingAvailable = false,
                    GpuCurveFactoryResetAvailable = true,
                    FanAutomaticCeilingActive = activeFanPlan?.Strategy == "Auto",
                    ActiveFanControlPlan = activeFanPlan,
                    KeyboardLightingError = lightingError,
                    KeyboardLightingElapsedSeconds = appliedLighting is null ? null : lightingClock.Elapsed.TotalSeconds,
                    GpuVf = gpuVf,
                    GpuClockLimit = gpuClockLimit,
                    FanEcControl = ReadProviderHardware(ReadFanEcControlLocked),
                    FanEcMapping = ReadProviderHardware(ReadFanEcMappingLocked)
                }
            });
        }
    }

    public Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureProviderReadAccess();
            try
            {
                ReinitializeSensorComputer(cancellationToken);
                return DiagnoseAsync(cancellationToken);
            }
            catch (Exception error) when (CpuHardwareReadAllowed && error is not OperationCanceledException)
            {
                string reason = sensorLifecycleUnknown ? "sensorLifecycleUnknown" : "hardwareUnavailable";
                return Task.FromResult(new HomeHardwareState(
                    new CapabilitySnapshot([new CapabilityDescriptor("monitoring", CapabilityState.Unavailable, reason)])
                    {
                        Identity = identity,
                        SupportState = DeviceSupportState.RepairRequired,
                        Reason = reason
                    },
                    DeviceSupportState.RepairRequired,
                    reason,
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

    public Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken) => ReadControlsCoreAsync(cancellationToken, true);

    public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) =>
        ReadControlsCoreAsync(token, includeCpuAdvanced, freshSmu: includeCpuAdvanced);

    // Explicit diagnostic capture only: saved OEM bytes are not effective SMU limits.
    public Task<OemCpuConfigurationSnapshot?> ReadOemCpuConfigurationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureProviderReadAccess();
            if (!string.Equals(identity.BoardProduct, "MRID6-23", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<OemCpuConfigurationSnapshot?>(null);
            long work = Volatile.Read(ref backgroundWorkPaused);
            var lifecycle = Volatile.Read(ref powerLifecycle);
            long? version = lifecycle?.Version;
            bool Current() => !disposed && CpuHardwareReadAllowed && Volatile.Read(ref backgroundWorkPaused) == work &&
                ReferenceEquals(Volatile.Read(ref powerLifecycle), lifecycle) && lifecycle?.Version == version;
            var reader = new PawnIoOemCpuConfigurationReader(Current);
            return Task.FromResult<OemCpuConfigurationSnapshot?>(ReadProviderHardware(() => reader.Read(token)));
        }
    }

    private Task<HomeControlState> ReadControlsCoreAsync(CancellationToken cancellationToken, bool includeCpuAdvanced, bool freshSmu = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureProviderReadAccess();
            if (freshSmu)
            {
                // Explicit transaction baselines must not reuse a pre-write PM table or confirm one cached sample twice.
                smuLimitCacheAt = default;
                smuLimitCacheAccess = null;
            }
            var provider = telemetryProvider ?? observedMiProvider;
            var radios = ReadProviderHardware(() => radioController.ReadAsync(cancellationToken).GetAwaiter().GetResult());
            var controls = new HomeControlState(
                ReadProviderHardware(() => ReadPerformanceMode(provider, cancellationToken)),
                ReadStrongCoolingStateLocked(ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.StrongCooling, provider, cancellationToken))),
                [
                    new QuickSettingStatus(QuickSettingKind.Wifi, radios.WifiEnabled, null),
                    new QuickSettingStatus(QuickSettingKind.Bluetooth, radios.BluetoothEnabled, null),
                    new QuickSettingStatus(QuickSettingKind.Touchpad, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.Touchpad, provider, cancellationToken)), null),
                    new QuickSettingStatus(QuickSettingKind.FnLock, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.FnLock, provider, cancellationToken)), null),
                     new QuickSettingStatus(QuickSettingKind.LidLogo, ReadProviderHardware(() => ReadBooleanControl(MiHomeControlKind.LidLogo, provider, cancellationToken)), null)
                 ])
            {
                MuxMode = ReadProviderHardware(() => ReadMuxMode(provider, cancellationToken)),
                ModeEvidence = lastModeEvidence,
                CpuTuning = ReadProviderHardware(() => ReadCpuTuningState(cancellationToken, includeCpuAdvanced)),
                KeyboardLighting = ReadProviderHardware(() => ReadLightingLocked(cancellationToken)),
                KeyboardLightingPreviewAvailable = true,
                KeyboardLightingNativeCycleAvailable = false,
                KeyboardLightingPreviewActive = lightingPreview.Original is not null,
                FanTargetCeilingAvailable = true,
                FanAutomaticCeilingAvailable = false,
                GpuCurveFactoryResetAvailable = true,
                FanAutomaticCeilingActive = activeFanPlan?.Strategy == "Auto",
                ActiveFanControlPlan = activeFanPlan,
                KeyboardLightingError = lightingError,
                KeyboardLightingElapsedSeconds = appliedLighting is null ? null : lightingClock.Elapsed.TotalSeconds,
                GpuVf = ReadProviderHardware(() => ReadGpuVfLocked(cancellationToken)),
                GpuClockLimit = ReadProviderHardware(() => ReadGpuClockLimitLocked(cancellationToken)),
                FanEcControl = ReadProviderHardware(ReadFanEcControlLocked),
                FanEcMapping = ReadProviderHardware(ReadFanEcMappingLocked)
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
                if (activeFanPlan is not null || strongCoolingActive || fanTouched)
                {
                    // FanTick also owns gate; a mode transaction cannot race its EC writes.
                    lock (gate) return Task.FromResult(ExecuteLocked(command, cancellationToken));
                }
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
            return Rejected(command.OperationId, ErrorCode.ReadOnlySafeMode) with { Mutation = HardwareMutation.NoMutation };

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
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };

        var payload = command switch
        {
            SetPerformanceModeCommand mode when MiPerformanceModeCodec.TryEncode(mode.Mode, out var wireMode) => new[] { wireMode },
            SetLidLogoCommand logo => new[] { logo.Enabled ? (byte)1 : (byte)0 },
            SetQuickSettingCommand quick => new[] { quick.Enabled ? (byte)1 : (byte)0 },
            _ => Array.Empty<byte>()
        };
        if (payload.Length == 0)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
        bool writeAttempted = false;
        try
        {
            if (!TryReadControlByte(binding!, out var before, cancellationToken))
                return Rejected(command.OperationId, ErrorCode.HardwareReadFailed) with { Mutation = HardwareMutation.NoMutation };
            var beforeEvidence = lastModeEvidence;
            PersistNativeModeRecovery(command, before, payload[0], null, cancellationToken);
            MiReadResult write;
            writeAttempted = true;
            try { write = miInterface.WriteOnceAsync(binding!, payload, cancellationToken).GetAwaiter().GetResult(); }
            catch (Exception error) when (error.Data["miWriteAttempted"] is false)
            {
                writeAttempted = false;
                throw;
            }
            if (write.Quality != DataQuality.Good)
                throw new IOException(write.Reason ?? "miWriteResponseUnknown");

            if (TryReadControlByte(binding!, out var after, cancellationToken) && after == payload[0])
            {
                PersistNativeModeRecovery(command, before, payload[0], after, cancellationToken);
                return new CommandResult(command.OperationId, CommandState.Applied,
                    command is SetPerformanceModeCommand ? null : ReadTelemetryLocked(cancellationToken),
                    RequiredUserAction.None, null, false)
                {
                    Mutation = HardwareMutation.Changed,
                    ModeEvidence = command is SetPerformanceModeCommand mode && lastModeEvidence is { } evidence
                        ? evidence with
                        {
                            RequestedMode = mode.Mode, VerificationLevel = "InterfaceAcknowledged",
                            BeforeRawResponseHex = beforeEvidence?.RawResponseHex, BeforeRawValue = before
                        }
                        : null
                };
            }

            var rollback = miInterface.WriteOnceAsync(binding!, new[] { before }, cancellationToken).GetAwaiter().GetResult();
            var restored = rollback.Quality == DataQuality.Good &&
                           TryReadControlByte(binding!, out var restoredValue, cancellationToken) && restoredValue == before;
            if (restored) PersistNativeModeRecovery(command, before, payload[0], before, cancellationToken);
            return new CommandResult(command.OperationId, restored ? CommandState.RolledBack : CommandState.RecoveryRequired,
                null, RequiredUserAction.None,
                ServiceError.Create(restored ? ErrorCode.ReadBackMismatch : ErrorCode.RollbackFailed, command.OperationId, false), false)
            {
                Mutation = HardwareMutation.Unknown,
                ModeEvidence = restored && command is SetPerformanceModeCommand requested && lastModeEvidence is { } rollbackEvidence
                    ? rollbackEvidence with { RequestedMode = requested.Mode, BeforeRawResponseHex = beforeEvidence?.RawResponseHex, BeforeRawValue = before }
                    : null
            };
        }
        catch (Exception error)
        {
            var code = writeAttempted ? ErrorCode.HardwareWriteFailed :
                error is OperationCanceledException || error.Message == "hardwareAccessPaused" ? ErrorCode.CancelledBeforeApply : ErrorCode.HardwareReadFailed;
            var detail = ServiceError.Create(code, command.OperationId, false) with
            { Details = new() { ["reason"] = error.Message, ["writeAttempted"] = writeAttempted.ToString() } };
            return new CommandResult(command.OperationId, writeAttempted ? CommandState.RecoveryRequired : CommandState.Rejected,
                null, RequiredUserAction.None, detail, false)
            { Mutation = writeAttempted ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
        }
    }

    private CommandResult ExecuteCpuTuningLocked(
        SetCpuTuningCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.RiskConfirmed || cpuTuningTransport is null || performanceController is null)
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };

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
            if (target is null or < -50 or > 0 ||
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
            BackgroundWorkPaused || hasCurveOptimizer || result.State == CommandState.Rejected ? null : ReadTelemetryLocked(cancellationToken),
            RequiredUserAction.None,
            error,
            false)
        { Mutation = result.State == CommandState.Rejected ? HardwareMutation.NoMutation : HardwareMutation.Unknown };
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
            int.TryParse(ReadManagementValue("Win32_Processor", "NumberOfCores"), out var physicalCoreCount);
            var probe = physicalCoreCount is >= 1 and <= 16 ? new PawnIoCurveOptimizerTransport(physicalCoreCount,
                hardwareAccessAllowed: () => !BackgroundWorkPaused, hardwareReadAllowed: () => CpuHardwareReadAllowed) : null;
            try
            {
                if (probe is not null && probe.TryInitialize() &&
                    probe.ReadPerCore(CancellationToken.None).Count == physicalCoreCount)
                {

                    curveOptimizer = probe;
                }
                else probe?.Dispose();
            }
            catch (Exception error)
            {
                probe?.Dispose();
                if (!CpuHardwareReadAllowed)
                {
                    curveProbeCompleted = false;
                    throw;
                }
                logger?.LogDebug(error, "Curve optimizer hardware readback unavailable");
            }
        }

        if (!pboProbeCompleted && decision.Capabilities.Items.Any(item =>
                item.Key == "cpuTuning:pbo" && item.State == CapabilityState.Available))
        {
            pboProbeCompleted = true;
            int.TryParse(ReadManagementValue("Win32_Processor", "NumberOfCores"), out var physicalCoreCount);
            var probe = physicalCoreCount is >= 1 and <= 16 ? new PawnIoCurveOptimizerTransport(physicalCoreCount,
                hardwareAccessAllowed: () => !BackgroundWorkPaused, hardwareReadAllowed: () => CpuHardwareReadAllowed) : null;
            try
            {
                if (probe is not null && probe.TryInitialize() && probe.Read(CancellationToken.None) is >= 1 and <= 10)
                    pboScalarTransport = probe;
                else
                    probe?.Dispose();
            }
            catch
            {
                probe?.Dispose();
                if (!CpuHardwareReadAllowed)
                {
                    pboProbeCompleted = false;
                    throw;
                }
            }
        }

        if (cpuTuningTransport is not null)
            return;

        powerSettings = new WindowsPowerSettingsTransport(hardwareAccessAllowed: () => !BackgroundWorkPaused,
            hardwareReadAllowed: () => CpuHardwareReadAllowed);
        cpuTuningTransport = new WindowsCpuTuningTransport(
            miInterface,
            binding!,
            powerSettings,
            curveOptimizer,
            pboScalarTransport,
            curveOptimizer ?? pboScalarTransport,
            hardwareAccessAllowed: () => !BackgroundWorkPaused,
            hardwareReadAllowed: () => CpuHardwareReadAllowed);
        performanceController = new PerformanceController(cpuTransport: cpuTuningTransport,
            hardwareAccessAllowed: () => !BackgroundWorkPaused);
    }

    private CpuTuningState? ReadCpuTuningState(CancellationToken cancellationToken, bool includeAdvanced = true)
    {
        EnsureProviderReadAccess();
        if (cpuTuningTransport is null) return null;

        try
        {
            var limitSnapshot = includeAdvanced ? ReadSmuLimits(cancellationToken) : null;
            EnsureProviderReadAccess();
            var temperatureValue = limitSnapshot?.Limits.Mp1TemperatureC;
            var splValue = limitSnapshot?.OemSplWatts;
            var fast = limitSnapshot?.Limits.FastPptWatts;
            int? spptValue = fast is double watts && watts == Math.Round(watts) ? (int)watts : null;
            var temperatureLimit = temperatureValue is >= 40 and <= 100 ? temperatureValue : null;
            int? splWatts = splValue is > 0 and <= 200 && splValue == Math.Round(splValue.Value)
                ? (int)splValue.Value : null;
            var spptWatts = spptValue is > 0 and <= 250 ? spptValue : null;
            var windows = cpuTuningTransport.ReadWindowsPowerSettingsAsync(cancellationToken).GetAwaiter().GetResult();
            var parking = windows.AcMinActiveCoresPercent is int acParking && windows.DcMinActiveCoresPercent is int dcParking
                ? new WindowsCoreParkingValue(acParking, dcParking) : null;
            var cores = includeAdvanced ? ReadOptionalCpuField(CpuTuningField.EnabledCoreCount, cancellationToken) : null;
            var curveSnapshot = includeAdvanced ? ReadOptionalCpuField(CpuTuningField.NegativeCurveOptimizer, cancellationToken) : null;
            var curveCores = curveSnapshot as IReadOnlyDictionary<int, int>;
            int? curve = curveSnapshot as int?;
            if (curveCores?.Count is > 0 and <= 16 && curveCores.Values.Distinct().Count() == 1)
                curve = curveCores.Values.First();
            var result = new CpuTuningState(
                temperatureLimit,
                splWatts,
                spptWatts,
                windows.AcMaxFrequencyMhz,
                new WindowsPowerBoostValue(windows.AcBoostMode, windows.DcBoostMode).IsEnabled,
                cores as int?,
                windows.ActiveSchemeId,
                curve,
                curveCores?.Count is > 0 and <= 16 ? "hardwareReadback" : null)
            {
                AcMaxFrequencyMhz = windows.AcMaxFrequencyMhz,
                DcMaxFrequencyMhz = windows.DcMaxFrequencyMhz,
                AcBoostMode = windows.AcBoostMode, DcBoostMode = windows.DcBoostMode,
                AcMinActiveCoresPercent = parking?.Ac is >= 0 and <= 100 ? parking.Ac : null,
                DcMinActiveCoresPercent = parking?.Dc is >= 0 and <= 100 ? parking.Dc : null,
                WindowsPowerSchemeName = cpuTuningTransport.ActivePowerSchemeName,
                PboScalar = includeAdvanced ? ReadOptionalCpuField(CpuTuningField.PboScalar, cancellationToken) as int? : null,
                PerCoreCurveOptimizer = curveCores,
                AdvancedLimits = limitSnapshot?.Limits,
                ObservedSplWatts = splValue,
                OemCustomPowerMode = cpuTuningTransport.OemCustomPowerMode
            };
            EnsureProviderReadAccess();
            return result;
        }
        catch (Exception error) when (CpuHardwareReadAllowed && error is not OperationCanceledException)
        {
            return null;
        }
    }

    private object? ReadCpuField(CpuTuningField field, CancellationToken cancellationToken) =>
        cpuTuningTransport!.ReadFieldAsync(field, cancellationToken).GetAwaiter().GetResult();

    private CpuSmuLimitSnapshot? ReadSmuLimits(CancellationToken cancellationToken)
    {
        if (smuPmTableUnsupported) return null;
        var access = CaptureSensorAccess();
        EnsureSensorReadAccess(access);
        if (!cancellationToken.IsCancellationRequested && smuLimitCacheAccess == access &&
            DateTimeOffset.UtcNow - smuLimitCacheAt < TimeSpan.FromSeconds(1)) return smuLimitCache;
        smuLimitCache = null;
        smuLimitCacheAccess = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = (curveOptimizer ?? pboScalarTransport)?.ReadLimitSnapshot(cancellationToken);
            EnsureSensorReadAccess(access);
            smuLimitCache = snapshot;
            smuLimitCacheAccess = access;
        }
        catch (InvalidOperationException error) when (error.Message == "advancedCpuPmTableUnsupported")
        { smuLimitCache = null; smuPmTableUnsupported = true; }
        catch (OperationCanceledException)
        {
            smuLimitCache = null;
            smuLimitCacheAccess = null;
            smuLimitCacheAt = default;
            throw;
        }
        catch { smuLimitCache = null; }
        smuLimitCacheAt = DateTimeOffset.UtcNow;
        return smuLimitCache;
    }

    private bool smuPmTableUnsupported;

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
        plan.TemperatureLimitC is not null ||
        plan.NegativeCurveOptimizer is not null ||
        plan.Advanced is not null;

    private void DisposeCpuTuning(Action? ensureAccess = null)
    {
        smuLimitCache = null;
        smuLimitCacheAt = default;
        smuLimitCacheAccess = null;
        smuPmTableUnsupported = false;
        ensureAccess?.Invoke();
        curveOptimizer?.Dispose();
        curveOptimizer = null;
        ensureAccess?.Invoke();
        pboScalarTransport?.Dispose();
        pboScalarTransport = null;
        ensureAccess?.Invoke();
        cpuTuningTransport = null;
        performanceController = null;
        powerSettings = null;
        curveProbeCompleted = false;
        pboProbeCompleted = false;
    }

    private PerformanceMode? ReadPerformanceMode(WmiProviderEvidence? provider, CancellationToken cancellationToken)
    {
        if (provider is null) lastModeEvidence = null;
        var value = ReadControlByte(MiHomeControlKind.PerformanceMode, provider, cancellationToken);
        return value is byte wireValue ? MiPerformanceModeCodec.Decode(wireValue) : null;
    }

    private PerformanceModeEvidence? lastModeEvidence;

    public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureProviderReadAccess();
            ReadProviderHardware(() => ReadPerformanceMode(telemetryProvider ?? observedMiProvider, token));
            EnsureProviderReadAccess();
            return Task.FromResult(lastModeEvidence);
        }
    }

    public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureProviderReadAccess();
            ReadProviderHardware(() => ReadPerformanceMode(telemetryProvider ?? observedMiProvider, token));
            if (lastModeEvidence is null) return Task.FromResult<PerformanceModeEvidence?>(null);
            var fields = new Dictionary<string, string>();
            try
            {
                if (cpuTuningTransport is not null)
                {
                    var power = ReadProviderHardware(() => cpuTuningTransport.ReadWindowsPowerSettingsAsync(token).GetAwaiter().GetResult());
                    fields["windowsPowerScheme"] = power.ActiveSchemeId.ToString();
                    fields["acFrequencyLimitMhz"] = power.AcMaxFrequencyMhz.ToString();
                    fields["dcFrequencyLimitMhz"] = power.DcMaxFrequencyMhz.ToString();
                    fields["acBoostMode"] = power.AcBoostMode.ToString();
                    fields["dcBoostMode"] = power.DcBoostMode.ToString();
                    fields["acMinActiveCoresPercent"] = power.AcMinActiveCoresPercent?.ToString() ?? "unreadable";
                    fields["dcMinActiveCoresPercent"] = power.DcMinActiveCoresPercent?.ToString() ?? "unreadable";
                }
                var cpu = ReadProviderHardware(() => ReadCpuTuningState(token));
                fields["oemTemperatureLimitC"] = cpu?.TemperatureLimitC?.ToString() ?? "unreadable";
                fields["oemSplWatts"] = ((double?)cpu?.SplWatts ?? cpu?.ObservedSplWatts)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable";
                fields["oemSpptWatts"] = ((double?)cpu?.SpptWatts ?? cpu?.AdvancedLimits?.FastPptWatts)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable";
                fields["oemCustomPowerMode"] = cpu?.OemCustomPowerMode?.ToString() ?? "unreadable";
                if (compatibilityDecision is not null && MiCpuPowerBindingFactory.TryCreate(compatibilityDecision, out var cpuBinding))
                {
                    var custom = ReadProviderHardware(() => miInterface.ReadAsync(cpuBinding!, token).GetAwaiter().GetResult());
                    fields["oemCustomRaw"] = Convert.ToHexString(custom.Payload ?? []);
                    fields["oemCustomPowerMode"] = custom.Quality == DataQuality.Good
                        ? MiCpuPowerCodec.ReadCustomMode(custom.Payload ?? [])?.ToString() ?? "unreadable" : "unreadable";
                    fields["oemCustomObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
                }
                fields["acConnected"] = ReadProviderHardware(() => SystemPowerStatusReader.Read()).AcPowerConnected?.ToString() ?? "unreadable";
                fields["limitCapturedAtUtc"] = smuLimitCacheAt.ToString("O");
            }
            catch (Exception error) when (CpuHardwareReadAllowed && error is not OperationCanceledException)
            { fields["effectiveStateReadError"] = error.GetType().Name; }
            EnsureProviderReadAccess();
            return Task.FromResult<PerformanceModeEvidence?>(lastModeEvidence with { EffectiveState = fields });
        }
    }

    private void CaptureModeEvidence(string source, int offset, byte[] payload, byte? raw)
    {
        lastModeEvidence = new(DateTimeOffset.UtcNow, source, offset, Convert.ToHexString(payload), raw,
            raw is { } value ? MiPerformanceModeCodec.Decode(value) : null, null,
            raw is null ? "ReadFailed" : "RequestStateUnverified");
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
        var offset = MiHomeControlBindingFactory.ResponseIndex(control);
        var parsed = WmiResponseParser.ReadByte(result.Payload ?? [], offset);
        var value = result.Quality == DataQuality.Good && parsed.Quality == DataQuality.Good ? parsed.Value : null;
        if (control == MiHomeControlKind.PerformanceMode)
            CaptureModeEvidence($"{provider.Namespace}/{provider.Class}/{MiHomeControlBindingFactory.MethodName(control)}", offset, result.Payload ?? [], value);
        return value;
    }

    private bool TryReadControlByte(VerifiedWmiBinding binding, out byte value, CancellationToken cancellationToken)
    {
        var result = miInterface.ReadAsync(binding, cancellationToken).GetAwaiter().GetResult();
        var parsed = WmiResponseParser.ReadByte(
            result.Payload ?? [],
            MiHomeControlBindingFactory.ResponseIndex(binding.MethodName));
        if (binding.MethodName == MiHomeControlBindingFactory.MethodName(MiHomeControlKind.PerformanceMode))
            CaptureModeEvidence($"{binding.Namespace}/{binding.Class}/{binding.MethodName}",
                MiHomeControlBindingFactory.ResponseIndex(binding.MethodName), result.Payload ?? [],
                result.Quality == DataQuality.Good && parsed.Quality == DataQuality.Good ? parsed.Value : null);
        if (result.Quality != DataQuality.Good || parsed.Value is not byte current || parsed.Quality != DataQuality.Good)
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
            if (disposed) { ReleaseGpuNativeContexts(shutdown: true); DisposeSensorResources(); return; }
            if (!BackgroundWorkPaused && lightingPreview.Original is not null)
                RestoreLightingPreviewLocked(Guid.NewGuid(), CancellationToken.None);
            disposed = true;
            try
            {
                if (!BackgroundWorkPaused)
                {
                    FinishFanLocked();
                    fanTransport?.Dispose();
                    fanTransport = null;
                }
                else if (fanTouched || strongCoolingActive || fanTransport?.HasMappingLease == true || fanPreviousStrongCooling is not null || lightingPreview.Original is not null)
                    TryLogShutdown(() => logger?.LogWarning("Shutdown hardware restoration deferred while access is paused; fan and lighting snapshots remain unconfirmed"),
                        "Shutdown fan and lighting restoration deferred while access is paused.");
            }
            catch (Exception ex) { TryLogShutdown(() => logger?.LogError(ex, "Fan EC shutdown release failed"), $"Fan EC shutdown release failed: {ex}"); }
            fanTimer?.Dispose();
            fanTimer = null;
            lightingTimer?.Dispose();
            lightingTimer = null;
            ReleaseGpuNativeContexts(shutdown: true);
            DisposeSensorResources();
        }
    }

    private static void TryLogShutdown(Action log, string fallback)
    {
        try { log(); }
        catch (Exception error)
        {
            try { System.Diagnostics.Trace.WriteLine($"{fallback}; logger failed: {error}"); }
            catch { }
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

    internal sealed record SensorReading(IHardware Hardware, SensorType SensorType, string Name, float? Value);
    private sealed record SensorSnapshot(SensorReading[] Sensors, double? CpuUsagePercent, double? CpuFrequencyMhz, DateTimeOffset CapturedAtUtc);
    internal sealed record GpuPowerSelection(int? Watts, double? RawWatts, string? RawSource, string? Source,
        DateTimeOffset? ObservedAtUtc, DateTimeOffset? RawObservedAtUtc, string Unit, string Quality,
        string RawQuality, string Reason);

    private void Collect(IHardware hardware, ICollection<SensorReading> sensors, SensorAccess access)
    {
        EnsureSensorReadAccess(access);
        try { hardware.Update(); }
        finally { EnsureSensorReadAccess(access); }
        foreach (var sensor in hardware.Sensors)
        {
            EnsureSensorReadAccess(access);
            var reading = new SensorReading(hardware, sensor.SensorType, sensor.Name, sensor.Value);
            EnsureSensorReadAccess(access);
            sensors.Add(reading);
        }
        EnsureSensorReadAccess(access);
        foreach (var child in hardware.SubHardware) Collect(child, sensors, access);
    }

    private SensorSnapshot ReadSensorSnapshotLocked()
    {
        if (disposed || !CpuHardwareReadAllowed || sensorLifecycleUnknown) return Volatile.Read(ref sensorSnapshot);
        var access = CaptureSensorAccess();
        // Sensor drivers can take seconds. Their single sampler must never own the control gate.
        if ((sensorSamplingTask is null || sensorSamplingTask.IsCompleted) &&
            DateTimeOffset.UtcNow - Volatile.Read(ref sensorSnapshot).CapturedAtUtc >= TimeSpan.FromSeconds(1))
            sensorSamplingTask = Task.Run(() =>
            {
                lock (sensorGate)
                {
                    if (disposed) return;
                    try
                    {
                        EnsureSensorReadAccess(access);
                        if (sensorLifecycleUnknown) return;
                        if (!opened) OpenSensorComputer(access);
                        var sensors = new List<SensorReading>();
                        foreach (var hardware in getSensorHardware()) Collect(hardware, sensors, access);
                        EnsureSensorReadAccess(access);
                        var cpu = sensors.Where(sensor => sensor.Hardware.HardwareType == HardwareType.Cpu).ToArray();
                        double? usage = Preferred(cpu, SensorType.Load, "CPU Total", "Total");
                        if (usage is null) { EnsureSensorReadAccess(access); usage = ReadCpuUsagePercent(); EnsureSensorReadAccess(access); }
                        double? frequency = Average(cpu, SensorType.Clock, "Core");
                        if (frequency is null) { EnsureSensorReadAccess(access); frequency = ReadCpuFrequencyMhz(); EnsureSensorReadAccess(access); }
                        var snapshot = new SensorSnapshot(sensors.ToArray(), usage, frequency, DateTimeOffset.UtcNow);
                        EnsureSensorReadAccess(access);
                        Volatile.Write(ref sensorSnapshot, snapshot);
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
                .OrderByDescending(item => AdapterMatches(item.Name, identity.GpuName))
                .ThenBy(item => item.HardwareType == HardwareType.GpuNvidia ? 0 : item.HardwareType == HardwareType.GpuAmd ? 1 : 2)
                .FirstOrDefault();
            var gpu = sensors.Where(item => ReferenceEquals(item.Hardware, gpuHardware)).ToArray();
            var memory = sensors.Where(item => item.Hardware.HardwareType == HardwareType.Memory &&
                item.Hardware.Identifier.ToString() == "/ram").ToArray();
            var fans = sensors.Where(item => item.SensorType == SensorType.Fan).ToArray();
            var verifiedNvidiaAdapter = identity.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            var nvidiaReading = verifiedNvidiaAdapter
                ? nvidiaSmiReader.ReadAsync(cancellationToken, identity.GpuName).GetAwaiter().GetResult()
                : NvidiaSmiReading.Unknown;
            var nvidiaObservedAtUtc = DateTimeOffset.UtcNow;
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
            var hardwareCpuTemperature = ReadMiCpuTemperature(readProvider, cancellationToken);
            var miCpuTemperature = hardwareCpuTemperature ??
                (DateTimeOffset.UtcNow - sensorSample.CapturedAtUtc <= TimeSpan.FromSeconds(3) ? cpuTemperature : null);
            var cpuPowerWatts = RoundInt(Preferred(cpu, SensorType.Power, "Package", "CPU")) ??
                RoundInt(packagePower);
            var gpuUsagePercent = Preferred(gpu, SensorType.Load, "GPU Core", "Core", "D3D 3D") ?? nvidiaReading.UtilizationPercent;
            var gpuPower = SelectGpuPower(gpu, sensorSample.CapturedAtUtc, nvidiaReading, nvidiaObservedAtUtc,
                identity.GpuName, gpuUsagePercent, previousGpuPowerWatts, previousGpuUsagePercent, previousGpuPowerAtUtc);
            if (gpuPower.Quality == "good" && gpuPower.Watts is int confirmedWatts && gpuUsagePercent is double usage)
            {
                previousGpuPowerWatts = confirmedWatts;
                previousGpuUsagePercent = (int)Math.Round(usage);
                previousGpuPowerAtUtc = gpuPower.ObservedAtUtc ?? DateTimeOffset.UtcNow;
            }
            (double? CpuRpm, double? GpuRpm) miFans = cpuFanRpm.HasValue && gpuFanRpm.HasValue
                ? (null, null)
                : ReadMiFanTelemetry(readProvider, cancellationToken);
            var snapshot = new HardwareSnapshot(
                DateTimeOffset.UtcNow,
                miCpuTemperature.HasValue || gpuTemperature.HasValue ? "normal" : "unknown",
                miCpuTemperature,
                gpuTemperature,
                cpuPowerWatts,
                gpuPower.Watts)
            {
                CpuUsagePercent = sensorSample.CpuUsagePercent,
                CpuUsageObservedAtUtc = sensorSample.CapturedAtUtc,
                GpuUsageObservedAtUtc = Preferred(gpu, SensorType.Load, "GPU Core", "Core", "D3D 3D") is null ? nvidiaObservedAtUtc : sensorSample.CapturedAtUtc,
                CpuTemperatureObservedAtUtc = hardwareCpuTemperature.HasValue ? DateTimeOffset.UtcNow : sensorSample.CapturedAtUtc,
                GpuTemperatureObservedAtUtc = Preferred(gpu, SensorType.Temperature, "GPU Core", "Core") is null ? nvidiaObservedAtUtc : sensorSample.CapturedAtUtc,
                BatterySaver = SystemPowerStatusReader.ReadBatterySaver(),
                CpuFrequencyMhz = sensorSample.CpuFrequencyMhz,
                CpuVoltageVolts = cpuVoltage,
                GpuUsagePercent = gpuUsagePercent,
                GpuFrequencyMhz = Preferred(gpu, SensorType.Clock, "Core", "Graphics"),
                GpuCoreVoltageVolts = Preferred(gpu, SensorType.Voltage, "GPU Core", "Core"),
                GpuPerformanceState = nvidiaReading.PerformanceState,
                GpuPerformanceLimitReason = nvidiaReading.PerformanceLimitReason,
                GpuEnforcedPowerLimitWatts = nvidiaReading.EnforcedPowerLimitWatts,
                GpuMaximumPowerLimitWatts = nvidiaReading.MaximumPowerLimitWatts,
                GpuPowerRawWatts = gpuPower.RawWatts,
                GpuPowerRawObservedAtUtc = gpuPower.RawObservedAtUtc,
                GpuPowerObservedAtUtc = gpuPower.ObservedAtUtc,
                GpuPowerRawSource = gpuPower.RawSource,
                GpuPowerSource = gpuPower.Source,
                GpuPowerUnit = gpuPower.Unit,
                GpuPowerQuality = gpuPower.Quality,
                GpuPowerRawQuality = gpuPower.RawQuality,
                GpuPowerSelectionReason = gpuPower.Reason,
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

    internal static GpuPowerSelection SelectGpuPower(
        IEnumerable<SensorReading> sensors,
        DateTimeOffset sensorAtUtc,
        NvidiaSmiReading driver,
        DateTimeOffset driverAtUtc,
        string expectedAdapter,
        double? gpuUsagePercent,
        int? previousWatts,
        int? previousUsagePercent,
        DateTimeOffset previousAtUtc)
    {
        var sensor = sensors.Where(item => item.SensorType == SensorType.Power &&
                item.Hardware.HardwareType == HardwareType.GpuNvidia &&
                AdapterMatches(item.Hardware.Name, expectedAdapter) && item.Value.HasValue &&
                (item.Name.Contains("GPU Package", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Name.Contains("GPU Package", StringComparison.OrdinalIgnoreCase) ? 0 :
                item.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault();
        var rawSensor = sensor?.Value is float raw && float.IsFinite(raw) ? raw : (double?)null;
        var freshSensor = DateTimeOffset.UtcNow - sensorAtUtc is var sensorAge &&
            sensorAge >= TimeSpan.Zero && sensorAge <= TimeSpan.FromSeconds(3);
        var sensorValid = sensor is not null && rawSensor is >= 0 and <= int.MaxValue && freshSensor;
        var driverFresh = DateTimeOffset.UtcNow - driverAtUtc is var driverAge &&
            driverAge >= TimeSpan.Zero && driverAge <= TimeSpan.FromSeconds(3);
        var driverValid = driverFresh && driver.Quality == DataQuality.Good && driver.RawPowerWatts is double driverRaw &&
            double.IsFinite(driverRaw) && driverRaw is >= 0 and <= int.MaxValue && !string.IsNullOrWhiteSpace(driver.AdapterName) &&
            AdapterMatches(driver.AdapterName, expectedAdapter);
        var maximum = driverValid && driver.MaximumPowerLimitWatts is double max && double.IsFinite(max) && max is >= 10 and <= 5000
            ? max
            : (double?)null;
        var sensorOverRange = sensorValid && maximum.HasValue && rawSensor > maximum * 1.15;
        var driverOverRange = driverValid && maximum.HasValue && driver.RawPowerWatts > maximum * 1.15;
        var severeConflict = sensorValid && driverValid && (maximum is null || sensorOverRange) &&
            rawSensor > driver.RawPowerWatts * 4 &&
            rawSensor - driver.RawPowerWatts > 100;
        var previousFresh = previousWatts.HasValue && previousUsagePercent.HasValue &&
            DateTimeOffset.UtcNow - previousAtUtc is var previousAge && previousAge >= TimeSpan.Zero && previousAge <= TimeSpan.FromSeconds(3);
        var isolatedUnknownRangeSpike = !maximum.HasValue && sensorValid && previousFresh &&
            gpuUsagePercent is <= 2 && previousUsagePercent <= 2 &&
            rawSensor > previousWatts * 4 && rawSensor - previousWatts > 100;
        var rawValue = rawSensor ?? (driverValid ? driver.RawPowerWatts : null);
        var rawSource = sensor?.Value.HasValue == true && sensor is not null
            ? $"LibreHardwareMonitor:{sensor.Hardware.Name}/{sensor.Name}"
            : driverValid ? $"nvidia-smi:{driver.AdapterName}/{driver.AdapterUuid ?? "unknownUuid"}" : null;
        DateTimeOffset? rawAt = sensor?.Value.HasValue == true ? sensorAtUtc : driverValid ? driverAtUtc : null;
        var rawQuality = rawSensor.HasValue
            ? !sensorValid || sensorOverRange || severeConflict || isolatedUnknownRangeSpike ? "invalid" : "good"
            : sensor?.Value.HasValue == true ? "invalid" : driverValid ? "good" : "unknown";
        if (sensorValid && !sensorOverRange && !severeConflict && !isolatedUnknownRangeSpike)
            return new(RoundInt(rawSensor), rawValue, rawSource, "LibreHardwareMonitor", sensorAtUtc, rawAt,
                "W", "good", "good", "freshSensorWithinDriverRangeOrUnbounded");
        if (driverValid && !driverOverRange)
            return new(RoundInt(driver.RawPowerWatts), rawValue, rawSource, "nvidia-smi", driverAtUtc, rawAt,
                "W", "good", rawQuality,
                sensorOverRange ? "sensorAboveDriverMaximum" : severeConflict ? "sensorConflictsWithDriver" :
                isolatedUnknownRangeSpike ? "unboundedSpikeAtIdle" :
                !sensorValid ? "sensorUnavailableOrStale" : "driverFallback");
        return new(null, rawValue, rawSource, null, null, rawAt, "W",
            rawValue.HasValue ? "invalid" : "unknown", rawValue.HasValue ? "invalid" : "unknown",
            sensorOverRange ? "sensorAboveDriverMaximumWithoutFallback" :
            driverOverRange ? "driverAboveItsMaximumWithoutFallback" :
            isolatedUnknownRangeSpike ? "unboundedSpikeAtIdleWithoutFallback" : "noFreshVerifiedNvidiaPowerSource");
    }

    private static bool AdapterMatches(string? actual, string expected) =>
        !string.IsNullOrWhiteSpace(actual) && !string.IsNullOrWhiteSpace(expected) &&
        (actual.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
         expected.Contains(actual, StringComparison.OrdinalIgnoreCase));

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
