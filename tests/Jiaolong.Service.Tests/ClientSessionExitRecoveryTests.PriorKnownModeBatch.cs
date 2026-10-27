using System.Text.Json;
using System.Text.Json.Nodes;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Known_mode_mixed_cpu_batch_restores_and_refuses_owner_or_target_tampering_without_writes()
    {
        foreach (string fault in new[] { "none", "owner", "target" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            fixture.Provider.JournalEveryModeStep = true;
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key,
                "known mode CO and Windows", JsonSerializer.SerializeToElement(new
                {
                    // Required preset references remain Keep; these are not hardware observations or writes.
                    temperatureLimitC = 85,
                    splWatts = 55,
                    spptWatts = 65,
                    acMaxFrequencyMhz = 4600,
                    dcMaxFrequencyMhz = 4500,
                    isBoostEnabled = false,
                    windowsPowerSchemeId = Fixture.Scheme,
                    acMinActiveCoresPercent = (int?)null,
                    dcMinActiveCoresPercent = (int?)null,
                    negativeCurveOptimizer = -5,
                    advancedCpuTuning = (AdvancedCpuTuningPlan?)null
                }), DateTimeOffset.UtcNow)
            {
                Revision = 1,
                Settings = new()
                {
                    ["acMaxFrequencyMhz"] = SettingRequest.Set(4600),
                    ["dcMaxFrequencyMhz"] = SettingRequest.Set(4500),
                    ["isBoostEnabled"] = SettingRequest.Set(false),
                    ["negativeCurveOptimizer"] = SettingRequest.Set(-5)
                }
            };
            Assert.IsTrue(PresetValidation.Validate(preset).IsValid, string.Join(";", PresetValidation.Validate(preset).Errors));
            Assert.AreEqual(CommandState.Applied,
                (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), handle)).State);

            fixture.Provider.InterruptCpuBatchAfterPlans = 1;
            Guid parent = Guid.NewGuid();
            var request = new ApplyControlRequestCommand(parent, new(parent, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Performance, true, key, 1)]));
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Write(request, handle)).State, fault);

            var seeded = await fixture.Journal.ReadAsync(CancellationToken.None);
            var pending = seeded.Unresolved.Single();
            Assert.AreEqual(parent, pending.OperationId);
            Assert.AreEqual(nameof(ApplyControlRequestCommand), pending.CommandKind);
            Assert.IsNotNull(pending.NativeModeAttempt, "The mode write must be recorded by the real producer path.");
            Assert.AreEqual(parent, pending.NativeModeAttempt!.StepId);
            Assert.AreEqual(handle.SessionId, pending.NativeModeAttempt.ClientSessionId);
            Assert.AreEqual(fixture.Owner.UserSid, pending.NativeModeAttempt.OwnerSid);
            Assert.AreEqual(PerformanceMode.Turbo, pending.NativeModeAttempt.Before);
            Assert.AreEqual(PerformanceMode.Balanced, pending.NativeModeAttempt.Target);
            Assert.AreEqual(PerformanceMode.Balanced, pending.NativeModeAttempt.Observed);
            Assert.AreEqual(PerformanceMode.Balanced, seeded.Current!.NativeMode);
            Assert.AreEqual(ApplyState.Confirmed, seeded.Current.NativeModeApplyState);
            Assert.AreEqual(handle.SessionId, seeded.Current.NativeModeOwnerClientSessionId);
            Assert.IsNull(seeded.Current.CpuWriteBatch!.NativeMode);
            var plan = seeded.Current.CpuWriteBatch.Plans.Single();
            Assert.AreEqual(4600, plan.AcMaxFrequencyMhz);
            Assert.AreEqual(4500, plan.DcMaxFrequencyMhz);
            Assert.AreEqual(-5, plan.NegativeCurveOptimizer);
            Assert.IsNull(plan.TemperatureLimitC);
            Assert.IsNull(plan.SplWatts);
            Assert.IsNull(plan.SpptWatts);
            Assert.IsNull(plan.Advanced?.PboScalar);

            if (fault != "none")
            {
                string journalPath = Path.Combine(fixture.Root, "control-execution.json");
                var node = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!;
                var savedPending = node["unresolved"]!.AsArray().Single()!;
                var mode = savedPending["nativeModeAttempt"]!;
                if (fault == "owner")
                {
                    const string foreignOwner = "S-1-5-21-999";
                    savedPending["ownerSid"] = foreignOwner;
                    mode["ownerSid"] = foreignOwner;
                }
                else mode["target"] = (int)PerformanceMode.Quiet;
                await File.WriteAllTextAsync(journalPath, node.ToJsonString());
            }

            int writes = fixture.Provider.WriteCount;
            string journalBeforeRecovery = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "control-execution.json"));
            var result = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
                new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
            if (fault == "none")
            {
                Assert.AreEqual(CommandState.Applied, result!.State, result.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
                Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
                Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
                Assert.AreEqual(4700, fixture.Provider.Cpu.DcFrequency());
                Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
                var final = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
                Assert.AreEqual(0, final.Unresolved.Length);
                Assert.AreEqual(0, final.Current!.Fields.Count);
                Assert.AreEqual(ClientControlSessionPhase.Completed,
                    (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
            }
            else
            {
                Assert.AreEqual(CommandState.RecoveryRequired, result!.State, fault);
                Assert.AreEqual(writes, fixture.Provider.WriteCount, fault);
                Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")), fault);
                Assert.AreEqual(journalBeforeRecovery,
                    await File.ReadAllTextAsync(Path.Combine(fixture.Root, "control-execution.json")), fault);
            }
        }
    }
}
