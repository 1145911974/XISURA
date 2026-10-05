using System.Runtime.InteropServices.WindowsRuntime;
using Jiaolong_ControlCenter.Branding;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class LogoLabWindow : Window
{
    private const int CanonicalSize = 1254;
    private bool synchronizing = true;
    private HeroLogoPixelDiff? lastDiff;

    public LogoLabWindow(bool adaptiveCorePreview = false)
    {
        CurrentProfile = HeroLogoProfile.Default;
        InitializeComponent();
        AppWindow.ResizeClient(new SizeInt32(1500, 900));

        LogoPreview.AdaptiveCorePreview = adaptiveCorePreview;
        AdaptiveCoreBadge.Visibility = adaptiveCorePreview ? Visibility.Visible : Visibility.Collapsed;
        AdaptiveMotionToggle.Visibility = adaptiveCorePreview ? Visibility.Visible : Visibility.Collapsed;

        var loaded = HeroLogoProfileStore.LoadOrDefault(HeroLogoProfileStore.EditableProfilePath());
        CurrentProfile = loaded.Profile;
        SyncControls();
        synchronizing = false;
        ApplyProfile();
        if (loaded.Error is not null) ShowStatus(InfoBarSeverity.Warning, "配置已回退", loaded.Error);
    }

    public HeroLogoProfile CurrentProfile { get; private set; }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!synchronizing) SetProfileValue(sender, e.NewValue);
    }

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!synchronizing && double.IsFinite(args.NewValue)) SetProfileValue(sender, args.NewValue);
    }

    private void SetProfileValue(object sender, double value)
    {
        synchronizing = true;
        if (sender is Slider slider) PairNumber(slider).Value = value;
        else if (sender is NumberBox number) PairSlider(number).Value = value;

        if (sender is FrameworkElement element)
        {
            CurrentProfile = element.Name switch
            {
                "LogoXSlider" or "LogoXNumber" => CurrentProfile with { Left = value },
                "LogoYSlider" or "LogoYNumber" => CurrentProfile with { Top = value },
                "LogoSizeSlider" or "LogoSizeNumber" => CurrentProfile with { Size = value },
                "CrystalSlider" or "CrystalNumber" => CurrentProfile with { CrystalOpacity = value },
                "ReflectionSlider" or "ReflectionNumber" => CurrentProfile with { ReflectionOpacity = value },
                "DurationSlider" or "DurationNumber" => CurrentProfile with { TransitionMilliseconds = (int)Math.Round(value) },
                _ => CurrentProfile,
            };
        }
        synchronizing = false;
        ApplyProfile();
    }

    private NumberBox PairNumber(Slider slider) => slider.Name switch
    {
        "LogoXSlider" => LogoXNumber,
        "LogoYSlider" => LogoYNumber,
        "LogoSizeSlider" => LogoSizeNumber,
        "CrystalSlider" => CrystalNumber,
        "ReflectionSlider" => ReflectionNumber,
        _ => DurationNumber,
    };

    private Slider PairSlider(NumberBox number) => number.Name switch
    {
        "LogoXNumber" => LogoXSlider,
        "LogoYNumber" => LogoYSlider,
        "LogoSizeNumber" => LogoSizeSlider,
        "CrystalNumber" => CrystalSlider,
        "ReflectionNumber" => ReflectionSlider,
        _ => DurationSlider,
    };

    private void SyncControls()
    {
        synchronizing = true;
        LogoXSlider.Value = LogoXNumber.Value = CurrentProfile.Left;
        LogoYSlider.Value = LogoYNumber.Value = CurrentProfile.Top;
        LogoSizeSlider.Value = LogoSizeNumber.Value = CurrentProfile.Size;
        CrystalSlider.Value = CrystalNumber.Value = CurrentProfile.CrystalOpacity;
        ReflectionSlider.Value = ReflectionNumber.Value = CurrentProfile.ReflectionOpacity;
        DurationSlider.Value = DurationNumber.Value = CurrentProfile.TransitionMilliseconds;
        synchronizing = false;
    }

    private void ApplyProfile()
    {
        LogoPreview.Profile = CurrentProfile;
        LogoPreview.TransitionDuration = TimeSpan.FromMilliseconds(CurrentProfile.TransitionMilliseconds);
        LogoPreview.Mode = (PrototypePerformanceMode)Math.Clamp(ModeSelector.SelectedIndex, 0, 3);
        Place(LogoPreview);
        Place(ReferenceImage);
        Place(DiffImage);
        ExportButton.IsEnabled = false;
        lastDiff = null;
    }

    private void Place(FrameworkElement element)
    {
        Canvas.SetLeft(element, CurrentProfile.Left);
        Canvas.SetTop(element, CurrentProfile.Top);
        element.Width = CurrentProfile.Size;
        element.Height = CurrentProfile.Size;
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!synchronizing) LogoPreview.Mode = (PrototypePerformanceMode)Math.Clamp(ModeSelector.SelectedIndex, 0, 3);
    }

    private void OnAdaptiveMotionToggled(object sender, RoutedEventArgs e)
    {
        if (LogoPreview is not null)
            LogoPreview.ReducedMotion = !AdaptiveMotionToggle.IsOn;
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogoPreview is null) return;
        LogoPreview.Visibility = ReferenceImage.Visibility = DiffImage.Visibility = Visibility.Visible;
        LogoPreview.Opacity = ViewSelector.SelectedIndex switch { 0 => 1, 2 => 0.5, _ => 0 };
        ReferenceImage.Opacity = ViewSelector.SelectedIndex switch { 1 => 1, 2 => 0.5, _ => 0 };
        DiffImage.Opacity = ViewSelector.SelectedIndex == 3 ? 1 : 0;
    }

    private async void OnValidateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var actual = await RenderModeAsync(PrototypePerformanceMode.Office);
            var expected = await ReadApprovedPixelsAsync();
            if (expected.Length != actual.Length)
                throw new InvalidDataException($"pixel buffer length mismatch: expected {expected.Length}, actual {actual.Length}");
            lastDiff = HeroLogoPixelComparer.Compare(expected, actual);
            await ShowHeatmapAsync(lastDiff.HeatmapBgra);
            // Source-layer exactness is tested separately; this mean absorbs DPI rasterizer edge antialiasing.
            var valid = CurrentProfile.Validate().Count == 0 && lastDiff.MeanChannelDelta <= 4;
            ExportButton.IsEnabled = valid;
            ShowStatus(
                valid ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                valid ? "像素验证通过" : "像素验证未通过",
                $"最大通道差 {lastDiff.MaxChannelDelta}，平均通道差 {lastDiff.MeanChannelDelta:F4}");
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, "验证失败", error.ToString());
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (lastDiff is null || lastDiff.MeanChannelDelta > 4) return;
        try
        {
            var path = HeroLogoProfileStore.EditableProfilePath();
            HeroLogoProfileStore.SaveAtomic(path, CurrentProfile);
            ShowStatus(InfoBarSeverity.Success, "已导出", path);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, "导出失败", error.Message);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        CurrentProfile = HeroLogoProfile.Default;
        SyncControls();
        ApplyProfile();
        ShowStatus(InfoBarSeverity.Informational, "已恢复参考值", "尚未写入文件");
    }

    private async void OnCaptureModesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var capturePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jiaolong", "LogoLabCaptures");
            Directory.CreateDirectory(capturePath);
            var folder = await StorageFolder.GetFolderFromPathAsync(capturePath);
            foreach (var mode in Enum.GetValues<PrototypePerformanceMode>())
            {
                var pixels = await RenderModeAsync(mode);
                await SavePngAsync(folder, $"HeroLogo{mode}.png", pixels);
            }
            ShowStatus(InfoBarSeverity.Success, "四模式截取完成", folder.Path);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, "截取失败", error.ToString());
        }
    }

    private async Task<byte[]> RenderModeAsync(PrototypePerformanceMode mode)
    {
        var previousMode = LogoPreview.Mode;
        var previousDuration = LogoPreview.TransitionDuration;
        try
        {
            LogoPreview.TransitionDuration = TimeSpan.Zero;
            LogoPreview.Mode = mode;
            return await RenderElementAsync(LogoPreview);
        }
        finally
        {
            LogoPreview.Mode = previousMode;
            LogoPreview.TransitionDuration = previousDuration;
        }
    }

    private static async Task<byte[]> RenderElementAsync(FrameworkElement element)
    {
        var previousWidth = element.Width;
        var previousHeight = element.Height;
        var previousVisibility = element.Visibility;
        var previousOpacity = element.Opacity;
        try
        {
            var rasterScale = element.XamlRoot?.RasterizationScale ?? 1;
            var logicalSize = Math.Max(1, (int)Math.Round(CanonicalSize / rasterScale));
            element.Visibility = Visibility.Visible;
            element.Opacity = 1;
            element.Width = logicalSize;
            element.Height = logicalSize;
            await Task.Yield();
            var render = new RenderTargetBitmap();
            await render.RenderAsync(element, logicalSize, logicalSize);
            return ReadBuffer(await render.GetPixelsAsync());
        }
        finally
        {
            element.Width = previousWidth;
            element.Height = previousHeight;
            element.Visibility = previousVisibility;
            element.Opacity = previousOpacity;
            element.InvalidateMeasure();
            element.UpdateLayout();
        }
    }

    private static async Task<byte[]> ReadApprovedPixelsAsync()
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.Combine(
            AppContext.BaseDirectory, "Assets", "Brand", "HeroLogoApproved.png"));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var length = (uint)(CanonicalSize * CanonicalSize * 4);
        var buffer = new Windows.Storage.Streams.Buffer(length) { Length = length };
        bitmap.CopyToBuffer(buffer);
        return ReadBuffer(buffer);
    }

    private async Task ShowHeatmapAsync(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(CanonicalSize, CanonicalSize);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            await stream.WriteAsync(pixels);
        }
        bitmap.Invalidate();
        DiffImage.Source = bitmap;
    }

    private static async Task SavePngAsync(StorageFolder folder, string name, byte[] pixels)
    {
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, CanonicalSize, CanonicalSize, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        var step = shift ? 10 : 1;
        CurrentProfile = e.Key switch
        {
            VirtualKey.Left => CurrentProfile with { Left = CurrentProfile.Left - step },
            VirtualKey.Right => CurrentProfile with { Left = CurrentProfile.Left + step },
            VirtualKey.Up => CurrentProfile with { Top = CurrentProfile.Top - step },
            VirtualKey.Down => CurrentProfile with { Top = CurrentProfile.Top + step },
            _ => CurrentProfile,
        };
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;
        CurrentProfile = CurrentProfile with
        {
            Left = Math.Clamp(CurrentProfile.Left, -609, 609),
            Top = Math.Clamp(CurrentProfile.Top, -835, 835),
        };
        SyncControls();
        ApplyProfile();
        e.Handled = true;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.IsOpen = true;
    }
}
