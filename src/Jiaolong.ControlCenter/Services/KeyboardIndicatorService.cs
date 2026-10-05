using Jiaolong.Contracts.Models;
using System.Runtime.InteropServices;

namespace Jiaolong_ControlCenter.Services;

public sealed class KeyboardIndicatorService
{
    public bool IsCapsLockOn => IsToggleOn(0x14);
    public bool IsNumLockOn => IsToggleOn(0x90);

    public bool? Read(QuickSettingKind setting) => setting switch
    {
        QuickSettingKind.CapsLock => IsCapsLockOn,
        QuickSettingKind.NumLock => IsNumLockOn,
        _ => null
    };

    public static int? VirtualKeyFor(QuickSettingKind setting) => setting switch
    {
        QuickSettingKind.CapsLock => 0x14,
        QuickSettingKind.NumLock => 0x90,
        _ => null
    };

    public bool TryToggle(QuickSettingKind setting) =>
        VirtualKeyFor(setting) is int virtualKey && TryToggle(virtualKey);

    public bool TryToggle(int virtualKey)
    {
        if (virtualKey is not (0x14 or 0x90)) return false;
        var inputs = new[]
        {
            new INPUT
            {
                Type = 1,
                Keyboard = new KEYBDINPUT { Vk = (ushort)virtualKey, Flags = 0 }
            },
            new INPUT
            {
                Type = 1,
                Keyboard = new KEYBDINPUT { Vk = (ushort)virtualKey, Flags = KeyEventKeyUp }
            }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static bool IsToggleOn(int virtualKey) => (GetKeyState(virtualKey) & 1) == 1;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
