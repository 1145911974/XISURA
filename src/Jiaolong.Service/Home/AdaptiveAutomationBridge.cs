using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
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
    long Revision)
{
    public string? OwnerSid { get; init; }
}
public sealed record AdaptiveDecisionRecord(DateTimeOffset RecordedAtUtc, double MonotonicSeconds, long ConfigurationRevision,
    long HardwareEpoch, PerformanceMode? NativeMode, AdaptiveStage? Candidate,
    AdaptiveFilterEvidence Evidence, double? CpuSampleAgeSeconds, double? GpuSampleAgeSeconds,
    string Reason, Guid? OperationId = null, PageApplyResult[]? Pages = null);

public sealed class AdaptiveAutomationStateStore
{
    public const int MaximumBytes = 256 * 1024;
    private const string FileName = "adaptive-automation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private AtomicJsonStore? jsonStore;
    private AdaptiveAutomationConfiguration? configuration;
    private string? configurationOwnerSid;
    private AdaptiveAutomationRecovery? recovery;
    private AdaptiveAutomationClientContext? clientContext;
    private DateTimeOffset? manualOverrideUntilUtc;
    private AdaptiveAutomationStatus? status;
    private bool manualModeChanged;
    private long revision;
    private bool loaded;
    private string? loadFailure;
    private readonly Queue<AdaptiveDecisionRecord> decisions = new();
    public void RecordDecision(AdaptiveDecisionRecord decision)
    {
        lock (decisions)
        {
            decisions.Enqueue(decision);
            while (decisions.Count > 600) decisions.Dequeue();
        }
    }
    public AdaptiveDecisionRecord[] ReadDecisions() { lock (decisions) return decisions.ToArray(); }
    public async Task<object> ReadDiagnosticsAsync(CancellationToken token, string? requestingSid = null)
    {
        var saved = await ReadAsync(token);
        // Machine diagnostics contain control policy, never another console's executable names or preset payloads.
        bool ownsConfiguration = requestingSid is not null && requestingSid == saved.OwnerSid;
        var configuration = ownsConfiguration ? saved.Configuration : null;
        return new { configuration = configuration is null ? null : new
            { configuration.Enabled, configuration.Strategy, configuration.Revision, configuration.CatalogRevision,
              policy = configuration.Policy with { ApplicationRules = [] },
              applicationRules = configuration.Policy.ApplicationRules.Select(rule => new { rule.ForegroundOnly, rule.Target, rule.RequireActivity }),
              configuration.TargetMap,
              presets = configuration.Presets.Select(preset => new { preset.Key, preset.PerformanceMode,
                  preset.Selections, pages = preset.Pages.Select(page => new { page.Page, page.Key, page.Revision }) }) },
            configurationRestricted = saved.Configuration is not null && !ownsConfiguration,
            saved.Revision, status = saved.Status is null ? null : saved.Status with { Reason = DiagnosticReason(saved.Status.Reason) },
            recoveryRetained = saved.Recovery is not null, saved.ManualModeChanged,
            saved.ManualOverrideUntilUtc, loadFailure,
            decisions = ReadDecisions().Where(decision => ownsConfiguration && decision.ConfigurationRevision == saved.Revision)
                .Select(decision => decision with { Reason = DiagnosticReason(decision.Reason)! }) };
    }

