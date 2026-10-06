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
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "JiaolongControlService");
    builder.Services.AddSingleton<ServiceLifetimeCoordinator>();
    builder.Services.AddSingleton<WindowsHomeHardwareProvider>();
    builder.Services.AddSingleton<AdaptiveAutomationStateStore>();
    builder.Services.AddSingleton<AdaptiveAutomationHardwareProvider>(services => new AdaptiveAutomationHardwareProvider(
        services.GetRequiredService<WindowsHomeHardwareProvider>(),
        services.GetRequiredService<AdaptiveAutomationStateStore>()));
    builder.Services.AddSingleton<IHomeHardwareProvider>(services => services.GetRequiredService<AdaptiveAutomationHardwareProvider>());
    builder.Services.AddSingleton(_ => new TelemetryRingBuffer(120));
    var logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JiaolongControlCenter", "Diagnostics", "Logs");
    builder.Services.AddSingleton(_ => new LocalJsonLogSink(logDirectory, maxBytes: 4L * 1024 * 1024));
    builder.Services.AddSingleton<IDiagnosticEventWriter>(services => services.GetRequiredService<LocalJsonLogSink>());
    builder.Services.AddSingleton<IDiagnosticEventReader>(services => services.GetRequiredService<LocalJsonLogSink>());
    builder.Services.AddSingleton<IDiagnosticBundleBuilder, DiagnosticBundleBuilder>();
    builder.Services.AddSingleton<HomeServiceRuntime>();
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

    var host = builder.Build();
    host.Run();
}
