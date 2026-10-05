namespace Jiaolong_ControlCenter.Services;

public enum AdaptiveStrategyId
{
    QuietFirst,
    BalancedAdaptive,
    ResponseFirst
}

public sealed class AdaptiveStrategySelection
{
    public AdaptiveStrategySelection(AdaptiveStrategyId active = AdaptiveStrategyId.BalancedAdaptive)
    {
        Active = Enum.IsDefined(active) ? active : AdaptiveStrategyId.BalancedAdaptive;
        Editing = Active;
    }

    public AdaptiveStrategyId Editing { get; private set; }
    public AdaptiveStrategyId Active { get; private set; }

    public void Choose(AdaptiveStrategyId strategy)
    {
        if (!Enum.IsDefined(strategy)) throw new ArgumentOutOfRangeException(nameof(strategy));
        Editing = strategy;
    }

    public void UseEditing() => Active = Editing;
}