    internal static string? DiagnosticReason(string? reason) => reason is null ? null :
        System.Text.RegularExpressions.Regex.Replace(reason, @"[^\s\""'，。；：、()（）]+\.exe", "<application>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));

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
            return new(configuration, clientContext, manualOverrideUntilUtc, recovery, status, manualModeChanged, revision)
                { OwnerSid = configurationOwnerSid };
        }
        finally { gate.Release(); }
    }

    public async Task SetConfigurationAsync(AdaptiveAutomationConfiguration value, CancellationToken cancellationToken,
        long? expectedRevision = null, AutomationConfigurationIntent? intent = null, string? ownerSid = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            if (expectedRevision is long expected && revision != expected)
                throw new InvalidOperationException("adaptiveRevisionConflict");
            var prior = configuration;
            var priorOwner = configurationOwnerSid;
            var priorOverride = manualOverrideUntilUtc;
            bool priorManualModeChanged = manualModeChanged;
            bool enabled = intent switch
            {
                AutomationConfigurationIntent.Enable => true,
                AutomationConfigurationIntent.Disable => false,
                AutomationConfigurationIntent.Update => prior?.Enabled == true,
                _ => value.Enabled
            };
            if (enabled && recovery is not null)
                throw new InvalidOperationException("adaptiveRecoveryPending");
            var candidate = value with { Enabled = enabled, Revision = revision + 1 };
            AdaptiveAutomationConfigurationValidator.Validate(candidate);
            configuration = AdaptiveAutomationConfigurationValidator.Clone(candidate);
            configurationOwnerSid = ownerSid;
            if (configuration.Enabled && prior?.Enabled != true)
            {
                manualOverrideUntilUtc = null;
                manualModeChanged = false;
            }
            try { await SaveLockedAsync(cancellationToken); }
            catch
            {
                configuration = prior;
                configurationOwnerSid = priorOwner;
                manualOverrideUntilUtc = priorOverride;
                manualModeChanged = priorManualModeChanged;
                throw;
            }
            loadFailure = null;
            revision++;
            // Applied must expose this persisted configuration immediately, even
            // while disabled or before the worker's next evaluation tick.
            status = new AdaptiveAutomationStatus(configuration.Enabled, false, null,
                configuration.Enabled ? "调度配置已保存，等待服务判定。" : "自动调度已关闭。",
                status?.LastEvaluationUtc, status?.LastApplyUtc, null,
                AdaptiveAutomationConfigurationValidator.Identity(configuration)) { Revision = revision };
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
            if (recovery is not null) return;
            if (configuration?.Enabled != true && manualOverrideUntilUtc == DateTimeOffset.MaxValue && (!modeChanged || manualModeChanged)) return;
            var priorConfiguration = configuration;
            var priorOverride = manualOverrideUntilUtc;
            bool priorModeChanged = manualModeChanged;
            if (configuration is not null) configuration = configuration with { Enabled = false, Revision = revision + 1 };
            // Legacy recovery readers also honor this persisted fence; it has no timed expiry.
            manualOverrideUntilUtc = DateTimeOffset.MaxValue;
            manualModeChanged |= modeChanged;
            try { await SaveLockedAsync(cancellationToken); }
            catch
            {
                configuration = priorConfiguration;
                manualOverrideUntilUtc = priorOverride;
                manualModeChanged = priorModeChanged;
                throw;
            }
            revision++;
            status = new(false, false, null, "手动操作已关闭自动调度。", status?.LastEvaluationUtc,
                status?.LastApplyUtc, null, configuration is null ? null : AdaptiveAutomationConfigurationValidator.Identity(configuration)) { Revision = revision };
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
            status = value with { LastError = loadFailure ?? value.LastError, Revision = revision };
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
                    configurationOwnerSid = saved.OwnerSid;
                    recovery = saved.Recovery;
                    manualOverrideUntilUtc = saved.ManualOverrideUntilUtc;
                    revision = saved.Revision;
                    status = configuration is null ? null : new(configuration.Enabled, false, null,
                        configuration.Enabled ? "等待新鲜遥测暖机。" : "自动调度已关闭。", null, null, null,
                        AdaptiveAutomationConfigurationValidator.Identity(configuration)) { Revision = revision };
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
        var saved = new PersistedState(configuration, recovery, manualOverrideUntilUtc, configuration?.Revision ?? revision)
            { OwnerSid = configurationOwnerSid };
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
        DateTimeOffset? ManualOverrideUntilUtc,
        long Revision = 0)
    {
        public string? OwnerSid { get; init; }
    }
}

