using Jiaolong.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CandidateHostStopTests
{
    [TestMethod]
    public void Explicit_stop_marker_is_optional_and_does_not_create_or_change_records()
    {
        using var fixture = new StopFixture();
        string[] args = ["--control-journal-root", fixture.JournalRoot, "--candidate-stop-file", fixture.Marker];
        Assert.AreEqual(fixture.JournalRoot, CandidateJournalRoot.Parse(args, out string? marker));
        Assert.AreEqual(fixture.Marker, marker);
        Assert.IsFalse(File.Exists(marker));
        Assert.AreEqual("original journal", File.ReadAllText(fixture.Journal));
        Assert.AreEqual(fixture.JournalRoot, CandidateJournalRoot.Parse(["--control-journal-root", fixture.JournalRoot], out marker));
        Assert.IsNull(marker);
        Assert.IsNull(CandidateJournalRoot.Parse([], out marker));
        Assert.IsNull(marker);
    }

    [TestMethod]
    public void Stop_marker_rejects_ambiguous_existing_or_recovery_paths_without_modifying_them()
    {
        using var fixture = new StopFixture();
        string[] invalidPaths = [".", @"D:\stop", @"C:relative", fixture.Marker + ":stream",
            Path.Combine(fixture.Root, "missing", "stop"), fixture.Journal, fixture.JournalRoot,
            Path.Combine(fixture.JournalRoot, "client-control-session.json"), fixture.Marker + " "];
        foreach (string path in invalidPaths)
            Assert.Throws<Exception>(() => CandidateJournalRoot.Parse(
                ["--control-journal-root", fixture.JournalRoot, "--candidate-stop-file", path], out _));
        Assert.ThrowsExactly<ArgumentException>(() => CandidateJournalRoot.Parse(["--candidate-stop-file", fixture.Marker], out _));
        Assert.ThrowsExactly<ArgumentException>(() => CandidateJournalRoot.Parse(
            ["--control-journal-root", fixture.JournalRoot, "--candidate-stop-file=" + fixture.Marker], out _));
        File.WriteAllText(fixture.Marker, "existing marker");
        Assert.ThrowsExactly<IOException>(() => CandidateJournalRoot.Parse(
            ["--control-journal-root", fixture.JournalRoot, "--candidate-stop-file", fixture.Marker], out _));
        Assert.AreEqual("existing marker", File.ReadAllText(fixture.Marker));
        Assert.AreEqual("original journal", File.ReadAllText(fixture.Journal));
        Assert.AreEqual(1, Directory.GetFiles(fixture.JournalRoot).Length);
    }

    [TestMethod]
    public async Task Stop_marker_runs_the_normal_hosted_service_shutdown()
    {
        using var fixture = new StopFixture();
        var probe = new ShutdownProbe();
        using IHost host = CreateHost(fixture.Marker, probe);
        Task run = host.RunAsync();
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(run.IsCompleted);
            File.WriteAllText(fixture.Marker, "stop");
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, probe.Stops);
            Assert.AreEqual("original journal", File.ReadAllText(fixture.Journal));
            Assert.AreEqual("stop", File.ReadAllText(fixture.Marker));
        }
        finally { await StopIfRunningAsync(host, run); }
    }

    [TestMethod]
    public async Task Ordinary_host_shutdown_cancels_the_marker_watcher_without_creating_a_marker()
    {
        using var fixture = new StopFixture();
        var probe = new ShutdownProbe();
        using IHost host = CreateHost(fixture.Marker, probe);
        Task run = host.RunAsync();
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, probe.Stops);
            Assert.IsFalse(File.Exists(fixture.Marker));
        }
        finally { await StopIfRunningAsync(host, run); }
    }

    private static IHost CreateHost(string marker, ShutdownProbe probe)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<IHostedService>(probe);
        builder.Services.AddHostedService(services => new CandidateStopWorker(marker,
            services.GetRequiredService<IHostApplicationLifetime>()));
        return builder.Build();
    }

    private static async Task StopIfRunningAsync(IHost host, Task run)
    {
        if (run.IsCompleted) return;
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ShutdownProbe : IHostedService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken token) { Started.SetResult(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
    }

    private sealed class StopFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(@"C:\Users\Administrator\AppData\Local\Temp", "XisuraCandidateStop-" + Guid.NewGuid().ToString("N"));
        public string JournalRoot => Path.Combine(Root, "journal");
        public string Journal => Path.Combine(JournalRoot, "control-execution.json");
        public string Marker => Path.Combine(Root, "control", "stop");
        public StopFixture()
        {
            Directory.CreateDirectory(JournalRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            File.WriteAllText(Journal, "original journal");
        }
        public void Dispose()
        {
            if (File.Exists(Marker)) File.Delete(Marker);
            File.Delete(Journal);
            Directory.Delete(JournalRoot);
            Directory.Delete(Path.GetDirectoryName(Marker)!);
            Directory.Delete(Root);
        }
    }
}
