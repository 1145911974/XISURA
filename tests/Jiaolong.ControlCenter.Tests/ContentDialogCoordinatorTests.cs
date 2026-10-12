using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ContentDialogCoordinatorTests
{
    [TestMethod]
    public async Task Dialog_queue_cancels_waiting_request_without_showing_it()
    {
        var queue = new DialogRequestQueue<int>(() => -1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int canceledShowCount = 0;
        var active = queue.EnqueueAsync(async () =>
        {
            entered.SetResult();
            return await release.Task;
        }, CancellationToken.None);
        await entered.Task;

        using var cancellation = new CancellationTokenSource();
        var queued = queue.EnqueueAsync(() =>
        {
            canceledShowCount++;
            return Task.FromResult(2);
        }, cancellation.Token);
        cancellation.Cancel();

        Assert.AreEqual(-1, await queued);
        release.SetResult(1);
        Assert.AreEqual(1, await active);
        Assert.AreEqual(0, canceledShowCount);
    }

    [TestMethod]
    public async Task Dialog_queue_coalesces_in_flight_duplicate_keys()
    {
        var queue = new DialogRequestQueue<int>(() => -1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int showCount = 0;
        var first = queue.EnqueueAsync(async () =>
        {
            showCount++;
            entered.SetResult();
            return await release.Task;
        }, CancellationToken.None, "same-error");
        await entered.Task;
        var duplicate = queue.EnqueueAsync(() => Task.FromResult(99), CancellationToken.None, "same-error");

        Assert.AreSame(first, duplicate);
        release.SetResult(7);
        Assert.AreEqual(7, await first);
        Assert.AreEqual(7, await duplicate);
        Assert.AreEqual(1, showCount);
    }
}
