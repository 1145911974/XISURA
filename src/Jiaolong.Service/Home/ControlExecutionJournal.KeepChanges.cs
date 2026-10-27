namespace Jiaolong.Service.Home;

public sealed partial class ControlExecutionJournal
{
    internal async Task ReleaseRetainedSessionAsync(ControlJournalSnapshot original, ControlJournalSnapshot released, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var current = await LoadAsync(token);
            var disk = await new ControlExecutionJournal(RootDirectory).ReadAsync(token);
            bool Same(ControlJournalSnapshot left, ControlJournalSnapshot right) =>
                System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(left),
                    System.Text.Json.JsonSerializer.SerializeToElement(right));
            if (!Same(current, disk) || !Same(disk, original) && !Same(disk, released))
                throw new InvalidDataException("exitRetentionJournalChanged");
            if (Same(disk, released)) return;
            await WriteSnapshotAsync(released, token);
            snapshot = Clone(released);
        }
        finally { gate.Release(); }
    }
}
