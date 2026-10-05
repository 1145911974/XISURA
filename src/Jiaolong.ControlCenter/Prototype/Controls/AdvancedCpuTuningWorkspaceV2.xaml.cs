using Jiaolong_ControlCenter.ViewModels;
using Jiaolong_ControlCenter.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AdvancedCpuTuningWorkspaceV2 : UserControl
{
    private readonly Dictionary<int, int> perCoreCurveOptimizer = new();
    private IReadOnlyDictionary<int, int>? hardwareCurveOptimizer;
    private bool synchronizing;
    private bool touched;
    private int selectedCore;
    private CpuCurveOptimizerMode curveMode;
    private int allCoreCurveValue;
    private int? hardwarePboScalar;
    private bool parkingDraftSpecified;
    private bool pboDraftDirty;
    private bool pboDraftSpecified;
    private bool pboAvailable;
    private int? hardwareAcParking;
    private int? hardwareDcParking;
    private bool parkingAvailable;

    public AdvancedCpuTuningWorkspaceV2()
    {
        InitializeComponent();
        CoreHelpButton.Help = Help(
            "显示电脑启用的物理核心和逻辑线程，帮助确认可并行执行多少任务；这里只查看状态。",
            "保持全部可用核心，由 Windows 调度。",
            "保持当前核心配置；这里不关闭或屏蔽核心。",
            "以处理器和 Windows 实际读回数量为准；本功能没有可填写的数值极限。",
            "核心停泊不等于关闭物理核心；减少核心可能降低并行性能。");
        CoreParkingHelp.Help = Help(
            "设置当前 Windows 计划至少保持多少逻辑处理器活跃；比例高响应更及时，比例低更利于省电，不指定某个核心。",
            "接通电源优先保留当前系统值；内置预设使用 100%，电池使用 17%，仅作软件参考。",
            "保留当前 Windows 值；需节能时逐步降低电池比例，并检查唤醒延迟。",
            "输入范围 0–100%；100% 禁用核心停泊，0% 允许系统尽量停泊，不代表关闭全部核心。",
            "使用预设前必须能读回原值；过高增加待机功耗，过低可能增加响应延迟。");
        PowerBoundaryHelp.Help = Help(
            "限制处理器长时和短时允许消耗的功率；提高可改善持续性能，也会增加热量与噪声。本机 Fast PPT、PPT 与 SPPT 对应同一上限，须一致。",
            "优先采用本机厂商当前值；软件功耗参考起点 55 W，仅在 45–75 W 输入范围内调整。",
            "保留已验证的当前值；需降温时逐步下调，避免同时提高多个功耗项。",
            "75 W 为软件输入上限，不是硬件安全保证；实际写入受本机能力约束，恢复固件原值不受此边界截断。",
            "保存是草稿，使用后才写入；过高可能触发温度或供电保护，过低降低持续性能。");
        CurrentBoundaryHelp.Help = Help(
            "限制处理器持续和瞬时可用电流；提高可能减少电流限制降频，也会增加供电压力。本机 VRM 与 TDC 为同一限值，须一致。",
            "保留本机读回的厂商电流值；缺少厂商规格与稳定性验证时不提高。",
            "保持当前值；需降低供电压力时小幅下调并检查性能。软件输入范围 1–200 A。",
            "200 A（200000 mA）仅是软件输入上限；没有通用硬件安全电流，不以界面最大值作为建议。",
            "供电和处理器承受能力因设备而异；过高可能过热或触发保护。");
        TemperatureBoundaryHelp.Help = Help(
            "设置处理器开始温度限制的阈值；调低更容易降温、降噪，调高会延后温度限制。本机温度墙、MP1、RSMU 是同一阈值，须一致。",
            "优先保留本机厂商温限；内置办公、游戏、狂飙标准档分别使用 75、85、90°C，仅作软件参考。",
            "保持厂商值，或在性能可接受时逐步降低；软件输入范围 40–100°C。",
            "100°C 是软件输入上限，不是推荐温度或硬件安全保证；实际可写范围以本机能力为准。",
            "提高可能增加温度、噪声和长期热负担；降低可能提前降频。");
        AutoBoostHelp.Help = Help(
            "调整固件允许自动加速的判断尺度，可能延长较高频率的维持时间；不是把实际频率乘以这个数字。",
            "1X 为界面参考起点；已有稳定配置可保持本机当前 Scalar。",
            "保留当前值或从 1X 开始，避免放大加速条件。",
            "软件输入 1–10X；10X 是输入边界，不代表十倍频率或稳定保证。",
            "保存后使用预设并确认才写入；须先读回原值，失败会恢复。增大可能增加温度与功耗。");
        CurveHelp.Help = Help(
            "调整处理器频率对应的电压请求；负偏移可能降低功耗、温度，但过大会崩溃。全核统一调整，逐核只提交已编辑的核心。",
            "优先沿用 BIOS；需要调整时从接近 0 步开始，验证轻载、待机和重负载稳定性。",
            "沿用 BIOS 不额外写入；0 步是一个新目标，不等于恢复此前 BIOS 值。",
            "输入 -30–0 步；-30 只是软件负向边界，步数不等于固定毫伏，不能作为通用稳定值。",
            "使用保存的性能预设后才提交；沿用 BIOS 不撤销已有软件偏移，异常时应恢复已知稳定设置。");
        UpdateCurveMode();
        foreach (var row in SmuLimitRows())
        {
            row.EnableDraftEditing();
            if (ReferenceEquals(row, Mp1Row) || ReferenceEquals(row, RsmuRow))
                row.Maximum = row.LimitValue = 100;
            else if (ReferenceEquals(row, VrmRow) || ReferenceEquals(row, TdcRow) || ReferenceEquals(row, EdcRow))
                row.Minimum = 1000;
            else
            {
                row.Minimum = 45;
                row.Maximum = row.LimitValue = 75;
                row.RecommendedValue = 55;
            }
        }
        foreach (AdvancedCpuFieldRowV2 row in Rows())
            row.ValueChanged += OnRowValueChanged;
        CoreParkingAcRow.ValueChanged += OnRowValueChanged;
        CoreParkingDcRow.ValueChanged += OnRowValueChanged;
        CoreParkingAcRow.ValueChanged += (_, _) => RefreshCoreParkingStatus();
        CoreParkingDcRow.ValueChanged += (_, _) => RefreshCoreParkingStatus();
    }

    private static ParameterHelpContent Help(string effect, string recommended, string safe, string extreme, string risk) =>
        new(effect, "由硬件回读；未连接时显示不可用。", recommended, safe, extreme, risk);

    public event EventHandler? CollapseRequested;
    public event EventHandler? Changed;
    private IEnumerable<AdvancedCpuFieldRowV2> SmuLimitRows() =>
        [StapmRow, FastPptRow, SlowPptRow, PptRow, VrmRow, TdcRow, EdcRow, Mp1Row, RsmuRow];

    public int? DraftPboScalar => PboScalarRow.Value is double value ? (int)Math.Round(value) : null;
    public (int Ac, int Dc)? DraftCoreParking =>
        CoreParkingAcRow.Value is double ac && CoreParkingDcRow.Value is double dc
            ? ((int)Math.Round(ac), (int)Math.Round(dc)) : null;

    public void SetAvailability(bool available, bool advancedAvailable, bool pboScalarAvailable = false)
    {
        IsEnabled = available;
        foreach (AdvancedCpuFieldRowV2 row in Rows())
            row.IsEnabled = available && advancedAvailable && SmuLimitRows().Contains(row);
        pboAvailable = available && pboScalarAvailable && hardwarePboScalar is not null;
        PboScalarRow.IsEnabled = pboAvailable;
        RefreshPboStatus();
        parkingAvailable = available && hardwareAcParking is not null && hardwareDcParking is not null;
        CoreParkingAcRow.IsEnabled = parkingAvailable;
        CoreParkingDcRow.IsEnabled = parkingAvailable;
        RefreshCoreParkingStatus();
        PerCoreCurveRow.IsEnabled = CurveCoreSelector.Items.Count > 0;
        CurveCoreSelector.IsEnabled = CurveCoreSelector.Items.Count > 0;
        UpdateCurveMode();
        AvailabilityText.Text = available
            ? advancedAvailable || pboAvailable ? "已连接" : "高级能力不可用"
            : "硬件不可用";
    }

    public void SetPboHardware(int? scalar)
    {
        hardwarePboScalar = scalar;
        if (!pboDraftDirty && !pboDraftSpecified)
        {
            bool wasSynchronizing = synchronizing;
            synchronizing = true;
            PboScalarRow.SetValue(scalar);
            synchronizing = wasSynchronizing;
        }
        RefreshPboStatus();
    }

    public void SetSmuHardware(Jiaolong.Contracts.Commands.AdvancedCpuTuningPlan? limits)
    {
        StapmRow.SetHardwareValue(limits?.StapmWatts);
        FastPptRow.SetHardwareValue(limits?.FastPptWatts);
        SlowPptRow.SetHardwareValue(limits?.SlowPptWatts);
        PptRow.SetHardwareValue(limits?.PptWatts);
        VrmRow.SetHardwareValue(limits?.VrmCurrentMilliamps);
        TdcRow.SetHardwareValue(limits?.TdcCurrentMilliamps);
        EdcRow.SetHardwareValue(limits?.EdcCurrentMilliamps);
        Mp1Row.SetHardwareValue(limits?.Mp1TemperatureC);
        RsmuRow.SetHardwareValue(limits?.RsmuTemperatureC);
    }

    public void SynchronizeBasicAliases(double? temperature, double? fastPpt)
    {
        if (temperature is double limit && (Mp1Row.Value is not null || RsmuRow.Value is not null))
        {
            Mp1Row.SetValue(limit);
            RsmuRow.SetValue(limit);
        }
        if (fastPpt is double watts && (FastPptRow.Value is not null || PptRow.Value is not null))
        {
            FastPptRow.SetValue(watts);
            PptRow.SetValue(watts);
        }
    }

    public void SetCurveHardware(IReadOnlyDictionary<int, int>? values)
    {
        hardwareCurveOptimizer = values;
        CurveHelp.Help = CurveHelp.Help with { CurrentValue =
            values?.TryGetValue(selectedCore, out int current) == true
                ? $"已读回 {values.Count} 核；核心 {selectedCore + 1}：{current} 步。"
                : "硬件偏移未读回。" };
        if (touched) return;
        bool wasSynchronizing = synchronizing;
        synchronizing = true;
        if (values?.TryGetValue(selectedCore, out int actual) == true)
            PerCoreCurveRow.SetValue(actual);
        synchronizing = wasSynchronizing;
    }

    private void RefreshPboStatus()
    {
        AutoBoostHelp.Help = AutoBoostHelp.Help with
        {
            CurrentValue = hardwarePboScalar is int hardware ? $"硬件 {hardware}×" : "驱动未提供当前值。"
        };
    }

    public void SetCoreParkingHardware(int? ac, int? dc)
    {
        bool hasDraft = hardwareAcParking is not null && hardwareDcParking is not null &&
            DraftCoreParking is { } draft &&
            (draft.Ac != hardwareAcParking || draft.Dc != hardwareDcParking);
        hardwareAcParking = ac;
        hardwareDcParking = dc;
        if (!parkingDraftSpecified && (!hasDraft || ac is null || dc is null))
        {
            CoreParkingAcRow.SetValue(ac);
            CoreParkingDcRow.SetValue(dc);
        }
        RefreshCoreParkingStatus();
    }

    private void RefreshCoreParkingStatus()
    {
        CoreParkingHelp.Help = CoreParkingHelp.Help with
        {
            CurrentValue = hardwareAcParking is int ac && hardwareDcParking is int dc
                ? $"接通电源 {ac}% / 电池 {dc}%" : "Windows 未提供当前停泊比例。"
        };
    }

    public void ApplyDraft(
        AdvancedCpuTuningDraft? draft,
        int? legacyAllCore = null,
        int? coreParkingAc = null,
        int? coreParkingDc = null)
    {
        synchronizing = true;
        touched = draft is not null;
        perCoreCurveOptimizer.Clear();

        if (draft?.PerCoreCurveOptimizer is not null)
            foreach (var pair in draft.PerCoreCurveOptimizer) perCoreCurveOptimizer[pair.Key] = pair.Value;

        StapmRow.SetValue(draft?.StapmWatts);
        FastPptRow.SetValue(draft?.FastPptWatts);
        SlowPptRow.SetValue(draft?.SlowPptWatts);
        PptRow.SetValue(draft?.PptWatts);
        VrmRow.SetValue(draft?.VrmCurrentMilliamps);
        TdcRow.SetValue(draft?.TdcCurrentMilliamps);
        EdcRow.SetValue(draft?.EdcCurrentMilliamps);
        Mp1Row.SetValue(draft?.Mp1TemperatureC);
        RsmuRow.SetValue(draft?.RsmuTemperatureC);
        PboScalarRow.SetValue(draft?.PboScalar ?? hardwarePboScalar);
        pboDraftDirty = false;
        pboDraftSpecified = draft?.PboScalar is not null;
        parkingDraftSpecified = coreParkingAc is not null && coreParkingDc is not null;
        CoreParkingAcRow.SetValue(coreParkingAc ?? hardwareAcParking);
        CoreParkingDcRow.SetValue(coreParkingDc ?? hardwareDcParking);
        curveMode = draft?.ResolveCurveOptimizerMode(legacyAllCore)
            ?? (legacyAllCore.HasValue ? CpuCurveOptimizerMode.AllCore : CpuCurveOptimizerMode.Bios);
        allCoreCurveValue = draft?.CurveOptimizerAll ?? legacyAllCore ?? 0;
        UpdateCurveMode();
        RefreshCoreLabels();
        ApplySelectedCoreValues();
        UnsavedText.Visibility = Visibility.Collapsed;
        synchronizing = false;
    }

    public AdvancedCpuTuningDraft? ReadDraft()
    {
        if (!touched && Rows().All(row => row.Value is null)) return null;
        return new AdvancedCpuTuningDraft
        {
            CurveOptimizerMode = curveMode,
            StapmWatts = StapmRow.Value,
            FastPptWatts = FastPptRow.Value,
            SlowPptWatts = SlowPptRow.Value,
            PptWatts = PptRow.Value,
            VrmCurrentMilliamps = ToInt(VrmRow.Value),
            TdcCurrentMilliamps = ToInt(TdcRow.Value),
            EdcCurrentMilliamps = ToInt(EdcRow.Value),
            Mp1TemperatureC = ToInt(Mp1Row.Value),
            RsmuTemperatureC = ToInt(RsmuRow.Value),
            PboScalar = pboDraftSpecified || pboDraftDirty ? DraftPboScalar : null,
            CurveOptimizerAll = allCoreCurveValue,
            PerCoreCurveOptimizer = perCoreCurveOptimizer.Count == 0 ? null : new Dictionary<int, int>(perCoreCurveOptimizer)
        };
    }

    private IEnumerable<AdvancedCpuFieldRowV2> Rows()
    {
        yield return StapmRow;
        yield return FastPptRow;
        yield return SlowPptRow;
        yield return PptRow;
        yield return VrmRow;
        yield return TdcRow;
        yield return EdcRow;
        yield return Mp1Row;
        yield return RsmuRow;
        yield return PboScalarRow;
        yield return PerCoreCurveRow;
    }

    private void OnRowValueChanged(object? sender, double? value)
    {
        if (synchronizing)
            return;

        synchronizing = true;
        if (ReferenceEquals(sender, FastPptRow)) PptRow.SetValue(value);
        if (ReferenceEquals(sender, PptRow)) FastPptRow.SetValue(value);
        if (ReferenceEquals(sender, VrmRow)) TdcRow.SetValue(value);
        if (ReferenceEquals(sender, TdcRow)) VrmRow.SetValue(value);
        if (ReferenceEquals(sender, Mp1Row)) RsmuRow.SetValue(value);
        if (ReferenceEquals(sender, RsmuRow)) Mp1Row.SetValue(value);
        synchronizing = false;
        if (ReferenceEquals(sender, PboScalarRow))
        {
            pboDraftDirty = true;
            pboDraftSpecified = true;
            touched = true;
            RefreshPboStatus();
        }
        else touched = true;
        if (ReferenceEquals(sender, CoreParkingAcRow) || ReferenceEquals(sender, CoreParkingDcRow))
            parkingDraftSpecified = true;
        if (ReferenceEquals(sender, PerCoreCurveRow) && value is double curve)
        {
            perCoreCurveOptimizer[selectedCore] = (int)Math.Round(curve);
            bool wasSynchronizing = synchronizing;
            synchronizing = true;
            RefreshCoreLabels();
            synchronizing = wasSynchronizing;
        }
        UnsavedText.Visibility = Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCoreSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizing || sender is not ComboBox selector || selector.SelectedIndex < 0)
            return;
        selectedCore = selector.SelectedIndex;
        synchronizing = true;
        CurveCoreSelector.SelectedIndex = selectedCore;
        ApplySelectedCoreValues();
        synchronizing = false;
    }

    private void ApplySelectedCoreValues()
    {
        int? hardware = hardwareCurveOptimizer?.TryGetValue(selectedCore, out int actual) == true ? actual : null;
        PerCoreCurveRow.SetValue(perCoreCurveOptimizer.TryGetValue(selectedCore, out int curve) ? curve : hardware ?? 0);
    }

    public CpuCurveOptimizerMode CurveMode => curveMode;
    public int AllCoreCurveValue => allCoreCurveValue;
    public IReadOnlyDictionary<int, int> PerCoreCurveValues => new Dictionary<int, int>(perCoreCurveOptimizer);

    public void SetAllCoreCurveOffset(double value)
    {
        if (curveMode != CpuCurveOptimizerMode.AllCore || !double.IsFinite(value)) return;
        allCoreCurveValue = (int)Math.Round(Math.Clamp(value, -30, 30));
        touched = true;
        UnsavedText.Visibility = Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SelectCurveMode(CpuCurveOptimizerMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        if (curveMode == mode) return;
        curveMode = mode;
        UpdateCurveMode();
        touched = true;
        UnsavedText.Visibility = Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCoreCount(int? count)
    {
        EnabledCoreValueText.Text = count is > 0 ? $"{count} 核" : "--";
        LogicalProcessorValueText.Text = count is > 0 ? $"{count * 2} 线程" : "--";
        int coreCount = count is > 0 and <= 16 ? count.Value : 0;
        if (CurveCoreSelector.Items.Count == coreCount) return;
        bool wasSynchronizing = synchronizing;
        synchronizing = true;
        CurveCoreSelector.Items.Clear();
        for (int core = 0; core < coreCount; core++)
        {
            CurveCoreSelector.Items.Add(string.Empty);
        }
        selectedCore = Math.Clamp(selectedCore, 0, Math.Max(0, coreCount - 1));
        CurveCoreSelector.SelectedIndex = coreCount > 0 ? selectedCore : -1;
        RefreshCoreLabels();
        ApplySelectedCoreValues();
        CurveCoreSelector.IsEnabled = coreCount > 0;
        UpdateCurveMode();
        synchronizing = wasSynchronizing;
    }

    private void RefreshCoreLabels()
    {
        for (int core = 0; core < CurveCoreSelector.Items.Count; core++)
            CurveCoreSelector.Items[core] = perCoreCurveOptimizer.TryGetValue(core, out int value)
                ? $"核心 {core + 1}    {value:+0;-0;0} 步" : $"核心 {core + 1}    未设置";
        CurveCoreSelector.SelectedIndex = CurveCoreSelector.Items.Count > 0 ? selectedCore : -1;
    }

    private void UpdateCurveMode()
    {
        bool editable = curveMode == CpuCurveOptimizerMode.PerCore && CurveCoreSelector.Items.Count > 0;
        CurveCoreSelector.IsEnabled = editable;
        PerCoreCurveRow.IsEnabled = editable;
        PerCoreModeHint.Text = curveMode == CpuCurveOptimizerMode.PerCore
            ? CurveCoreSelector.Items.Count > 0 ? "逐核负偏移" : "等待核心拓扑回读"
            : "在基础调校中选择逐核偏移";
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    private static int? ToInt(double? value) => value is double actual && double.IsFinite(actual) ? (int)Math.Round(actual) : null;
}
