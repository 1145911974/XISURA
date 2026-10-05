using Jiaolong_ControlCenter.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class GpuWorkspaceV2
{
    private void InitializeGpuHelp()
    {
        CoreFrequencyHelp.Help = new(
            "设置 GPU 核心频率上限；降低可减少峰值功耗和热量，也可能降低性能。空闲时仍由驱动降频。",
            "正在读取驱动范围",
            "优先保留驱动默认上限；需降温时在驱动可用范围内逐步下调。",
            "保持当前已验证上限，不超过本机驱动返回范围；缺少有效回读时不调整。",
            "最小、最大值来自当前驱动，不能用固定 MHz 作为所有显卡的安全上限。",
            "编辑与保存不写入；使用已保存整页预设并确认后才提交。驱动接受命令不代表受载频率或稳定性已验证；原值未知时不能自动恢复。");
        MemoryOffsetHelp.Help = new(
            "在显卡当前显存频率基础上增加或减少偏移；正值可能提高带宽，负值可能降低性能和功耗。",
            "正在读取显存偏移",
            "0 MHz 表示不增加偏移；需调节时从接近 0 的值开始验证实际负载。",
            "保留已验证当前值或使用 0 MHz，避免未经验证的正偏移。",
            "软件保护范围 -200–200 MHz，还须满足本机驱动范围；±200 MHz 不是稳定保证。",
            "编辑与保存不写入硬件；使用后观察花屏、崩溃等异常，发生异常恢复已知稳定值。");
        CoreOffsetHelp.Help = new(
            "把核心频率曲线整体上移或下移；正值可能提高性能和发热，负值可能减少功耗。它与绝对频率上限分别生效。",
            "正在读取核心偏移",
            "0 MHz 表示不增加偏移；需要调整时从接近 0 的值开始验证。",
            "保留当前已验证值或使用 0 MHz，避免一次提高整条曲线。",
            "软件保护范围 -200–200 MHz，还须满足本机驱动范围；边界值不是稳定保证。",
            "正偏移过大可能花屏、崩溃或驱动重启；负值可能降低性能。保存后使用并确认才提交。");
        OperatingPointHelp.Help = new(
            "把 GPU 最近 24 秒的核心频率与电压放在同一时间轴，帮助观察负载、降频和限制原因；圆点是最新采样。",
            "来自硬件遥测；右侧显示 P-State 与近期稳定的性能限制原因。",
            "结合实际负载、温度与功耗观察，不把瞬时尖峰当成持续性能。",
            "仅查看，不修改频率或电压；缺少读数时等待真实回读。",
            "这是只读监测，没有可设置的数值极限；频率、电压受当前驱动与负载决定。",
            "短时丢读最多保留 2 秒，过期后淡出；缺少数据时不绘制模拟曲线。");
    }

    private static void SetGpuHelpState(ParameterHelpButton button, string current)
    {
        if (button.Help.CurrentValue != current)
            button.Help = button.Help with { CurrentValue = current };
    }
}
