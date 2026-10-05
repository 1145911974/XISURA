using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Home;

public sealed record AdaptiveAutomationRecovery(
    PerformanceMode? OriginalMode,
    CpuTuningPlan? OriginalCpuWindowsTuning,
    PerformanceMode? LastAppliedMode,
    bool Pending = false,
    bool Active = false);

public sealed record AdaptiveAutomationStoreSnapshot(
    AdaptiveAutomationConfiguration? Configuration,
    AdaptiveAutomationClientContext? ClientContext,
    DateTimeOffset? ManualOverrideUntilUtc,
    AdaptiveAutomationRecovery? Recovery,
    AdaptiveAutomationStatus? Status,
    bool ManualModeChanged,
    long Revision);

public sealed class AdaptiveAutomationStateStore
{
    public const int MaximumBytes = 256 * 1024;
    private const string FileName = "adaptive-automation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private AtomicJsonStore? jsonStore;
    private AdaptiveAutomationConfiguration? configuration;
    private AdaptiveAutomationRecovery? recovery;
    private AdaptiveAutomationClientContext? clientContext;
    private DateTimeOffset? manualOverrideUntilUtc;
    private AdaptiveAutomationStatus? status;
    private bool manualModeChanged;
    private long revision;
    private bool loaded;
    private string? loadFailure;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await EnsureLoadedLockedAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<AdaptiveAutomationStoreSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            return new(configuration, clientContext, manualOverrideUntilUtc, recovery, status, manualModeChanged, revision);
        }
        finally { gate.Release(); }
    }

    public async Task SetConfigurationAsync(AdaptiveAutomationConfiguration value, CancellationToken cancellationToken)
    {
        AdaptiveAutomationConfigurationValidator.Validate(value);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            var prior = configuration;
            configuration = AdaptiveAutomationConfigurationValidator.Clone(value);
            try { await SaveLockedAsync(cancellationToken); }
            catch { configuration = prior; throw; }
            loadFailure = null;
            revision++;
            // Applied must expose this persisted configuration immediately, even
            // while disabled or before the worker's next evaluation tick.
            status = new AdaptiveAutomationStatus(configuration.Enabled, false, null,
                configuration.Enabled ? "调度配置已保存，等待服务判定。" : "自动调度已关闭。",
                status?.LastEvaluationUtc, status?.LastApplyUtc, null,
                AdaptiveAutomationConfigurationValidator.Identity(configuration));
        }
        finally { gate.Release(); }
    }

    public async Task SetClientContextAsync(AdaptiveAutomationClientContext context, CancellationToken cancellationToken)
    {
        ValidateContext(context);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            clientContext = context;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> TryBeginRecoveryAsync(
        AdaptiveAutomationRecovery value,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            if (revision != expectedRevision || manualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
                return false;
            var prior = recovery;
            recovery = value;
            try { await SaveLockedAsync(cancellationToken); }
            catch { recovery = prior; throw; }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ReplaceOwnedRecoveryAsync(
        AdaptiveAutomationRecovery? value,
        AdaptiveAutomationRecovery expectedOwner,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            if (recovery != expectedOwner || manualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
                return false;
            var prior = recovery;
            recovery = value;
            try { await SaveLockedAsync(cancellationToken); }
            catch { recovery = prior; throw; }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task MarkManualModeChangeAsync(DateTimeOffset now, bool modeChanged, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            var dwell = configuration?.Policy.MinimumDwellSeconds ?? 90;
            manualOverrideUntilUtc = now.AddSeconds(Math.Clamp(dwell, 1, 3600));
            manualModeChanged = modeChanged;
            recovery = null;
            await SaveLockedAsync(cancellationToken);
            revision++;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CompleteRecoveryAsync(
        AdaptiveAutomationRecovery value,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            if (revision != expectedRevision || manualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until ||
                recovery is not { Pending: true } current || current.LastAppliedMode != value.LastAppliedMode)
                return false;
            var prior = recovery;
            recovery = value;
            try { await SaveLockedAsync(cancellationToken); }
            catch { recovery = prior; throw; }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task ClearRecoveryAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            if (recovery is null) return;
            recovery = null;
            await SaveLockedAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task SetStatusAsync(AdaptiveAutomationStatus value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            string? currentIdentity = configuration is null ? null : AdaptiveAutomationConfigurationValidator.Identity(configuration);
            if (value.OwnerConfigurationIdentity != currentIdentity || value.Enabled != (configuration?.Enabled == true)) return;
            status = value with { LastError = loadFailure ?? value.LastError };
        }
        finally { gate.Release(); }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        var root = Path.GetFullPath(MachinePaths.BaseDirectory);
        if (!root.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Adaptive automation storage must reside on C:\\ProgramData.");
        MachinePaths.EnsureSafeDirectory(root);
        jsonStore = new AtomicJsonStore(root, FileName);
        MachinePaths.EnsureSafeFile(jsonStore.FilePath);
        try
        {
            if (File.Exists(jsonStore.FilePath))
            {
                var info = new FileInfo(jsonStore.FilePath);
                if (info.Length > MaximumBytes) throw new InvalidDataException("Adaptive automation state exceeds 256 KB.");
                var saved = await jsonStore.ReadAsync<PersistedState>(cancellationToken);
                if (saved is not null)
                {
                    if (saved.Configuration is not null) AdaptiveAutomationConfigurationValidator.Validate(saved.Configuration);
                    ValidateRecovery(saved.Recovery);
                    configuration = saved.Configuration;
                    recovery = saved.Recovery;
                    manualOverrideUntilUtc = saved.ManualOverrideUntilUtc;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A broken scheduling file must not break all hardware telemetry and unrelated commands.
            configuration = null;
            recovery = null;
            loadFailure = "调度配置读取失败；自动执行已停止，原文件保留。";
        }
        loaded = true;
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        var saved = new PersistedState(configuration, recovery, manualOverrideUntilUtc);
        if (JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions).Length > MaximumBytes)
            throw new InvalidDataException("Adaptive automation state exceeds 256 KB.");
        await jsonStore!.WriteAsync(saved, cancellationToken);
    }

    private static void ValidateContext(AdaptiveAutomationClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RunningExecutables is { } running && (running.Length > 8 || running.Any(executable =>
            string.IsNullOrWhiteSpace(executable) || executable.Length > 120 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executable.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || executable.Any(char.IsControl))))
            throw new ArgumentException("Client running-process context is invalid.");
        if (context.IdleSeconds is < 0 or > 86_400 || context.ForegroundExecutable is { } exe &&
            (exe.Length > 120 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             exe.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || exe.Any(char.IsControl)))
            throw new ArgumentException("Client automation context is invalid.");
    }

    private static void ValidateRecovery(AdaptiveAutomationRecovery? value)
    {
        if (value is null) return;
        if (value.OriginalMode is { } original && !Enum.IsDefined(original) ||
            value.LastAppliedMode is { } applied && !Enum.IsDefined(applied) ||
            value.OriginalCpuWindowsTuning is { } cpu && !AdaptiveAutomationConfigurationValidator.IsSafeRestorableCpuPlan(cpu))
            throw new InvalidDataException("Persisted adaptive recovery state is invalid.");
    }

    private sealed record PersistedState(
        AdaptiveAutomationConfiguration? Configuration,
        AdaptiveAutomationRecovery? Recovery,
        DateTimeOffset? ManualOverrideUntilUtc);
}

public sealed class AdaptiveAutomationHardwareProvider(
    IHomeHardwareProvider inner,
    AdaptiveAutomationStateStore store) : IHomeHardwareProvider
{
    private const string ConfigurationCapability = nameof(SetAdaptiveAutomationConfigurationCommand);
    private const string ContextCapability = nameof(UpdateAdaptiveAutomationContextCommand);
    private readonly ConcurrentDictionary<Guid, long> automaticOperations = new();
    private readonly SemaphoreSlim cpuModeCommandGate = new(1, 1);

    public async Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken) =>
        AddAutomationCapabilities(await inner.DiagnoseAsync(cancellationToken));

    public async Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken) =>
        AddAutomationCapabilities(await inner.ReinitializeAsync(cancellationToken));

    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken) => inner.ReadTelemetryAsync(cancellationToken);

    public async Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken)
    {
        var controls = await inner.ReadControlsAsync(cancellationToken);
        var snapshot = await store.ReadAsync(cancellationToken);
        return controls with { AdaptiveAutomation = snapshot.Status };
    }

    public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command)
            {
                case SetAdaptiveAutomationConfigurationCommand set:
                    await cpuModeCommandGate.WaitAsync(cancellationToken);
                    try
                    {
                        await store.SetConfigurationAsync(set.Configuration, cancellationToken);
                        return Applied(command.OperationId);
                    }
                    finally { cpuModeCommandGate.Release(); }
                case UpdateAdaptiveAutomationContextCommand context:
                    await store.SetClientContextAsync(context.Context, cancellationToken);
                    return Applied(command.OperationId);
            }
        }
        catch (ArgumentException)
        {
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        }
        catch (InvalidDataException)
        {
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        }
        catch (IOException)
        {
            return Rejected(command.OperationId, ErrorCode.ServiceUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Rejected(command.OperationId, ErrorCode.ServiceUnavailable);
        }

        bool serialize = command is SetPerformanceModeCommand or SetCpuTuningCommand or SetCpuTuningBatchCommand;
        if (serialize) await cpuModeCommandGate.WaitAsync(cancellationToken);
        try
        {
            if (serialize && automaticOperations.TryGetValue(command.OperationId, out var expectedRevision))
            {
                var automation = await store.ReadAsync(cancellationToken);
                if (automation.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
                    return Rejected(command.OperationId, ErrorCode.CommandInProgress);
                if (expectedRevision >= 0 && automation.Revision != expectedRevision)
                    return Rejected(command.OperationId, ErrorCode.CommandInProgress);
            }
            var result = await inner.ExecuteAsync(command, cancellationToken);
            if (serialize && result.State == CommandState.Applied && result.Error is null &&
                !automaticOperations.ContainsKey(command.OperationId))
                await store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow,
                    command is SetPerformanceModeCommand or SetCpuTuningBatchCommand { NativeMode: not null }, CancellationToken.None);
            return result;
        }
        finally { if (serialize) cpuModeCommandGate.Release(); }
    }

    public async Task<CommandResult> ExecuteAutomaticCommandAsync(
        HomeServiceRuntime runtime,
        HardwareCommand command,
        CancellationToken cancellationToken,
        long? expectedConfigurationRevision = null)
    {
        var state = await store.ReadAsync(cancellationToken);
        if (state.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
            return new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CommandInProgress, command.OperationId, true), false);
        if (expectedConfigurationRevision is long expected && state.Revision != expected)
            return new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CommandInProgress, command.OperationId, true), false);
        automaticOperations.TryAdd(command.OperationId, expectedConfigurationRevision ?? -1);
        try { return await runtime.ExecuteAsync(command, cancellationToken); }
        finally { automaticOperations.TryRemove(command.OperationId, out _); }
    }

    private static HomeHardwareState AddAutomationCapabilities(HomeHardwareState state)
    {
        var items = state.Capabilities.Items
            .Where(item => item.Key != ConfigurationCapability && item.Key != ContextCapability)
            .Append(new CapabilityDescriptor(ConfigurationCapability, CapabilityState.Available, null))
            .Append(new CapabilityDescriptor(ContextCapability, CapabilityState.Available, null))
            .ToArray();
        return state with { Capabilities = state.Capabilities with { Items = items } };
    }

    private static CommandResult Applied(Guid id) => new(id, CommandState.Applied, null, RequiredUserAction.None, null, false);

    private static CommandResult Rejected(Guid id, ErrorCode error) =>
        new(id, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(error, id, false), false);
}

