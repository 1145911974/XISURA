using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AutomationWorkspaceV2
{
    private AdaptiveStrategyAppearance savedAppearance = AdaptiveStrategyAppearance.Default(AdaptiveStrategyId.BalancedAdaptive);
    private AdaptiveStrategyAppearance draftAppearance = AdaptiveStrategyAppearance.Default(AdaptiveStrategyId.BalancedAdaptive);

    private AdaptiveStrategyAppearance LoadAppearance(AdaptiveStrategyId strategy)
    {
        var entries = preferences.Load().AdaptiveStrategyAppearances;
        if (entries is not null && entries.TryGetValue(strategy.ToString(), out var appearance) && appearance is not null)
        {
            try { appearance.Validate(); return appearance; } catch (ArgumentException) { }
        }
        return AdaptiveStrategyAppearance.Default(strategy);
    }

    private void RenderAppearances()
    {
        void Render(AdaptiveStrategyId strategy, TextBlock name, TextBlock note, Image icon)
        {
            var appearance = strategy == selection.Editing ? draftAppearance : LoadAppearance(strategy);
            name.Text = appearance.Name;
            note.Text = appearance.Note;
            icon.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Automation/{appearance.Icon}.png"));
            ToolTipService.SetToolTip(name, appearance.Name);
            ToolTipService.SetToolTip(note, appearance.Note);
        }
        Render(AdaptiveStrategyId.QuietFirst, QuietStrategyName, QuietStrategyNote, QuietStrategyIcon);
        Render(AdaptiveStrategyId.BalancedAdaptive, BalancedStrategyName, BalancedStrategyNote, BalancedStrategyIcon);
        Render(AdaptiveStrategyId.ResponseFirst, ResponseStrategyName, ResponseStrategyNote, ResponseStrategyIcon);
    }

    private async void OnEditAppearance(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AdaptiveStrategyId>(tag, out var strategy)) return;
        if (strategy != selection.Editing) await ChooseAsync(strategy);
        if (strategy != selection.Editing) return;
        var name = new TextBox { Header = "策略昵称", Text = draftAppearance.Name, MaxLength = 12 };
        var note = new TextBox { Header = "自定义备注", Text = draftAppearance.Note, MaxLength = 60, TextWrapping = TextWrapping.Wrap };
        var iconList = new ComboBox { Header = "同风格图标", HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] labels = ["叶片", "天平", "闪电", "公文包", "手柄", "仪表", "处理器", "显卡", "温度", "规则"];
        for (var index = 0; index < AdaptiveStrategyAppearance.Icons.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new Image { Width = 28, Height = 28, Source = new BitmapImage(new Uri($"ms-appx:///Assets/Automation/{AdaptiveStrategyAppearance.Icons[index]}.png")) });
            row.Children.Add(new TextBlock { Text = labels[index], VerticalAlignment = VerticalAlignment.Center });
            iconList.Items.Add(new ComboBoxItem { Content = row });
        }
        iconList.SelectedIndex = Array.IndexOf(AdaptiveStrategyAppearance.Icons, draftAppearance.Icon);
        var reset = new Button { Content = "恢复默认名称、备注与图标" };
        reset.Click += (_, _) => { var value = AdaptiveStrategyAppearance.Default(strategy); name.Text = value.Name; note.Text = value.Note; iconList.SelectedIndex = Array.IndexOf(AdaptiveStrategyAppearance.Icons, value.Icon); };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 16 };
        foreach (var control in new UIElement[] { name, note, iconList, reset, error }) panel.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "编辑策略外观", Content = panel, PrimaryButtonText = "加入方案草稿", CloseButtonText = "取消" };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var appearance = new AdaptiveStrategyAppearance(name.Text.Trim(), note.Text.Trim(), AdaptiveStrategyAppearance.Icons[Math.Max(0, iconList.SelectedIndex)]);
                appearance.Validate(); draftAppearance = appearance; RenderSelection();
            }
            catch (ArgumentException exception) { error.Text = exception.Message; args.Cancel = true; }
        };
        await dialog.ShowAsync();
    }

    private static int ReadInteger(NumberBox box)
    {
        if (!double.IsFinite(box.Value) || box.Value != Math.Truncate(box.Value) || box.Value < box.Minimum || box.Value > box.Maximum)
            throw new ArgumentException($"{box.Header}：请输入范围内的整数。");
        return (int)box.Value;
    }

    private static NumberBox AdvancedNumber(string label, int value, int min, int max) =>
        new() { Header = label, Value = value, Minimum = min, Maximum = max, Width = 185, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    private static StackPanel Pair(UIElement left, UIElement right)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(left); row.Children.Add(right); return row;
    }

    private static Expander Section(string title, StackPanel body) => new()
    { Header = title, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private (UIElement Content, Func<AdaptiveAdvancedSettings> Read) CreateAdvancedEditor(Func<AdaptiveTriggerPolicy> readLoadPolicy)
    {
        var settings = draftPolicy.Advanced;
        var root = new StackPanel { Spacing = 12 };
        var low = AdvancedNumber("电量保护 ≤ %", settings.LowBatteryPercent, 5, 80);
        var recovery = AdvancedNumber("解除保护 ≥ %", settings.BatteryRecoveryPercent, 6, 100);
        var saver = new CheckBox { Content = "电池供电时遵循系统节能模式", IsChecked = settings.RespectBatterySaver };
        var power = new StackPanel { Spacing = 12 }; power.Children.Add(Pair(low, recovery)); power.Children.Add(saver);
        root.Children.Add(Section("电源与电量", power));
        var cpuHeat = AdvancedNumber("CPU 禁止升档 ≥ °C", settings.CpuTemperatureCeiling, 50, 95);
        var gpuHeat = AdvancedNumber("GPU 禁止升档 ≥ °C", settings.GpuTemperatureCeiling, 45, 87);
        var cooldown = AdvancedNumber("切换冷却 / 秒", settings.CooldownSeconds, 1, 600);
        var dwell = AdvancedNumber("降档最短驻留 / 秒", settings.MinimumDwellSeconds, 1, 3600);
        var idle = AdvancedNumber("无输入时长 / 秒", settings.IdleSeconds, 30, 3600);
        var appSeconds = AdvancedNumber("应用连续命中 / 秒", settings.ApplicationSeconds, 1, 300);
        var idleEnabled = new CheckBox { Content = "回办公还需满足空闲时长（仍需低负载）", IsChecked = settings.IdleReturnEnabled };
        var protection = new StackPanel { Spacing = 12 };
        protection.Children.Add(new TextBlock { Text = "候选软件保护阈值，不修改 BIOS 温度墙、降压或限频。", TextWrapping = TextWrapping.Wrap });
        protection.Children.Add(Pair(cpuHeat, gpuHeat)); protection.Children.Add(Pair(cooldown, dwell)); protection.Children.Add(Pair(idle, appSeconds)); protection.Children.Add(idleEnabled);
        root.Children.Add(Section("温度、时间与空闲约束", protection));

        var apps = new StackPanel { Spacing = 10 };
        var rows = new List<(TextBox Exe, ComboBox Match, ComboBox Target)>();
        var appRows = new StackPanel { Spacing = 12 };
        var add = new Button { Content = "添加应用规则（最多8条）" };
        void AddRule(AdaptiveApplicationRule rule)
        {
            var exe = new TextBox { Header = "进程名（不启动应用）", Text = rule.Executable, PlaceholderText = "例如 ffmpeg.exe", MaxLength = 120 };
            var match = new ComboBox { Header = "生效范围", Width = 150, ItemsSource = new[] { "运行中（含后台）", "仅前台" }, SelectedIndex = rule.ForegroundOnly ? 1 : 0 };
            var target = new ComboBox { Header = "目标场景", Width = 120, ItemsSource = new[] { "办公", "游戏", "狂飙" }, SelectedIndex = (int)rule.Target };
            var delete = new Button { Content = "删除", VerticalAlignment = VerticalAlignment.Bottom };
            var line = Pair(match, target); line.Children.Add(delete);
            var group = new StackPanel { Spacing = 8 }; group.Children.Add(exe); group.Children.Add(line);
            var entry = (exe, match, target); rows.Add(entry); appRows.Children.Add(group);
            delete.Click += (_, _) => { rows.Remove(entry); appRows.Children.Remove(group); add.IsEnabled = rows.Count < 8; };
            add.IsEnabled = rows.Count < 8;
        }
        foreach (var rule in settings.ApplicationRules) AddRule(rule);
        add.Click += (_, _) => { if (rows.Count < 8) AddRule(new("", false, AdaptiveStage.Game)); };
        apps.Children.Add(new TextBlock { Text = "按准确进程名匹配；视频压缩请选运行中。多条冲突按列表顺序，电池与热保护优先。", TextWrapping = TextWrapping.Wrap });
        apps.Children.Add(appRows); apps.Children.Add(add); root.Children.Add(Section("应用规则", apps));
        AdaptiveAdvancedSettings Read()
        {
            var value = new AdaptiveAdvancedSettings
            {
                LowBatteryPercent = ReadInteger(low), BatteryRecoveryPercent = ReadInteger(recovery), RespectBatterySaver = saver.IsChecked == true,
                CpuTemperatureCeiling = ReadInteger(cpuHeat), GpuTemperatureCeiling = ReadInteger(gpuHeat), CooldownSeconds = ReadInteger(cooldown),
                MinimumDwellSeconds = ReadInteger(dwell), ApplicationSeconds = ReadInteger(appSeconds), IdleSeconds = ReadInteger(idle), IdleReturnEnabled = idleEnabled.IsChecked == true,
                ApplicationRules = rows.Select(r => new AdaptiveApplicationRule(r.Exe.Text.Trim(), r.Match.SelectedIndex == 1, (AdaptiveStage)r.Target.SelectedIndex)).ToArray()
            };
            value.Validate(); return value;
        }

        var trial = new StackPanel { Spacing = 12 };
        trial.Children.Add(new TextBlock { Text = "手动场景试算，不执行硬件切换。读取本机只填有效即时读数；连续时长须手动指定，不把快照当持续历史。", TextWrapping = TextWrapping.Wrap });
        var cpu = AdvancedNumber("CPU %", 50, 0, 100); var gpu = AdvancedNumber("GPU %", 40, 0, 100);
        var cpuTemp = AdvancedNumber("CPU °C", 70, 0, 110); var gpuTemp = AdvancedNumber("GPU °C", 60, 0, 110);
        var battery = AdvancedNumber("电量 %", 80, 0, 100); var seconds = AdvancedNumber("条件已持续 / 秒", 0, 0, 3600);
        var since = AdvancedNumber("距上次切换 / 秒", 0, 0, 3600); var idleTime = AdvancedNumber("无输入 / 秒", 0, 0, 3600);
        var ac = new CheckBox { Content = "交流电供电", IsChecked = true, IsThreeState = true };
        var saving = new CheckBox { Content = "系统节能已开启" };
        var latch = new CheckBox { Content = "此前已触发低电量保护" };
        var foreground = new CheckBox { Content = "该应用当前位于前台", IsChecked = true };
        var process = new TextBox { Header = "试算应用进程名", MaxLength = 120 };
        var current = new ComboBox { Header = "当前场景", ItemsSource = new[] { "办公", "游戏", "狂飙" }, SelectedIndex = 0 };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var readLive = new Button { Content = "读取本机即时数值" };
        readLive.Click += (_, _) =>
        {
            var snapshot = lastTelemetry;
            var age = snapshot is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - snapshot.CapturedAtUtc;
            var fresh = age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(10);
            cpu.Value = fresh ? snapshot!.CpuUsagePercent ?? double.NaN : double.NaN;
            gpu.Value = fresh ? snapshot!.GpuUsagePercent ?? double.NaN : double.NaN;
            cpuTemp.Value = fresh ? snapshot!.CpuTemperatureC ?? double.NaN : double.NaN;
            gpuTemp.Value = fresh ? snapshot!.GpuTemperatureC ?? double.NaN : double.NaN;
            battery.Value = fresh ? snapshot!.BatteryPercent ?? double.NaN : double.NaN;
            ac.IsChecked = fresh ? snapshot!.AcPowerConnected : null;
            seconds.Value = 0; since.Value = 0;
            result.Text = "只读取负载、温度、电量与供电；应用、节能、历史时长仍为手动模拟。空白表示无有效读数。";
        };
        var run = new Button { Content = "试算当前草稿" };
        run.Click += (_, _) =>
        {
            try
            {
                var policy = readLoadPolicy() with { Advanced = Read() };
                double? NullableValue(NumberBox box) => double.IsFinite(box.Value) ? box.Value : null;
                var input = new AdaptiveTrialInput
                {
                    CpuPercent = NullableValue(cpu), GpuPercent = NullableValue(gpu), CpuTemperature = NullableValue(cpuTemp), GpuTemperature = NullableValue(gpuTemp),
                    BatteryPercent = double.IsFinite(battery.Value) ? ReadInteger(battery) : null, AcConnected = ac.IsChecked, BatterySaver = saving.IsChecked == true,
                    LowBatteryLatched = latch.IsChecked == true, Executable = process.Text, Foreground = foreground.IsChecked == true,
                    ConditionSeconds = ReadInteger(seconds), SinceSwitchSeconds = ReadInteger(since), IdleSeconds = ReadInteger(idleTime), Current = (AdaptiveStage)current.SelectedIndex
                };
                var verdict = AdaptiveRuleTrial.Evaluate(policy, input);
                result.Text = (verdict.Target is { } stage ? $"候选：{new[] { "办公", "游戏", "狂飙" }[(int)stage]}\n" : "保持 / 等待\n") + verdict.Reason;
            }
            catch (ArgumentException exception) { result.Text = exception.Message; }
            result.UpdateLayout();
            result.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 1, AnimationDesired = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled });
        };
        foreach (var element in new UIElement[] { Pair(cpu, gpu), Pair(cpuTemp, gpuTemp), Pair(battery, seconds), Pair(since, idleTime), ac, saving, latch, process, foreground, current, Pair(readLive, run), result }) trial.Children.Add(element);
        root.Children.Add(Section("场景试算与命中原因", trial));
        return (root, Read);
    }
}
