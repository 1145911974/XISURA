using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed record WindowsPowerState(Guid SchemeId, bool BoostEnabled);

public interface IWindowsPowerTransport
{
    Task<WindowsPowerState> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(Guid schemeId, bool boostEnabled, CancellationToken cancellationToken);
}

public static class WindowsPowerBoostMode
{
    public static int SelectTarget(int currentMode, bool enabled) =>
        enabled ? currentMode == 0 ? 2 : currentMode : 0;

    public static bool IsEnabled(int mode) => mode != 0;
}

public sealed record WindowsPowerSettingsSnapshot(
    Guid ActiveSchemeId,
    int AcMaxFrequencyMhz,
    int DcMaxFrequencyMhz,
    int AcBoostMode,
    int DcBoostMode)
{
    public string? ActiveSchemeName { get; init; }
    public int? AcMinActiveCoresPercent { get; init; }
    public int? DcMinActiveCoresPercent { get; init; }
}

public sealed record WindowsCoreParkingValue(int Ac, int Dc);

public interface IWindowsPowerSettingsTransport
{
    Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task SetSchemeAsync(Guid schemeId, CancellationToken cancellationToken);
    Task SetMaxFrequencyAsync(int acMegahertz, int dcMegahertz, CancellationToken cancellationToken);
    Task SetBoostAsync(bool enabled, CancellationToken cancellationToken);
    Task SetCoreParkingAsync(int acPercent, int dcPercent, CancellationToken cancellationToken);
    Task RestoreAsync(CpuTuningField field, WindowsPowerSettingsSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class WindowsPowerSettingsTransport : IWindowsPowerSettingsTransport
{
    private static readonly Guid ProcessorPowerManagementSubgroup =
        Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMaximumFrequency =
        Guid.Parse("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    private static readonly Guid ProcessorBoostMode =
        Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid ProcessorCoreParkingMinCores =
        Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");

    public Task<WindowsPowerSettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = ReadActiveScheme();
        return Task.FromResult(ReadScheme(scheme));
    }

    public Task SetSchemeAsync(Guid schemeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ReadScheme(schemeId);
        SetActiveScheme(schemeId);
        return Task.CompletedTask;
    }

    public Task SetMaxFrequencyAsync(int acMegahertz, int dcMegahertz, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = ReadActiveScheme();
        WriteFrequency(scheme, checked((uint)acMegahertz), checked((uint)dcMegahertz));
        SetActiveScheme(scheme);
        return Task.CompletedTask;
    }

    public Task SetBoostAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = ReadActiveScheme();
        var current = ReadScheme(scheme);
        WriteBoost(
            scheme,
            WindowsPowerBoostMode.SelectTarget(current.AcBoostMode, enabled),
            WindowsPowerBoostMode.SelectTarget(current.DcBoostMode, enabled));
        SetActiveScheme(scheme);
        return Task.CompletedTask;
    }

    public Task SetCoreParkingAsync(int acPercent, int dcPercent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (acPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(acPercent));
        if (dcPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(dcPercent));
        var scheme = ReadActiveScheme();
        WriteCoreParking(scheme, acPercent, dcPercent);
        SetActiveScheme(scheme);
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        CpuTuningField field,
        WindowsPowerSettingsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (field)
        {
            case CpuTuningField.WindowsPowerSchemeId:
                SetActiveScheme(snapshot.ActiveSchemeId);
                break;
            case CpuTuningField.MaxFrequencyMhz:
                WriteFrequency(snapshot.ActiveSchemeId,
                    checked((uint)snapshot.AcMaxFrequencyMhz),
                    checked((uint)snapshot.DcMaxFrequencyMhz));
                SetActiveScheme(snapshot.ActiveSchemeId);
                break;
            case CpuTuningField.BoostEnabled:
                WriteBoost(snapshot.ActiveSchemeId, snapshot.AcBoostMode, snapshot.DcBoostMode);
                SetActiveScheme(snapshot.ActiveSchemeId);
                break;
            case CpuTuningField.CoreParkingPercent:
                if (snapshot.AcMinActiveCoresPercent is not int acParking ||
                    snapshot.DcMinActiveCoresPercent is not int dcParking)
                    throw new InvalidOperationException("coreParkingRestoreUnavailable");
                WriteCoreParking(snapshot.ActiveSchemeId,
                    acParking, dcParking);
                SetActiveScheme(snapshot.ActiveSchemeId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        return Task.CompletedTask;
    }

    private static WindowsPowerSettingsSnapshot ReadScheme(Guid scheme)
    {
        var acFrequency = ReadIndex(scheme, ProcessorMaximumFrequency, ac: true);
        var dcFrequency = ReadIndex(scheme, ProcessorMaximumFrequency, ac: false);
        var acBoost = ReadIndex(scheme, ProcessorBoostMode, ac: true);
        var dcBoost = ReadIndex(scheme, ProcessorBoostMode, ac: false);
        var acParking = TryReadIndex(scheme, ProcessorCoreParkingMinCores, ac: true);
        var dcParking = TryReadIndex(scheme, ProcessorCoreParkingMinCores, ac: false);
        return new WindowsPowerSettingsSnapshot(
            scheme,
            checked((int)acFrequency),
            checked((int)dcFrequency),
            checked((int)acBoost),
            checked((int)dcBoost))
        {
            ActiveSchemeName = ReadFriendlyName(scheme),
            AcMinActiveCoresPercent = acParking is uint acValue and <= 100 ? (int)acValue : null,
            DcMinActiveCoresPercent = dcParking is uint dcValue and <= 100 ? (int)dcValue : null
        };
    }

    private static uint ReadIndex(Guid scheme, Guid setting, bool ac)
    {
        var subgroup = ProcessorPowerManagementSubgroup;
        var settingCopy = setting;
        var result = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingCopy, out var value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingCopy, out value);
        ThrowIfFailed(result, ac ? "PowerReadACValueIndex" : "PowerReadDCValueIndex");
        return value;
    }

    private static uint? TryReadIndex(Guid scheme, Guid setting, bool ac)
    {
        try { return ReadIndex(scheme, setting, ac); }
        catch (Win32Exception) { return null; }
    }

    private static void WriteFrequency(Guid scheme, uint ac, uint dc)
    {
        WriteIndex(scheme, ProcessorMaximumFrequency, ac, acMode: true);
        WriteIndex(scheme, ProcessorMaximumFrequency, dc, acMode: false);
    }

    private static void WriteBoost(Guid scheme, int ac, int dc)
    {
        WriteIndex(scheme, ProcessorBoostMode, checked((uint)ac), acMode: true);
        WriteIndex(scheme, ProcessorBoostMode, checked((uint)dc), acMode: false);
    }

    private static void WriteCoreParking(Guid scheme, int ac, int dc)
    {
        WriteIndex(scheme, ProcessorCoreParkingMinCores, checked((uint)ac), acMode: true);
        WriteIndex(scheme, ProcessorCoreParkingMinCores, checked((uint)dc), acMode: false);
    }

    private static void WriteIndex(Guid scheme, Guid setting, uint value, bool acMode)
    {
        var subgroup = ProcessorPowerManagementSubgroup;
        var settingCopy = setting;
        var result = acMode
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingCopy, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingCopy, value);
        ThrowIfFailed(result, acMode ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex");
    }

    private static Guid ReadActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        ThrowIfFailed(result, "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero) _ = LocalFree(pointer);
        }
    }

    private static void SetActiveScheme(Guid scheme)
    {
        var schemeCopy = scheme;
        ThrowIfFailed(PowerSetActiveScheme(IntPtr.Zero, ref schemeCopy), "PowerSetActiveScheme");
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        const uint ErrorMoreData = 234;
        uint size = 0;
        uint result = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (result != ErrorMoreData && result != 0)
            ThrowIfFailed(result, "PowerReadFriendlyName(size)");
        if (size == 0) return null;

        byte[] buffer = new byte[size];
        ThrowIfFailed(PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size), "PowerReadFriendlyName");
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0)
            throw new Win32Exception(checked((int)result), operation);
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subgroupGuid,
        IntPtr powerSettingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettings,
        ref Guid powerSetting,
        out uint acValueIndex);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettings,
        ref Guid powerSetting,
        out uint dcValueIndex);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettings,
        ref Guid powerSetting,
        uint acValueIndex);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettings,
        ref Guid powerSetting,
        uint dcValueIndex);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}

