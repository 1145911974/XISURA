using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed record GpuCurveNode(double VoltageMv, double BaseFrequencyMhz, double OffsetMhz)
{
    public double TargetFrequencyMhz => BaseFrequencyMhz + OffsetMhz;
}

public sealed record GpuCurveState(
    bool Supported,
    string? Reason,
    double MinimumOffsetMhz,
    double MaximumOffsetMhz,
    IReadOnlyList<GpuCurveNode> Nodes)
{
    public int? MemoryOffsetKhz { get; init; }
}

public sealed partial class GpuVoltageFrequencyCurve : UserControl
{
    private enum EditorMode { Basic, Linear, Manual }
    private const double PlotWidth = 820d;
    private const double PlotHeight = 278d;
    private const double BlendVoltageMv = 90d;
    private const double CurveAnimationDurationMs = 180d;
    private const int MaximumLinearPoints = 12;
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 255, 139, 111));
    private static readonly SolidColorBrush ReferenceBrush = new(Windows.UI.Color.FromArgb(255, 216, 208, 214));
    private GpuCurveState state = EmptyState();
    private GpuCurveState displayedState = EmptyState();
    private EditorMode editorMode = EditorMode.Linear;
    private int animationVersion;
    private bool syncingControls;
    private bool readbackGeometryDirty = true;
    private string? validationWarning;
    private string? statusOverride;
    private GpuCurveNode[] readbackNodes = [];
    private GpuCurveNode[] referenceNodes = [];
    private GpuCurveNode[] linearReferenceNodes = [];
    private int anchorIndex;
    private readonly List<LinearPoint> linearPoints = [];
    private int selectedLinearIndex = -1;
    private int draggedLinearAnchor = -1;
    private Point linearPressPoint;
    private bool linearDragMoved;
    private DateTimeOffset lastDragRenderAt;
    private double targetFrequencyMhz;
    private double minimumVoltageMv = 600d;
    private double maximumVoltageMv = 1400d;
    private double minimumFrequencyMhz = 600d;
    private double maximumFrequencyMhz = 3000d;
    private sealed record LinearPoint(int NodeIndex, double DeltaMhz);

    public GpuVoltageFrequencyCurve()
    {
        InitializeComponent();
        CurveHelp.Help = new(
            "调整 GPU 在不同电压档位下的目标频率，可能改变性能、功耗与稳定性；电压档位不改写。灰线是驱动曲线，主题色是草稿预览。基础模式调目标点和高压平台，线性模式用控制点平滑连接，手动模式改所选节点。",
            "正在读取驱动曲线",
            "优先保留原始曲线；需调节时从接近 0 MHz 的偏移开始，再测试实际游戏或负载。",
            "保持当前已验证曲线或 0 MHz 偏移，避免一次上调整条曲线。",
            "偏移必须满足驱动回读范围和软件保护边界，具体范围显示在当前值中；不存在通用安全电压或频率。",
            "编辑、切换与保存不会写硬件；使用整页已保存预设并确认后才提交。恢复默认会立即把核心及各档位频率偏移恢复为 0 MHz 并核对回读；过高可能花屏、崩溃或驱动重启。");
        TargetVoltageBox.ValueChanged += (_, value) => { if (!syncingControls) SelectVoltage(value); };
        TargetFrequencyBox.ValueChanged += (_, value) => { if (!syncingControls) ApplyTarget(value); };
        LinearOffsetBox.ValueChanged += (_, value) => { if (!syncingControls) ApplyLinearDelta(value); };
        ManualVoltageBox.ValueChanged += (_, value) => { if (!syncingControls) SelectManualVoltage(value); };
        ManualOffsetBox.ValueChanged += (_, value) => { if (!syncingControls) ApplyManualOffset(value); };
        foreach (var input in new[] { TargetVoltageBox, TargetFrequencyBox, LinearOffsetBox,
                     ManualVoltageBox, ManualOffsetBox })
            input.InputRejected += OnCurveInputRejected;
        Loaded += (_, _) => RenderState(state);
    }

    public event EventHandler<IReadOnlyList<GpuCurveNode>>? CurveChanged;
    public event EventHandler? RestoreDefaultsRequested;

    public bool AnimationsEnabled => new UISettings().AnimationsEnabled;

    public void ApplyState(GpuCurveState next, bool animate = true)
    {
        ArgumentNullException.ThrowIfNull(next);
        var nodes = next.Nodes.OrderBy(node => node.VoltageMv).ToArray();
        if (nodes.Zip(nodes.Skip(1), (left, right) => right.VoltageMv > left.VoltageMv).Any(increasing => !increasing))
            throw new ArgumentException("GPU V/F voltage nodes must be strictly increasing.", nameof(next));
        readbackNodes = nodes;
        statusOverride = null;
        readbackGeometryDirty = true;
        validationWarning = null;
        referenceNodes = nodes;
        linearReferenceNodes = nodes;
        ResetLinearPoints();
        state = next with { Nodes = nodes };
        anchorIndex = SelectDefaultAnchor();
        targetFrequencyMhz = nodes.Length == 0 ? 0d : nodes[anchorIndex].TargetFrequencyMhz;
        int version = ++animationVersion;
        if (animate && AnimationsEnabled && CanInterpolate(displayedState, state))
            _ = AnimateStateAsync(displayedState, state, version);
        else
            RenderState(state);
    }

    public void ResetOffsets()
    {
        if (!state.Supported || state.Nodes.Count == 0) return;
        referenceNodes = readbackNodes.Select(node => node with { OffsetMhz = 0d }).ToArray();
        linearReferenceNodes = referenceNodes;
        ResetLinearPoints();
        targetFrequencyMhz = referenceNodes[anchorIndex].TargetFrequencyMhz;
        ApplyEditedNodes(referenceNodes);
    }

    private static GpuCurveState EmptyState() => new(false, "等待曲线能力回读", 0d, 0d, []);

    private void RenderState(GpuCurveState visual)
    {
        displayedState = visual;
        UpdateModePresentation();
        bool available = visual.Supported && visual.Nodes.Count > 1 &&
            readbackNodes.Length == visual.Nodes.Count && referenceNodes.Length == visual.Nodes.Count &&
            linearReferenceNodes.Length == visual.Nodes.Count;
        double minimumTarget = 0d;
        double maximumTarget = 0d;
        bool feasible = available && editorMode == EditorMode.Basic && TryGetTargetRange(out minimumTarget, out maximumTarget);
        TargetVoltageBox.IsEnabled = ManualVoltageBox.IsEnabled = available;
        TargetFrequencyBox.IsEnabled = feasible;
        ManualOffsetBox.IsEnabled = available;
        CurveHelp.Help = CurveHelp.Help with { CurrentValue = available
            ? $"驱动曲线已读回  ·  频率偏移保护范围 {visual.MinimumOffsetMhz:0}～{visual.MaximumOffsetMhz:0} MHz" +
              (visual.MemoryOffsetKhz is int memory ? $"  ·  显存偏移 {memory / 1000d:0} MHz" : "")
            : visual.Reason ?? "曲线不可用" };
        CapabilityStatusText.Text = available ? statusOverride ?? "驱动已连接" : "未连接";
        CapabilityDot.Fill = new SolidColorBrush(available
            ? Windows.UI.Color.FromArgb(255, 255, 36, 79)
            : Windows.UI.Color.FromArgb(255, 89, 99, 109));

        TargetAnchor.Visibility = AnchorGuide.Visibility = Visibility.Collapsed;
        LinearAnchorLayer.Visibility = Visibility.Collapsed;
        if (!available)
        {
            CurvePath.Data = ReadbackCurvePath.Data = null;
            LinearAnchorLayer.Children.Clear();
            SetEditorUnavailable();
            SetAxisUnavailable();
            return;
        }

        UpdateRanges(readbackNodes, visual.MinimumOffsetMhz, visual.MaximumOffsetMhz);
        if (readbackGeometryDirty)
        {
            ReadbackCurvePath.Data = BuildLine(readbackNodes);
            readbackGeometryDirty = false;
        }
        CurvePath.Data = BuildLine(visual.Nodes);
        CurvePath.Stroke = (Brush)Application.Current.Resources["ModeAccentBrush"];
        TargetAnchor.Fill = (Brush)Application.Current.Resources["ModeAccentBrush"];
        if (editorMode == EditorMode.Linear)
            RenderLinearAnchors(visual.Nodes);
        else
        {
            var point = ToPoint(visual.Nodes[anchorIndex]);
            PlaceAnchor(TargetAnchor, visual.Nodes[anchorIndex]);
            AnchorGuide.Visibility = Visibility.Visible;
            AnchorGuide.X1 = AnchorGuide.X2 = point.X;
        }
        syncingControls = true;
        TargetVoltageBox.Minimum = referenceNodes[0].VoltageMv;
        TargetVoltageBox.Maximum = referenceNodes[^1].VoltageMv;
        TargetVoltageBox.Value = referenceNodes[anchorIndex].VoltageMv;
        if (feasible)
        {
            TargetFrequencyBox.Minimum = minimumTarget;
            TargetFrequencyBox.Maximum = maximumTarget;
            TargetFrequencyBox.Value = targetFrequencyMhz;
        }
        ManualVoltageBox.Minimum = readbackNodes[0].VoltageMv;
        ManualVoltageBox.Maximum = readbackNodes[^1].VoltageMv;
        ManualVoltageBox.Value = readbackNodes[anchorIndex].VoltageMv;
        ManualOffsetBox.Minimum = visual.MinimumOffsetMhz;
        ManualOffsetBox.Maximum = visual.MaximumOffsetMhz;
        ManualOffsetBox.Value = visual.Nodes[anchorIndex].OffsetMhz;
        bool linearFeasible = TryGetLinearRange(selectedLinearIndex, out double linearMinimum, out double linearMaximum);
        LinearOffsetBox.IsEnabled = linearFeasible;
        if (linearFeasible)
        {
            LinearOffsetBox.Minimum = linearMinimum;
            LinearOffsetBox.Maximum = linearMaximum;
            LinearOffsetBox.Value = linearPoints[selectedLinearIndex].DeltaMhz;
        }
        syncingControls = false;
        FeasibleRangeText.Text = feasible
            ? $"此档位可用目标频率 {minimumTarget:0}～{maximumTarget:0} MHz"
            : "此电压无法形成平台，请选更高档位";
        ReadbackPointText.Text = $"{readbackNodes[anchorIndex].VoltageMv:0} mV · {readbackNodes[anchorIndex].TargetFrequencyMhz:0} MHz";
        PreviewPointText.Text = $"{visual.Nodes[anchorIndex].VoltageMv:0} mV · {visual.Nodes[anchorIndex].TargetFrequencyMhz:0} MHz";
        ManualReadbackText.Text = $"{readbackNodes[anchorIndex].VoltageMv:0} mV · {readbackNodes[anchorIndex].TargetFrequencyMhz:0} MHz";
        ManualPreviewText.Text = $"{visual.Nodes[anchorIndex].VoltageMv:0} mV · {visual.Nodes[anchorIndex].TargetFrequencyMhz:0} MHz";
        LinearSelectedText.Text = selectedLinearIndex >= 0 && selectedLinearIndex < linearPoints.Count
            ? $"{linearReferenceNodes[linearPoints[selectedLinearIndex].NodeIndex].VoltageMv:0} mV · {selectedLinearIndex + 1}/{linearPoints.Count} 点"
            : "点击曲线添加控制点";
        LinearRangeText.Text = linearFeasible
            ? $"此点可调 {linearMinimum:0}～{linearMaximum:0} MHz · 最多 {MaximumLinearPoints} 点"
            : $"左键添加/拖动 · 右键删除 · 最多 {MaximumLinearPoints} 点";
        ReferenceText.Text = validationWarning ?? string.Empty;
        ReferenceText.Visibility = validationWarning is null ? Visibility.Collapsed : Visibility.Visible;
        ReferenceText.Foreground = validationWarning is null ? ReferenceBrush : WarningBrush;
        XAxisMinimumText.Text = $"{minimumVoltageMv:0}";
        XAxisQuarterText.Text = $"{minimumVoltageMv + (maximumVoltageMv - minimumVoltageMv) * 0.25d:0}";
        XAxisMiddleText.Text = $"{(minimumVoltageMv + maximumVoltageMv) / 2d:0}";
        XAxisThreeQuarterText.Text = $"{minimumVoltageMv + (maximumVoltageMv - minimumVoltageMv) * 0.75d:0}";
        XAxisMaximumText.Text = $"{maximumVoltageMv:0}";
        YAxisMinimumText.Text = $"{minimumFrequencyMhz:0}";
        YAxisQuarterText.Text = $"{minimumFrequencyMhz + (maximumFrequencyMhz - minimumFrequencyMhz) * 0.25d:0}";
        YAxisMiddleText.Text = $"{(minimumFrequencyMhz + maximumFrequencyMhz) / 2d:0}";
        YAxisThreeQuarterText.Text = $"{minimumFrequencyMhz + (maximumFrequencyMhz - minimumFrequencyMhz) * 0.75d:0}";
        YAxisMaximumText.Text = $"{maximumFrequencyMhz:0}";
        UpdateDragTooltip(visual.Nodes);
    }

    private void UpdateModePresentation()
    {
        BasicEditor.Visibility = editorMode == EditorMode.Basic ? Visibility.Visible : Visibility.Collapsed;
        LinearEditor.Visibility = editorMode == EditorMode.Linear ? Visibility.Visible : Visibility.Collapsed;
        ManualEditor.Visibility = editorMode == EditorMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        ModeSummaryText.Text = editorMode switch
        {
            EditorMode.Basic => "基础模式 · 目标工作点",
            EditorMode.Linear => "线性模式 · 关键控制点",
            _ => "手动模式 · 单节点精调"
        };
        if (state.Supported && readbackNodes.Length == state.Nodes.Count &&
            !state.Nodes.SequenceEqual(readbackNodes))
            ModeSummaryText.Text += " · 未应用草稿";
        ChartInstructionText.Text = editorMode switch
        {
            EditorMode.Basic => "选择一个目标工作点",
            EditorMode.Linear => "左键添加或拖动控制点 · 右键删除控制点",
            _ => "点击曲线选择需要微调的节点"
        };
        foreach (var (button, mode) in new[]
        {
            (BasicModeButton, EditorMode.Basic),
            (LinearModeButton, EditorMode.Linear),
            (ManualModeButton, EditorMode.Manual)
        })
        {
            bool selected = editorMode == mode;
            button.Background = (Brush)Application.Current.Resources[selected ? "ModeSelectionBrush" : "PrototypeControlAcrylicBrush"];
            button.BorderBrush = (Brush)Application.Current.Resources[selected ? "ModeAccentBrush" : "PrototypeStrokeBrush"];
            button.BorderThickness = new Thickness(selected ? 2d : 1d);
        }
    }

    private void PlaceAnchor(Ellipse anchor, GpuCurveNode node)
    {
        var point = ToPoint(node);
        Canvas.SetLeft(anchor, point.X - anchor.Width / 2d);
        Canvas.SetTop(anchor, point.Y - anchor.Height / 2d);
        anchor.Visibility = Visibility.Visible;
    }

    private int FindLinearAnchorIndex(double fraction) => Enumerable.Range(1, readbackNodes.Length - 1)
        .MinBy(index => Math.Abs((readbackNodes[index].VoltageMv - readbackNodes[0].VoltageMv) /
            (readbackNodes[^1].VoltageMv - readbackNodes[0].VoltageMv) - fraction));

    private void ResetLinearPoints()
    {
        linearPoints.Clear();
        if (linearReferenceNodes.Length > 2)
        {
            linearPoints.Add(new LinearPoint(FindLinearAnchorIndex(0.45d), 0d));
            int high = FindLinearAnchorIndex(0.75d);
            if (high != linearPoints[0].NodeIndex) linearPoints.Add(new LinearPoint(high, 0d));
        }
        selectedLinearIndex = linearPoints.Count - 1;
    }

    private static double Smooth(double t) => t * t * (3d - 2d * t);

    private double LinearDeltaAt(int nodeIndex, int variedPoint = -1, double variedDelta = 0d)
    {
        if (linearPoints.Count == 0) return 0d;
        int right = linearPoints.FindIndex(point => point.NodeIndex >= nodeIndex);
        if (right < 0) return variedPoint == linearPoints.Count - 1 ? variedDelta : linearPoints[^1].DeltaMhz;
        var current = linearPoints[right];
        double end = right == variedPoint ? variedDelta : current.DeltaMhz;
        if (nodeIndex == current.NodeIndex) return end;
        int startIndex = right == 0 ? 0 : linearPoints[right - 1].NodeIndex;
        double start = right == 0 ? 0d : right - 1 == variedPoint ? variedDelta : linearPoints[right - 1].DeltaMhz;
        double t = (linearReferenceNodes[nodeIndex].VoltageMv - linearReferenceNodes[startIndex].VoltageMv) /
            (linearReferenceNodes[current.NodeIndex].VoltageMv - linearReferenceNodes[startIndex].VoltageMv);
        return start + (end - start) * Smooth(t);
    }

    private bool TryGetLinearRange(int pointIndex, out double minimum, out double maximum)
    {
        minimum = double.NegativeInfinity;
        maximum = double.PositiveInfinity;
        if (!state.Supported || linearReferenceNodes.Length < 2 || pointIndex < 0 || pointIndex >= linearPoints.Count) return false;
        for (int index = 1; index < linearReferenceNodes.Length; index++)
        {
            var node = linearReferenceNodes[index];
            double other = LinearDeltaAt(index, pointIndex, 0d);
            double weight = LinearDeltaAt(index, pointIndex, 1d) - other;
            if (weight < 0.000001d) continue;
            minimum = Math.Max(minimum, (state.MinimumOffsetMhz - node.OffsetMhz - other) / weight);
            maximum = Math.Min(maximum, (state.MaximumOffsetMhz - node.OffsetMhz - other) / weight);
        }
        minimum = Math.Max(Math.Ceiling(minimum), state.MinimumOffsetMhz);
        maximum = Math.Min(Math.Floor(maximum), state.MaximumOffsetMhz);
        return double.IsFinite(minimum) && double.IsFinite(maximum) && minimum <= maximum;
    }

    private void RenderLinearAnchors(IReadOnlyList<GpuCurveNode> nodes)
    {
        LinearAnchorLayer.Visibility = Visibility.Visible;
        while (LinearAnchorLayer.Children.Count < linearPoints.Count)
            LinearAnchorLayer.Children.Add(new Ellipse
            {
                Width = 18, Height = 18,
                Fill = (Brush)Application.Current.Resources["ModeAccentBrush"],
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 2.5d
            });
        while (LinearAnchorLayer.Children.Count > linearPoints.Count)
            LinearAnchorLayer.Children.RemoveAt(LinearAnchorLayer.Children.Count - 1);
        for (int index = 0; index < linearPoints.Count; index++)
        {
            var anchor = (Ellipse)LinearAnchorLayer.Children[index];
            anchor.Fill = (Brush)Application.Current.Resources["ModeAccentBrush"];
            anchor.StrokeThickness = index == selectedLinearIndex ? 3d : 1.5d;
            PlaceAnchor(anchor, nodes[linearPoints[index].NodeIndex]);
        }
    }

    private void OnCurveInputRejected(object? sender, double attempted)
    {
        if (sender is not Jiaolong_ControlCenter.Controls.GpuValueStepper input) return;
        string unit = ReferenceEquals(input, TargetVoltageBox) || ReferenceEquals(input, ManualVoltageBox)
            ? "mV" : "MHz";
        ShowValidationWarning($"{attempted:0} {unit} 超出 {input.Minimum:0}～{input.Maximum:0}，已拒绝");
    }

    private void ShowValidationWarning(string message)
    {
        validationWarning = message;
        ReferenceText.Text = message;
        ReferenceText.Visibility = Visibility.Visible;
        ReferenceText.Foreground = WarningBrush;
        CapabilityStatusText.Text = message;
    }

    private void ApplyLinearDelta(double value)
    {
        if (editorMode != EditorMode.Linear || !TryGetLinearRange(selectedLinearIndex, out double minimum, out double maximum)) return;
        double requested = Math.Round(value);
        double delta = Math.Clamp(requested, minimum, maximum);
        bool limited = requested != delta;
        if (Math.Abs(delta - linearPoints[selectedLinearIndex].DeltaMhz) < 0.001d)
        {
            if (limited) ShowValidationWarning($"已达到可用边界 {minimum:0}～{maximum:0} MHz");
            return;
        }
        linearPoints[selectedLinearIndex] = linearPoints[selectedLinearIndex] with { DeltaMhz = delta };
        ApplyLinearPoints();
        if (limited) ShowValidationWarning($"已达到可用边界 {minimum:0}～{maximum:0} MHz");
    }

    private void ApplyLinearPoints()
    {
        var nodes = linearReferenceNodes.Select((node, index) =>
        {
            if (index == 0) return node;
            return node with { OffsetMhz = node.OffsetMhz + LinearDeltaAt(index) };
        }).ToArray();
        ApplyEditedNodes(nodes);
    }

    private bool IsLinearDraftValid() => Enumerable.Range(1, linearReferenceNodes.Length - 1).All(index =>
    {
        double offset = linearReferenceNodes[index].OffsetMhz + LinearDeltaAt(index);
        return offset >= state.MinimumOffsetMhz - 0.001d && offset <= state.MaximumOffsetMhz + 0.001d;
    });

    private void SelectManualVoltage(double millivolts)
    {
        if (editorMode != EditorMode.Manual || readbackNodes.Length < 2) return;
        anchorIndex = Enumerable.Range(1, readbackNodes.Length - 1)
            .MinBy(index => Math.Abs(readbackNodes[index].VoltageMv - millivolts));
        RenderState(state);
    }

    private void ApplyManualOffset(double offsetMhz)
    {
        if (editorMode != EditorMode.Manual || !state.Supported || anchorIndex < 1) return;
        var nodes = state.Nodes.ToArray();
        nodes[anchorIndex] = nodes[anchorIndex] with
        {
            OffsetMhz = Math.Clamp(Math.Round(offsetMhz), state.MinimumOffsetMhz, state.MaximumOffsetMhz)
        };
        ApplyEditedNodes(nodes);
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Tag?.ToString(), out EditorMode selected) ||
            selected == editorMode) return;
        draggedLinearAnchor = -1;
        validationWarning = null;
        editorMode = selected;
        referenceNodes = state.Nodes.ToArray();
        linearReferenceNodes = state.Nodes.ToArray();
        ResetLinearPoints();
        if (state.Nodes.Count > anchorIndex)
            targetFrequencyMhz = state.Nodes[anchorIndex].TargetFrequencyMhz;
        RenderState(state);
    }

    private void UpdateRanges(IReadOnlyList<GpuCurveNode> nodes, double minimumOffsetMhz, double maximumOffsetMhz)
    {
        minimumVoltageMv = nodes[0].VoltageMv;
        maximumVoltageMv = Math.Max(minimumVoltageMv + 1d, nodes[^1].VoltageMv);
        double minimum = nodes.Min(node => Math.Min(node.TargetFrequencyMhz, node.BaseFrequencyMhz + minimumOffsetMhz));
        double maximum = nodes.Max(node => Math.Max(node.TargetFrequencyMhz, node.BaseFrequencyMhz + maximumOffsetMhz));
        double padding = Math.Max(100d, (maximum - minimum) * 0.12d);
        minimumFrequencyMhz = Math.Max(0d, Math.Floor((minimum - padding) / 100d) * 100d);
        maximumFrequencyMhz = Math.Ceiling((maximum + padding) / 100d) * 100d;
        if (maximumFrequencyMhz <= minimumFrequencyMhz) maximumFrequencyMhz = minimumFrequencyMhz + 100d;
    }

    private double MapVoltageToX(double value) => (value - minimumVoltageMv) / (maximumVoltageMv - minimumVoltageMv) * PlotWidth;
    private double MapFrequencyToY(double value) => PlotHeight - (value - minimumFrequencyMhz) / (maximumFrequencyMhz - minimumFrequencyMhz) * PlotHeight;
    private Point ToPoint(GpuCurveNode node) => new(MapVoltageToX(node.VoltageMv), MapFrequencyToY(node.TargetFrequencyMhz));

    private PathGeometry BuildLine(IReadOnlyList<GpuCurveNode> nodes)
    {
        var figure = new PathFigure { StartPoint = ToPoint(nodes[0]) };
        var segment = new PolyLineSegment();
        foreach (var node in nodes.Skip(1)) segment.Points.Add(ToPoint(node));
        figure.Segments.Add(segment);
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); return geometry;
    }

    private int SelectDefaultAnchor()
    {
        if (referenceNodes.Length < 2) return 0;
        for (int index = Math.Max(1, referenceNodes.Length * 2 / 3); index < referenceNodes.Length; index++)
        {
            anchorIndex = index;
            if (TryGetTargetRange(out double minimum, out double maximum) &&
                referenceNodes[index].TargetFrequencyMhz >= minimum &&
                referenceNodes[index].TargetFrequencyMhz <= maximum) return index;
        }
        return referenceNodes.Length - 1;
    }

    private bool TryGetTargetRange(out double minimum, out double maximum)
    {
        minimum = double.NegativeInfinity;
        maximum = double.PositiveInfinity;
        if (anchorIndex < 1 || anchorIndex >= referenceNodes.Length) return false;
        double anchorVoltage = referenceNodes[anchorIndex].VoltageMv;
        double blendStart = Math.Max(referenceNodes[0].VoltageMv, anchorVoltage - BlendVoltageMv);
        double anchorFrequency = referenceNodes[anchorIndex].TargetFrequencyMhz;
        for (int index = 1; index < referenceNodes.Length; index++)
        {
            var node = referenceNodes[index];
            if (index >= anchorIndex)
            {
                minimum = Math.Max(minimum, node.BaseFrequencyMhz + state.MinimumOffsetMhz);
                maximum = Math.Min(maximum, node.BaseFrequencyMhz + state.MaximumOffsetMhz);
            }
            else if (node.VoltageMv > blendStart)
            {
                double t = (node.VoltageMv - blendStart) / (anchorVoltage - blendStart);
                double weight = t * t * (3d - 2d * t);
                minimum = Math.Max(minimum, anchorFrequency + (state.MinimumOffsetMhz - node.OffsetMhz) / weight);
                maximum = Math.Min(maximum, anchorFrequency + (state.MaximumOffsetMhz - node.OffsetMhz) / weight);
            }
        }
        minimum = Math.Max(minimum, referenceNodes.Take(anchorIndex).Max(node => node.TargetFrequencyMhz));
        minimum = Math.Ceiling(minimum);
        maximum = Math.Floor(maximum);
        return minimum <= maximum;
    }

    private void SelectVoltage(double millivolts)
    {
        if (editorMode != EditorMode.Basic || !state.Supported || referenceNodes.Length < 2) return;
        int selected = Enumerable.Range(1, referenceNodes.Length - 1)
            .MinBy(index => Math.Abs(referenceNodes[index].VoltageMv - millivolts));
        if (selected == anchorIndex) return;
        anchorIndex = selected;
        ApplyTarget(referenceNodes[selected].TargetFrequencyMhz);
    }

    private void ApplyTarget(double frequencyMhz)
    {
        if (editorMode != EditorMode.Basic || !state.Supported || referenceNodes.Length < 2) return;
        if (!TryGetTargetRange(out double minimum, out double maximum))
        {
            ApplyEditedNodes(readbackNodes);
            ShowValidationWarning("该电压档位无法在保护范围内形成平台");
            return;
        }
        double requested = Math.Round(frequencyMhz);
        targetFrequencyMhz = Math.Clamp(requested, minimum, maximum);
        double anchorVoltage = referenceNodes[anchorIndex].VoltageMv;
        double blendStart = Math.Max(referenceNodes[0].VoltageMv, anchorVoltage - BlendVoltageMv);
        double change = targetFrequencyMhz - referenceNodes[anchorIndex].TargetFrequencyMhz;
        var nodes = referenceNodes.Select((node, index) =>
        {
            if (index == 0 || node.VoltageMv <= blendStart) return node;
            if (index >= anchorIndex)
                return node with { OffsetMhz = targetFrequencyMhz - node.BaseFrequencyMhz };
            double t = (node.VoltageMv - blendStart) / (anchorVoltage - blendStart);
            double weight = t * t * (3d - 2d * t);
            return node with { OffsetMhz = node.OffsetMhz + change * weight };
        }).ToArray();
        if (nodes.Skip(1).Any(node => node.OffsetMhz < state.MinimumOffsetMhz - 0.001d ||
            node.OffsetMhz > state.MaximumOffsetMhz + 0.001d))
        {
            ApplyEditedNodes(readbackNodes);
            SetStatus("目标频率超出本机节点偏移范围");
            return;
        }
        ApplyEditedNodes(nodes);
        if (requested != targetFrequencyMhz)
            ShowValidationWarning($"目标频率已限制在 {minimum:0}～{maximum:0} MHz");
    }

    private void ApplyEditedNodes(IReadOnlyList<GpuCurveNode> nodes)
    {
        validationWarning = null;
        state = state with { Nodes = nodes };
        RenderState(state);
        CurveChanged?.Invoke(this, nodes);
    }

    private void OnPlotPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!state.Supported || referenceNodes.Length < 2) return;
        var current = e.GetCurrentPoint(PlotCanvas);
        var pointer = current.Position;
        if (editorMode == EditorMode.Linear)
        {
            int nearby = -1;
            double distance = 24d;
            for (int index = 0; index < linearPoints.Count; index++)
            {
                var point = ToPoint(state.Nodes[linearPoints[index].NodeIndex]);
                double candidate = Math.Sqrt(Math.Pow(pointer.X - point.X, 2d) + Math.Pow(pointer.Y - point.Y, 2d));
                if (candidate >= distance) continue;
                nearby = index;
                distance = candidate;
            }
            if (current.Properties.IsRightButtonPressed)
            {
                if (nearby >= 0)
                {
                    var removed = linearPoints[nearby];
                    linearPoints.RemoveAt(nearby);
                    if (IsLinearDraftValid())
                    {
                        selectedLinearIndex = linearPoints.Count == 0 ? -1 : Math.Min(nearby, linearPoints.Count - 1);
                        ApplyLinearPoints();
                    }
                    else
                    {
                        linearPoints.Insert(nearby, removed);
                        ShowValidationWarning("删除后曲线超出驱动允许范围，请先调整相邻控制点");
                    }
                }
                e.Handled = true;
                return;
            }
            if (!current.Properties.IsLeftButtonPressed) return;
            if (nearby < 0)
            {
                if (linearPoints.Count >= MaximumLinearPoints)
                {
                    ShowValidationWarning($"最多添加 {MaximumLinearPoints} 个控制点");
                    e.Handled = true;
                    return;
                }
                double voltage = minimumVoltageMv + Math.Clamp(pointer.X, 0d, PlotWidth) / PlotWidth *
                    (maximumVoltageMv - minimumVoltageMv);
                int nodeIndex = Enumerable.Range(1, linearReferenceNodes.Length - 1)
                    .MinBy(index => Math.Abs(linearReferenceNodes[index].VoltageMv - voltage));
                nearby = linearPoints.FindIndex(point => point.NodeIndex == nodeIndex);
                if (nearby < 0)
                {
                    // Insert on the current draft so a simple click does not pull the GPU curve toward the cursor.
                    double desired = state.Nodes[nodeIndex].OffsetMhz - linearReferenceNodes[nodeIndex].OffsetMhz;
                    nearby = linearPoints.FindIndex(point => point.NodeIndex > nodeIndex);
                    if (nearby < 0) nearby = linearPoints.Count;
                    linearPoints.Insert(nearby, new LinearPoint(nodeIndex, 0d));
                    if (!TryGetLinearRange(nearby, out double minimum, out double maximum))
                    {
                        linearPoints.RemoveAt(nearby);
                        ShowValidationWarning("此档位无法在驱动保护范围内添加控制点");
                        e.Handled = true;
                        return;
                    }
                    selectedLinearIndex = nearby;
                    linearPoints[nearby] = linearPoints[nearby] with { DeltaMhz = Math.Clamp(desired, minimum, maximum) };
                    ApplyLinearPoints();
                }
            }
            selectedLinearIndex = nearby;
            draggedLinearAnchor = linearPoints[nearby].NodeIndex;
            linearPressPoint = pointer;
            linearDragMoved = false;
            lastDragRenderAt = DateTimeOffset.UtcNow;
            PlotCanvas.CapturePointer(e.Pointer);
            RenderState(state);
        }
        else
        {
            double voltage = minimumVoltageMv + Math.Clamp(pointer.X, 0d, PlotWidth) / PlotWidth *
                (maximumVoltageMv - minimumVoltageMv);
            if (editorMode == EditorMode.Basic) SelectVoltage(voltage);
            else SelectManualVoltage(voltage);
        }
        e.Handled = true;
    }

    private void OnPlotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (draggedLinearAnchor < 1 || editorMode != EditorMode.Linear) return;
        var pointer = e.GetCurrentPoint(PlotCanvas).Position;
        linearDragMoved |= Math.Abs(pointer.X - linearPressPoint.X) > 3d ||
            Math.Abs(pointer.Y - linearPressPoint.Y) > 3d;
        if (!linearDragMoved) return;
        var now = DateTimeOffset.UtcNow;
        if ((now - lastDragRenderAt).TotalMilliseconds >= 33d)
        {
            lastDragRenderAt = now;
            UpdateDraggedLinearAnchor(pointer.Y);
        }
        e.Handled = true;
    }

    private void UpdateDraggedLinearAnchor(double pointerY)
    {
        double y = Math.Clamp(pointerY, 0d, PlotHeight);
        double frequency = minimumFrequencyMhz + (1d - y / PlotHeight) *
            (maximumFrequencyMhz - minimumFrequencyMhz);
        if (selectedLinearIndex < 0 || selectedLinearIndex >= linearPoints.Count ||
            linearPoints[selectedLinearIndex].NodeIndex != draggedLinearAnchor) return;
        var node = linearReferenceNodes[draggedLinearAnchor];
        ApplyLinearDelta(frequency - node.TargetFrequencyMhz);
    }

    private void OnPlotPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (draggedLinearAnchor >= 1 && editorMode == EditorMode.Linear)
        {
            var pointer = e.GetCurrentPoint(PlotCanvas).Position;
            if (linearDragMoved || Math.Abs(pointer.Y - linearPressPoint.Y) > 3d)
                UpdateDraggedLinearAnchor(pointer.Y);
        }
        draggedLinearAnchor = -1;
        DragTooltip.Visibility = Visibility.Collapsed;
        PlotCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnPlotPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        draggedLinearAnchor = -1;
        linearDragMoved = false;
        DragTooltip.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragTooltip(IReadOnlyList<GpuCurveNode> nodes)
    {
        if (draggedLinearAnchor < 1 || draggedLinearAnchor >= nodes.Count || editorMode != EditorMode.Linear)
        {
            DragTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        GpuCurveNode node = nodes[draggedLinearAnchor];
        Point point = ToPoint(node);
        DragTooltipText.Text = $"{node.VoltageMv:0} mV  ·  {node.TargetFrequencyMhz:0} MHz";
        Canvas.SetLeft(DragTooltip, Math.Clamp(point.X + 14d, 0d, PlotWidth - 164d));
        Canvas.SetTop(DragTooltip, Math.Clamp(point.Y - 42d, 0d, PlotHeight - 34d));
        DragTooltip.Visibility = Visibility.Visible;
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e) => RestoreDefaultsRequested?.Invoke(this, EventArgs.Empty);
    public void SetStatus(string status)
    {
        statusOverride = status;
        CapabilityStatusText.Text = status;
        CurveHelp.Help = CurveHelp.Help with { CurrentValue = status };
    }

    private void SetEditorUnavailable()
    {
        ReferenceText.Text = string.Empty;
        ReferenceText.Visibility = Visibility.Collapsed;
        FeasibleRangeText.Text = LinearRangeText.Text = "--";
        LinearSelectedText.Text = "--";
        LinearOffsetBox.IsEnabled = false;
        ReadbackPointText.Text = PreviewPointText.Text = ManualReadbackText.Text = ManualPreviewText.Text = "--";
    }

    private void SetAxisUnavailable()
    {
        XAxisMinimumText.Text = XAxisQuarterText.Text = XAxisMiddleText.Text = XAxisThreeQuarterText.Text = XAxisMaximumText.Text = "--";
        YAxisMinimumText.Text = YAxisQuarterText.Text = YAxisMiddleText.Text = YAxisThreeQuarterText.Text = YAxisMaximumText.Text = "--";
    }

    private static bool CanInterpolate(GpuCurveState from, GpuCurveState to) =>
        from.Nodes.Count == to.Nodes.Count && from.Nodes.Count > 0 && from.Nodes.Zip(to.Nodes).All(pair => pair.First.VoltageMv == pair.Second.VoltageMv);

    private Task AnimateStateAsync(GpuCurveState from, GpuCurveState to, int version)
    {
        var started = DateTimeOffset.UtcNow;
        var completion = new TaskCompletionSource();
        void OnFrame(object? sender, object e)
        {
            if (version != animationVersion) { CompositionTarget.Rendering -= OnFrame; completion.TrySetResult(); return; }
            double progress = Math.Clamp((DateTimeOffset.UtcNow - started).TotalMilliseconds / CurveAnimationDurationMs, 0d, 1d);
            var nodes = from.Nodes.Zip(to.Nodes, (left, right) => right with { OffsetMhz = left.OffsetMhz + (right.OffsetMhz - left.OffsetMhz) * progress }).ToArray();
            RenderState(to with { Nodes = nodes });
            if (progress < 1d) return;
            CompositionTarget.Rendering -= OnFrame; RenderState(to); completion.TrySetResult();
        }
        CompositionTarget.Rendering += OnFrame;
        return completion.Task;
    }
}
