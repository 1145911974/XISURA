using System.Reflection;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuViewModelTests
{
    [TestMethod]
    public async Task Mux_command_is_not_sent_without_restart_impact_confirmation()
    {
        var client = new RecordingControlCenterClient();
        var result = await GpuFixture.Create(client).ApplyMuxAsync(MuxMode.Discrete, false, CancellationToken.None);

        Assert.AreEqual(ErrorCode.ValidationFailed, result.Error?.Code);
        Assert.AreEqual(0, client.SentCommands.Count);
    }

    [TestMethod]
    public void Frequency_limit_cannot_exceed_manifest_reported_official_maximum()
    {
        var result = GpuDraftValidator.ValidateFrequencyLimit(requestedMhz: 2_500, officialMaximumMhz: 2_400);

        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public void Page_contract_contains_no_tgp_vbios_or_memory_clock_command()
    {
        var names = typeof(GpuViewModel).GetMethods(BindingFlags.Instance | BindingFlags.Public).Select(x => x.Name).ToArray();

        Assert.IsFalse(names.Any(x => x.Contains("Tgp") || x.Contains("Vbios") || x.Contains("MemoryClock")));
    }
}

internal static class GpuFixture
{
    public static GpuViewModel Create(RecordingControlCenterClient client) => new(client, 300, 2_400);
}
