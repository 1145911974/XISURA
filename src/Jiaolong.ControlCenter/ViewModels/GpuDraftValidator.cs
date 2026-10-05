namespace Jiaolong_ControlCenter.ViewModels;

public static class GpuDraftValidator
{
    public static ValidationSummary ValidateFrequencyLimit(int? requestedMhz, int officialMaximumMhz, int officialMinimumMhz = 300)
    {
        if (requestedMhz is null) return ValidationSummary.Valid;
        return requestedMhz < officialMinimumMhz || requestedMhz > officialMaximumMhz
            ? new ValidationSummary(false, ["GPU 频率上限不在已验证清单范围内"])
            : ValidationSummary.Valid;
    }
}
