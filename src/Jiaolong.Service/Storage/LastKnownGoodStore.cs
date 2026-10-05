using Jiaolong.Contracts.Commands;

namespace Jiaolong.Service.Storage;

public sealed class LastKnownGoodStore
{
    private readonly AtomicJsonStore store;

    public LastKnownGoodStore(string? root = null)
    {
        root ??= MachinePaths.BaseDirectory;
        store = new AtomicJsonStore(root, "last-known-good.json");
    }

    public bool WasUpdated { get; private set; }

    public async Task UpdateAsync(CommandResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.State != CommandState.Applied) return;

        await store.WriteAsync(result, cancellationToken);
        WasUpdated = true;
    }

    public Task<CommandResult?> ReadAsync(CancellationToken cancellationToken) => store.ReadAsync<CommandResult>(cancellationToken);
}
