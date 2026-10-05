namespace Jiaolong_ControlCenter.ViewModels;

public sealed record FanPoint(int TemperatureC, int TargetPercent);

public sealed record FanCurve(IReadOnlyList<FanPoint> Points);

public static class FanCurveValidator
{
    public static ValidationSummary Validate(FanCurve curve)
    {
        if (curve.Points.Count != 6) return new ValidationSummary(false, ["温度曲线必须有六个点"]);
        for (var index = 0; index < curve.Points.Count; index++)
        {
            var point = curve.Points[index];
            if (point.TemperatureC is < 0 or > 120 || point.TargetPercent is < 0 or > 100)
            {
                return new ValidationSummary(false, ["曲线点超出清单范围"]);
            }

            if (index == 0) continue;
            var previous = curve.Points[index - 1];
            if (point.TemperatureC <= previous.TemperatureC)
            {
                return new ValidationSummary(false, ["温度点必须严格递增"]);
            }

            if (point.TargetPercent < previous.TargetPercent)
            {
                return new ValidationSummary(false, ["风扇目标转速不能下降"]);
            }
        }

        return ValidationSummary.Valid;
    }
}
