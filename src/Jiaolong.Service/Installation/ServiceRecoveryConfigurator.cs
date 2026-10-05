using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Installation;

internal static class ServiceRecoveryConfigurator
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceConfigFailureActions = 2;
    private const int ErrorInsufficientBuffer = 122;

    public static int Run()
    {
        try
        {
            Configure();
            return 0;
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"Service recovery configuration failed: {exception.NativeErrorCode}");
            return exception.NativeErrorCode == 0 ? 1 : exception.NativeErrorCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Service recovery configuration failed: {exception.Message}");
            return 1;
        }
    }

    private static void Configure()
    {
        using var manager = NativeMethods.OpenScManager(null, null, ScManagerConnect);
        EnsureHandle(manager, "OpenSCManager");

        using var service = NativeMethods.OpenService(
            manager,
            ServiceRecoveryPlan.ServiceName,
            ServiceQueryConfig | ServiceChangeConfig | ServiceStart);
        EnsureHandle(service, "OpenService");

        var actionSize = Marshal.SizeOf<NativeMethods.ScAction>();
        var actionsBuffer = Marshal.AllocHGlobal(actionSize * ServiceRecoveryPlan.Actions.Count);
        var failureActionsBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceFailureActions>());
        try
        {
            for (var index = 0; index < ServiceRecoveryPlan.Actions.Count; index++)
            {
                var action = ServiceRecoveryPlan.Actions[index];
                Marshal.StructureToPtr(
                    new NativeMethods.ScAction(action.Type, action.DelayMilliseconds),
                    IntPtr.Add(actionsBuffer, index * actionSize),
                    fDeleteOld: false);
            }

            Marshal.StructureToPtr(
                new NativeMethods.ServiceFailureActions(
                    ServiceRecoveryPlan.ResetPeriodSeconds,
                    ServiceRecoveryPlan.Actions.Count,
                    actionsBuffer),
                failureActionsBuffer,
                fDeleteOld: false);

            if (!NativeMethods.ChangeServiceConfig2(service, ServiceConfigFailureActions, failureActionsBuffer))
            {
                throw LastWin32Error("ChangeServiceConfig2");
            }

            Verify(service, actionSize);
        }
        finally
        {
            Marshal.FreeHGlobal(failureActionsBuffer);
            Marshal.FreeHGlobal(actionsBuffer);
        }
    }

    private static void Verify(NativeMethods.ServiceHandle service, int actionSize)
    {
        _ = NativeMethods.QueryServiceConfig2(service, ServiceConfigFailureActions, IntPtr.Zero, 0, out var bytesNeeded);
        var queryError = Marshal.GetLastWin32Error();
        if (queryError != ErrorInsufficientBuffer || bytesNeeded == 0)
        {
            throw new Win32Exception(queryError, "QueryServiceConfig2 size probe failed.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(service, ServiceConfigFailureActions, buffer, bytesNeeded, out _))
            {
                throw LastWin32Error("QueryServiceConfig2");
            }

            var configured = Marshal.PtrToStructure<NativeMethods.ServiceFailureActions>(buffer);
            if (configured.ResetPeriod != ServiceRecoveryPlan.ResetPeriodSeconds ||
                configured.ActionsCount != ServiceRecoveryPlan.Actions.Count ||
                configured.Actions == IntPtr.Zero)
            {
                throw new InvalidOperationException("Service recovery configuration did not match the required plan.");
            }

            for (var index = 0; index < ServiceRecoveryPlan.Actions.Count; index++)
            {
                var actual = Marshal.PtrToStructure<NativeMethods.ScAction>(IntPtr.Add(configured.Actions, index * actionSize));
                var expected = ServiceRecoveryPlan.Actions[index];
                if (actual.Type != expected.Type || actual.DelayMilliseconds != expected.DelayMilliseconds)
                {
                    throw new InvalidOperationException("Service recovery action did not match the required plan.");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void EnsureHandle(NativeMethods.ServiceHandle handle, string operation)
    {
        if (handle.IsInvalid)
        {
            throw LastWin32Error(operation);
        }
    }

    private static Win32Exception LastWin32Error(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed.");

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenScManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, IntPtr data);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig2(ServiceHandle service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct ScAction(uint type, uint delayMilliseconds)
        {
            internal readonly uint Type = type;
            internal readonly uint DelayMilliseconds = delayMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct ServiceFailureActions(uint resetPeriod, int actionsCount, IntPtr actions)
        {
            internal readonly uint ResetPeriod = resetPeriod;
            internal readonly IntPtr RebootMessage = IntPtr.Zero;
            internal readonly IntPtr Command = IntPtr.Zero;
            internal readonly int ActionsCount = actionsCount;
            internal readonly IntPtr Actions = actions;
        }

        internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal ServiceHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }
    }
}
