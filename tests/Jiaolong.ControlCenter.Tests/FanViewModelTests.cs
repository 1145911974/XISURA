using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class FanViewModelTests
{
    [TestMethod]
    public void Curve_requires_six_strictly_increasing_temperatures_and_non_decreasing_targets()
    {
        var valid = FanFixture.ValidCurve;
        var crossed = valid with
        {
            Points = new[]
            {
                new FanPoint(40, 20), new(60, 30), new(55, 40),
                new(70, 55), new(80, 75), new(90, 100)
            }
        };

        Assert.IsTrue(FanCurveValidator.Validate(valid).IsValid);
        Assert.IsFalse(FanCurveValidator.Validate(crossed).IsValid);
    }

    [TestMethod]
    public async Task Preview_never_sends_hardware_command()
    {
        var client = new RecordingControlCenterClient();
        await FanFixture.Create(client).PreviewAsync(FanFixture.ValidDraft, CancellationToken.None);

        Assert.AreEqual(0, client.SentCommands.Count);
    }

    [TestMethod]
    public async Task Release_remains_available_when_other_fan_controls_are_disabled()
    {
        var vm = await FanFixture.CreateReadOnlyWithActiveOverrideAsync();

        Assert.IsFalse(vm.ApplyCommand.CanExecute(null));
        Assert.IsTrue(vm.ReleaseCommand.CanExecute(null));
    }
}

internal static class FanFixture
{
    public static FanCurve ValidCurve { get; } = new(
    [
        new FanPoint(40, 20), new(50, 30), new(60, 40),
        new(70, 55), new(80, 75), new(90, 100)
    ]);

    public static FanDraft ValidDraft { get; } = new(
        FanControlMode.TemperatureCurve,
        null,
        ValidCurve,
        ValidCurve,
        false);

    public static FanViewModel Create(RecordingControlCenterClient client) => new(client);

    public static Task<FanViewModel> CreateReadOnlyWithActiveOverrideAsync() =>
        Task.FromResult(new FanViewModel(readOnly: true, activeOverride: true));
}
