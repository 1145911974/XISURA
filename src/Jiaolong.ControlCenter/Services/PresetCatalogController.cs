using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

// One catalog and publication order per window; catalog edits never execute hardware.
public sealed class PresetCatalogController(HomeControlSession session, ControlPresetStore? store = null,
    UserPreferencesStore? preferences = null)
{
    private readonly ControlPresetStore store = store ?? new(capabilities: () => session.State?.Capabilities, baseline: () => session.SchedulingBaseline);
    private readonly UserPreferencesStore preferences = preferences ?? new();
    private readonly SemaphoreSlim publication = new(1, 1);
    private readonly Dictionary<(ControlPageId, PresetKey), Task> saves = [];
    private readonly Dictionary<(ControlPageId, PresetKey), CancellationTokenSource> delays = [];
    private readonly Dictionary<(ControlPageId, PresetKey), (string Name, JsonElement Payload, Action? Completed, PerformanceDraft? Draft)> failedSaves = [];
    private long catalogRevision;
    private long applyVersion;
    private static readonly ControlPageId[] FollowPages = [ControlPageId.Performance, ControlPageId.Gpu, ControlPageId.Fan];
    private sealed class PageFollowState(ControlPageId page)
    {
        internal readonly PageModeFollowObserver Observer = new(page);
        internal int EnablesInFlight;
    }
    private readonly Dictionary<ControlPageId, PageFollowState> pageFollow = FollowPages.ToDictionary(page => page, page => new PageFollowState(page));
    private readonly SemaphoreSlim followApplication = new(1, 1);
    private CancellationTokenSource? modeFollowCancellation;
    private Task modeFollowTask = Task.CompletedTask;
    private long modeFollowVersion;
    private int explicitApplications;
    private PerformanceMode? manualFollowTarget;
    private long manualFollowVersion;
    private readonly Dictionary<ControlPageId, long> followPreferenceVersions = [];
    private bool followApplicationsStopped;
    private PerformanceMode? pendingFollowMode;
    public event Action? FollowChanged;
    public event Action<ControlPageId, PresetKey>? Saved;

    private static bool IsFollowing(UserPreferences saved, ControlPageId page) => page switch
    {
        ControlPageId.Performance => saved.PerformanceFollowPreset,
        ControlPageId.Gpu => saved.GpuFollowPreset,
        ControlPageId.Fan => saved.FanFollowPreset,
        _ => false
    };

    internal Task ObserveModeAsync(HomeStateSnapshot snapshot, long connectionGeneration,
        CancellationToken token = default, Func<bool>? canApply = null)
    {
        bool CommonCurrent() => !followApplicationsStopped && !token.IsCancellationRequested && canApply?.Invoke() != false &&
            connectionGeneration == session.ConnectionGeneration && session.IsServiceConnected &&
            session.Status == HomeSessionStatus.Connected && session.ConfigurationRestorationSettled &&
            session.State?.Controls.AdaptiveAutomation?.Enabled != true;
        if (!CommonCurrent() || modeFollowCancellation is not null && snapshot.Controls.PerformanceMode != pendingFollowMode)
            CancelModeFollow();
        var targets = new List<(ControlPageId Page, PerformanceMode Mode, long PreferenceVersion)>();
        foreach (var page in FollowPages)
        {
            var state = pageFollow[page];
            bool enabled = CommonCurrent() && ReferenceEquals(snapshot, session.State) && IsFollowing(preferences.Load(), page);
            var mode = state.Observer.Observe(snapshot, connectionGeneration, enabled && state.EnablesInFlight == 0,
                manualFollowTarget, explicitApplications > 0, deferTransitions: state.EnablesInFlight > 0);
            if (mode is { } target) targets.Add((page, target, followPreferenceVersions.GetValueOrDefault(page)));
        }
        if (explicitApplications == 0 && manualFollowTarget == snapshot.Controls.PerformanceMode) manualFollowTarget = null;
        if (targets.Count == 0) return Task.CompletedTask;
        // One serial batch preserves ApplyAsync's global intent ordering and the service's single-page contract.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        modeFollowCancellation = cancellation;
        pendingFollowMode = snapshot.Controls.PerformanceMode;
        long version = modeFollowVersion, manualIntent = manualFollowVersion;
        var previous = modeFollowTask;
        modeFollowTask = ApplyObservedAsync();
        return modeFollowTask;

        async Task ApplyObservedAsync()
        {
            try
            {
                try { await previous; }
                catch (OperationCanceledException) { }
                catch (Exception error) { AppRuntimeLog.Write($"Previous preset mode follow: {error}"); }
                foreach (var (page, mode, preferenceVersion) in targets)
                {
                    bool Current() => !cancellation.IsCancellationRequested && version == modeFollowVersion &&
                        manualIntent == manualFollowVersion && explicitApplications == 0 && CommonCurrent() &&
                        followPreferenceVersions.GetValueOrDefault(page) == preferenceVersion &&
                        pageFollow[page].EnablesInFlight == 0 && IsFollowing(preferences.Load(), page) &&
                        session.State is { } current && current.Controls.PerformanceMode == mode && pageFollow[page].Observer.IsAvailable(current);
                    if (!Current()) continue;
                    var logical = mode == PerformanceMode.Quiet ? ControlModeId.Office :
                        mode == PerformanceMode.Turbo ? ControlModeId.Turbo : ControlModeId.Gaming;
                    await ApplyAsync(logical, mode, ControlOrigin.ModeFollow, token: cancellation.Token,
                        isCurrent: Current, applyOnlyPage: page);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(modeFollowCancellation, cancellation)) modeFollowCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void CancelModeFollow()
    {
        modeFollowVersion++;
        modeFollowCancellation?.Cancel();
        modeFollowCancellation = null;
    }

    internal async Task StopModeFollowAsync()
    {
        followApplicationsStopped = true;
        CancelModeFollow();
        foreach (var state in pageFollow.Values) state.Observer.Reset();
        try { await modeFollowTask; }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppRuntimeLog.Write($"Stopping preset mode follow: {error}"); }
        finally
        {
            await followApplication.WaitAsync();
            followApplication.Release();
        }
    }

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
        Action? completed = null, PerformanceDraft? performanceDraft = null)
    {
        payload = payload.Clone();
        performanceDraft = SnapshotDraft(performanceDraft);
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
                await SavePayloadAsync(page, key, name, payload, CancellationToken.None, performanceDraft: performanceDraft);
                failedSaves.Remove(identity);
                completed?.Invoke();
            }
            catch (Exception error)
            {
                failedSaves[identity] = (name, payload, completed, performanceDraft);
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
                await SavePayloadAsync(identity.Item1, identity.Item2, failed.Name, failed.Payload, token, performanceDraft: failed.Draft);
                failedSaves.Remove(identity);
                failed.Completed?.Invoke();
            }
            return;
        }
    }

    public async Task SavePayloadAsync(ControlPageId page, PresetKey key, string name,
        JsonElement payload, CancellationToken token, bool copyCurrent = false, PerformanceDraft? performanceDraft = null)
    {
        payload = payload.Clone();
        performanceDraft = SnapshotDraft(performanceDraft);
        var previous = await store.LoadAsync(page, key, token) ?? DefaultControlPresets.Create(page, key);
        // Picker labels include the mode; only RenameAsync changes the stored name.
        await store.SaveAsync(MergeDraft(previous, previous.DisplayName, payload, copyCurrent, performanceDraft), token);
        Saved?.Invoke(page, key);
        if (session.IsServiceConnected)
        {
            try { await PublishAsync(CurrentLogicalMode(), token); }
            catch (Exception error) { AppRuntimeLog.Write($"Preset catalog publication deferred: {error}"); }
        }
    }

    public static PagePresetEnvelope MergeDraft(PagePresetEnvelope previous, string name,
        JsonElement payload, bool copyCurrent = false, PerformanceDraft? performanceDraft = null)
    {
        if (previous.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion)
            throw new InvalidOperationException("预设来自更新版本，原文件已保留");
        if (performanceDraft is not null && (previous.Page != ControlPageId.Performance ||
            !JsonElement.DeepEquals(payload, JsonSerializer.SerializeToElement(performanceDraft, new JsonSerializerOptions(JsonSerializerDefaults.Web)))))
            throw new InvalidOperationException("性能草稿与保存快照不一致，原预设未更改");
        var before = previous.Payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
            StringComparer.OrdinalIgnoreCase);
        var incoming = payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value,
            StringComparer.OrdinalIgnoreCase);
        if (performanceDraft is { KnownFields: null } || copyCurrent && !incoming.ContainsKey("knownFields"))
            before.Remove("knownFields");
        if (previous.Page == ControlPageId.Performance && before.TryGetValue("boostPolicy", out var savedPolicy) &&
            savedPolicy.ValueKind == JsonValueKind.Object && incoming.TryGetValue("isBoostEnabled", out var enabled) &&
            before.TryGetValue("isBoostEnabled", out var oldEnabled))
        {
            bool sameSwitch = JsonElement.DeepEquals(enabled, oldEnabled);
            if (!incoming.TryGetValue("boostPolicy", out var policy) || policy.ValueKind == JsonValueKind.Null)
            {
                if (!sameSwitch) incoming["boostPolicy"] = JsonSerializer.SerializeToElement((CpuBoostPolicy?)null);
                else if (!copyCurrent) incoming.Remove("boostPolicy");
            }
        }
        var candidate = previous with { SchemaVersion = PagePresetEnvelope.CurrentSchemaVersion,
            DisplayName = name, Payload = JsonSerializer.SerializeToElement(before.Concat(incoming).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value)), SavedAtUtc = DateTimeOffset.UtcNow, UserModified = true };
        var inferred = DefaultControlPresets.InferSettings(candidate);
        var settings = new Dictionary<string, SettingRequest>(previous.Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var property in incoming)
        {
            var field = JsonNamingPolicy.CamelCase.ConvertName(property.Key);
            if (!inferred.TryGetValue(field, out var intent)) continue;
            bool changed = copyCurrent || !before.TryGetValue(property.Key, out var old) || !JsonElement.DeepEquals(old, property.Value);
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
        if (FlagChanged("boostPolicy") && inferred.TryGetValue("isBoostEnabled", out var boost))
            settings["isBoostEnabled"] = boost;
        if (performanceDraft?.SettingRequests is { } explicitRequests)
            foreach (var (field, request) in explicitRequests) settings[field] = request;
        var result = candidate with { Settings = settings };
        if (performanceDraft is not null)
        {
            var validation = PresetValidation.Validate(result);
            if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0]);
        }
        return result;
    }

    private static PerformanceDraft? SnapshotDraft(PerformanceDraft? draft) =>
        draft is null ? null : PerformancePresetProjection.ApplyEdits(draft, draft, []);

    public Task<PagePresetEnvelope[]> PublishAsync(ControlModeId mode, CancellationToken token, Func<bool>? isCurrent = null) =>
        PublishAsync(mode, token, isCurrent, null);

    private async Task<PagePresetEnvelope[]> PublishAsync(ControlModeId mode, CancellationToken token, Func<bool>? isCurrent,
        (ControlPageId Page, PresetKey Key)? requestedPreset)
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
                    Selections(logical, items, requireFollowed: false, requestedPreset: requestedPreset))
                    .Where(s => s.Revision > 0).ToArray();
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
        CancellationToken token = default, Func<bool>? isCurrent = null, ControlPageId? applyOnlyPage = null)
    {
        bool explicitRequest = origin != ControlOrigin.ModeFollow;
        bool followHeld = false;
        bool manualAccepted = false;
        bool manualStarted = false;
        long manualIntent = 0;
        long intent = 0;
        bool Current() => intent == Volatile.Read(ref applyVersion) && (explicitRequest || !followApplicationsStopped) && isCurrent?.Invoke() != false;
        try
        {
            if (!explicitRequest)
            {
                await followApplication.WaitAsync(token);
                followHeld = true;
                if (followApplicationsStopped || isCurrent?.Invoke() == false) return null;
            }
            else
            {
                explicitApplications++;
                manualStarted = true;
                manualIntent = ++manualFollowVersion;
                manualFollowTarget = nativeMode;
                CancelModeFollow();
            }
            intent = Interlocked.Increment(ref applyVersion);
            await FlushAsync(token);
            if (!Current()) return null;
            // Mode-only requests must not wait behind catalog publication on the polling connection.
            if (origin == ControlOrigin.ManualMode && explicitPage is null && explicitKey is null &&
                SelectionKeys(mode).All(selection => !selection.Follow))
            {
                var modeResult = await session.ApplyControlRequestAsync(mode, nativeMode, [], origin, token);
                manualAccepted = modeResult is { State: CommandState.Applied, Error: null };
                return Current() ? modeResult : null;
            }
            if (explicitPage is ControlPageId.Performance or ControlPageId.Gpu or ControlPageId.Fan &&
                explicitKey is { Slot: 2 } balanced && session.SchedulingBaseline is null &&
                (await store.LoadAsync(explicitPage.Value, balanced, token)) is { UserModified: false })
                throw new InvalidOperationException("尚未读取完整的本机均衡基准，未应用；请等待服务读回，或保存自己的手动配置。");
            (ControlPageId Page, PresetKey Key)? requestedPreset =
                explicitPage is { } page && explicitKey is { } key ? (page, key) : null;
            var pages = await PublishAsync(mode, token, Current, requestedPreset);
            if (!Current()) return null;
            var selections = Selections(mode, pages);
            if (explicitPage is { } selectedPage && explicitKey is { } selectedKey)
            {
                var item = pages.First(p => p.Page == selectedPage && p.Key == selectedKey);
                selections = selections.Select(s => s.Page == selectedPage
                    ? new PageSelection(selectedPage, true, selectedKey, item.Revision) : s).ToArray();
            }
            var result = await session.ApplyControlRequestAsync(mode, nativeMode,
                selections.Where(s => s.Follow && (applyOnlyPage is null || s.Page == applyOnlyPage)).ToArray(), origin, token);
            manualAccepted = result is { State: CommandState.Applied, Error: null };
            if (!Current()) return null;
            if (result is { State: CommandState.Applied, Error: null } && requestedPreset is { } applied)
            {
                var selected = selections.Single(selection => selection.Page == applied.Page);
                var pageResults = result.ExecutionState?.Pages.Where(page => page.Page == applied.Page).ToArray();
                // Aggregate Applied can include skipped or failed pages; retain legacy replies without page details.
                if (pageResults is not null && (pageResults.Length != 1 || pageResults[0] is not
                    { State: ApplyState.Confirmed, ErrorCode: null, UnverifiedFields.Length: 0, SkippedFields.Length: 0 } confirmed ||
                    confirmed.Key != selected.Key || confirmed.Revision != selected.Revision)) return result;
                try { RememberSlot(applied.Page, applied.Key); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    session.ReportOperationFailure("预设已应用，但记忆档位保存失败；请检查配置目录。");
                    AppRuntimeLog.Write($"Applied preset slot save: {error}");
                }
            }
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        { if (Current()) session.ReportOperationFailure(DescribeApplyFailure(error)); AppRuntimeLog.Write($"Preset request: {error}"); return null; }
        finally
        {
            if (manualStarted)
            {
                explicitApplications--;
                if (manualIntent == manualFollowVersion && !manualAccepted) manualFollowTarget = null;
            }
            if (followHeld) followApplication.Release();
        }
    }

    public async Task FollowPreferenceChangedAsync(ControlPageId page, bool enabled, CancellationToken token = default)
    {
        long version = followPreferenceVersions.GetValueOrDefault(page) + 1;
        followPreferenceVersions[page] = version;
        var target = session.State;
        long manualIntent = manualFollowVersion;
        long connection = session.ConnectionGeneration;
        var logicalMode = CurrentLogicalMode(target);
        var nativeMode = target?.Controls.PerformanceMode;
        bool apply = enabled && page != ControlPageId.Lighting;
        pageFollow.TryGetValue(page, out var followState);
        if (apply) followState!.EnablesInFlight++;
        if (followState is not null)
        {
            followState.Observer.Reset();
            if (apply && target is not null) followState.Observer.Observe(target, connection, canApply: false);
        }
        bool PreferenceCurrent()
        {
            if (followPreferenceVersions.GetValueOrDefault(page) != version) return false;
            var saved = preferences.Load();
            return page switch
            {
                ControlPageId.Performance => saved.PerformanceFollowPreset == enabled,
                ControlPageId.Gpu => saved.GpuFollowPreset == enabled,
                ControlPageId.Fan => saved.FanFollowPreset == enabled,
                ControlPageId.Lighting => saved.LightingFollowPreset == enabled,
                _ => false
            };
        }
        bool Current()
        {
            if (token.IsCancellationRequested || !PreferenceCurrent()) return false;
            if (!apply) return true;
            var current = session.State;
            return !followApplicationsStopped && manualIntent == manualFollowVersion && explicitApplications == 0 &&
                nativeMode is not null && session.IsServiceConnected &&
                session.Status == HomeSessionStatus.Connected && session.ConfigurationRestorationSettled &&
                connection == session.ConnectionGeneration && current?.Controls.PerformanceMode == nativeMode &&
                current.Controls.AdaptiveAutomation?.Enabled != true &&
                CurrentLogicalMode(current) == logicalMode;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            await FlushAsync(token);
            if (!Current()) return;
            await PublishAsync(logicalMode, token, Current);
            if (!Current()) return;
            // Lighting has its own application coordinator; enabling it must not reapply the mode or other pages.
            if (apply && session.State?.Controls.AdaptiveAutomation?.Enabled != true)
                await ApplyAsync(logicalMode, nativeMode!.Value,
                    ControlOrigin.ModeFollow, token: token, isCurrent: Current, applyOnlyPage: page);
        }
        catch (Exception error)
        { if (PreferenceCurrent()) session.ReportOperationFailure("跟随配置尚未确认，请重试"); AppRuntimeLog.Write($"Preset follow configuration: {error}"); }
        finally
        {
            if (apply) followState!.EnablesInFlight--;
            if (PreferenceCurrent()) FollowChanged?.Invoke();
        }
    }

    public PerformanceMode NativeModeForSelection(ControlModeId mode, PresetKey? explicitPreset = null) =>
        explicitPreset is { } key ? NativeModeFor(key) :
        AdaptiveTargetMap.HardwareModeFor(PresetKey.Create(mode, 2), preferences.Load().CustomBaseModes);

    public PerformanceMode NativeModeFor(PresetKey key) => key.Slot == 2 && session.SchedulingBaseline is { } baseline
        ? baseline.NativeMode : AdaptiveTargetMap.HardwareModeFor(key, preferences.Load().CustomBaseModes);

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

    public ControlModeId CurrentLogicalMode() => CurrentLogicalMode(session.State);

    private static ControlModeId CurrentLogicalMode(HomeStateSnapshot? snapshot) => snapshot?.Controls.ExecutionState is { NativeModeApplyState: ApplyState.Confirmed } execution
        ? execution.LogicalMode : snapshot?.Controls.PerformanceMode switch
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
        IEnumerable<PresetKey> targets = keys;
        if (preferences.Load().LightingFollowPreset)
            targets = targets.Concat(new[] { ControlModeId.Office, ControlModeId.Gaming, ControlModeId.Turbo }
                .Select(mode => PresetKey.Create(mode, 1)));
        foreach (var key in targets.Distinct())
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
        bool requireFollowed = true, (ControlPageId Page, PresetKey Key)? requestedPreset = null) => SelectionKeys(mode)
        .Select(s => { var key = s.Page == ControlPageId.Performance ? performanceKey ?? s.Key : s.Key;
            if (requestedPreset is { } requested && requested.Page == s.Page && requested.Key.Mode == mode)
                key = requested.Key;
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
