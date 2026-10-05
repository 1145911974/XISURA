using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Jiaolong_ControlCenter;

internal static class Program
{
    private static readonly object activationLock = new();
    private static Action? restoreWindow;
    private static bool activationPending;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey($"XISURA.{WindowsIdentity.GetCurrent().User!.Value}");
        if (!instance.IsCurrent)
        {
            RedirectActivation(instance);
            return;
        }
        instance.Activated += OnActivated;
        // Select the compatible TIP before WinUI creates any input-site HWNDs.
        Services.ProcessInputMethod.UseMicrosoftPinyin();
        Application.Start(initialization =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App();
            lock (activationLock)
            {
                restoreWindow = () => dispatcher.TryEnqueue(app.RestoreExistingWindow);
                if (activationPending) restoreWindow();
                activationPending = false;
            }
        });
        instance.Activated -= OnActivated;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        lock (activationLock)
        {
            if (restoreWindow is null) activationPending = true;
            else restoreWindow();
        }
    }

    private static void RedirectActivation(AppInstance instance)
    {
        using var completed = new ManualResetEvent(false);
        // Pump COM on the entry STA while activation redirects on an MTA thread.
        var redirect = Task.Run(async () =>
        {
            try
            {
                AllowSetForegroundWindow(instance.ProcessId);
                await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            }
            finally { completed.Set(); }
        });
        int result = CoWaitForMultipleObjects(0, uint.MaxValue, 1,
            [completed.SafeWaitHandle.DangerousGetHandle()], out _);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        redirect.GetAwaiter().GetResult();
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
