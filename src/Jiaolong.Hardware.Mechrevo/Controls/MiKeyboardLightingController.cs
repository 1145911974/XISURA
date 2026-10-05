using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed class MiKeyboardLightingController
{
    private readonly MiCommonInterfaceClient client;
    private readonly VerifiedWmiBinding mode;
    private readonly VerifiedWmiBinding color;
    private readonly VerifiedWmiBinding brightness;
    private readonly VerifiedWmiBinding? logo;

    public MiKeyboardLightingController(MiCommonInterfaceClient client, CompatibilityDecision decision)
    {
        this.client = client;
        mode = Require(MiHomeControlKind.KeyboardMode);
        color = Require(MiHomeControlKind.KeyboardColor);
        brightness = Require(MiHomeControlKind.KeyboardBrightness);
        MiHomeControlBindingFactory.TryCreate(decision, MiHomeControlKind.LidLogo, out logo);
        VerifiedWmiBinding Require(MiHomeControlKind kind) =>
            MiHomeControlBindingFactory.TryCreate(decision, kind, out var binding)
                ? binding! : throw new InvalidOperationException("keyboardLightingUnavailable");
    }

    public async Task<KeyboardLightingPlan> ApplyAsync(KeyboardLightingPlan plan, CancellationToken token)
    {
        if (!plan.IsValid()) throw new ArgumentException("Invalid keyboard lighting plan.", nameof(plan));
        if (plan.LogoEnabled.HasValue && logo is null) throw new InvalidOperationException("lidLogoUnavailable");
        var beforeMode = await Read(mode, 1, token);
        var beforeColor = await Read(color, 3, token);
        var beforeBrightness = await Read(brightness, 1, token);
        var beforeLogo = plan.LogoEnabled.HasValue ? await Read(logo!, 1, token) : null;
        if (beforeMode[0] is not (0 or 1 or 2) || beforeBrightness[0] > 3)
            throw new InvalidOperationException("keyboardLightingReadFailed");
        int level = plan.BrightnessLevel ?? (int)Math.Round(plan.Brightness * 3d / 100);
        var resolved = plan with { Red = plan.Red ?? beforeColor[0], Green = plan.Green ?? beforeColor[1], Blue = plan.Blue ?? beforeColor[2], BrightnessLevel = level };
        bool colorAttempted = false;
        try
        {
            // Cycle uses the firmware palette; writing RGB would interfere with that palette.
            // This firmware coerces mode 0 to 2; brightness 0 turns off either supported effect.
            await WriteChecked(mode, [(byte)(plan.Effect == "Cycle" ? 1 : 2)], token);
            if (plan.Effect != "Cycle")
            {
                colorAttempted = true;
                await WriteColorAsync(resolved.ColorAt(0), token);
            }
            await WriteChecked(brightness, [(byte)level], token);
            if (beforeLogo is not null) await WriteChecked(logo!, [(byte)(plan.LogoEnabled == true ? 1 : 0)], token);
            return resolved;
        }
        catch (Exception original)
        {
            // Cancellation of the request must not cancel restoration after a partial write.
            var errors = new List<Exception>();
            foreach (var item in new[] { (color, colorAttempted ? beforeColor : null), (logo, beforeLogo), (mode, beforeMode), (brightness, beforeBrightness) })
            {
                if (item.Item1 is null || item.Item2 is null) continue;
                try { await WriteChecked(item.Item1, item.Item2, CancellationToken.None); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0) throw new InvalidOperationException("rollbackFailed", new AggregateException(errors.Prepend(original)));
            throw;
        }
    }

    public async Task<KeyboardLightingPlan> ReadHardwareAsync(CancellationToken token)
    {
        var currentMode = (await Read(mode, 1, token))[0];
        var currentColor = await Read(color, 3, token);
        var currentBrightness = (await Read(brightness, 1, token))[0];
        var currentLogo = logo is null ? (byte?)null : (await Read(logo, 1, token))[0];
        if (currentMode is not (0 or 1 or 2) || currentBrightness > 3 || currentLogo is > 1)
            throw new InvalidOperationException("keyboardLightingReadFailed");
        int level = currentMode == 0 ? 0 : currentBrightness;
        return new KeyboardLightingPlan(currentMode == 1 ? "Cycle" : "Fixed", (int)Math.Round(level * 100d / 3d))
        {
            BrightnessLevel = level,
            Red = currentColor[0],
            Green = currentColor[1],
            Blue = currentColor[2],
            LogoEnabled = currentLogo.HasValue ? currentLogo == 1 : null
        };
    }

    public Task WriteColorAsync(KeyboardRgb value, CancellationToken token) =>
        WriteChecked(color, [value.Red, value.Green, value.Blue], token);

    private async Task<byte[]> Read(VerifiedWmiBinding binding, int length, CancellationToken token)
    {
        var result = await client.ReadAsync(binding, token);
        if (result.Quality != DataQuality.Good || result.Payload is null || result.Payload.Length < 4 + length)
            throw new InvalidOperationException("keyboardLightingReadFailed");
        return result.Payload.AsSpan(4, length).ToArray();
    }

    private async Task WriteChecked(VerifiedWmiBinding binding, byte[] bytes, CancellationToken token)
    {
        var result = await client.WriteOnceAsync(binding, bytes, token);
        if (result.Quality != DataQuality.Good) throw new InvalidOperationException("keyboardLightingWriteFailed");
        if (!(await Read(binding, bytes.Length, token)).AsSpan().SequenceEqual(bytes))
            throw new InvalidOperationException("keyboardLightingReadbackMismatch");
    }
}
