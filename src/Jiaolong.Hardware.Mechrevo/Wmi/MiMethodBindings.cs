using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed record MiReadBinding(string Namespace, string Class, string InstanceName, int ReadType, byte MethodName = 0)
{
    public static MiReadBinding FromVerifiedProvider(VerifiedOemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName) ||
            provider.ReadType <= 0)
        {
            throw new ArgumentException("The manifest does not contain a valid read binding.", nameof(provider));
        }

        return new MiReadBinding(provider.Namespace, provider.Class, provider.InstanceName, provider.ReadType);
    }
}

public sealed record VerifiedWmiBinding(
    string Namespace,
    string Class,
    string InstanceName,
    int ReadType,
    int WriteType,
    byte MethodName,
    string ManifestId)
{
    public static bool TryCreate(
        CompatibilityDecision decision,
        ControlKey key,
        out VerifiedWmiBinding? binding)
    {
        binding = null;
        if (decision.Mode != CompatibilityMode.Writable || decision.Manifest is null || !key.IsKnown)
        {
            return false;
        }

        if (!decision.Manifest.Capabilities.Any(candidate =>
                string.Equals(candidate.Key, key.Value, StringComparison.Ordinal) &&
                candidate.State == Jiaolong.Contracts.Models.CapabilityState.Available))
        {
            return false;
        }

        var methodName = key.Value switch
        {
            ControlKeys.PerformanceMode => (byte)8,
            ControlKeys.MuxMode => (byte)9,
            ControlKeys.LidLogo => (byte)15,
            ControlKeys.KeyboardLighting => (byte)16,
            _ => (byte)0
        };
        var provider = decision.Manifest.WmiProvider;
        if (methodName == 0 || provider.ReadType != 250 || provider.WriteType != 251 ||
            string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName))
        {
            return false;
        }

        binding = new VerifiedWmiBinding(
            provider.Namespace,
            provider.Class,
            provider.InstanceName,
            provider.ReadType,
            provider.WriteType,
            methodName,
            decision.Manifest.ManifestId);
        return true;
    }
}

public static class MiCpuPowerBindingFactory
{
    public static bool TryCreate(
        CompatibilityDecision decision,
        out VerifiedWmiBinding? binding)
    {
        binding = null;
        if (decision.Mode != CompatibilityMode.Writable || decision.Manifest is null ||
            !decision.Capabilities.Items.Any(candidate =>
                string.Equals(candidate.Key, "cpuTuning", StringComparison.Ordinal) &&
                candidate.State == CapabilityState.Available))
        {
            return false;
        }

        var provider = decision.Manifest.WmiProvider;
        if (provider.ReadType != 250 || provider.WriteType != 251 ||
            string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName))
        {
            return false;
        }

        binding = new VerifiedWmiBinding(
            provider.Namespace,
            provider.Class,
            provider.InstanceName,
            provider.ReadType,
            provider.WriteType,
            MiCpuPowerCodec.MethodName,
            decision.Manifest.ManifestId);
        return true;
    }
}

public enum MiHomeControlKind
{
    StrongCooling,
    PerformanceMode,
    Touchpad,
    FnLock,
    LidLogo,
    KeyboardMode,
    KeyboardColor,
    KeyboardBrightness
}

public static class MiHomeControlBindingFactory
{
    public static byte MethodName(MiHomeControlKind control) => control switch
    {
        MiHomeControlKind.StrongCooling => 20,
        MiHomeControlKind.PerformanceMode => 8,
        MiHomeControlKind.Touchpad => 12,
        MiHomeControlKind.FnLock => 11,
        MiHomeControlKind.LidLogo => 15,
        MiHomeControlKind.KeyboardMode => 16,
        MiHomeControlKind.KeyboardColor => 17,
        MiHomeControlKind.KeyboardBrightness => 18,
        _ => 0
    };

    public static int ResponseIndex(MiHomeControlKind control) => 4;

    public static int ResponseIndex(byte methodName) => 4;

    public static bool TryCreate(
        CompatibilityDecision decision,
        MiHomeControlKind control,
        out VerifiedWmiBinding? binding)
    {
        binding = null;
        if (decision.Mode != CompatibilityMode.Writable || decision.Manifest is null)
            return false;

        var (key, methodName) = control switch
        {
            MiHomeControlKind.StrongCooling => ("strongCooling", MethodName(control)),
            MiHomeControlKind.PerformanceMode => ("performanceMode", MethodName(control)),
            MiHomeControlKind.Touchpad => ("quickSetting:touchpad", MethodName(control)),
            MiHomeControlKind.FnLock => ("quickSetting:fnLock", MethodName(control)),
            MiHomeControlKind.LidLogo => ("quickSetting:lidLogo", MethodName(control)),
            MiHomeControlKind.KeyboardMode or MiHomeControlKind.KeyboardColor or MiHomeControlKind.KeyboardBrightness => ("keyboardLighting", MethodName(control)),
            _ => (string.Empty, (byte)0)
        };
        if (methodName == 0 || !decision.Capabilities.Items.Any(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal) &&
                candidate.State == CapabilityState.Available))
        {
            return false;
        }

        var provider = decision.Manifest.WmiProvider;
        if (provider.ReadType != 250 || provider.WriteType != 251 ||
            string.IsNullOrWhiteSpace(provider.Namespace) ||
            string.IsNullOrWhiteSpace(provider.Class) ||
            string.IsNullOrWhiteSpace(provider.InstanceName))
        {
            return false;
        }

        binding = new VerifiedWmiBinding(
            provider.Namespace,
            provider.Class,
            provider.InstanceName,
            provider.ReadType,
            provider.WriteType,
            methodName,
            decision.Manifest.ManifestId);
        return true;
    }
}
