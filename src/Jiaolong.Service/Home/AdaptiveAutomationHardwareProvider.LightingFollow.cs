using System.Collections.Concurrent;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class AdaptiveAutomationHardwareProvider
{
    private sealed record ModeLightingIntent(long Revision, string OwnerSid, PerformanceMode NativeMode, PageSelection Selection);
    private readonly ConcurrentDictionary<Guid, ModeLightingIntent> modeLightingOperations = new();

    internal bool IsModeLightingFollowOperation(Guid id) => modeLightingOperations.ContainsKey(id);

    internal bool MatchesModeLightingFollowRequest(ApplyControlRequestCommand command) =>
        modeLightingOperations.TryGetValue(command.OperationId, out var intent) &&
        command.Request is { Origin: ControlOrigin.PresetUse, Pages: [var page] } request &&
        request.NativeMode == intent.NativeMode && request.LogicalMode == intent.Selection.Key.Mode && page == intent.Selection;

    private async Task<bool> IsModeLightingIntentCurrentAsync(ModeLightingIntent intent, CancellationToken token)
    {
        var saved = await store.ReadAsync(token);
        if (saved.Revision != intent.Revision || saved.OwnerSid != intent.OwnerSid ||
            NativeModeLightingFollowPolicy.Resolve(saved, intent.NativeMode)?.Selection != intent.Selection) return false;
        return (await inner.ReadNativeModeEvidenceAsync(token))?.ParsedMode == intent.NativeMode;
    }

    internal async Task<CommandResult> ExecuteModeLightingFollowAsync(HomeServiceRuntime runtime,
        PerformanceMode observed, long expectedRevision, CancellationToken token)
    {
        Guid id = Guid.NewGuid();
        var saved = await store.ReadAsync(token);
        var target = NativeModeLightingFollowPolicy.Resolve(saved, observed);
        if (saved.Revision != expectedRevision || saved.OwnerSid is not { Length: > 0 } owner || target is null)
            return Rejected(id, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };

        string catalogOwner = HomeServiceRuntime.AutomaticCatalogOwner(owner);
        var items = saved.Configuration!.Presets.SelectMany(preset => preset.Pages ?? [])
            .Where(page => page.Page == ControlPageId.Lighting).DistinctBy(page => (page.Page, page.Key)).ToArray();
        var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), runtime.Execution.Catalog.Revision(catalogOwner), items);
        var published = await runtime.ExecuteAsync(publication, token, userSid: catalogOwner);
        if (published.State != CommandState.Applied || published.Error is not null) return published;

        var intent = new ModeLightingIntent(saved.Revision, owner, observed, target.Selection);
        modeLightingOperations.TryAdd(id, intent);
        try
        {
            var request = new ApplyControlRequestCommand(id,
                new(id, 0, ControlOrigin.PresetUse, target.Selection.Key.Mode, observed, [target.Selection]));
            return await runtime.ExecuteAsync(request, token, userSid: owner);
        }
        finally { modeLightingOperations.TryRemove(id, out _); }
    }
}
