using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class DirectSmuCompilationTests
{
    private static readonly CapabilitySnapshot Capabilities = new([])
    { IsOnAcPower = true, Fields = new[] { "pptWatts", "mp1TemperatureC", "tdcCurrentMilliamps", "edcCurrentMilliamps" }.Select(key =>
        new FieldCapability(key, CapabilityState.Available, true, true, null, null, null, "", "fake",
            "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)).ToArray() };
    private static CompiledControlPage Compile(Dictionary<string, SettingRequest> settings) =>
        ControlPlanCompiler.CompileTransient([new(new(ControlPageId.Performance, true,
            PresetKey.Create(ControlModeId.Gaming, 1), 1), settings, [])], 0, Capabilities, 0).Pages.Single();

    [TestMethod]
    public void Aggregate_limits_split_into_scalar_owners_and_never_drop_unsupported_residuals()
    {
        var page = Compile(new() { ["advancedCpuTuning"] = SettingRequest.Set(new AdvancedCpuTuningPlan(
            PptWatts: 60.125, Mp1TemperatureC: 80, TdcCurrentMilliamps: 107999, EdcCurrentMilliamps: 159999, VrmCurrentMilliamps: 160000)) });
        Assert.AreEqual(60.125, page.Settings["pptWatts"].Value!.Value.GetDouble());
        Assert.AreEqual(80, page.Settings["mp1TemperatureC"].Value!.Value.GetInt32());
        Assert.AreEqual(107999, page.Settings["tdcCurrentMilliamps"].Value!.Value.GetInt32());
        Assert.AreEqual(159999, page.Settings["edcCurrentMilliamps"].Value!.Value.GetInt32());
        CollectionAssert.AreEquivalent(new[] { "advancedCpuTuning" }, page.Skipped);
        var command = (SetCpuTuningBatchCommand)ControlPlanCompiler.Commands(ControlPageId.Performance, page.Settings, new(null, null, [])).Single();
        Assert.AreEqual(4, command.Plans.Length);
        CollectionAssert.AreEquivalent(new[] { new AdvancedCpuTuningPlan(PptWatts: 60.125), new AdvancedCpuTuningPlan(Mp1TemperatureC: 80), new AdvancedCpuTuningPlan(TdcCurrentMilliamps: 107999), new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999) },
            command.Plans.Select(plan => plan.Advanced).ToArray());
        Assert.IsNull(CommandValidation.Validate(command));
        Assert.ThrowsExactly<InvalidDataException>(() => Compile(new()
        { ["pptWatts"] = SettingRequest.Set(65), ["advancedCpuTuning"] = SettingRequest.Set(new AdvancedCpuTuningPlan(PptWatts: 60.125)) }));
        Assert.ThrowsExactly<InvalidDataException>(() => Compile(new()
        { ["edcCurrentMilliamps"] = SettingRequest.Set(159998), ["advancedCpuTuning"] = SettingRequest.Set(new AdvancedCpuTuningPlan(EdcCurrentMilliamps: 159999)) }));
        var plan = new CpuTuningPlan(null, null, null, null, null, null, null, null);
        Assert.IsNull(CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(),
            [plan with { Advanced = new(PptWatts: 60) }, plan with { Advanced = new(PptWatts: 65) }], true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(),
            [plan with { SpptWatts = 60 }, plan with { Advanced = new(PptWatts: 65) }], true)));
        Assert.IsFalse(PresetValidation.ValidatePayload(ControlPageId.Performance,
            System.Text.Json.JsonSerializer.SerializeToElement(new { pptWatts = 60.1254 })).IsValid);
    }

    [TestMethod]
    public void Official_and_readback_preserve_distinct_direct_fields_and_keep_does_not_generate_writes()
    {
        var official = Compile(new() { ["advancedCpuTuning"] = SettingRequest.Official });
        Assert.IsTrue(official.Settings.ContainsKey("pptWatts"));
        Assert.IsTrue(official.Settings.ContainsKey("mp1TemperatureC"));
        Assert.IsTrue(official.Settings.ContainsKey("tdcCurrentMilliamps"));
        Assert.IsTrue(official.Settings.ContainsKey("edcCurrentMilliamps"));
        Assert.AreEqual(0, Compile(new() { ["advancedCpuTuning"] = SettingRequest.Keep }).Settings.Count);
        var values = ControlPlanCompiler.Read(ControlPageId.Performance, new(null, null, [])
        { CpuTuning = new(99, null, null, null, null, null, null, null, null) { AdvancedLimits = new(PptWatts: 120.125, Mp1TemperatureC: 99, TdcCurrentMilliamps: 108000, EdcCurrentMilliamps: 160000) } });
        Assert.AreEqual(120.125, values["pptWatts"].Value!.Value.GetDouble());
        Assert.AreEqual(99, values["mp1TemperatureC"].Value!.Value.GetInt32());
        Assert.AreEqual(108000, values["tdcCurrentMilliamps"].Value!.Value.GetInt32());
        Assert.AreEqual(160000, values["edcCurrentMilliamps"].Value!.Value.GetInt32());
        Assert.IsFalse(values.ContainsKey("advancedCpuTuning"));
        Assert.IsFalse(values.ContainsKey("temperatureLimitC"), "The same MP1 temperature cannot have two independent owners");
        Assert.IsTrue(PresetValidation.ValidatePayload(ControlPageId.Performance,
            System.Text.Json.JsonSerializer.SerializeToElement(new { tdcCurrentMilliamps = 107999 })).IsValid);
        Assert.IsFalse(PresetValidation.ValidatePayload(ControlPageId.Performance,
            System.Text.Json.JsonSerializer.SerializeToElement(new { tdcCurrentMilliamps = 107999.5 })).IsValid);
        foreach (int value in new[] { 1000, 159999, 200000 })
            Assert.IsTrue(PresetValidation.ValidatePayload(ControlPageId.Performance,
                System.Text.Json.JsonSerializer.SerializeToElement(new { edcCurrentMilliamps = value })).IsValid);
        foreach (double value in new[] { 999d, 159999.5, 200001d })
            Assert.IsFalse(PresetValidation.ValidatePayload(ControlPageId.Performance,
                System.Text.Json.JsonSerializer.SerializeToElement(new { edcCurrentMilliamps = value })).IsValid);
    }
}
