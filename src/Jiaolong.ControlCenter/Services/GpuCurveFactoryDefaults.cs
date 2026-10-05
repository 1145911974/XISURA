using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed record GpuCurveRestoreResult(bool Succeeded, string Message);

public static class GpuCurveFactoryDefaults
{
    public static async Task<GpuCurveRestoreResult> RestoreAsync(GpuVfState before,
        Func<HardwareCommand, Task<CommandResult>> execute, Func<GpuVfState?> readState)
    {
        if (before is not { Nodes.Length: 127, Error: null, CoreOffsetKhz: int originalCore })
            return new(false, "缺少完整驱动读回，无法恢复默认曲线");

        var originalOffsets = before.Nodes.Select(node => node.OffsetKhz).ToArray();
        bool coreChanged = originalCore != 0;
        // P0 changes shift the table. The expected table also guards an already-zero core reset.
        var expectedOffsets = originalOffsets.Select((value, index) => index == 0 ? value : checked(value - originalCore)).ToArray();
        var coreResult = await execute(new SetGpuCoreOffsetCommand(Guid.NewGuid(), originalCore, 0, true)
            { ExpectedVfOffsetsKhz = originalOffsets });
        if (!Applied(coreResult) || !Matches(readState(), 0, expectedOffsets))
            return new(false, "核心偏移恢复未确认，未继续恢复曲线");

        var curveResult = await execute(new SetGpuVfCurveCommand(Guid.NewGuid(), expectedOffsets, new int[127], true));
        if (Applied(curveResult) && Matches(readState(), 0, new int[127]))
            return new(true, "默认频率曲线已恢复 · 核心及各档位偏移均为 0 MHz");

        if (coreChanged && Matches(readState(), 0, expectedOffsets))
        {
            var recovery = await execute(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, originalCore, true)
                { ExpectedVfOffsetsKhz = expectedOffsets });
            if (Applied(recovery) && Matches(readState(), originalCore, originalOffsets))
                return new(false, "恢复默认失败，原曲线已还原");
        }
        else if (Matches(readState(), originalCore, originalOffsets))
            return new(false, "恢复默认失败，原曲线未改变");

        return new(false, "恢复默认未完成，请检查硬件状态后重试");
    }

    private static bool Applied(CommandResult result) => result.State == CommandState.Applied && result.Error is null;

    private static bool Matches(GpuVfState? state, int core, int[] offsets) =>
        state is { Nodes.Length: 127, Error: null } && state.CoreOffsetKhz == core &&
        state.Nodes.Select(node => node.OffsetKhz).SequenceEqual(offsets);
}
