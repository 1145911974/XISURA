using Jiaolong.Contracts.Commands;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class GpuVfCommandTests
{
    [TestMethod]
    public void Curve_command_requires_confirmed_complete_bounded_table()
    {
        var offsets = new int[127];
        offsets[50] = 90_000;
        var expected = new int[127];
        Assert.IsNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected, offsets, true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected, offsets, false)));
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected, offsets[..126], true)));
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected[..126], offsets, true)));
        offsets[50] = 200_001;
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected, offsets, true)));
        var core = new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 0, true) { ExpectedVfOffsetsKhz = expected };
        Assert.IsNull(CommandValidation.Validate(core));
        Assert.IsNotNull(CommandValidation.Validate(core with { ExpectedVfOffsetsKhz = expected[..126] }));
        var invalidExpected = new int[127];
        invalidExpected[0] = 1;
        Assert.IsNotNull(CommandValidation.Validate(core with { ExpectedVfOffsetsKhz = invalidExpected }));
        offsets[50] = 0;
        offsets[0] = 1;
        Assert.IsNotNull(CommandValidation.Validate(new SetGpuVfCurveCommand(Guid.NewGuid(), expected, offsets, true)));
    }
}
