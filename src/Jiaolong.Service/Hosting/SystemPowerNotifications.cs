using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Jiaolong.Service.Hosting;

// Powrprof callback works in a service without a UI window or SystemEvents message pump.
internal sealed class SystemPowerNotifications : IAsyncDisposable
{
    private readonly NotificationCallback callback;
    private readonly Channel<(uint Type, long Version)> events = Channel.CreateUnbounded<(uint, long)>(new()
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task consumer;
    private IntPtr registration;

    public SystemPowerNotifications(Func<bool, long, CancellationToken, Task> handle, ILogger logger, CancellationToken stopping,
        bool registerNative = true, Func<bool, long>? prepare = null)
    {
        callback = (_, type, _) =>
        {
            if (type is 4 or 7 or 18) events.Writer.TryWrite((type, prepare?.Invoke(type != 4) ?? 0));
            return 0;
        };
        if (registerNative)
        {
            var parameters = new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(callback) };
            uint error = PowerRegisterSuspendResumeNotification(2, ref parameters, out registration);
            if (error != 0) throw new Win32Exception((int)error);
        }
        consumer = ConsumeAsync(handle, logger, stopping);
    }

    private async Task ConsumeAsync(Func<bool, long, CancellationToken, Task> handle, ILogger logger, CancellationToken stopping)
    {
        (bool Direction, long Version)? previous = null;
        try
        {
            await foreach (var (type, version) in events.Reader.ReadAllAsync(stopping))
            {
                bool? resume = type switch { 4 => false, 7 or 18 => true, _ => null };
                if (resume is not bool value || previous == (value, version)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try { await handle(value, version, deadline.Token); previous = (value, version); }
                catch (Exception error)
                {
                    previous = null; // A partially applied transition invalidates the last successful direction.
                    try { logger.LogWarning(error, "Power lifecycle {type} failed; unresolved ownership remains retained.", type); }
                    catch (Exception loggingError)
                    {
                        try { System.Diagnostics.Trace.WriteLine($"Power lifecycle {type} failed: {error}; logger failed: {loggingError}"); }
                        catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (registration != IntPtr.Zero) { _ = PowerUnregisterSuspendResumeNotification(registration); registration = IntPtr.Zero; }
        events.Writer.TryComplete();
        await consumer;
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SubscribeParameters { public IntPtr Callback, Context; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint NotificationCallback(IntPtr context, uint type, IntPtr setting);
    [DllImport("Powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref SubscribeParameters recipient, out IntPtr registration);
    [DllImport("Powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
