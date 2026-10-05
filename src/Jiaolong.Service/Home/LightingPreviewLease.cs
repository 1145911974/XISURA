using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed class LightingPreviewLease
{
    public KeyboardLightingPlan? Original { get; private set; }
    private DateTimeOffset deadline;

    public void Begin(KeyboardLightingPlan? original, DateTimeOffset now)
    {
        // The independently controlled lid logo is outside keyboard preview ownership.
        Original ??= (original ?? throw new InvalidOperationException("Lighting original is unavailable.")) with { LogoEnabled = null };
        deadline = now.AddSeconds(8);
    }

    public bool Expired(DateTimeOffset now) => Original is not null && now >= deadline;
    public void Complete() => Original = null;
}
