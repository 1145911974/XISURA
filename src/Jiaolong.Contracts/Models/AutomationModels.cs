namespace Jiaolong.Contracts.Models;

public sealed record AutomationRule(string Id, bool Enabled, string Trigger, string Action);

public sealed record AutomationProfile(string Name, bool Enabled, AutomationRule[] Rules);
