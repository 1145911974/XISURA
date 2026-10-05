using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public enum KeyboardBrightness
{
    Off,
    Low,
    Medium,
    High
}

public enum LightingEffect
{
    Fixed,
    Rainbow,
    Breathe
}

public sealed record RgbColor(byte Red, byte Green, byte Blue);

public sealed record LightingDraft(
    bool KeyboardEnabled,
    KeyboardBrightness Brightness,
    RgbColor Color,
    LightingEffect Effect,
    bool LidLogoEnabled);

public sealed class LightingViewModel
{
    private readonly IHardwareCommandSender? client;
    private readonly HashSet<LightingEffect> supportedEffects;

    public LightingViewModel(IHardwareCommandSender? client = null, IEnumerable<LightingEffect>? supportedEffects = null)
    {
        this.client = client;
        this.supportedEffects = supportedEffects is null
            ? Enum.GetValues<LightingEffect>().ToHashSet()
            : supportedEffects.ToHashSet();
        Draft = new LightingDraft(true, KeyboardBrightness.Medium, new RgbColor(255, 255, 255), LightingEffect.Fixed, false);
    }

    public LightingDraft Draft { get; private set; }
    public IReadOnlySet<LightingEffect> SupportedEffects => supportedEffects;

    public async Task<CommandResult> ApplyAsync(LightingDraft draft, RiskAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        if (!supportedEffects.Contains(draft.Effect)) return Reject(ErrorCode.CapabilityUnavailable);
        if (!acknowledgement.CpuPowerAndTemperatureConfirmed) return Reject(ErrorCode.ValidationFailed);
        if (client is null) return Reject(ErrorCode.ServiceUnavailable);

        var lighting = await client.SendAsync(
            new SetKeyboardLightingCommand(
                Guid.NewGuid(),
                new KeyboardLightingPlan(draft.Effect.ToString(), (int)draft.Brightness)),
            cancellationToken);
        if (lighting.Error is not null) return lighting;
        return await client.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), draft.LidLogoEnabled), cancellationToken);
    }

    private static CommandResult Reject(ErrorCode code)
    {
        var operationId = Guid.NewGuid();
        return new CommandResult(operationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, operationId, false), false);
    }
}
