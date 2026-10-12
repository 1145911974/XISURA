using System.Reflection;
using Jiaolong.Service.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class SystemPowerNotificationsTests
{
    private static void Notify(SystemPowerNotifications notifications, uint type)
    {
        var callback = (Delegate)typeof(SystemPowerNotifications).GetField("callback", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(notifications)!;
        callback.DynamicInvoke(IntPtr.Zero, type, IntPtr.Zero);
    }

    [TestMethod]
    public async Task Burst_keeps_all_transitions_while_lifecycle_consumer_waits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<bool>();
        var notifications = new SystemPowerNotifications(async (resume, _, token) =>
        {
            observed.Add(resume);
            if (observed.Count == 1) { entered.SetResult(); await unblock.Task.WaitAsync(token); }
        }, NullLogger.Instance, deadline.Token, registerNative: false);
        try
        {
            Notify(notifications, 4);
            await entered.Task.WaitAsync(deadline.Token);
            for (int i = 0; i < 20; i++) Notify(notifications, i % 2 == 0 ? 18u : 4u);
        }
        finally { unblock.TrySetResult(); await notifications.DisposeAsync(); }
        Assert.AreEqual(21, observed.Count, "A full queue must not silently drop suspend/resume transitions.");
        for (int i = 0; i < observed.Count; i++) Assert.AreEqual(i % 2 == 1, observed[i]);
    }

    [TestMethod]
    public async Task Failed_transition_allows_retry_or_reverse_then_deduplicates_success()
    {
        foreach (bool failureIsResume in new[] { true, false })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int failedDirectionAttempts = 0;
            var observed = new List<bool>();
            var notifications = new SystemPowerNotifications((resume, _, _) =>
            {
                observed.Add(resume);
                if (resume == failureIsResume && ++failedDirectionAttempts == 1)
                    throw new IOException("Simulated lifecycle failure after pause flags changed");
                return Task.CompletedTask;
            }, failureIsResume ? new ThrowingLogger() : NullLogger.Instance, deadline.Token, registerNative: false);
            Notify(notifications, failureIsResume ? 4u : 18u);
            Notify(notifications, failureIsResume ? 18u : 4u);
            Notify(notifications, 7);
            Notify(notifications, 18);
            await notifications.DisposeAsync();
            CollectionAssert.AreEqual(new[] { !failureIsResume, failureIsResume, true }, observed.ToArray(),
                "A failed transition must not let an earlier success suppress recovery; completed duplicates still collapse.");
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("fakeLoggerFailure");
    }
}
