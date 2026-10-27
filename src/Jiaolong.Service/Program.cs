using System.Text.Json;
using Jiaolong.Service;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Diagnostics;
using Jiaolong.Service.Hosting;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;
using Jiaolong.Service.Installation;

if (args.Length == 1 && string.Equals(args[0], "--remove-startup-tasks", StringComparison.Ordinal))
{
    Environment.ExitCode = StartupTaskCleanup.Run();
}
else if (args.Length == 1 && string.Equals(args[0], "--print-home-telemetry", StringComparison.Ordinal))
{
    using var provider = new WindowsHomeHardwareProvider();
    var snapshot = provider.ReadTelemetryAsync(CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine(JsonSerializer.Serialize(snapshot, ProtocolJsonContext.Default.HardwareSnapshot));
}
else if (args.Length == 1 && string.Equals(args[0], "--print-home-state", StringComparison.Ordinal))
{
    using var provider = new WindowsHomeHardwareProvider();
    var state = provider.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
    var snapshot = new Jiaolong.Contracts.Models.HomeStateSnapshot(state.Capabilities, state.Telemetry, state.Controls);
    Console.WriteLine(JsonSerializer.Serialize(snapshot, ProtocolJsonContext.Default.HomeStateSnapshot));
}
else if (args.Length == 1 && string.Equals(args[0], "--remove-data", StringComparison.Ordinal))
{
    Environment.ExitCode = ProgramDataCleanup.Run();
}
else if (args.Length == 1 && string.Equals(args[0], "--configure-service-recovery", StringComparison.Ordinal))
{
    Environment.ExitCode = ServiceRecoveryConfigurator.Run();
}
else if (args.Length > 0 && args[0] == "--acknowledge-idle-fan-restart")
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("An enabled administrator token is required for explicit recovery acknowledgement.");
    if (args.Length != 4 || !Guid.TryParse(args[1], out var sessionId) || sessionId == Guid.Empty || args[2] != "--control-journal-root")
        throw new ArgumentException("Expected --acknowledge-idle-fan-restart <original-session-id> --control-journal-root <original-directory>.");
    if (System.Diagnostics.Process.GetProcessesByName("Jiaolong.ControlCenter").Length != 0 ||
        System.Diagnostics.Process.GetProcessesByName("Jiaolong.Service").Any(process => process.Id != Environment.ProcessId) ||
        File.Exists(@"\\.\pipe\Jiaolong.ControlCenter.v1"))
        throw new IOException("Stop every GUI and service before acknowledging the original recovery records.");
    var root = CandidateJournalRoot.Parse([args[2], args[3]], out _)!;
    var journal = new ControlExecutionJournal(root);
    using var provider = new WindowsHomeHardwareProvider(controlJournal: journal);
    var runtime = new HomeServiceRuntime(provider, journal: journal, requireClientSessions: true,
        readBootTime: new WindowsSystemBootProbe().ReadAsync,
        readPowerContinuity: new WindowsPowerContinuityProbe().ReadAsync);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await runtime.AcknowledgeIdleFanRestartAsync(sessionId, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, ProtocolJsonContext.Default.CommandResult));
    Environment.ExitCode = result is { State: Jiaolong.Contracts.Commands.CommandState.Applied, Error: null } ? 0 : 2;
}
else
{
    var journalRoot = CandidateJournalRoot.Parse(args, out string? stopFile);
    var builder = Host.CreateApplicationBuilder(journalRoot is null ? args : []);
    builder.Services.AddWindowsService(options => options.ServiceName = "JiaolongControlService");
    builder.Services.AddSingleton<ServiceLifetimeCoordinator>();
    builder.Services.AddSingleton<WindowsHomeHardwareProvider>();
    builder.Services.AddSingleton<AdaptiveAutomationStateStore>();
    builder.Services.AddSingleton(_ => new ControlExecutionJournal(journalRoot));
    builder.Services.AddSingleton<AdaptiveAutomationHardwareProvider>(services => new AdaptiveAutomationHardwareProvider(
        services.GetRequiredService<WindowsHomeHardwareProvider>(),
        services.GetRequiredService<AdaptiveAutomationStateStore>()));
    builder.Services.AddSingleton<IHomeHardwareProvider>(services => services.GetRequiredService<AdaptiveAutomationHardwareProvider>());
    builder.Services.AddSingleton(_ => new TelemetryRingBuffer(120));
    builder.Services.AddSingleton(_ => new LocalJsonLogSink(DiagnosticPaths.ServiceLogsDirectory, maxBytes: 4L * 1024 * 1024));
    builder.Services.AddSingleton<IDiagnosticEventWriter>(services => services.GetRequiredService<LocalJsonLogSink>());
    builder.Services.AddSingleton<IDiagnosticEventReader>(services => services.GetRequiredService<LocalJsonLogSink>());
    builder.Services.AddSingleton<IDiagnosticBundleBuilder, DiagnosticBundleBuilder>();
    builder.Services.AddSingleton<HomeServiceRuntime>(services => new(
        services.GetRequiredService<IHomeHardwareProvider>(), services.GetRequiredService<IDiagnosticEventWriter>(),
        services.GetRequiredService<ControlExecutionJournal>(), requireClientSessions: true,
        readBootTime: new WindowsSystemBootProbe().ReadAsync,
        readPowerContinuity: new WindowsPowerContinuityProbe().ReadAsync));
    builder.Services.AddSingleton<IClientProcessProbe, WindowsClientProcessProbe>();
    builder.Services.AddSingleton<RequestDispatcher>(services => new RequestDispatcher(
        homeRuntime: services.GetRequiredService<HomeServiceRuntime>(),
        diagnosticBundleBuilder: services.GetRequiredService<IDiagnosticBundleBuilder>(),
        telemetryRingBuffer: services.GetRequiredService<TelemetryRingBuffer>(),
        diagnosticEventReader: services.GetRequiredService<IDiagnosticEventReader>()));
    builder.Services.AddSingleton<NamedPipeServiceHost>(services => new NamedPipeServiceHost(
        dispatcher: services.GetRequiredService<RequestDispatcher>(),
        homeRuntime: services.GetRequiredService<HomeServiceRuntime>(),
        logger: services.GetRequiredService<ILogger<NamedPipeServiceHost>>(),
        telemetryRingBuffer: services.GetRequiredService<TelemetryRingBuffer>()));
    builder.Services.AddHostedService<ServiceWorker>();
    builder.Services.AddHostedService<AdaptiveAutomationWorker>();
    builder.Services.AddHostedService<NativeModeLightingFollowWorker>();
    builder.Services.AddHostedService<ClientSessionRecoveryWorker>();
    if (stopFile is not null)
        builder.Services.AddHostedService(services => new CandidateStopWorker(stopFile,
            services.GetRequiredService<IHostApplicationLifetime>()));

    var host = builder.Build();
    host.Run();
}
