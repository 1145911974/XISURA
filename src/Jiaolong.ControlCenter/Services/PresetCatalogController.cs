using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

// One catalog and publication order per window; catalog edits never execute hardware.
public sealed class PresetCatalogController(HomeControlSession session, ControlPresetStore? store = null,
    UserPreferencesStore? preferences = null)
{
    private readonly ControlPresetStore store = store ?? new(capabilities: () => session.State?.Capabilities);
    private readonly UserPreferencesStore preferences = preferences ?? new();
    private readonly SemaphoreSlim publication = new(1, 1);
    private readonly Dictionary<(ControlPageId, PresetKey), Task> saves = [];
    private readonly Dictionary<(ControlPageId, PresetKey), CancellationTokenSource> delays = [];
    private readonly Dictionary<(ControlPageId, PresetKey), (string Name, JsonElement Payload, Action? Completed)> failedSaves = [];
    private long catalogRevision;
    private long applyVersion;
    public event Action? FollowChanged;
    public event Action<ControlPageId, PresetKey>? Saved;

    public static string DescribeApplyFailure(Exception error) => $"预设应用未完成：{error.GetBaseException().Message}";

    public static string DescribePublicationFailure(ServiceError? error) => error?.Details.GetValueOrDefault("cause") switch
    {
        "presetRevisionRegressed" or "presetRevisionContentConflict" => "预设存档与服务当前版本冲突，本次未下发硬件请求。请刷新预设后重试。",
        "expectedCatalogRevisionMismatch" => "预设目录在提交期间已变更，本次未下发硬件请求。请刷新预设后重试。",
        "invalidCatalog" or "selectionRevisionMismatch" => "预设目录内容或所选版本无效，本次未下发硬件请求。请检查预设后重试。",
        "userCatalogCapacityExceeded" => "服务中的预设目录数量已达上限，本次未下发硬件请求。请导出日志反馈。",
        _ => "预设目录发布未完成，本次未下发硬件请求。请导出日志反馈。"
    };

    public void ScheduleSave(ControlPageId page, PresetKey key, string name, JsonElement payload,
        Action? completed = null)
    {
        var identity = (page, key);
        saves.TryGetValue(identity, out var previousSave);
        if (delays.Remove(identity, out var old)) old.Cancel();
        var delay = new CancellationTokenSource();
        delays[identity] = delay;
        saves[identity] = SaveAfterDelayAsync();
        async Task SaveAfterDelayAsync()
        {
            try { await Task.Delay(500, delay.Token); }
            catch (OperationCanceledException)
            { if (!delays.TryGetValue(identity, out var current) || current != delay) return; }
            if (previousSave is not null) await previousSave;
            try
            {
                await SavePayloadAsync(page, key, name, payload, CancellationToken.None);
                failedSaves.Remove(identity);
                completed?.Invoke();
            }
            catch (Exception error)
            {
                failedSaves[identity] = (name, payload, completed);
                session.ReportOperationFailure("预设自动保存失败，草稿已保留");
                AppRuntimeLog.Write($"Preset autosave: {error}");
            }
            finally
            {
                if (delays.TryGetValue(identity, out var current) && current == delay) delays.Remove(identity);
                delay.Dispose();
            }
        }
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        while (true)
        {
            foreach (var delay in delays.Values.ToArray()) delay.Cancel();
            var pending = saves.Values.ToArray();
            await Task.WhenAll(pending).WaitAsync(token);
            if (!pending.SequenceEqual(saves.Values)) continue;
            foreach (var (identity, failed) in failedSaves.ToArray())
            {
                await SavePayloadAsync(identity.Item1, identity.Item2, failed.Name, failed.Payload, token);
                failedSaves.Remove(identity);
                failed.Completed?.Invoke();
            }
            return;
        }
    }

    public async Task SavePayloadAsync(ControlPageId page, PresetKey key, string name,
        JsonElement payload, CancellationToken token, bool copyCurrent = false)
    {
        var previous = await store.LoadAsync(page, key, token) ?? DefaultControlPresets.Create(page, key);
        // Picker labels include the mode; only RenameAsync changes the stored name.
        await store.SaveAsync(MergeDraft(previous, previous.DisplayName, payload, copyCurrent), token);
        Saved?.Invoke(page, key);
        if (session.IsServiceConnected)
        {
            try { await PublishAsync(CurrentLogicalMode(), token); }
            catch (Exception error) { AppRuntimeLog.Write($"Preset catalog publication deferred: {error}"); }
        }
    }

    public static PagePresetEnvelope MergeDraft(PagePresetEnvelope previous, string name,
        JsonElement payload, bool copyCurrent = false)
    {
        if (previous.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion)
            throw new InvalidOperationException("预设来自更新版本，原文件已保留");
        var before = previous.Payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
            StringComparer.OrdinalIgnoreCase);
        var candidate = previous with { SchemaVersion = PagePresetEnvelope.CurrentSchemaVersion,
            DisplayName = name, Payload = JsonSerializer.SerializeToElement(before.Concat(payload.EnumerateObject()
                .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value))).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value)), SavedAtUtc = DateTimeOffset.UtcNow, UserModified = true };
        var inferred = DefaultControlPresets.InferSettings(candidate);
        var settings = new Dictionary<string, SettingRequest>(previous.Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            var field = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!inferred.TryGetValue(field, out var intent)) continue;
            bool changed = copyCurrent || !before.TryGetValue(property.Name, out var old) || !JsonElement.DeepEquals(old, property.Value);
            if (changed) settings[field] = intent;
            else if (!settings.ContainsKey(field)) settings[field] = SettingRequest.Keep;
        }
        // These UI flags are explicit requests; untouched numeric placeholders retain Official.
        bool FlagChanged(string flag) => before.TryGetValue(flag, out var old) && payload.EnumerateObject()
            .Any(p => p.Name.Equals(flag, StringComparison.OrdinalIgnoreCase) && !JsonElement.DeepEquals(old, p.Value));
        if (FlagChanged("useOfficialCpuPolicy") && payload.EnumerateObject().Any(p =>
            p.Name.Equals("useOfficialCpuPolicy", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True))
            foreach (var field in new[] { "temperatureLimitC", "splWatts", "spptWatts" })
                if (inferred.TryGetValue(field, out var intent)) settings[field] = intent;
        if (FlagChanged("resetCoreFrequencyLimit") && inferred.TryGetValue("coreFrequencyLimitMhz", out var clock))
            settings["coreFrequencyLimitMhz"] = clock;
        return candidate with { Settings = settings };
    }

    public async Task<PagePresetEnvelope[]> PublishAsync(ControlModeId mode, CancellationToken token, Func<bool>? isCurrent = null)
    {
        await publication.WaitAsync(token);
        try
        {
            if (isCurrent?.Invoke() == false) return [];
            ServiceError? publicationError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var items = new List<PagePresetEnvelope>();
                foreach (var page in Enum.GetValues<ControlPageId>())
                    foreach (var key in PresetKey.All)
                    {
                        var item = await store.LoadAsync(page, key, token) ?? DefaultControlPresets.Create(page, key);
                        if (item.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion) continue;
                        var validation = PresetValidation.Validate(item);
                        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
                        items.Add(item);
                    }
                if (isCurrent?.Invoke() == false) return [];
                var selections = PresetKey.All.Select(key => key.Mode).Distinct().SelectMany(logical =>
                    Selections(logical, items, requireFollowed: false)).Where(s => s.Revision > 0).ToArray();
                var result = await session.PublishPresetsAsync(items.ToArray(), selections, catalogRevision, token);
                if (result.CatalogRevision is { } actual) catalogRevision = actual;
                if (result.State == CommandState.Applied && result.Error is null) return items.ToArray();
                publicationError = result.Error;
                AppRuntimeLog.Write($"Preset catalog rejected: code={result.Error?.Code}; cause={result.Error?.Details.GetValueOrDefault("cause")}; operation={result.OperationId}; mutation={result.Mutation}");
                if (isCurrent?.Invoke() == false) return [];
                if (result.State == CommandState.Rejected && result.Mutation == HardwareMutation.NoMutation &&
                    result.Error?.Code == ErrorCode.IdempotencyConflict &&
                    result.Error.Details.GetValueOrDefault("cause") is "presetRevisionRegressed" or "presetRevisionContentConflict")
                {
                    foreach (var item in items)
                    {
                        if (isCurrent?.Invoke() == false) return [];
                        if (result.Error.Details.TryGetValue($"presetRevision:{(int)item.Page}:{(int)item.Key.Mode}:{item.Key.Slot}", out var floorText) &&
                            long.TryParse(floorText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var floor) &&
                            floor >= item.Revision && floor < long.MaxValue - 1)
                            await store.TryAdvanceRevisionAsync(item, floor + 1, token);
                    }
                }
                if (result.CatalogRevision is null) throw new InvalidOperationException(
                    DescribePublicationFailure(publicationError));
            }
            throw new InvalidOperationException(DescribePublicationFailure(publicationError));
        }
        finally { publication.Release(); }
    }

    public async Task<CommandResult?> ApplyAsync(ControlModeId mode, PerformanceMode nativeMode,
        ControlOrigin origin, ControlPageId? explicitPage = null, PresetKey? explicitKey = null,
        CancellationToken token = default, Func<bool>? isCurrent = null)
    {
        long intent = Interlocked.Increment(ref applyVersion);
        bool Current() => intent == Volatile.Read(ref applyVersion) && isCurrent?.Invoke() != false;
        try
        {
            await FlushAsync(token);
            if (!Current()) return null;
            if (explicitPage is { } page && explicitKey is { } key) RememberSlot(page, key);
            var pages = await PublishAsync(mode, token, Current);
            if (!Current()) return null;
            var selections = Selections(mode, pages);
            if (explicitPage is { } selectedPage && explicitKey is { } selectedKey)
            {
                var item = pages.First(p => p.Page == selectedPage && p.Key == selectedKey);
                selections = selections.Select(s => s.Page == selectedPage
                    ? new PageSelection(selectedPage, true, selectedKey, item.Revision) : s).ToArray();
            }
            var result = await session.ApplyControlRequestAsync(mode, nativeMode, selections.Where(s => s.Follow).ToArray(), origin, token);
            return Current() ? result : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        { if (Current()) session.ReportOperationFailure(DescribeApplyFailure(error)); AppRuntimeLog.Write($"Preset request: {error}"); return null; }
    }

    public async Task FollowPreferenceChangedAsync(bool enabled, CancellationToken token = default)
    {
        try
        {
            await FlushAsync(token);
            var mode = CurrentLogicalMode();
            await PublishAsync(mode, token);
            FollowChanged?.Invoke();
            if (enabled && session.State?.Controls.AdaptiveAutomation?.Enabled != true)
                await ApplyAsync(mode, session.State?.Controls.PerformanceMode ?? NativeModeFor(PresetKey.Create(mode, 2)),
                    ControlOrigin.PresetUse, token: token);
        }
        catch (Exception error)
        { session.ReportOperationFailure("跟随配置尚未确认，请重试"); AppRuntimeLog.Write($"Preset follow configuration: {error}"); }
    }

    public PerformanceMode NativeModeFor(PresetKey key) => AdaptiveTargetMap.HardwareModeFor(key, preferences.Load().CustomBaseModes);

    public void RecordManualLive(ControlPageId page)
    {
        preferences.Update(saved => page switch
        {
            ControlPageId.Performance => saved with { PerformanceFollowPreset = false, AdaptiveModeEnabled = false },
            ControlPageId.Gpu => saved with { GpuFollowPreset = false, AdaptiveModeEnabled = false },
            ControlPageId.Fan => saved with { FanFollowPreset = false, AdaptiveModeEnabled = false },
            _ => saved with { LightingFollowPreset = false }
        });
        if (page == ControlPageId.Lighting) FollowChanged?.Invoke();
    }

    public async Task PublishManualLiveAsync(CancellationToken token = default)
    {
        if (!session.IsServiceConnected) return;
        try { await PublishAsync(CurrentLogicalMode(), token); }
        catch (Exception error) { AppRuntimeLog.Write($"Manual follow publication deferred: {error}"); }
    }

    public async Task RenameAsync(PresetKey key, CancellationToken token = default)
    {
        await FlushAsync(token);
        var names = preferences.Load().PresetNames;
        foreach (var page in Enum.GetValues<ControlPageId>())
        {
            var saved = await store.LoadAsync(page, key, token) ?? DefaultControlPresets.Create(page, key);
            await store.SaveAsync(saved with { DisplayName = PresetNameCatalog.GetName(names, key) }, token);
        }
        if (session.IsServiceConnected) await PublishAsync(CurrentLogicalMode(), token);
    }

    public ControlModeId CurrentLogicalMode() => session.State?.Controls.ExecutionState is { NativeModeApplyState: ApplyState.Confirmed } execution
        ? execution.LogicalMode : session.State?.Controls.PerformanceMode switch
        { PerformanceMode.Quiet => ControlModeId.Office, PerformanceMode.Turbo => ControlModeId.Turbo, _ => ControlModeId.Gaming };

    private async Task<PagePresetEnvelope[]> ReadPagesAsync(ControlModeId mode, CancellationToken token)
    {
        var result = new List<PagePresetEnvelope>();
        foreach (var selection in SelectionKeys(mode))
            result.Add(await store.LoadAsync(selection.Page, selection.Key, token) ?? DefaultControlPresets.Create(selection.Page, selection.Key));
        return result.ToArray();
    }

    public async Task<AdaptiveAutomationPreset[]> ReadAutomaticServicePresetsAsync(IReadOnlyCollection<PresetKey> keys,
        CancellationToken token)
    {
        await FlushAsync(token);
        var snapshots = new List<AdaptiveAutomationPreset>();
        foreach (var key in keys.Distinct())
        {
            var pages = (await ReadPagesAsync(key.Mode, token))
                .Where(page => page.SchemaVersion <= PagePresetEnvelope.CurrentSchemaVersion).ToArray();
            var cpu = await store.LoadAsync(ControlPageId.Performance, key, token) ?? DefaultControlPresets.Create(ControlPageId.Performance, key);
            pages = pages.Select(p => p.Page == ControlPageId.Performance ? cpu : p).ToArray();
            foreach (var page in pages)
                if (!PresetValidation.Validate(page).IsValid) throw new InvalidOperationException("自动目标包含无效预设");
            snapshots.Add(new(key, NativeModeFor(key), null)
                { Pages = pages, Selections = Selections(key.Mode, pages, key) });
        }
        return snapshots.ToArray();
    }

    private PageSelection[] Selections(ControlModeId mode, IEnumerable<PagePresetEnvelope> items, PresetKey? performanceKey = null,
        bool requireFollowed = true) => SelectionKeys(mode)
        .Select(s => { var key = s.Page == ControlPageId.Performance ? performanceKey ?? s.Key : s.Key;
            var item = items.FirstOrDefault(p => p.Page == s.Page && p.Key == key);
            if (item is null && s.Follow && requireFollowed) throw new InvalidOperationException($"预设版本不受支持：{s.Page}/{key}");
            return s with { Key = key, Revision = item?.Revision ?? 0 }; }).ToArray();

    private PageSelection[] SelectionKeys(ControlModeId mode)
    {
        var saved = preferences.Load();
        return Enum.GetValues<ControlPageId>().Select(page =>
        {
            var (follow, slots) = page switch
            {
                ControlPageId.Performance => (saved.PerformanceFollowPreset, saved.PerformancePresetSlots),
                ControlPageId.Gpu => (saved.GpuFollowPreset, saved.GpuPresetSlots),
                ControlPageId.Fan => (saved.FanFollowPreset, saved.FanPresetSlots),
                _ => (saved.LightingFollowPreset, saved.LightingPresetSlots)
            };
            int slot = slots.TryGetValue(mode.ToString(), out var value) && value is >= 1 and <= 3 ? value : 2;
            return new PageSelection(page, follow, PresetKey.Create(mode, slot), 0);
        }).ToArray();
    }

    private void RememberSlot(ControlPageId page, PresetKey key) => preferences.Update(saved =>
    {
        var slots = new Dictionary<string, int>(page switch
        { ControlPageId.Performance => saved.PerformancePresetSlots, ControlPageId.Gpu => saved.GpuPresetSlots,
            ControlPageId.Fan => saved.FanPresetSlots, _ => saved.LightingPresetSlots }) { [key.Mode.ToString()] = key.Slot };
        return page switch
        { ControlPageId.Performance => saved with { PerformancePresetSlots = slots }, ControlPageId.Gpu => saved with { GpuPresetSlots = slots },
            ControlPageId.Fan => saved with { FanPresetSlots = slots }, _ => saved with { LightingPresetSlots = slots } };
    });
}
