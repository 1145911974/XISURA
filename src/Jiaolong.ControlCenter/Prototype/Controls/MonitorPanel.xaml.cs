using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class MonitorPanel : UserControl
{
    private static void SetReadout(TextBlock text, string value)
    {
        if (text.Text != value) text.Text = value;
    }

    private static readonly TimeSpan ThermalTransition = TimeSpan.FromMilliseconds(360);
    private const double RailMinimumTemperatureC = 30;
    private const double DefaultTemperatureWallC = 95;
    private static readonly Color MarkerWhiteColor = Color.FromArgb(242, 255, 255, 255);
    private static readonly Color MarkerRedColor = Color.FromArgb(255, 222, 50, 54);
    private readonly Stopwatch thermalClock = new();
    private long lastThermalFrameTimestamp;
    private readonly Stopwatch temperatureWallClock = new();
    private double? temperatureC;
    private double? displayedTemperatureC;
    private double? temperatureFromC;
    private double currentRailProgress;
    private double temperatureProgressFrom;
    private double targetTemperatureProgress;
    private double temperatureWallC = DefaultTemperatureWallC;
    private double currentWallProgress;
    private double wallTemperatureProgressFrom;
    private double targetWallProgress;
    private ThermalVisualState currentState;
    private ThermalVisualState fromState;
    private ThermalVisualState targetState;
    private bool renderingSubscribed;
    private bool reducedMotion;

    private double MaximumTemperatureC => DeviceKind == ThermalDeviceKind.Cpu ? 95 : 90;

    public string Title { get; set; } = string.Empty;
    public string IconSource { get; set; } = "ms-appx:///Assets/Icons/HomeMonitorCpuFilled.svg";
    public ThermalDeviceKind DeviceKind { get; set; }
    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            UsageCurve.ReducedMotion = value;
        }
    }
    public string Detail1Label { get; set; } = string.Empty;
    public string Detail1Value { get; set; } = string.Empty;
    public string Detail2Label { get; set; } = string.Empty;
    public string Detail2Value { get; set; } = string.Empty;

    public double? TemperatureC
    {
        get => temperatureC;
        set
        {
            if (temperatureC == value) return;
            temperatureC = value;
            targetTemperatureProgress = RailProgress(value);
            if (IsLoaded) TransitionTemperature(value);
        }
    }

    public MonitorPanel()
    {
        InitializeComponent();
        UsageCurve.FrameRendered += (_, _) => SetReadout(UsageValue, Format(UsageCurve.DisplayedValue, "0", "%"));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void ApplyTelemetry(double? temperature, double? usagePercent, string detail1, string detail2, DateTimeOffset? observedAtUtc = null)
    {
        ApplyTelemetry(temperature, usagePercent, observedAtUtc);
        ApplyDetailValues(detail1, detail2);
    }

    public void ApplyTelemetry(double? temperature, double? usagePercent, DateTimeOffset? observedAtUtc = null)
    {
        TemperatureC = temperature;
        if (!usagePercent.HasValue || !double.IsFinite(usagePercent.Value))
        {
            SetReadout(UsageValue, "-- %");
            UsageCurve.UpdateSample(null, observedAtUtc);
            return;
        }

        var rawUsage = Math.Clamp(usagePercent.Value, 0, 100);
        SetReadout(UsageValue, Format(rawUsage, "0", "%"));
        UsageCurve.UpdateSample(usagePercent, observedAtUtc);
    }

    public void ApplyDetailValues(string detail1, string detail2)
    {
        if (Detail1ValueText.Text != detail1) Detail1ValueText.Text = detail1;
        if (Detail2ValueText.Text != detail2) Detail2ValueText.Text = detail2;
    }

    public void ApplyTemperatureWall(double wallC)
    {
        if (!double.IsFinite(wallC)) return;
        temperatureWallC = Math.Clamp(wallC, RailMinimumTemperatureC, MaximumTemperatureC);
        targetWallProgress = RailProgress(temperatureWallC);
        if (!CanAnimate())
        {
            currentWallProgress = targetWallProgress;
            temperatureWallClock.Stop();
            UpdateTemperatureRail(currentRailProgress, currentState.IsAvailable);
            StopRenderingIfIdle();
            return;
        }

        wallTemperatureProgressFrom = currentWallProgress;
        temperatureWallClock.Restart();
        EnsureRendering();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        displayedTemperatureC = temperatureC;
        currentRailProgress = targetTemperatureProgress = RailProgress(temperatureC);
        currentWallProgress = targetWallProgress = RailProgress(temperatureWallC);
        SetState(ThermalVisualState.Resolve(DeviceKind, temperatureC));
    }

    private void TransitionTemperature(double? value)
    {
        targetState = ThermalVisualState.Resolve(DeviceKind, value);
        temperatureFromC = displayedTemperatureC;
        targetTemperatureProgress = RailProgress(value);
        if (ReducedMotion || !CanAnimate())
        {
            displayedTemperatureC = value;
            currentRailProgress = targetTemperatureProgress;
            StopThermalAnimation();
            SetState(targetState);
            return;
        }

        fromState = currentState;
        temperatureProgressFrom = currentRailProgress;
        thermalClock.Restart();
        EnsureRendering();
    }

    private void OnRendering(object? sender, object args)
    {
        if (!CanAnimate())
        {
            displayedTemperatureC = temperatureC;
            currentRailProgress = targetTemperatureProgress;
            currentWallProgress = targetWallProgress;
            thermalClock.Stop();
            temperatureWallClock.Stop();
            SetState(ThermalVisualState.Resolve(DeviceKind, temperatureC));
            UpdateTemperatureRail(currentRailProgress, currentState.IsAvailable);
            StopRenderingIfIdle();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        bool completing = (thermalClock.IsRunning && thermalClock.Elapsed >= ThermalTransition) ||
            (temperatureWallClock.IsRunning && temperatureWallClock.Elapsed >= ThermalTransition);
        if (!completing && now - lastThermalFrameTimestamp < Stopwatch.Frequency / 30) return;
        lastThermalFrameTimestamp = now;
        if (thermalClock.IsRunning)
        {
            var progress = Math.Clamp(thermalClock.Elapsed.TotalMilliseconds / ThermalTransition.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            displayedTemperatureC = InterpolateTemperature(temperatureFromC, temperatureC, eased);
            currentRailProgress = Lerp(temperatureProgressFrom, targetTemperatureProgress, eased);
            SetState(targetState with
            {
                Heat = Lerp(fromState.Heat, targetState.Heat, eased),
                PrimaryOpacity = Lerp(fromState.PrimaryOpacity, targetState.PrimaryOpacity, eased),
                SecondaryOpacity = Lerp(fromState.SecondaryOpacity, targetState.SecondaryOpacity, eased),
                TertiaryOpacity = Lerp(fromState.TertiaryOpacity, targetState.TertiaryOpacity, eased),
                Scale = Lerp(fromState.Scale, targetState.Scale, eased)
            });
            if (progress >= 1)
            {
                displayedTemperatureC = temperatureC;
                currentRailProgress = targetTemperatureProgress;
                thermalClock.Stop();
            }
        }

        if (temperatureWallClock.IsRunning)
        {
            var progress = Math.Clamp(temperatureWallClock.Elapsed.TotalMilliseconds / ThermalTransition.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            currentWallProgress = Lerp(wallTemperatureProgressFrom, targetWallProgress, eased);
            UpdateTemperatureRail(currentRailProgress, currentState.IsAvailable);
            if (progress >= 1)
            {
                currentWallProgress = targetWallProgress;
                temperatureWallClock.Stop();
                UpdateTemperatureRail(currentRailProgress, currentState.IsAvailable);
            }
        }

        StopRenderingIfIdle();
    }

    private void SetState(ThermalVisualState state)
    {
        currentState = state;
        SetReadout(TemperatureValue, FormatTemperature(displayedTemperatureC));
        SetReadout(TemperatureStatus, StatusText(state));
        SetReadout(TemperaturePeak, TemperatureWallText());
        UpdateTemperatureRail(currentRailProgress, state.IsAvailable);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => StopThermalAnimation();

    private void StopThermalAnimation()
    {
        thermalClock.Stop();
        temperatureWallClock.Stop();
        StopRenderingIfIdle();
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static double? InterpolateTemperature(double? from, double? to, double amount) =>
        from is double fromValue && to is double toValue && double.IsFinite(fromValue) && double.IsFinite(toValue)
            ? Lerp(fromValue, toValue, amount)
            : amount < 1 ? from : to;

    private static string FormatTemperature(double? value) =>
        value is double valid && double.IsFinite(valid) ? $"{valid:0} °C" : "-- °C";

    private void UpdateTemperatureRail(double progress, bool available)
    {
        const double railWidth = 184;
        const double markerSize = 16;
        const double wallMarkerWidth = 4;
        var markerLeft = Math.Clamp(progress * railWidth - markerSize / 2, 0, railWidth - markerSize);
        var wallLeft = Math.Clamp(currentWallProgress * railWidth - wallMarkerWidth / 2, 0, railWidth - wallMarkerWidth);
        Canvas.SetLeft(TemperatureMarker, markerLeft);
        Canvas.SetLeft(TemperatureWallMarker, wallLeft);
        TemperatureMarker.Opacity = available ? 1 : 0;
        TemperatureMarker.Fill = new SolidColorBrush(IsAtTemperatureWall(available) ? MarkerRedColor : MarkerWhiteColor);
        SetReadout(TemperaturePeak, TemperatureWallText());
    }

    private double RailProgress(double? value) =>
        value is double valid && double.IsFinite(valid)
            ? Math.Clamp((valid - RailMinimumTemperatureC) / (MaximumTemperatureC - RailMinimumTemperatureC), 0, 1)
            : 0;

    private string StatusText(ThermalVisualState state) => !state.IsAvailable
        ? "温度未知"
        : state.Heat >= 0.92
            ? "温度过高"
            : state.Heat >= 0.72
                ? "温度偏高"
                : "温度正常";

    private string TemperatureWallText()
    {
        var displayedWall = RailMinimumTemperatureC + currentWallProgress * (MaximumTemperatureC - RailMinimumTemperatureC);
        return $"最高 {displayedWall:0}°C";
    }

    private bool IsAtTemperatureWall(bool available) => available &&
        temperatureC is double currentTemperature &&
        double.IsFinite(currentTemperature) &&
        currentTemperature >= temperatureWallC &&
        currentRailProgress >= currentWallProgress;

    private void EnsureRendering()
    {
        if (renderingSubscribed || !CanAnimate()) return;
        lastThermalFrameTimestamp = 0;
        CompositionTarget.Rendering += OnRendering;
        renderingSubscribed = true;
    }

    private bool CanAnimate()
    {
        if (!IsLoaded || ReducedMotion || XamlRoot?.IsHostVisible != true) return false;
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void StopRenderingIfIdle()
    {
        if (!renderingSubscribed || thermalClock.IsRunning || temperatureWallClock.IsRunning) return;
        CompositionTarget.Rendering -= OnRendering;
        renderingSubscribed = false;
    }

    private static string Format(double? value, string format, string unit) =>
        value.HasValue && double.IsFinite(value.Value) ? $"{value.Value.ToString(format)} {unit}" : $"-- {unit}";

}
