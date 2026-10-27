using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

internal static class FanArchivedRecoveryFixture
{
    internal static async Task<FanEcRecoverySnapshot> SeedAsync(string root, FanEcRecoveryScope current, string fault = "none")
    {
        var capture = (await new ClientControlSessionStore(root).ReadAsync(CancellationToken.None))!;
        var scope = current with { ServiceSessionId = Guid.NewGuid(), ClientSessionId = Guid.NewGuid() };
        var registers = new Dictionary<ushort, byte> { [0x1060] = 0, [0x0B20] = 0, [0xC83C] = 0, [0xC83D] = 0 };
        var ec = new FanEcRecoverySnapshot(scope, new(new(registers), new(registers), new(), fault != "pending"));
        var saved = await new ControlExecutionJournal(root).ReadAsync(CancellationToken.None);
        await new AtomicJsonStore(root, "control-execution.json").WriteAsync(saved with { FanEcRecovery = ec }, CancellationToken.None);
        if (fault != "missing")
        {
            var prior = capture with { ServiceSessionId = scope.ServiceSessionId,
                Handle = capture.Handle with { SessionId = scope.ClientSessionId }, Phase = ClientControlSessionPhase.Completed };
            await new AtomicJsonStore(Path.Combine(root, "completed-client-sessions"), scope.ClientSessionId.ToString("N") + ".json")
                .WriteAsync(new { Session = prior, Recovery = new { SessionId = scope.ClientSessionId, GpuRecoveryReady = true,
                    Steps = new[] { new { Status = "Verified" } } }, FanEcRecovery = fault == "mismatch" ? ec with
                    { Scope = scope with { OwnerSid = "other-owner" } } : ec }, CancellationToken.None);
        }
        return ec;
    }
}
