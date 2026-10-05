using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class TuningCommandTests
{
    [TestMethod]
    public void Unsupported_tuning_stays_unavailable_and_positive_curve_is_invalid()
    {
        var result = CurveOptimizerController.Validate(5, -30, 0);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(CommandState.Rejected, new WindowsPowerController().UnavailableResult.State);
    }
}
