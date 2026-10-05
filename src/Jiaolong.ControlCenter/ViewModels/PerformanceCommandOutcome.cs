using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;

namespace Jiaolong_ControlCenter.ViewModels;

public static class PerformanceCommandOutcome
{
    public static string Describe(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.State switch
        {
            CommandState.Applied when result.Error is null && result.VerifiedState is not null
                => "已应用并完成硬件回读",
            CommandState.Applied => "服务返回应用，但未收到硬件回读，未确认设置已生效",
            CommandState.Rejected when result.Error?.Code == ErrorCode.CapabilityUnavailable
                => "未应用：当前设备未验证该硬件能力",
            CommandState.Rejected => "未应用：服务拒绝了这次设置",
            CommandState.RolledBack => "应用失败，已回滚",
            CommandState.RecoveryRequired => "应用失败，需要恢复硬件状态",
            _ => "设置未完成，未应用"
        };
    }
}
