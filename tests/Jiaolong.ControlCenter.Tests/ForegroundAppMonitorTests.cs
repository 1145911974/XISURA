using System.Text.Json;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ForegroundAppMonitorTests
{
    [TestMethod]
    public async Task Foreground_observation_sends_basename_hash_and_session_but_not_full_path()
    {
        var observation = await new ForegroundAppMonitor().ObserveAsync(
            @"C:\Games\Example\game.exe", sessionId: 2, CancellationToken.None);

        Assert.AreEqual("game.exe", observation.ProcessName);
        Assert.AreEqual(64, observation.ExecutableHash.Length);
        Assert.AreEqual(2, observation.SessionId);
        Assert.IsFalse(JsonSerializer.Serialize(observation).Contains(@"C:\Games\", StringComparison.OrdinalIgnoreCase));
    }
}