public sealed partial class AdaptiveAutomationHardwareProvider(
    IHomeHardwareProvider inner,
    AdaptiveAutomationStateStore store) : IHomeHardwareProvider
{
    private const string ConfigurationCapability = nameof(SetAdaptiveAutomationConfigurationCommand);
    private const string ContextCapability = nameof(UpdateAdaptiveAutomationContextCommand);
    private readonly ConcurrentDictionary<Guid, long> automaticOperations = new();
    private readonly SemaphoreSlim cpuModeCommandGate = new(1, 1);
    private readonly SemaphoreSlim automationCycleGate = new(1, 1);
    public bool IsAutomaticOperation(Guid operationId) => automaticOperations.ContainsKey(operationId) || modeLightingOperations.ContainsKey(operationId);
    public Task<object> ReadDiagnosticsAsync(CancellationToken token, string? requestingSid = null) => store.ReadDiagnosticsAsync(token, requestingSid);
    public async Task<long?> ReadAutomaticConfigurationRevisionAsync(Guid operationId, CancellationToken token)
    {
        if (modeLightingOperations.TryGetValue(operationId, out var lighting))
            return await IsModeLightingIntentCurrentAsync(lighting, token) ? lighting.Revision : null;
        if (!automaticOperations.TryGetValue(operationId, out long expected)) return null;
        var state = await store.ReadAsync(token);
        return state.Recovery is null && state.Configuration?.Enabled == true && (expected < 0 || state.Revision == expected)
            ? state.Revision : null;
    }

    internal async Task<bool> HasRetainedLegacyRecoveryAsync(CancellationToken token) =>
        (await store.ReadAsync(token)).Recovery is not null;

    public Task PrepareManualCommandAsync(HardwareCommand command, CancellationToken token) =>
        ControlExecutionCoordinator.IsManaged(command) && !IsAutomaticOperation(command.OperationId) &&
            command is not ApplyControlRequestCommand { Request.Origin: ControlOrigin.ModeFollow }
            ? store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow,
                command is SetPerformanceModeCommand or ApplyControlRequestCommand or ApplyTurboBranchCommand or ReleaseTurboBranchCommand or SetCpuTuningBatchCommand { NativeMode: not null }, token)
            : Task.CompletedTask;

    public async Task<CommandResult> ExecuteCoordinatedAsync(HardwareCommand command, CancellationToken token, string? ownerSid = null)
    {
        // The runtime already owns the hardware gate. Never wait for a worker cycle that may queue there.
        if (command is SetAdaptiveAutomationConfigurationCommand set)
        {
            try { await store.SetConfigurationAsync(set.Configuration, token, set.ExpectedRevision, set.Intent, ownerSid); return Applied(command.OperationId); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException)
            { return Rejected(command.OperationId, ErrorCode.ValidationFailed) with { Mutation = HardwareMutation.NoMutation }; }
            catch (InvalidOperationException)
            { return Rejected(command.OperationId, ErrorCode.IdempotencyConflict) with { Mutation = HardwareMutation.NoMutation }; }
        }
        if (command is UpdateAdaptiveAutomationContextCommand context)
        {
            await store.SetClientContextAsync(context.Context, token);
            return Applied(command.OperationId);
        }
        if (automaticOperations.TryGetValue(command.OperationId, out long expected))
        {
            var current = await store.ReadAsync(token);
            if (current.Recovery is not null || current.Configuration?.Enabled != true || expected >= 0 && current.Revision != expected)
                return Rejected(command.OperationId, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };
        }
        return await inner.ExecuteAsync(command, token);
    }

    public async Task RunAutomationCycleAsync(Func<CancellationToken, Task> cycle, CancellationToken token)
    {
        await automationCycleGate.WaitAsync(token);
        try { await cycle(token); }
        finally { automationCycleGate.Release(); }
    }

    public async Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken) =>
        AddAutomationCapabilities(await inner.DiagnoseAsync(cancellationToken));

    public async Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken) =>
        AddAutomationCapabilities(await inner.ReinitializeAsync(cancellationToken));

    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken) => inner.ReadTelemetryAsync(cancellationToken);
    public void SetBackgroundWorkPaused(bool paused) => inner.SetBackgroundWorkPaused(paused);
    public void SetBackgroundWorkState(PowerLifecycleState lifecycle) => inner.SetBackgroundWorkState(lifecycle);
    public long? PrepareShutdownCleanup() => inner.PrepareShutdownCleanup();
    public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) =>
        inner.ReadControlBaselineAsync(token, includeCpuAdvanced);
    public Task StopClientLightingDynamicsAsync(CancellationToken token) => inner.StopClientLightingDynamicsAsync(token);
    public Task RetainClientNativeCoolingAsync(CancellationToken token) => inner.RetainClientNativeCoolingAsync(token);
    public Task<OemCpuConfigurationSnapshot?> ReadOemCpuConfigurationAsync(CancellationToken token) =>
        inner.ReadOemCpuConfigurationAsync(token);
    public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) =>
        inner.ReadDeviceRecoveryKeyAsync(identity, token);
    public Task<bool> ReconcileGpuRecoveryAsync(CancellationToken token) => inner.ReconcileGpuRecoveryAsync(token);
    public Task<CommandResult> RecoverPendingGpuAsync(Guid id, CancellationToken token) => inner.RecoverPendingGpuAsync(id, token);
    public Task<bool> VerifyPendingGpuTargetAsync(Guid id, CancellationToken token) => inner.VerifyPendingGpuTargetAsync(id, token);
    public Task<CommandResult> RecoverSessionFanAsync(Guid id, FanEcRecoveryScope scope, CancellationToken token) => inner.RecoverSessionFanAsync(id, scope, token);
    public Task<WindowsPowerSettingsSnapshot?> ReadWindowsPowerSchemeAsync(Guid scheme, CancellationToken token) => inner.ReadWindowsPowerSchemeAsync(scheme, token);
    public Task<CommandResult> RestoreWindowsPowerSettingAsync(Guid id, Guid scheme, WindowsCpuSetting setting, int expected, int target, CancellationToken token) =>
        inner.RestoreWindowsPowerSettingAsync(id, scheme, setting, expected, target, token);
    public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline,
        CpuFieldRestorationValue expected, CancellationToken token) => inner.RestoreCpuFieldAsync(id, field, baseline, expected, token);
    public Task<CommandResult> RestoreCpuScalarAsync(Guid id, string field, SettingRequest baseline,
        SettingRequest expected, CancellationToken token) => inner.RestoreCpuScalarAsync(id, field, baseline, expected, token);

    public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken cancellationToken) =>
        inner.ReadModeEvidenceAsync(cancellationToken);
    public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token) =>
        inner.ReadNativeModeEvidenceAsync(token);

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
                    await automationCycleGate.WaitAsync(cancellationToken);
                    try
                    {
                        await store.SetConfigurationAsync(set.Configuration, cancellationToken, set.ExpectedRevision, set.Intent);
                        return Applied(command.OperationId);
                    }
                    finally { automationCycleGate.Release(); }
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
        catch (InvalidOperationException) when (command is SetAdaptiveAutomationConfigurationCommand)
        {
            return Rejected(command.OperationId, ErrorCode.IdempotencyConflict) with { Mutation = HardwareMutation.NoMutation };
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
        bool manual = serialize && !automaticOperations.ContainsKey(command.OperationId);
        if (manual)
        {
            if (CommandValidation.Validate(command) is { } validation)
                return Rejected(command.OperationId, validation.Code) with { Mutation = HardwareMutation.NoMutation };
            // Publish takeover before waiting for a driver call already in flight.
            try
            {
                await store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow,
                    command is SetPerformanceModeCommand or SetCpuTuningBatchCommand { NativeMode: not null }, cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Rejected(command.OperationId, ErrorCode.ServiceUnavailable) with { Mutation = HardwareMutation.NoMutation };
            }
            await automationCycleGate.WaitAsync(cancellationToken);
        }
        try
        {
            if (serialize) await cpuModeCommandGate.WaitAsync(cancellationToken);
            try
            {
                if (serialize && automaticOperations.TryGetValue(command.OperationId, out var expectedRevision))
                {
                    var automation = await store.ReadAsync(cancellationToken);
                    if (automation.Recovery is not null)
                        return Rejected(command.OperationId, ErrorCode.CommandInProgress) with { Mutation = HardwareMutation.NoMutation };
                    if (automation.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
                        return Rejected(command.OperationId, ErrorCode.CommandInProgress);
                    if (expectedRevision >= 0 && automation.Revision != expectedRevision)
                        return Rejected(command.OperationId, ErrorCode.CommandInProgress);
                }
                var result = await inner.ExecuteAsync(command, cancellationToken);
                return result;
            }
            finally { if (serialize) cpuModeCommandGate.Release(); }
        }
        finally { if (manual) automationCycleGate.Release(); }
    }

    public async Task<CommandResult> ExecuteAutomaticCommandAsync(
        HomeServiceRuntime runtime,
        HardwareCommand command,
        CancellationToken cancellationToken,
        long? expectedConfigurationRevision = null)
    {
        var state = await store.ReadAsync(cancellationToken);
        if (state.Recovery is not null)
            return Rejected(command.OperationId, ErrorCode.CommandInProgress) with { Mutation = HardwareMutation.NoMutation };
        if (state.ManualOverrideUntilUtc is { } until && DateTimeOffset.UtcNow < until)
            return new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CommandInProgress, command.OperationId, true), false);
        if (expectedConfigurationRevision is long expected && state.Revision != expected)
            return new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.CommandInProgress, command.OperationId, true), false);
        automaticOperations.TryAdd(command.OperationId, expectedConfigurationRevision ?? -1);
        try { return await runtime.ExecuteAsync(command, cancellationToken, userSid: state.OwnerSid); }
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
            PresetKey.Create(map.DcOffice.Mode, map.DcOffice.Slot);
            PresetKey.Create(map.DcGame.Mode, map.DcGame.Slot);
        }
        var required = new[] { map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame };
        foreach (var key in required) PresetKey.Create(key.Mode, key.Slot);
        var profiles = configuration.Presets.ToDictionary(preset => PresetKey.Create(preset.Key.Mode, preset.Key.Slot));
        if (configuration.Enabled && required.Any(key => !profiles.ContainsKey(key)))
            throw new ArgumentException("Every automatic target must reference a saved preset snapshot.");
        if (configuration.Enabled && (profiles[map.DcOffice].PerformanceMode == PerformanceMode.Turbo ||
            profiles[map.DcGame].PerformanceMode == PerformanceMode.Turbo))
            throw new ArgumentOutOfRangeException(nameof(configuration), "Battery targets cannot resolve to native Turbo.");
        foreach (var preset in configuration.Presets)
        {
            PresetKey.Create(preset.Key.Mode, preset.Key.Slot);
            var expectedMode = AdaptiveTargetMap.HardwareModeFor(preset.Key);
            bool custom = preset.Key.Mode is ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3;
            // Slot 2 restores the device's captured native mode, regardless of its logical group.
            bool capturedMode = custom || preset.Key.Slot == 2;
            if ((!capturedMode && preset.PerformanceMode != expectedMode || capturedMode &&
                preset.PerformanceMode is not (PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo)) ||
                preset.CpuWindowsTuning is { } cpu && !IsSafeRestorableCpuPlan(cpu))
                throw new ArgumentException("Preset snapshots may contain only the saved mode, restorable Windows CPU tuning, PBO Scalar and CO.");
        }
    }

    public static AdaptiveAutomationConfiguration Clone(AdaptiveAutomationConfiguration configuration) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(configuration,
            Jiaolong.Contracts.Protocol.ProtocolJsonContext.Default.AdaptiveAutomationConfiguration),
            Jiaolong.Contracts.Protocol.ProtocolJsonContext.Default.AdaptiveAutomationConfiguration)!;

    public static bool IsSafeRestorableCpuPlan(CpuTuningPlan plan)
    {
        if (plan.TemperatureLimitC is not null || plan.SplWatts is not null || plan.SpptWatts is not null ||
            plan.EnabledCoreCount is not null ||
            plan.NegativeCurveOptimizer is < -50 or > 0 ||
            plan.Advanced is { } advanced &&
            ((advanced with { PboScalar = null, CurveOptimizerAll = null, PerCoreCurveOptimizer = null }) != new AdvancedCpuTuningPlan() ||
             advanced.CurveOptimizerAll is < -50 or > 0 ||
             advanced.PerCoreCurveOptimizer is { } cores && (cores.Count is < 1 or > 16 ||
                 cores.Any(pair => pair.Key is < 0 or > 15 || pair.Value is < -50 or > 0))))
            return false;
        if (plan.AcMaxFrequencyMhz.HasValue != plan.DcMaxFrequencyMhz.HasValue ||
            plan.AcMinActiveCoresPercent.HasValue != plan.DcMinActiveCoresPercent.HasValue ||
            plan.MaxFrequencyMhz is not null && (plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null))
            return false;
        var result = CommandValidation.Validate(new SetCpuTuningCommand(Guid.NewGuid(), plan, true));
        return result is null && (plan.MaxFrequencyMhz is not null || plan.AcMaxFrequencyMhz is not null ||
            plan.BoostEnabled is not null || plan.BoostPolicy is not null || plan.WindowsPowerSchemeId is not null || plan.AcMinActiveCoresPercent is not null ||
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
            TurboExitCpuPercent = p.TurboExitCpuPercent,
            TurboExitGpuPercent = p.TurboExitGpuPercent,
            TurboExitSeconds = p.TurboExitSeconds,
            TurboMinimumDwellSeconds = p.TurboMinimumDwellSeconds,
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
                    rule.Executable, rule.ForegroundOnly, ToStage(rule.Target)) { RequireActivity = rule.RequireActivity }).ToArray()
            }
        };
    }

    public static AdaptiveTargetMap ToTargetMap(AdaptiveAutomationTargetMap map) => new(
        map.AcOffice, map.AcGame, map.AcTurbo, map.DcOffice, map.DcGame);

    public static string Identity(AdaptiveAutomationConfiguration configuration) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration with { Revision = 0 })))[..16];

    public static AdaptiveStage ToStage(AdaptiveAutomationStageId stage) => stage switch
    {
        AdaptiveAutomationStageId.Office => AdaptiveStage.Office,
        AdaptiveAutomationStageId.Game => AdaptiveStage.Game,
        _ => AdaptiveStage.Turbo
    };
}
