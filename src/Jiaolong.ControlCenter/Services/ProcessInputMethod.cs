using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

internal static class ProcessInputMethod
{
    private static readonly Guid ManagerClass = new("33C53A50-F456-4884-B049-85FD643ECFED");
    private static readonly Guid PinyinClass = new("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");
    private static readonly Guid PinyinProfile = new("FA550B04-5AD7-411F-A5AC-CA038EC515D7");
    private static readonly Guid DoubaoClass = new("9D2B2E2B-3C93-4D2F-9D35-6EEB85F0D2B0");
    private const uint ActiveProfile = 0x00000001;
    private const uint ForProcess = 0x10000000;
    private const uint DontCareCurrentInputLanguage = 0x00000004;
    private const ushort ChineseSimplified = 0x0804;
    private static readonly object RetryLock = new();
    private static readonly ProcessInputMethodAttemptGate RetryGate = new(TimeSpan.FromSeconds(15));

    public static void UseMicrosoftPinyin()
    {
        lock (RetryLock)
        {
            if (!RetryGate.ShouldAttempt(DateTimeOffset.UtcNow)) return;
            UseMicrosoftPinyinCore();
        }
    }

    private static void UseMicrosoftPinyinCore()
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ManagerClass, throwOnError: true)!);
            var manager = (IProfileManager)instance!;
            var readback = manager.GetProfile(1, ChineseSimplified, PinyinClass, PinyinProfile, 0, out var current);
            if (readback < 0)
            {
                AppRuntimeLog.Write($"[{DateTime.Now:O}] Process input compatibility: stage=targetProfile; result=0x{readback:X8}; targetAvailable=False\n");
                RetryGate.Failed(DateTimeOffset.UtcNow);
                return;
            }
            if ((current.Flags & ActiveProfile) != 0)
            {
                RetryGate.Succeeded();
                return;
            }

            // Doubao's OimeTsfNotifyWindow invalidates WinUI's native child tree.
            // Process scope only: never enable profiles or change session/default settings.
            manager.ReleaseInputProcessor(DoubaoClass, 0);
            var result = manager.ActivateProfile(1, ChineseSimplified, PinyinClass, PinyinProfile, 0,
                ForProcess | DontCareCurrentInputLanguage);
            readback = manager.GetProfile(1, ChineseSimplified, PinyinClass, PinyinProfile, 0, out current);
            var active = readback >= 0 && (current.Flags & ActiveProfile) != 0;
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Process input compatibility: activate=0x{result:X8}; readback=0x{readback:X8}; pinyinActive={active}\n");
            if (result >= 0 && active) RetryGate.Succeeded();
            else RetryGate.Failed(DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            RetryGate.Failed(DateTimeOffset.UtcNow);
            AppRuntimeLog.Write($"[{DateTime.Now:O}] Process input compatibility unavailable: {error.Message}\n");
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputProfile
    {
        public uint Type;
        public ushort Language;
        public Guid ClassId, ProfileId, CategoryId;
        public nint SubstituteLayout;
        public uint Capabilities;
        public nint Layout;
        public uint Flags;
    }

    // First five slots of ITfInputProcessorProfileMgr, in Windows SDK order.
    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProfileManager
    {
        [PreserveSig] int ActivateProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, out InputProfile profile);
        [PreserveSig] int EnumProfiles(ushort language, out nint enumerator);
        [PreserveSig] int ReleaseInputProcessor(in Guid classId, uint flags);
    }
}

internal sealed class ProcessInputMethodAttemptGate(TimeSpan backoff)
{
    private DateTimeOffset retryAfterUtc;
    private bool retryPending, retryConsumed;

    public bool ShouldAttempt(DateTimeOffset now)
    {
        if (retryConsumed && !retryPending) return false;
        if (!retryPending) return true;
        if (now < retryAfterUtc) return false;
        retryPending = false;
        retryConsumed = true;
        return true;
    }

    public void Failed(DateTimeOffset now)
    {
        if (retryConsumed) return;
        retryPending = true;
        retryAfterUtc = now + backoff;
    }

    public void Succeeded() => retryPending = retryConsumed = false;
}
