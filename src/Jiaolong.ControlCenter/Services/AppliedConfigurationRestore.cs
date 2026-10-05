using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Jiaolong_ControlCenter.Services;

public sealed record AutomaticRestoreReport(int Applied, int Skipped, string? Warning, bool BlockedPreviously = false);

public sealed class AppliedConfigurationRestore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Order = ["mode", "cpu", "gpu", "fan", "lighting", "logo"];
    private readonly IUserPreferencesPathProvider paths;
    private readonly SemaphoreSlim storageGate = new(1, 1);
    private string StatePath => Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "last-applied-controls.json");
    public bool HasSavedState => File.Exists(StatePath);

    public AppliedConfigurationRestore(IUserPreferencesPathProvider? paths = null) =>
        this.paths = paths ?? new DefaultUserPreferencesPathProvider();

    public async Task RecordAppliedAsync(HardwareCommand command, CommandResult result, CancellationToken cancellationToken)
    {
        if (command is SetGpuFrequencyLimitCommand or SetGpuPowerLimitCommand or SetGpuPowerPolicyCommand or SetGpuVoltageBoostCommand) return;
        // PBO is a deliberate, temperature-gated write; never replay it on startup.
        if (command is SetCpuTuningCommand { Plan.Advanced: not null }) return;
        if (command is SetCpuTuningCommand { Plan.AcMinActiveCoresPercent: not null }) return;
        if (command is SetCpuTuningCommand cpu && IsSingleOemCpuLimit(cpu.Plan)) return;
        if (HasCurveOptimizer(command)) return;
        var group = Group(command);
        if (group is null || !Succeeded(command, result) || !Valid(command)) return;
        await storageGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadAsync(cancellationToken);
            if (command is SetPerformanceModeCommand)
            {
                // An OEM mode may reset tuning. Never replay overrides belonging to the previous mode.
                foreach (var prior in new[] { "cpu", "gpu", "fan" })
                {
                    state.Commands.Remove(prior);
                    state.BlockedGroups.Remove(prior);
                }
            }
            if (command is ReleaseFanControlCommand) state.Commands.Remove(group);
            else state.Commands[group] = command;
            state.BlockedGroups.Remove(group);
            state.LastGroup = group;
            state.LastOperationId = command.OperationId;
            state.LastAttemptUtc = DateTimeOffset.UtcNow;
            state.LastOutcome = "manualApplied";
            await SaveAsync(state, cancellationToken);
        }
        finally { storageGate.Release(); }
    }

    private static bool IsSingleOemCpuLimit(CpuTuningPlan plan)
    {
        int count = (plan.TemperatureLimitC is null ? 0 : 1) +
            (plan.SplWatts is null ? 0 : 1) +
            (plan.SpptWatts is null ? 0 : 1);
        return count == 1 &&
            plan.MaxFrequencyMhz is null && plan.BoostEnabled is null &&
            plan.AcMaxFrequencyMhz is null && plan.DcMaxFrequencyMhz is null &&
            plan.AcMinActiveCoresPercent is null && plan.DcMinActiveCoresPercent is null &&
            plan.EnabledCoreCount is null && plan.NegativeCurveOptimizer is null &&
            plan.WindowsPowerSchemeId is null && plan.Advanced is null;
    }

    public async Task<AutomaticRestoreReport> RestoreAsync(
        Func<HardwareCommand, CancellationToken, Task<CommandResult>> send,
        Func<bool> canContinue, bool adaptiveEnabled, CancellationToken cancellationToken,
        Func<HardwareCommand, bool>? allowed = null)
    {
        await storageGate.WaitAsync(cancellationToken);
        try
        {
            RestoreState state;
            try { state = await LoadAsync(cancellationToken); }
            catch (InvalidDataException) { return new(0, 0, "自动恢复配置无法校验，已保留原文件并停止恢复"); }
            if (state.Commands.Count == 0) return new(0, 0, null);

            int applied = 0, skipped = 0;
            bool modeDeferred = false;
            bool blockedPreviously = state.BlockedGroups.Count > 0;
            foreach (var group in Order)
            {
                if (!state.Commands.TryGetValue(group, out var saved)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (!canContinue()) break;
                bool tuning = group is "mode" or "cpu" or "gpu" or "fan";
                if (state.BlockedGroups.Contains(group) ||
                    (tuning && (adaptiveEnabled || modeDeferred || state.BlockedGroups.Contains("mode"))) ||
                    (allowed is not null && !allowed(saved)))
                {
                    if (group == "mode") modeDeferred = true;
                    skipped++;
                    continue;
                }

                var command = saved with { OperationId = Guid.NewGuid() };
                state.PendingGroup = group;
                state.LastGroup = group;
                state.LastAttemptUtc = DateTimeOffset.UtcNow;
                state.LastOperationId = command.OperationId;
                state.LastOutcome = "applying";
                await SaveAsync(state, cancellationToken);
                // The durable marker remains if the process, OS or request is interrupted during a write.
                CommandResult result;
                try { result = await send(command, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    state.LastOutcome = "transportFailed";
                    state.BlockedGroups.Add(group);
                    state.PendingGroup = null;
                    await SaveAsync(state, CancellationToken.None);
                    return new(applied, skipped, "自动恢复连接失败，已停止本次恢复；请检查后重新使用预设");
                }
                state.PendingGroup = null;
                // A competing console refused the write before execution; it is not a damaged preset.
                if (result.State == CommandState.Rejected &&
                    result.Error?.Code == Jiaolong.Contracts.Errors.ErrorCode.ConflictDetected)
                {
                    state.LastOutcome = "deferredConflict";
                    await SaveAsync(state, CancellationToken.None);
                    skipped++;
                    if (group == "mode") modeDeferred = true;
                    continue;
                }
                bool succeeded = Succeeded(command, result);
                state.LastOutcome = succeeded ? "applied" : "rejected";
                if (!succeeded) state.BlockedGroups.Add(group);
                await SaveAsync(state, CancellationToken.None);
                Trace.WriteLine(JsonSerializer.Serialize(new { eventName = "automaticRestore", group,
                    operationId = command.OperationId, outcome = state.LastOutcome, error = result.Error?.Code.ToString() }));
                if (!succeeded)
                    return new(applied, skipped, "自动恢复未通过服务校验，已停止本次恢复；请检查后重新使用预设");
                applied++;
            }
            return new(applied, skipped, null, blockedPreviously);
        }
        finally { storageGate.Release(); }
    }

    private async Task<RestoreState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(StatePath)) return new();
        try
        {
            await using var stream = File.OpenRead(StatePath);
            if (stream.Length > 262_144) throw new InvalidDataException("Restore state exceeds size limit.");
            var state = await JsonSerializer.DeserializeAsync<RestoreState>(stream, JsonOptions, ct);
            // Discard writes without a restorable baseline before startup replay.
            if (state?.Version == 1 && state.Commands is not null &&
                state.Commands.TryGetValue("cpu", out var savedCpu) &&
                (HasCurveOptimizer(savedCpu) || savedCpu is SetCpuTuningCommand cpu &&
                    (cpu.Plan.Advanced is not null || IsSingleOemCpuLimit(cpu.Plan))))
            {
                state.Commands.Remove("cpu");
                state.BlockedGroups?.Remove("cpu");
                if (state.PendingGroup == "cpu") state.PendingGroup = null;
            }
            if (state?.Version == 1 && state.Commands?.Remove("gpu") == true)
            {
                state.BlockedGroups?.Remove("gpu");
                if (state.PendingGroup == "gpu") state.PendingGroup = null;
            }
            if (state is null || state.Version != 1 || state.Commands is null || state.BlockedGroups is null ||
                state.Commands.Count > Order.Length ||
                state.Commands.Any(pair => pair.Key != Group(pair.Value) || !Valid(pair.Value)) ||
                state.BlockedGroups.Any(group => !Order.Contains(group)) ||
                (state.PendingGroup is not null && !Order.Contains(state.PendingGroup)))
                throw new InvalidDataException("Invalid restore state.");
            if (state.PendingGroup is { } interrupted)
            {
                state.BlockedGroups.Add(interrupted);
                state.PendingGroup = null;
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Invalid restore state; original file preserved.", ex);
        }
    }

    private async Task SaveAsync(RestoreState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, StatePath, overwrite: true);
            paths.RecordWrite(StatePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Succeeded(HardwareCommand command, CommandResult result) =>
        result.OperationId == command.OperationId && result.State == CommandState.Applied && result.Error is null &&
        result.RequiredAction == RequiredUserAction.None && !result.IsReplay;

    private static string? Group(HardwareCommand? command) => command switch
    {
        SetPerformanceModeCommand => "mode",
        SetCpuTuningCommand => "cpu",
        SetGpuFrequencyLimitCommand => "gpu",
        // Fan control requires an explicit Apply after each client start.
        SetKeyboardLightingCommand { Preview: false } => "lighting",
        SetLidLogoCommand or SetQuickSettingCommand { Setting: QuickSettingKind.LidLogo } => "logo",
        _ => null
    };

    private static bool HasCurveOptimizer(HardwareCommand? command) =>
        command is SetCpuTuningCommand cpu &&
        (cpu.Plan?.NegativeCurveOptimizer is not null || cpu.Plan?.Advanced?.CurveOptimizerAll is not null ||
         cpu.Plan?.Advanced?.PerCoreCurveOptimizer is not null);

    private static bool Valid(HardwareCommand? command) => command is not null && !HasCurveOptimizer(command) && Group(command) is not null && command switch
    {
        SetPerformanceModeCommand mode => Enum.IsDefined(mode.Mode),
        SetCpuTuningCommand cpu => cpu.Plan is not null && cpu.Plan.Advanced is null && !IsSingleOemCpuLimit(cpu.Plan) && cpu.RiskConfirmed && CommandValidation.Validate(cpu) is null,
        SetGpuFrequencyLimitCommand => false,
        SetFanControlCommand fan => fan.RiskConfirmed && fan.Plan?.Points is { Length: > 0 and <= 64 } points &&
            points.All(p => p is not null) && CommandValidation.Validate(fan) is null,
        ReleaseFanControlCommand release => Enum.IsDefined(release.Reason),
        SetKeyboardLightingCommand lighting => lighting.Plan?.IsValid() == true,
        _ => true
    };

    private sealed class RestoreState
    {
        [System.Text.Json.Serialization.JsonRequired]
        public int Version { get; set; } = 1;
        public Dictionary<string, HardwareCommand> Commands { get; set; } = [];
        public HashSet<string> BlockedGroups { get; set; } = [];
        public string? PendingGroup { get; set; }
        public string? LastGroup { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public Guid? LastOperationId { get; set; }
        public string? LastOutcome { get; set; }
    }
}
