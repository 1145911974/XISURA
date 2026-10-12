using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;

namespace Jiaolong.Service.Home;

public sealed class ControlPresetCatalog
{
    private readonly object gate = new();
    private readonly Dictionary<string, Catalog> users = new(StringComparer.Ordinal);

    public CommandResult Publish(PublishPresetCatalogCommand command, string user)
    {
        if (command.Items is not { Length: > 0 and <= 72 } || command.Selections is null ||
            command.Items.Any(item => item is null || item.SchemaVersion != PagePresetEnvelope.CurrentSchemaVersion ||
                item.Revision <= 0 || !PresetValidation.Validate(item).IsValid) ||
            command.Items.Select(item => (item.Page, item.Key)).Distinct().Count() != command.Items.Length ||
            command.Selections.Length > 24 || command.Selections.Any(item => item is null || !Enum.IsDefined(item.Page) ||
                !Enum.IsDefined(item.Key.Mode) || item.Key.Slot is < 1 or > 3) ||
            command.Selections.Select(item => (item.Page, item.Key.Mode)).Distinct().Count() != command.Selections.Length)
            return Reject(command, ErrorCode.ValidationFailed, "invalidCatalog");
        lock (gate)
        {
            users.TryGetValue(user, out var prior);
            long revision = prior?.Revision ?? 0;
            if (command.ExpectedCatalogRevision != revision)
                return Reject(command, ErrorCode.IdempotencyConflict, "expectedCatalogRevisionMismatch") with { CatalogRevision = revision };
            if (prior is null && users.Count >= 8) return Reject(command, ErrorCode.CommandInProgress, "userCatalogCapacityExceeded");
            var items = prior is null ? new Dictionary<(ControlPageId, PresetKey), PagePresetEnvelope>() : new(prior.Items);
            var conflicts = command.Items.Where(item => items.TryGetValue((item.Page, item.Key), out var previous) &&
                (item.Revision < previous.Revision || item.Revision == previous.Revision && !Same(item, previous))).ToArray();
            if (conflicts.Length > 0)
            {
                var rejection = Reject(command, ErrorCode.IdempotencyConflict,
                    conflicts.Any(item => item.Revision < items[(item.Page, item.Key)].Revision)
                        ? "presetRevisionRegressed" : "presetRevisionContentConflict") with { CatalogRevision = revision };
                foreach (var item in conflicts)
                    rejection.Error!.Details[$"presetRevision:{(int)item.Page}:{(int)item.Key.Mode}:{item.Key.Slot}"] =
                        items[(item.Page, item.Key)].Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return rejection;
            }
            foreach (var item in command.Items)
            {
                items[(item.Page, item.Key)] = Clone(item);
            }
            var selections = command.Selections.Length == 0 ? prior?.Selections ?? [] : command.Selections.ToArray();
            if (selections.Any(item => !items.TryGetValue((item.Page, item.Key), out var envelope) || envelope.Revision != item.Revision))
                return Reject(command, ErrorCode.ValidationFailed, "selectionRevisionMismatch") with { CatalogRevision = revision };
            users[user] = new(revision + 1, items, selections);
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.NoMutation, CatalogRevision = revision + 1 };
        }
    }

    public long Revision(string user) { lock (gate) return users.GetValueOrDefault(user)?.Revision ?? 0; }

    public PageSelection[] Selections(string user, ControlModeId mode)
    {
        lock (gate) return users.GetValueOrDefault(user)?.Selections.Where(item => item.Key.Mode == mode).ToArray() ?? [];
    }

    public PagePresetEnvelope[] Resolve(string user, PageSelection[] pages)
    {
        lock (gate)
        {
            users.TryGetValue(user, out var catalog);
            return pages.Select(page => catalog is not null && catalog.Items.TryGetValue((page.Page, page.Key), out var item) &&
                    item.Revision == page.Revision ? Clone(item) : throw new InvalidDataException("presetRevisionNotPublished")).ToArray();
        }
    }

    private static bool Same(PagePresetEnvelope left, PagePresetEnvelope right) =>
        JsonSerializer.Serialize(left, ProtocolJsonContext.Default.PagePresetEnvelope) ==
        JsonSerializer.Serialize(right, ProtocolJsonContext.Default.PagePresetEnvelope);
    private static PagePresetEnvelope Clone(PagePresetEnvelope item) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(item, ProtocolJsonContext.Default.PagePresetEnvelope),
            ProtocolJsonContext.Default.PagePresetEnvelope)!;
    private static CommandResult Reject(HardwareCommand command, ErrorCode code, string cause) => new(command.OperationId,
        CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, command.OperationId, false) with
        { Details = new() { ["stage"] = "CatalogPublish", ["cause"] = cause } }, false)
    { Mutation = HardwareMutation.NoMutation };
    private sealed record Catalog(long Revision, Dictionary<(ControlPageId, PresetKey), PagePresetEnvelope> Items, PageSelection[] Selections);
}