public static class AdaptiveAutomationConfigurationValidator
{
    public static void Validate(AdaptiveAutomationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Policy);
        ArgumentNullException.ThrowIfNull(configuration.TargetMap);
        ArgumentNullException.ThrowIfNull(configuration.Presets);
        if (configuration.Policy.ApplicationRules is null || configuration.Policy.ApplicationRules.Any(rule => rule is null) ||
            configuration.Presets.Any(preset => preset is null))
            throw new ArgumentException("Adaptive automation configuration contains a missing rule or preset.");
        if (configuration.Policy.ApplicationRules.Any(rule => !Enum.IsDefined(rule.Target)))
            throw new ArgumentException("Adaptive automation rule target is invalid.");
        if (!Enum.IsDefined(configuration.Strategy) || configuration.Presets.Length > 18 ||
            (configuration.Enabled && configuration.Presets.Length == 0))
            throw new ArgumentException("Adaptive automation configuration is invalid.");

        var policy = ToTriggerPolicy(configuration);
        policy.Validate();
        var map = ToTargetMap(configuration.TargetMap);
        map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Office, map.AcOffice);
        map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Game, map.AcGame);
        map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Turbo, map.AcTurbo);
        if (configuration.Enabled)
        {
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Office, map.DcOffice);
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, map.DcGame);
        }
        var required = new[] { map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame };
        foreach (var key in required) PresetKey.Create(key.Mode, key.Slot);
        var profiles = configuration.Presets.ToDictionary(preset => PresetKey.Create(preset.Key.Mode, preset.Key.Slot));
        if (configuration.Enabled && required.Any(key => !profiles.ContainsKey(key)))
            throw new ArgumentException("Every automatic target must reference a saved preset snapshot.");
        foreach (var preset in configuration.Presets)
        {
            PresetKey.Create(preset.Key.Mode, preset.Key.Slot);
            var expectedMode = AdaptiveTargetMap.HardwareModeFor(preset.Key);
            if (preset.PerformanceMode != expectedMode || preset.CpuWindowsTuning is { } cpu && !IsSafeRestorableCpuPlan(cpu))
                throw new ArgumentException("Preset snapshots may contain only the saved mode, restorable Windows CPU tuning, PBO Scalar and CO.");
        }
    }

    public static AdaptiveAutomationConfiguration Clone(AdaptiveAutomationConfiguration configuration) => configuration with
    {
        Policy = configuration.Policy with { ApplicationRules = configuration.Policy.ApplicationRules.ToArray() },
        Presets = configuration.Presets.Select(preset => preset with
        {
            CpuWindowsTuning = preset.CpuWindowsTuning is { } cpu ? cpu with {
                Advanced = cpu.Advanced is { } advanced ? advanced with {
                    PerCoreCurveOptimizer = advanced.PerCoreCurveOptimizer is { } cores ? new Dictionary<int,int>(cores) : null
                } : null
            } : null
        }).ToArray()
    };

    public static bool IsSafeRestorableCpuPlan(CpuTuningPlan plan)
    {
        if (plan.TemperatureLimitC is not null || plan.SplWatts is not null || plan.SpptWatts is not null ||
            plan.EnabledCoreCount is not null ||
            plan.NegativeCurveOptimizer is < -30 or > 0 ||
            plan.Advanced is { } advanced &&
            ((advanced with { PboScalar = null, CurveOptimizerAll = null, PerCoreCurveOptimizer = null }) != new AdvancedCpuTuningPlan() ||
             advanced.CurveOptimizerAll is < -30 or > 0 ||
             advanced.PerCoreCurveOptimizer is { } cores && (cores.Count is < 1 or > 8 ||
                 cores.Any(pair => pair.Key is < 0 or > 7 || pair.Value is < -30 or > 0))))
            return false;
        if (plan.AcMaxFrequencyMhz.HasValue != plan.DcMaxFrequencyMhz.HasValue ||
            plan.AcMinActiveCoresPercent.HasValue != plan.DcMinActiveCoresPercent.HasValue ||
            plan.MaxFrequencyMhz is not null && (plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null))
            return false;
        var result = CommandValidation.Validate(new SetCpuTuningCommand(Guid.NewGuid(), plan, true));
        return result is null && (plan.MaxFrequencyMhz is not null || plan.AcMaxFrequencyMhz is not null ||
            plan.BoostEnabled is not null || plan.WindowsPowerSchemeId is not null || plan.AcMinActiveCoresPercent is not null ||
            plan.Advanced?.PboScalar is not null || plan.NegativeCurveOptimizer is not null ||
            plan.Advanced?.CurveOptimizerAll is not null || plan.Advanced?.PerCoreCurveOptimizer is { Count: > 0 });
    }

    public static AdaptiveTriggerPolicy ToTriggerPolicy(AdaptiveAutomationConfiguration configuration)
    {
        var p = configuration.Policy;
        return new AdaptiveTriggerPolicy(p.GameCpuPercent, p.GameGpuPercent, p.GameSeconds,
            p.TurboEnabled, p.TurboCpuPercent, p.TurboGpuPercent, p.TurboSeconds,
            p.OfficeCpuPercent, p.OfficeGpuPercent, p.OfficeSeconds)
        {
            Advanced = new AdaptiveAdvancedSettings
            {
                LowBatteryPercent = p.LowBatteryPercent,
                BatteryRecoveryPercent = p.BatteryRecoveryPercent,
                RespectBatterySaver = p.RespectBatterySaver,
                CpuTemperatureCeiling = p.CpuTemperatureCeiling,
                GpuTemperatureCeiling = p.GpuTemperatureCeiling,
                CooldownSeconds = p.CooldownSeconds,
                MinimumDwellSeconds = p.MinimumDwellSeconds,
                ApplicationSeconds = p.ApplicationSeconds,
                IdleReturnEnabled = p.IdleReturnEnabled,
                IdleSeconds = p.IdleSeconds,
                ApplicationRules = p.ApplicationRules.Select(rule => new AdaptiveApplicationRule(
                    rule.Executable, rule.ForegroundOnly, ToStage(rule.Target))).ToArray()
            }
        };
    }

    public static AdaptiveTargetMap ToTargetMap(AdaptiveAutomationTargetMap map) => new(
        map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame);

    public static string Identity(AdaptiveAutomationConfiguration configuration) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration)))[..16];

    public static AdaptiveStage ToStage(AdaptiveAutomationStageId stage) => stage switch
    {
        AdaptiveAutomationStageId.Office => AdaptiveStage.Office,
        AdaptiveAutomationStageId.Game => AdaptiveStage.Game,
        _ => AdaptiveStage.Turbo
    };
}
