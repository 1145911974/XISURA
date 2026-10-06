using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class FanCurveWorkspaceV2 : UserControl
{
    private bool updatingInspector;
    private bool reducedMotion;
    private Storyboard? selectionTransition;
    private Button[] selectionTargets = [];
    public bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            reducedMotion = value;
            if (CurvePlot is not null) CurvePlot.ReducedMotion = value;
            if (value) StopSelectionTransition();
        }
    }

    public event EventHandler? DraftChanged;
    public void SetPresetCaption(string name) => EditingPresetText.Text = $"当前编辑：{name}";
    public FanCurveState Export() => CurvePlot.Export();
    public bool IsInteracting => CurvePlot.IsInteracting;
    public void Import(FanCurveState state)
    {
        CurvePlot.Import(state);
    }
    public void SelectProfile(int profile)
    {
        CurvePlot.SelectProfile(profile);
    }

    public FanCurveWorkspaceV2()
    {
        InitializeComponent();
        CurvePlot.ReducedMotion = reducedMotion;
        CurveHelp.Help = new Jiaolong_ControlCenter.Controls.ParameterHelpContent(
            "设定不同温度对应的风扇目标；高温点提高可加强散热并增加噪声，设低可能使温度上升。空白位置按下拖动可建节点，双扇共用同一曲线时仍保留独立草稿。",
            "当前仅编辑草稿，尚未下发硬件",
            "使用办公、游戏、狂飙各模式可恢复的推荐基线，再按噪声需求微调；高温段优先保留推荐转速。",
            "沿用推荐曲线，温度节点递增；软件允许 0–100% 目标，但 0% 不是通用安全散热设置。",
            "软件节点温度范围 30–100°C、目标 0–100%，至少两个节点；100% 表示目标最高档，不保证固定实际 RPM。",
            "高温段低于推荐值时保存前提醒；推荐曲线不是硬件安全保证。保存后使用预设才下发，实际转速以回读为准。");
        CurvePlot.SelectionChanged += OnSelectionChanged;
        CurvePlot.DraftChanged += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) => UpdateInspector();
    }

    public void ResetRecommended() => CurvePlot.ResetRecommended();
    private void OnAddClick(object sender, RoutedEventArgs e) => CurvePlot.AddPoint();
    private void OnDeleteClick(object sender, RoutedEventArgs e) => CurvePlot.DeletePoint();
    private void OnResetClick(object sender, RoutedEventArgs e) => ResetRecommended();
    private void OnLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (CurvePlot is null) return;
        bool shared = (sender as FrameworkElement)?.Tag?.ToString() == "1";
        if (CurvePlot.Draft.IsShared == shared) return;
        CurvePlot.SetShared(shared);
        AnimateSelection(IndependentButton, SharedButton);
    }

    private void OnCpuSeriesClick(object sender, RoutedEventArgs e)
    {
        CurvePlot.SetActiveSeries(FanCurveSeries.Cpu);
        UpdateSeriesButtons();
        AnimateSelection(CpuSeriesButton, GpuSeriesButton);
    }

    private void OnGpuSeriesClick(object sender, RoutedEventArgs e)
    {
        CurvePlot.SetActiveSeries(FanCurveSeries.Gpu);
        UpdateSeriesButtons();
        AnimateSelection(CpuSeriesButton, GpuSeriesButton);
    }

    private void OnSelectionChanged(object? sender, CurvePointSelectedEventArgs e) => UpdateInspector();

    private void OnPointValueChanged(object? sender, double value)
    {
        if (updatingInspector || TemperatureBox is null || TargetBox is null || CurvePlot is null
            || double.IsNaN(TemperatureBox.Value) || double.IsNaN(TargetBox.Value)) return;
        CurvePlot.UpdateSelectedPoint((int)Math.Round(TemperatureBox.Value), (int)Math.Round(TargetBox.Value));
    }

    private void UpdateInspector()
    {
        updatingInspector = true;
        CurvePoint point = CurvePlot.SelectedPoint;
        int total = CurvePlot.PointCount;
        string name = CurvePlot.Draft.IsShared ? "双扇" : CurvePlot.ActiveSeries == FanCurveSeries.Cpu ? "CPU" : "GPU";
        SelectedNodeText.Text = $"{name} · 节点 {CurvePlot.SelectedIndex + 1} / {total}";
        SelectedNodeText.Foreground = new SolidColorBrush(CurvePlot.ActiveSeries == FanCurveSeries.Cpu
            ? Color.FromArgb(255, 255, 113, 140)
            : Color.FromArgb(255, 102, 231, 255));
        TemperatureBox.Value = point.Temperature;
        TargetBox.Value = point.TargetPercent;
        int recommended = FanCurveSafety.RecommendedPercent(CurvePlot.Profile, point.Temperature,
            CurvePlot.Draft.IsShared || CurvePlot.ActiveSeries == FanCurveSeries.Gpu);
        RecommendationText.Text = point.Temperature >= 70
            ? $"{point.Temperature}°C 本档建议至少 {recommended}% · 高温低转速保存时提醒"
            : $"{point.Temperature}°C 本档参考 {recommended}% · 可自由调整转速";
        DeleteButton.IsEnabled = total > 2;
        GpuSeriesButton.IsEnabled = true;
        AddNodeButton.Content = CurvePlot.IsAddingPoint ? "取消加点" : "＋ 添加节点";
        EditHint.Text = CurvePlot.IsAddingPoint ? "在图中按下并拖动放置新节点" : "空白处按下加点 · 拖动定位";
        UpdateSeriesButtons();
        SetSelected(IndependentButton, !CurvePlot.Draft.IsShared);
        SetSelected(SharedButton, CurvePlot.Draft.IsShared);
        updatingInspector = false;
    }

    private static void SetSelected(Button button, bool selected)
    {
        button.Background = (Brush)Application.Current.Resources[selected ? "ModeSelectionBrush" : "PrototypeControlAcrylicBrush"];
        button.BorderBrush = (Brush)Application.Current.Resources[selected ? "ModeAccentBrush" : "PrototypeStrokeBrush"];
    }

    private void AnimateSelection(params Button[] buttons)
    {
        StopSelectionTransition();
        var motion = new Jiaolong_ControlCenter.Services.MotionSettingsService();
        motion.Refresh();
        if (ReducedMotion || !new UISettings().AnimationsEnabled || motion.IsReducedMotionEnabled) return;

        selectionTargets = buttons;
        selectionTransition = new Storyboard();
        foreach (var button in buttons)
        {
            button.Opacity = 0.76;
            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, button);
            Storyboard.SetTargetProperty(animation, "Opacity");
            selectionTransition.Children.Add(animation);
        }
        selectionTransition.Begin();
    }

    private void StopSelectionTransition()
    {
        selectionTransition?.Stop();
        foreach (var button in selectionTargets) button.Opacity = 1;
        selectionTargets = [];
    }

    private void UpdateSeriesButtons()
    {
        bool cpu = CurvePlot.Draft.IsShared || CurvePlot.ActiveSeries == FanCurveSeries.Cpu;
        bool gpu = CurvePlot.Draft.IsShared || CurvePlot.ActiveSeries == FanCurveSeries.Gpu;
        CpuSeriesButton.Background = cpu ? (Brush)Resources["CpuSeriesActiveBrush"] : (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
        GpuSeriesButton.Background = gpu ? (Brush)Resources["GpuSeriesActiveBrush"] : (Brush)Application.Current.Resources["PrototypeControlAcrylicBrush"];
        CpuSeriesButton.BorderBrush = cpu ? (Brush)Resources["CpuSeriesStroke"] : (Brush)Application.Current.Resources["PrototypeStrokeBrush"];
        GpuSeriesButton.BorderBrush = gpu ? (Brush)Resources["GpuSeriesStroke"] : (Brush)Application.Current.Resources["PrototypeStrokeBrush"];
    }
}