public sealed record WindowsPowerCommandResult(
    Guid OperationId,
    CommandState State,
    WindowsPowerState? VerifiedState,
    ServiceError? Error);

public sealed class WindowsPowerController(IWindowsPowerTransport? transport = null)
{
    public static readonly Guid BalancedSchemeId =
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static readonly Guid HighPerformanceSchemeId =
        Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public WindowsPowerCommandResult UnavailableResult =>
        Rejected(Guid.Empty, ErrorCode.CapabilityUnavailable);

    public async Task<WindowsPowerCommandResult> ApplyAsync(
        Guid schemeId,
        bool boostEnabled,
        bool isAcConnected,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        if (!isAcConnected && boostEnabled)
        {
            return Rejected(operationId, ErrorCode.ValidationFailed);
        }

        if (schemeId != BalancedSchemeId && schemeId != HighPerformanceSchemeId)
        {
            return Rejected(operationId, ErrorCode.ValidationFailed);
        }

        if (transport is null)
        {
            return Rejected(operationId, ErrorCode.CapabilityUnavailable);
        }

        try
        {
            var before = await transport.ReadAsync(cancellationToken);
            await transport.WriteAsync(schemeId, boostEnabled, cancellationToken);
            var readBack = await transport.ReadAsync(cancellationToken);
            if (readBack != new WindowsPowerState(schemeId, boostEnabled))
            {
                try
                {
                    await transport.WriteAsync(before.SchemeId, before.BoostEnabled, cancellationToken);
                }
                catch
                {
                    return new WindowsPowerCommandResult(
                        operationId, CommandState.RecoveryRequired, readBack,
                        ServiceError.Create(ErrorCode.RollbackFailed, operationId, false));
                }

                return new WindowsPowerCommandResult(
                    operationId, CommandState.RolledBack, before,
                    ServiceError.Create(ErrorCode.ReadBackMismatch, operationId, false));
            }

            return new WindowsPowerCommandResult(operationId, CommandState.Applied, readBack, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Rejected(operationId, ErrorCode.HardwareWriteFailed);
        }
    }

    private static WindowsPowerCommandResult Rejected(Guid operationId, ErrorCode code) =>
        new(operationId, CommandState.Rejected, null,
            ServiceError.Create(code, operationId, false));
}
