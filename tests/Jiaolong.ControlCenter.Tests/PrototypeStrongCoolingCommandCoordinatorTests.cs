using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypeStrongCoolingCommandCoordinatorTests
{
    [TestMethod]
    public async Task Rapid_toggle_requests_are_serialized_and_latest_state_is_sent()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<bool>();
        var coordinator = new PrototypeStrongCoolingCommandCoordinator(async (enabled, _) =>
        {
            sent.Add(enabled);
            if (enabled)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        });

        var first = coordinator.RequestAsync(true, CancellationToken.None);
        await firstStarted.Task;
        var latest = coordinator.RequestAsync(false, CancellationToken.None);
        releaseFirst.SetResult();

        await Task.WhenAll(first, latest);

        CollectionAssert.AreEqual(new[] { true, false }, sent);

        sent.Clear();
        var synchronous = new PrototypeStrongCoolingCommandCoordinator((enabled, _) =>
        {
            sent.Add(enabled);
            return Task.CompletedTask;
        });
        await synchronous.RequestAsync(true, CancellationToken.None);
        await synchronous.RequestAsync(false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { true, false }, sent);

        sent.Clear();
        var retry = new PrototypeStrongCoolingCommandCoordinator((enabled, _) =>
        {
            sent.Add(enabled);
            return enabled ? Task.FromException(new IOException("write failed")) : Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => retry.RequestAsync(true, CancellationToken.None));
        await retry.RequestAsync(false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { true, false }, sent);
    }
}
