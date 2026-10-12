using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Services;

internal static class ContentDialogCoordinator
{
    private static readonly ConditionalWeakTable<XamlRoot, DialogRequestQueue<ContentDialogResult>> Queues = new();

    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken = default,
        string? operationId = null,
        Func<bool>? isCurrent = null)
    {
        var root = dialog.XamlRoot ?? throw new InvalidOperationException("ContentDialog must have a XamlRoot.");
        if (!root.IsHostVisible) return ContentDialogResult.None;

        using var visibilityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (!sender.IsHostVisible) visibilityCancellation.Cancel();
        }
        root.Changed += OnRootChanged;
        cancellationToken = visibilityCancellation.Token;

        string? deduplicationKey = operationId is not null
            ? $"operation:{operationId}"
            : dialog.Title is string title && dialog.Content is string body && IsErrorTitle(title)
                ? $"error:{title}\0{body}"
                : null;

        var queue = Queues.GetValue(root, static _ => new DialogRequestQueue<ContentDialogResult>(() => ContentDialogResult.None));
        try
        {
            return await queue.EnqueueAsync(async () =>
            {
                if (!root.IsHostVisible || isCurrent?.Invoke() == false || cancellationToken.IsCancellationRequested)
                    return ContentDialogResult.None;
                using var registration = cancellationToken.Register(() =>
                    dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
                if (cancellationToken.IsCancellationRequested) return ContentDialogResult.None;
                var result = await dialog.ShowAsync();
                return cancellationToken.IsCancellationRequested ? ContentDialogResult.None : result;
            },
                cancellationToken, deduplicationKey);
        }
        finally { root.Changed -= OnRootChanged; }
    }

    private static bool IsErrorTitle(string title) =>
        title.Contains("未完成", StringComparison.Ordinal) || title.Contains("失败", StringComparison.Ordinal);
}

internal sealed class DialogRequestQueue<TResult>(Func<TResult> canceledResult)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Task<TResult>> inFlight = new(StringComparer.Ordinal);

    public Task<TResult> EnqueueAsync(Func<Task<TResult>> show, CancellationToken cancellationToken, string? key = null)
    {
        TaskCompletionSource<TResult> completion;
        lock (sync)
        {
            if (key is not null && inFlight.TryGetValue(key, out var existing)) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (key is not null) inFlight.Add(key, completion.Task);
        }

        _ = RunAsync(show, cancellationToken, key, completion);
        return completion.Task;
    }

    private async Task RunAsync(
        Func<Task<TResult>> show,
        CancellationToken cancellationToken,
        string? key,
        TaskCompletionSource<TResult> completion)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                completion.TrySetResult(cancellationToken.IsCancellationRequested ? canceledResult() : await show());
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(canceledResult());
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            if (key is not null)
            {
                lock (sync)
                    if (inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, completion.Task))
                        inFlight.Remove(key);
            }
        }
    }
}
