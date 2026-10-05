using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.ViewModels;

public enum DataQuality
{
    Valid,
    Unknown,
    Stale
}

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected
}

public enum CompatibilityMode
{
    Normal,
    ReadOnly,
    SafeMode,
    Conflict
}

public sealed record MetricDisplay(
    string Label,
    string Value,
    string Unit,
    DataQuality Quality,
    string AccessibleText);

public sealed class HomeViewModel : ObservableObject
{
    private readonly ControlCenterClient? client;
    private CancellationTokenSource? activationCancellation;
    private Task? telemetryTask;
    private int telemetrySequence;
    private ConnectionState connectionState;
    private CompatibilityMode compatibilityMode;
    private string? capabilityReason;
    private MetricDisplay cpuTemperature;
    private MetricDisplay gpuTemperature;
    private double? cpuTemperatureValue;
    private double? gpuTemperatureValue;

    public HomeViewModel(
        DataQuality initialQuality = DataQuality.Unknown,
        ConnectionState connectionState = ConnectionState.Disconnected,
        CompatibilityMode compatibilityMode = CompatibilityMode.SafeMode,
        string? capabilityReason = null,
        ControlCenterClient? client = null)
    {
        this.connectionState = connectionState;
        this.compatibilityMode = compatibilityMode;
        this.capabilityReason = capabilityReason;
        this.client = client;
        cpuTemperature = UnknownMetric("CPU 温度", "°C", initialQuality);
        gpuTemperature = UnknownMetric("GPU 温度", "°C", initialQuality);
        SelectModeCommand = new AsyncRelayCommand<PerformanceMode>(SelectModeAsync, CanSelectMode);
    }

    public IAsyncRelayCommand<PerformanceMode> SelectModeCommand { get; }

    public ConnectionState ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                if (value == ConnectionState.Disconnected) ClearTemperatureValues();
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(WriteDisabledReason));
                OnPropertyChanged(nameof(IsWriteEnabled));
                SelectModeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public CompatibilityMode CompatibilityMode
    {
        get => compatibilityMode;
        private set
        {
            if (SetProperty(ref compatibilityMode, value))
            {
                OnPropertyChanged(nameof(WriteDisabledReason));
                OnPropertyChanged(nameof(IsWriteEnabled));
                SelectModeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string WriteDisabledReason => capabilityReason == "biosUnsupported"
        ? "当前 BIOS 未验证，仅提供只读监控"
        : CompatibilityMode switch
        {
            CompatibilityMode.Conflict => "检测到控制冲突，请关闭其他控制程序",
            CompatibilityMode.SafeMode => "只读安全模式：未验证的硬件能力已禁用",
            _ when ConnectionState == ConnectionState.Disconnected => "数据暂不可用，请确认蛟龙服务正在运行",
            _ when ConnectionState == ConnectionState.Connecting => "正在连接服务",
            _ => string.Empty
        };

    public bool IsWriteEnabled => CanSelectMode(PerformanceMode.Balanced);

    public MetricDisplay CpuTemperature
    {
        get => cpuTemperature;
        private set => SetProperty(ref cpuTemperature, value);
    }

    public MetricDisplay GpuTemperature
    {
        get => gpuTemperature;
        private set => SetProperty(ref gpuTemperature, value);
    }

    public double? CpuTemperatureValue
    {
        get => cpuTemperatureValue;
        private set => SetProperty(ref cpuTemperatureValue, value);
    }

    public double? GpuTemperatureValue
    {
        get => gpuTemperatureValue;
        private set => SetProperty(ref gpuTemperatureValue, value);
    }

    public TelemetryHistory CpuTemperatureHistory { get; } = new();

    public IReadOnlyList<double> CpuTrend => CpuTemperatureHistory.Points.Select(x => x.Value).ToArray();

    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Connected => "服务已连接",
        ConnectionState.Connecting => "正在连接服务",
        _ => "数据暂不可用"
    };

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        activationCancellation?.Cancel();
        activationCancellation?.Dispose();
        activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConnectionState = ConnectionState.Connecting;

        if (client is null)
        {
            ConnectionState = ConnectionState.Disconnected;
            OnPropertyChanged(nameof(ConnectionStateText));
            return;
        }

        try
        {
            await client.ConnectAsync(activationCancellation.Token);
            ConnectionState = ConnectionState.Connected;
            telemetryTask = ConsumeTelemetryAsync(activationCancellation.Token);
        }
        catch (OperationCanceledException) when (activationCancellation.IsCancellationRequested)
        {
            ConnectionState = ConnectionState.Disconnected;
        }
        catch
        {
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    public Task DeactivateAsync()
    {
        activationCancellation?.Cancel();
        return FinishDeactivationAsync();
    }

    public void ApplySnapshot(HardwareSnapshot snapshot, int sequence, DataQuality quality = DataQuality.Valid)
    {
        CpuTemperature = FormatMetric("CPU 温度", snapshot.CpuTemperatureC, "°C", quality);
        GpuTemperature = FormatMetric("GPU 温度", snapshot.GpuTemperatureC, "°C", quality);
        CpuTemperatureValue = ValidTemperature(snapshot.CpuTemperatureC, quality);
        GpuTemperatureValue = ValidTemperature(snapshot.GpuTemperatureC, quality);
        if (snapshot.CpuTemperatureC is double cpuTemperatureValue)
        {
            CpuTemperatureHistory.Add(new TelemetryPoint(sequence, snapshot.CapturedAtUtc, cpuTemperatureValue));
            OnPropertyChanged(nameof(CpuTrend));
        }
    }

    private bool CanSelectMode(PerformanceMode _) =>
        client is not null && ConnectionState == ConnectionState.Connected && CompatibilityMode == CompatibilityMode.Normal;

    private async Task SelectModeAsync(PerformanceMode mode)
    {
        if (!CanSelectMode(mode)) return;
        _ = await client!.SendAsync<SetPerformanceModeCommand, CommandResult>(
            new SetPerformanceModeCommand(Guid.NewGuid(), mode),
            CancellationToken.None);
    }

    private async Task ConsumeTelemetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in client!.SubscribeTelemetryAsync(cancellationToken))
            {
                ApplySnapshot(snapshot, Interlocked.Increment(ref telemetrySequence));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested) ConnectionState = ConnectionState.Disconnected;
        }
    }

    private async Task FinishDeactivationAsync()
    {
        var task = telemetryTask;
        activationCancellation?.Cancel();
        if (task is not null)
        {
            try { await task; } catch { }
        }

        telemetryTask = null;
        ConnectionState = ConnectionState.Disconnected;
        activationCancellation?.Dispose();
        activationCancellation = null;
    }

    private static MetricDisplay FormatMetric(string label, double? value, string unit, DataQuality quality) =>
        quality == DataQuality.Unknown || value is null
            ? UnknownMetric(label, unit, quality)
            : new MetricDisplay(label, value.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), unit, quality, $"{label} {value.Value:0.0} {unit}");

    private static double? ValidTemperature(double? value, DataQuality quality) =>
        quality != DataQuality.Unknown && value is double temperature && double.IsFinite(temperature) ? temperature : null;

    private void ClearTemperatureValues()
    {
        CpuTemperatureValue = null;
        GpuTemperatureValue = null;
    }

    private static MetricDisplay UnknownMetric(string label, string unit, DataQuality quality) =>
        new(label, "未知", unit, quality, $"{label} 未知");
}
