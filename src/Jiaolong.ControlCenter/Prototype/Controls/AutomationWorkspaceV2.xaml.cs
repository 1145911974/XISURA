using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Commands;
using Jiaolong_ControlCenter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AutomationWorkspaceV2 : UserControl
{
    private readonly UserPreferencesStore preferences = new();
    private readonly AdaptiveStrategySelection selection;
    private readonly AdaptiveRuleSession runtimeSession = new();
    private AdaptivePresetExecutor? presetExecutor;
    private AdaptiveTargetMap savedMap;
    private AdaptiveTargetMap draftMap;
    private AdaptiveTargetMap activeMap;
    private AdaptiveTriggerPolicy savedPolicy;
    private AdaptiveTriggerPolicy draftPolicy;
    private AdaptiveTriggerPolicy activePolicy = AdaptiveTriggerPolicy.Recommended(AdaptiveStrategyId.BalancedAdaptive);
    private AdaptiveStage? currentStage;
    private AdaptivePowerSource mapPower = AdaptivePowerSource.Ac;
    private AdaptiveStage mapStage = AdaptiveStage.Office;
    private bool loadingMap;
    private readonly Dictionary<FrameworkElement, (Storyboard Animation, Action Finish)> surfaceAnimations = [];
    private bool rulesOpen;
    private bool decisionOpen;
    private bool mapEditorOpen;
    private bool powerTabChosen;
    private HardwareSnapshot? lastTelemetry;
    private readonly DispatcherTimer staleTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool autoEnabled;
    private bool serviceReady;
    private CancellationTokenSource? pendingApplyCancellation;
    private (PresetKey Key, AdaptiveStage Stage)? automaticStageOverride;
    private string? automaticStatus;
    private DateTimeOffset? manualOverrideUntilUtc;

    public AutomationWorkspaceV2()
    {
        InitializeComponent();
        selection = new AdaptiveStrategySelection(preferences.Load().ActiveAdaptiveStrategy);
        activeMap = LoadMap(selection.Active);
        activePolicy = LoadPolicy(selection.Active);
        savedMap = LoadMap(selection.Editing);
        draftMap = savedMap;
        savedPolicy = LoadPolicy(selection.Editing);
        draftPolicy = savedPolicy;
        savedAppearance = draftAppearance = LoadAppearance(selection.Editing);
        TargetPresetPicker.SetSlotSummary("自动触发时应用已保存槽位，并检查服务回执");
        TargetPresetPicker.SelectedKeyChanged += OnTargetKeyChanged;
        staleTimer.Tick += (_, _) => RefreshTelemetry();
        Loaded += (_, _) => staleTimer.Start();
        Unloaded += (_, _) =>
        {
            pendingApplyCancellation?.Cancel();
            StopLoadMotion();
            staleTimer.Stop();
            foreach (var transition in surfaceAnimations.Values.ToArray())
            {
                transition.Animation.Stop();
                transition.Finish();
            }
            surfaceAnimations.Clear();
        };
        RenderMapping();
        RenderConditions();
        RenderSelection();
    }

    public void SetAutomationEnabled(bool enabled)
    {
        if (autoEnabled == enabled) return;
        runtimeSession.Reset();
        autoEnabled = enabled;
        RequestServiceConfiguration();
        automaticStatus = null;
        if (enabled) manualOverrideUntilUtc = null;
        if (!enabled) pendingApplyCancellation?.Cancel();
        if (!enabled && presetExecutor?.IsApplying != true)
        {
            presetExecutor?.Reset();
        }
        RenderReason();
        RefreshTelemetry();
    }

    public void SetPresetApplier(Func<PresetKey, CancellationToken, Task<AdaptivePresetApplyResult>> applyPreset) =>
        presetExecutor = new AdaptivePresetExecutor(applyPreset ?? throw new ArgumentNullException(nameof(applyPreset)));

    public void ResetAutomaticModeTracking()
    {
        pendingApplyCancellation?.Cancel();
        automaticStageOverride = null;
        manualOverrideUntilUtc = DateTimeOffset.UtcNow.AddSeconds(activePolicy.Advanced.MinimumDwellSeconds);
        runtimeSession.Reset();
        automaticStatus = null;
        if (presetExecutor?.IsApplying != true)
        {
            presetExecutor?.Reset();
        }
        RefreshTelemetry();
    }

    public void ApplyTelemetry(HardwareSnapshot? snapshot)
    {
        lastTelemetry = snapshot;
        RefreshTelemetry();
    }

    private void RefreshTelemetry()
    {
        var display = AdaptiveDecisionDisplay.From(lastTelemetry, DateTimeOffset.UtcNow);
        ApplyLoadTargets(display);
        PowerText.Text = display.Power;
        if (!powerTabChosen && display.Power != "供电待确认")
        {
            var actualPower = display.Power == "DC 供电" ? AdaptivePowerSource.Dc : AdaptivePowerSource.Ac;
            if (actualPower != mapPower)
            {
                mapPower = actualPower;
                RenderMapping();
            }
        }
        if (automationService is not null)
        {
            RefreshServiceStatus();
            return;
        }
        if (autoEnabled && manualOverrideUntilUtc is { } overrideUntil)
        {
            var remaining = overrideUntil - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                runtimeSession.Reset();
                DecisionDetailsText.Text = $"手动模式优先；{Math.Ceiling(remaining.TotalSeconds):0} 秒后恢复自动判定。";
                return;
            }
            manualOverrideUntilUtc = null;
        }
        if (!autoEnabled || !serviceReady || currentStage is null || lastTelemetry is not { } snapshot
            || display.CpuUsage == "--" || display.GpuUsage == "--" || display.Power == "供电待确认")
        {
            runtimeSession.Reset();
            if (presetExecutor?.IsApplying != true)
            {
                presetExecutor?.Reset();
            }
            DecisionDetailsText.Text = !autoEnabled ? "首页未开启自动判定。"
                : !serviceReady ? "服务状态不可用，停止自动判定。"
                : currentStage is null ? "当前硬件模式不可识别，停止自动判定。"
                : "CPU、GPU 或供电遥测缺失/过期；清除连续计时，不产生候选。";
            return;
        }

        var signals = AdaptiveRuntimeSignals.Read();
        if (activePolicy.Advanced.ApplicationRules.Length > 0 && !signals.Foreground)
        {
            runtimeSession.Reset();
            if (presetExecutor?.IsApplying != true) presetExecutor?.Reset();
            automaticStatus = null;
            DecisionDetailsText.Text = "无法读取当前前台进程；应用规则未执行，也不回退到负载自动切换。";
            return;
        }
        if (activePolicy.Advanced.IdleReturnEnabled && signals.IdleSeconds is null)
        {
            runtimeSession.Reset();
            if (presetExecutor?.IsApplying != true) presetExecutor?.Reset();
            automaticStatus = null;
            DecisionDetailsText.Text = "系统空闲时间不可用；自动判定暂停，未发送命令。";
            return;
        }
        var result = runtimeSession.Evaluate(activePolicy, new AdaptiveTrialInput
        {
            CpuPercent = snapshot.CpuUsagePercent,
            GpuPercent = snapshot.GpuUsagePercent,
            CpuTemperature = snapshot.CpuTemperatureC,
            GpuTemperature = snapshot.GpuTemperatureC,
            AcConnected = snapshot.AcPowerConnected,
            BatteryPercent = snapshot.BatteryPercent,
            BatterySaver = signals.BatterySaver,
            Executable = signals.Executable,
            Foreground = signals.Foreground,
            IdleSeconds = signals.IdleSeconds.GetValueOrDefault(),
            Current = currentStage.Value
        }, snapshot.CapturedAtUtc);
        var activeName = selection.Active switch
        {
            AdaptiveStrategyId.QuietFirst => "安静优先",
            AdaptiveStrategyId.ResponseFirst => "响应优先",
            _ => "均衡自适应"
        };
        if (result.Target is { } target)
        {
            var livePower = display.Power == "DC 供电" ? AdaptivePowerSource.Dc : AdaptivePowerSource.Ac;
            var targetKey = activeMap.GetTarget(livePower, target);
            if (presetExecutor is null)
            {
                automaticStatus = "自动目标没有连接到硬件应用器。";
            }
            else
            {
                var applyCancellation = new CancellationTokenSource();
                if (presetExecutor.TryStartApply(targetKey, applyCancellation.Token, out var completion))
                {
                    pendingApplyCancellation = applyCancellation;
                    automaticStatus = $"正在提交目标预设：{TargetName(targetKey, preferences.Load().PresetNames)}。";
                    _ = ObserveAutomaticApplyAsync(targetKey, target, completion, applyCancellation);
                }
                else
                {
                    applyCancellation.Dispose();
                    if (presetExecutor.IsApplying) automaticStatus = "等待当前目标的服务回执。";
                }
            }
        }
        else if (presetExecutor?.IsApplying != true)
        {
            presetExecutor?.Reset();
        }

        var details = result.Target is { } candidate
            ? $"实时遥测已满足条件：{StageName(candidate)} 候选（{result.Reason}）。使用方案：{activeName}。"
            : $"{result.Reason}\n使用已保存的{activeName}策略持续判定。";
        DecisionDetailsText.Text = string.IsNullOrWhiteSpace(automaticStatus) ? details : $"{details}\n{automaticStatus}";
    }

    private async Task ObserveAutomaticApplyAsync(
        PresetKey key,
        AdaptiveStage stage,
        Task<AdaptivePresetApplyResult?> completion,
        CancellationTokenSource cancellationSource)
    {
        try
        {
            AdaptivePresetApplyResult? application = await completion;
            if (application is null)
            {
                automaticStatus = "目标预设没有获得服务回执。";
            }
            else if (application.Command.State == CommandState.Applied && application.Command.Error is null)
            {
                automaticStageOverride = (key, stage);
                currentStage = stage;
                automaticStatus = application.IsPartial
                    ? $"自动目标部分应用：{application.PartialReason}。方案：{TargetName(key, preferences.Load().PresetNames)}。"
                    : $"已应用目标预设：{TargetName(key, preferences.Load().PresetNames)}；服务回执 Applied。";
            }
            else
            {
                var result = application.Command;
                automaticStatus = $"目标预设未应用：{result.State}{(result.Error is null ? "" : $"（{result.Error.Code}）")}。";
            }
        }
        catch (OperationCanceledException)
        {
            presetExecutor?.Reset();
            automaticStatus = "自动应用已被后续操作取消，等待重新判定。";
        }
        catch (Exception exception)
        {
            automaticStatus = $"目标预设应用失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(pendingApplyCancellation, cancellationSource))
            {
                pendingApplyCancellation.Dispose();
                pendingApplyCancellation = null;
            }
            if (!autoEnabled) presetExecutor?.Reset();
            RefreshTelemetry();
        }
    }

    public void ApplyState(HomeStateSnapshot snapshot)
    {
        serviceAutomationStatus = snapshot.Controls.AdaptiveAutomation;
        RefreshServiceConnection();
        serviceReady = snapshot.Capabilities.SupportState == DeviceSupportState.Ready;
        var mode = snapshot.Controls.PerformanceMode;
        if (automaticStageOverride is { } applied && AdaptiveTargetMap.PerformanceModeFor(applied.Key) == mode)
            currentStage = applied.Stage;
        else
        {
            automaticStageOverride = null;
            currentStage = serviceReady ? mode switch
            {
                PerformanceMode.Quiet => AdaptiveStage.Office,
                PerformanceMode.Balanced => AdaptiveStage.Game,
                PerformanceMode.Turbo => AdaptiveStage.Turbo,
                _ => null
            } : null;
        }
        CurrentModeText.Text = serviceReady ? snapshot.Controls.PerformanceMode switch
        {
            PerformanceMode.Quiet => "办公",
            PerformanceMode.Balanced => "游戏",
            PerformanceMode.Turbo => "狂飙",
            PerformanceMode.Custom => "自定义",
            _ => "--"
        } : "--";
        ApplyTelemetry(snapshot.Telemetry);
        RenderReason();
    }

    private void RenderReason()
    {
        if (automationService is not null)
        {
            RefreshServiceStatus();
            return;
        }
        DecisionReasonText.Text = !autoEnabled
        ? "首页未开启自动"
        : !serviceReady
            ? "服务状态不可用，停止自动判定"
            : "根据实时遥测连续判定；稳定目标经服务校验后应用保存预设";
    }

    private static string StageName(AdaptiveStage stage) => stage switch
    {
        AdaptiveStage.Office => "办公",
        AdaptiveStage.Game => "游戏",
        _ => "狂飙"
    };

    private void OnDecisionDetailsClick(object sender, RoutedEventArgs e)
    {
        decisionOpen = !decisionOpen;
        AnimateDisclosure(DecisionSurface, DecisionDetailsText, decisionOpen);
    }

    private async void OnChooseQuiet(object sender, RoutedEventArgs e) => await ChooseAsync(AdaptiveStrategyId.QuietFirst);
    private async void OnChooseBalanced(object sender, RoutedEventArgs e) => await ChooseAsync(AdaptiveStrategyId.BalancedAdaptive);
    private async void OnChooseResponse(object sender, RoutedEventArgs e) => await ChooseAsync(AdaptiveStrategyId.ResponseFirst);

    private async Task ChooseAsync(AdaptiveStrategyId strategy)
    {
        if (strategy == selection.Editing) return;
        if (HasUnsavedChanges)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "切换调度策略？",
                Content = "当前方案的目标映射或候选条件尚未保存。",
                PrimaryButtonText = "保存后切换",
                SecondaryButtonText = "放弃更改",
                CloseButtonText = "取消"
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None || result == ContentDialogResult.Primary && !SavePlan()) return;
        }
        selection.Choose(strategy);
        savedMap = LoadMap(strategy);
        draftMap = savedMap;
        savedPolicy = LoadPolicy(strategy);
        draftPolicy = savedPolicy;
        savedAppearance = draftAppearance = LoadAppearance(strategy);
        SetMapEditorVisible(false, false);
        RenderMapping();
        RenderConditions();
        ReportStatus("已切换编辑对象；正在使用的方案不变。");
        RenderSelection();
    }

    private async void OnUseStrategy(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges)
        {
            ReportStatus("请先保存目标映射和候选条件，再使用此方案。");
            return;
        }
        await SelectSavedStrategyAsync(selection.Editing);
    }

    private void RenderSelection()
    {
        RenderAppearances();
        SetCard(QuietCard, QuietBadge, QuietRadioRing, QuietRadioDot, AdaptiveStrategyId.QuietFirst);
        SetCard(BalancedCard, BalancedBadge, BalancedRadioRing, BalancedRadioDot, AdaptiveStrategyId.BalancedAdaptive);
        SetCard(ResponseCard, ResponseBadge, ResponseRadioRing, ResponseRadioDot, AdaptiveStrategyId.ResponseFirst);
        UseStrategyButton.Visibility = selection.Editing == selection.Active ? Visibility.Collapsed : Visibility.Visible;
        UseStrategyButton.IsEnabled = !HasUnsavedChanges && !servicePublishBusy;
    }

    private bool HasUnsavedChanges => draftMap != savedMap || draftPolicy != savedPolicy || draftAppearance != savedAppearance;

    private void ReportStatus(string message)
    {
        StrategyStatusText.Text = message;
        StrategyStatusText.Visibility = Visibility.Visible;
    }

    private void SetCard(Button card, TextBlock badge, Ellipse radioRing, Ellipse radioDot, AdaptiveStrategyId strategy)
    {
        var editing = selection.Editing == strategy;
        var active = selection.Active == strategy;
        card.BorderBrush = (Brush)Application.Current.Resources[editing ? "ModeAccentBrush" : "PrototypeStrokeBrush"];
        card.Background = (Brush)Application.Current.Resources[editing ? "ModeSelectionBrush" : "PrototypeInsetCardBrush"];
        card.BorderThickness = new Thickness(editing ? 2 : 1);
        radioRing.Stroke = (Brush)Application.Current.Resources[editing ? "ModeAccentBrush" : "PrototypeSecondaryTextBrush"];
        radioDot.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        badge.Text = strategy == AdaptiveStrategyId.BalancedAdaptive ? "推荐" : active && !editing ? "使用中" : "";
    }

    private AdaptiveTargetMap LoadMap(AdaptiveStrategyId strategy)
    {
        var stored = preferences.Load().AdaptiveTargetMaps;
        if (stored is null || !stored.TryGetValue(strategy.ToString(), out var map) || map is null)
            return AdaptiveTargetMap.Recommended;
        try
        {
            map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Office, map.AcOffice);
            map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Game, map.AcGame);
            map.WithTarget(AdaptivePowerSource.Ac, AdaptiveStage.Turbo, map.AcTurbo);
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Office, map.DcOffice);
            map.WithTarget(AdaptivePowerSource.Dc, AdaptiveStage.Game, map.DcGame);
            return map;
        }
        catch (ArgumentOutOfRangeException)
        {
            return AdaptiveTargetMap.Recommended;
        }
    }

    private AdaptiveTriggerPolicy LoadPolicy(AdaptiveStrategyId strategy)
    {
        var stored = preferences.Load().AdaptiveTriggerPolicies;
        if (stored is null || !stored.TryGetValue(strategy.ToString(), out var policy) || policy is null)
            return AdaptiveTriggerPolicy.Recommended(strategy);
        try
        {
            policy.Validate();
            return policy;
        }
        catch (ArgumentException)
        {
            return AdaptiveTriggerPolicy.Recommended(strategy);
        }
    }

    private void RenderConditions()
    {
        var advanced = draftPolicy.Advanced;
        RulesDetailsText.Text = $"应用规则 {advanced.ApplicationRules.Length} 条 · 电量保护 ≤ {advanced.LowBatteryPercent}% / 恢复 ≥ {advanced.BatteryRecoveryPercent}%\n"
            + $"温度门槛 CPU {advanced.CpuTemperatureCeiling}°C / GPU {advanced.GpuTemperatureCeiling}°C · 冷却 {advanced.CooldownSeconds} 秒 · 驻留 {advanced.MinimumDwellSeconds} 秒\n"
            + "启用后由系统服务持续判定；前台应用和空闲规则需要客户端在线。";
        GameConditionText.Text = $"CPU ≥ {draftPolicy.GameCpuPercent}% 或 GPU ≥ {draftPolicy.GameGpuPercent}% · 连续 {draftPolicy.GameSeconds} 秒";
        TurboConditionText.Text = draftPolicy.TurboEnabled
            ? $"CPU ≥ {draftPolicy.TurboCpuPercent}% 或 GPU ≥ {draftPolicy.TurboGpuPercent}% · 连续 {draftPolicy.TurboSeconds} 秒"
            : "此策略不自动进入狂飙";
        OfficeConditionText.Text = $"低负载 · 连续 {draftPolicy.OfficeSeconds} 秒";
        ToolTipService.SetToolTip(OfficeConditionText,
            $"CPU < {draftPolicy.OfficeCpuPercent}% 且 GPU < {draftPolicy.OfficeGpuPercent}% · 连续 {draftPolicy.OfficeSeconds} 秒");
    }

    private async void OnEditConditions(object sender, RoutedEventArgs e)
    {
        var gameCpu = CreateThresholdBox("CPU ≥ %", draftPolicy.GameCpuPercent);
        var gameGpu = CreateThresholdBox("GPU ≥ %", draftPolicy.GameGpuPercent);
        var gameSeconds = CreateThresholdBox("连续秒数", draftPolicy.GameSeconds, 3600);
        var turboCpu = CreateThresholdBox("CPU ≥ %", draftPolicy.TurboCpuPercent);
        var turboGpu = CreateThresholdBox("GPU ≥ %", draftPolicy.TurboGpuPercent);
        var turboSeconds = CreateThresholdBox("连续秒数", draftPolicy.TurboSeconds, 3600);
        var officeCpu = CreateThresholdBox("CPU < %", draftPolicy.OfficeCpuPercent);
        var officeGpu = CreateThresholdBox("GPU < %", draftPolicy.OfficeGpuPercent);
        var officeSeconds = CreateThresholdBox("连续秒数", draftPolicy.OfficeSeconds, 3600);
        var turboEnabled = new CheckBox
        {
            Content = "允许候选进入狂飙（仍受 AC 与热保护门槛约束）",
            IsChecked = draftPolicy.TurboEnabled
        };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["ModeAccentBrush"] };
        var editor = new StackPanel { Spacing = 14, Width = 460 };
        editor.Children.Add(new TextBlock
        {
            Text = "加入草稿后仍需保存方案。试算不会发送命令；启用后由系统服务执行，应用规则需要客户端在线。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["PrototypeSecondaryTextBrush"]
        });
        editor.Children.Add(CreateThresholdRow("进入游戏", gameCpu, gameGpu, gameSeconds));
        editor.Children.Add(turboEnabled);
        editor.Children.Add(CreateThresholdRow("进入狂飙", turboCpu, turboGpu, turboSeconds));
        editor.Children.Add(CreateThresholdRow("回到办公", officeCpu, officeGpu, officeSeconds));
        AdaptiveTriggerPolicy ReadLoadPolicy() => new(
            ReadInteger(gameCpu), ReadInteger(gameGpu), ReadInteger(gameSeconds), turboEnabled.IsChecked == true,
            ReadInteger(turboCpu), ReadInteger(turboGpu), ReadInteger(turboSeconds),
            ReadInteger(officeCpu), ReadInteger(officeGpu), ReadInteger(officeSeconds));
        var advanced = CreateAdvancedEditor(ReadLoadPolicy);
        editor.Children.Add(advanced.Content);
        editor.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑候选触发条件",
            Content = new ScrollViewer { Content = editor, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "加入方案草稿",
            CloseButtonText = "取消"
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var updated = ReadLoadPolicy() with { Advanced = advanced.Read() };
                updated.Validate();
                draftPolicy = updated;
                RenderConditions();
                RenderSelection();
                ReportStatus("候选条件已加入草稿；点击顶部“保存方案”后才会保存。");
            }
            catch (ArgumentException exception)
            {
                error.Text = exception.Message;
                args.Cancel = true;
            }
        };
        await dialog.ShowAsync();
    }

    private static NumberBox CreateThresholdBox(string header, int value, int maximum = 100) => new()
    {
        Header = header,
        Value = value,
        Minimum = 1,
        Maximum = maximum,
        Width = 128
    };

    private static StackPanel CreateThresholdRow(string title, NumberBox cpu, NumberBox gpu, NumberBox seconds)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(gpu, 1);
        Grid.SetColumn(seconds, 2);
        row.Children.Add(cpu);
        row.Children.Add(gpu);
        row.Children.Add(seconds);
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        section.Children.Add(row);
        return section;
    }

    private void OnToggleRuleDetails(object sender, RoutedEventArgs e)
    {
        var expanded = rulesOpen = !rulesOpen;
        AnimateDisclosure(ConditionsSurface, RulesDetailsText, expanded);
        RulesExpansionLabel.Text = expanded ? "收起" : "展开";
    }

    private void OnCloseMapEditor(object sender, RoutedEventArgs e)
    {
        SetMapEditorVisible(false);
    }

    private void SetMapEditorVisible(bool show, bool animate = true)
    {
        mapEditorOpen = show;
        AnimateDisclosure(MappingSurface, MapEditor, show, animate);
        RenderMapping();
    }

    private void OnAnimatedSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement surface)
            surface.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private void AnimateDisclosure(Border surface, FrameworkElement content, bool show, bool animate = true)
    {
        // Retarget from the current boundary, not the previous animation's destination.
        var fromHeight = surface.ActualHeight;
        var fromOpacity = content.Visibility == Visibility.Visible ? content.Opacity : 0;
        if (surfaceAnimations.Remove(surface, out var pending))
        {
            pending.Animation.Stop();
            pending.Finish();
        }
        content.IsHitTestVisible = show;
        surface.Height = double.NaN;
        if (!animate || ReducedMotion || !IsLoaded || !new UISettings().AnimationsEnabled)
        {
            content.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            content.Opacity = 1;
            return;
        }

        // Measure without temporarily collapsing the picker: its popup otherwise jumps to its origin.
        content.Visibility = Visibility.Visible;
        surface.Measure(new Windows.Foundation.Size(surface.ActualWidth, double.PositiveInfinity));
        double TargetHeight(Border border)
        {
            var panel = (StackPanel)border.Child;
            var removed = !show && panel.Children.Contains(content) ? content.DesiredSize.Height + panel.Spacing : 0;
            return Math.Max(border.MinHeight, panel.DesiredSize.Height - removed
                + border.Padding.Top + border.Padding.Bottom + border.BorderThickness.Top + border.BorderThickness.Bottom);
        }
        var targetHeight = TargetHeight(surface);
        content.Opacity = fromOpacity;
        surface.Height = fromHeight;
        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(show ? 360 : 320);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        // Animate the card itself; neighbouring cards retain their natural height.
        var height = new DoubleAnimation { From = fromHeight, To = targetHeight, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var fade = new DoubleAnimation { From = fromOpacity, To = show ? 1 : 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(height, surface);
        Storyboard.SetTargetProperty(height, "Height");
        Storyboard.SetTarget(fade, content);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(height);
        storyboard.Children.Add(fade);
        void Finish()
        {
            content.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            content.Opacity = 1;
            surface.Height = double.NaN;
        }
        storyboard.Completed += (_, _) =>
        {
            if (!surfaceAnimations.TryGetValue(surface, out var current) || current.Animation != storyboard) return;
            surfaceAnimations.Remove(surface);
            storyboard.Stop();
            Finish();
        };
        surfaceAnimations[surface] = (storyboard, Finish);
        storyboard.Begin();
    }
    private void OnSelectAc(object sender, RoutedEventArgs e) => SelectPower(AdaptivePowerSource.Ac);
    private void OnSelectDc(object sender, RoutedEventArgs e) => SelectPower(AdaptivePowerSource.Dc);

    private void SelectPower(AdaptivePowerSource power)
    {
        powerTabChosen = true;
        mapPower = power;
        if (power == AdaptivePowerSource.Dc && mapStage == AdaptiveStage.Turbo)
            mapStage = AdaptiveStage.Game;
        RenderMapping();
    }

    private void OnSelectOfficeTarget(object sender, RoutedEventArgs e) => SelectTarget(AdaptiveStage.Office);
    private void OnSelectGameTarget(object sender, RoutedEventArgs e) => SelectTarget(AdaptiveStage.Game);
    private void OnSelectTurboTarget(object sender, RoutedEventArgs e) => SelectTarget(AdaptiveStage.Turbo);

    private void SelectTarget(AdaptiveStage stage)
    {
        if (mapPower == AdaptivePowerSource.Dc && stage == AdaptiveStage.Turbo) return;
        mapStage = stage;
        SetMapEditorVisible(true);
    }

    private void OnTargetKeyChanged(object? sender, PresetKey key)
    {
        if (loadingMap || !mapEditorOpen) return;
        try
        {
            draftMap = draftMap.WithTarget(mapPower, mapStage, key);
        }
        catch (ArgumentOutOfRangeException)
        {
            RenderMapping();
            ReportStatus("电池目标不能指向狂飙或自定义预设的原生狂飙模式。");
            return;
        }
        RenderMapping();
        RenderSelection();
        StrategyStatusText.Visibility = Visibility.Collapsed;
    }

    private void RenderMapping()
    {
        var names = preferences.Load().PresetNames;
        var ac = mapPower == AdaptivePowerSource.Ac;
        TargetPresetPicker.SetAllowedModes(ac ? null : [ControlModeId.Office, ControlModeId.Gaming],
            "电池供电不可使用狂飙或自定义预设的原生狂飙模式");
        OfficeTargetText.Text = TargetName(ac ? draftMap.AcOffice : draftMap.DcOffice, names);
        GameTargetText.Text = TargetName(ac ? draftMap.AcGame : draftMap.DcGame, names);
        TurboTargetText.Text = ac ? TargetName(draftMap.AcTurbo, names) : "电池供电不可用";
        TurboTargetStatusText.Text = ac ? "按温度门槛判定" : "电池不可用";
        TurboTargetButton.IsEnabled = ac;
        var stroke = (Brush)Application.Current.Resources["PrototypeStrokeBrush"];
        var accent = (Brush)Application.Current.Resources["ModeAccentBrush"];
        AcMapButton.BorderBrush = ac ? accent : stroke;
        DcMapButton.BorderBrush = ac ? stroke : accent;
        AcMapButton.Background = (Brush)Application.Current.Resources[ac ? "ModeSelectionBrush" : "PrototypeControlAcrylicBrush"];
        DcMapButton.Background = (Brush)Application.Current.Resources[ac ? "PrototypeControlAcrylicBrush" : "ModeSelectionBrush"];
        var editing = mapEditorOpen;
        OfficeTargetButton.BorderBrush = editing && mapStage == AdaptiveStage.Office ? accent : stroke;
        GameTargetButton.BorderBrush = editing && mapStage == AdaptiveStage.Game ? accent : stroke;
        TurboTargetButton.BorderBrush = editing && mapStage == AdaptiveStage.Turbo && ac ? accent : stroke;
        ToolTipService.SetToolTip(OfficeTargetButton, OfficeTargetText.Text);
        ToolTipService.SetToolTip(GameTargetButton, GameTargetText.Text);
        ToolTipService.SetToolTip(TurboTargetButton, TurboTargetText.Text);
        MapEditorLabel.Text = $"{(ac ? "交流电" : "电池")} · {mapStage switch { AdaptiveStage.Office => "办公", AdaptiveStage.Game => "游戏", _ => "狂飙" }}";
        var key = (mapPower, mapStage) switch
        {
            (AdaptivePowerSource.Ac, AdaptiveStage.Office) => draftMap.AcOffice,
            (AdaptivePowerSource.Ac, AdaptiveStage.Game) => draftMap.AcGame,
            (AdaptivePowerSource.Ac, _) => draftMap.AcTurbo,
            (AdaptivePowerSource.Dc, AdaptiveStage.Office) => draftMap.DcOffice,
            _ => draftMap.DcGame
        };
        loadingMap = true;
        TargetPresetPicker.SelectedKey = key;
        loadingMap = false;
    }

    private static string TargetName(PresetKey key, IReadOnlyDictionary<string, string> names) =>
        $"{key.Mode switch { ControlModeId.Office => "办公", ControlModeId.Gaming => "游戏", ControlModeId.Turbo => "狂飙", ControlModeId.Custom1 => "自定义 1", ControlModeId.Custom2 => "自定义 2", _ => "自定义 3" }} · {PresetNameCatalog.GetName(names, key)}";

    private void OnSaveMap(object sender, RoutedEventArgs e) => SavePlan();

    private bool SavePlan()
    {
        if (!HasUnsavedChanges)
        {
            ReportStatus("当前方案没有未保存的更改。");
            return true;
        }
        try
        {
            draftAppearance.Validate();
            draftPolicy.Validate();
            preferences.Update(current =>
            {
                var maps = new Dictionary<string, AdaptiveTargetMap>(current.AdaptiveTargetMaps ?? []);
                var policies = new Dictionary<string, AdaptiveTriggerPolicy>(current.AdaptiveTriggerPolicies ?? []);
                var appearances = new Dictionary<string, AdaptiveStrategyAppearance>(current.AdaptiveStrategyAppearances ?? []);
                appearances[selection.Editing.ToString()] = draftAppearance;
                maps[selection.Editing.ToString()] = draftMap;
                policies[selection.Editing.ToString()] = draftPolicy;
                return current with { AdaptiveTargetMaps = maps, AdaptiveTriggerPolicies = policies, AdaptiveStrategyAppearances = appearances };
            });
            savedMap = draftMap;
            savedPolicy = draftPolicy;
            savedAppearance = draftAppearance;
            if (selection.Editing == selection.Active)
            {
                pendingApplyCancellation?.Cancel();
                activeMap = savedMap;
                activePolicy = savedPolicy;
                RequestServiceConfiguration();
                runtimeSession.Reset();
                presetExecutor?.Reset();
                automaticStatus = null;
            }
            ReportStatus("当前方案已保存到本机；不会自动切换硬件。");
            RenderSelection();
            return true;
        }
        catch (Exception exception)
        {
            ReportStatus($"保存失败：{exception.Message}");
            return false;
        }
    }

    private async void OnRestoreMap(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复当前方案的推荐值？",
            Content = "恢复目标映射与候选触发阈值；不修改性能、显卡或风扇页的预设。恢复后仍需点击“保存方案”。",
            PrimaryButtonText = "恢复推荐",
            CloseButtonText = "取消"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        draftMap = AdaptiveTargetMap.Recommended;
        draftPolicy = AdaptiveTriggerPolicy.Recommended(selection.Editing);
        RenderMapping();
        RenderConditions();
        RenderSelection();
        ReportStatus("推荐值已载入；点击“保存方案”后才会保存。");
    }
}
