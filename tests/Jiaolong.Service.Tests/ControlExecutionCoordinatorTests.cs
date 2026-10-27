using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ControlExecutionCoordinatorTests
{
    [TestMethod]
    public async Task Turbo_branch_retries_failed_fan_restore_without_rewriting_restored_cpu_or_losing_original_branch()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
            provider.Mode, null, "RequestStateUnverified");
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        const string owner = "field-retry";
        await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        provider.RejectFanRestoreWithoutMutation = true;
        var first = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Console.WriteLine("First release: " + JsonSerializer.Serialize(first));
        Console.WriteLine("First commands: " + string.Join(",", commands.Select(command => command.GetType().Name)));
        Assert.AreEqual(CommandState.RecoveryRequired, first.State, "A failed Fan page must not complete the branch or restore native mode.");
        Assert.AreEqual(3200, provider.Ac);
        Assert.AreEqual(2400, provider.Dc);
        Assert.IsNull(provider.FanPlan, "The failed restore leaves the branch in EC Auto.");
        Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
        Assert.IsTrue(commands.First() is SetCpuTuningBatchCommand && commands.Last() is SetFanControlCommand);
        var field = first.ExecutionState!.Fields["fan.strategy"];
        Assert.AreEqual(branch.OperationId, field.OwnerOperationId);
        Assert.AreEqual(ApplyState.Confirmed, field.WriteState);
        Assert.IsFalse(first.ExecutionState.Fields.ContainsKey("performance.acMaxFrequencyMhz"));
        provider.RejectFanRestoreWithoutMutation = false;
        commands.Clear();
        var retried = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Console.WriteLine("Retry: " + JsonSerializer.Serialize(retried));
        Assert.AreEqual(CommandState.Applied, retried.State);
        Assert.AreEqual(4200, provider.FanPlan!.FixedRpm);
        Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
        Assert.AreEqual(2, commands.Count);
        Assert.IsTrue(commands[0] is SetFanControlCommand && commands[1] is SetPerformanceModeCommand);
        commands.Clear();
        var completed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, completed.State);
        Assert.AreEqual(HardwareMutation.NoMutation, completed.Mutation);
        Assert.AreEqual(0, commands.Count);
    }

    [TestMethod]
    public async Task Turbo_branch_retry_field_failure_keeps_unknown_or_superseded_work_unresolved()
    {
        foreach (string change in new[] { "generation", "unknown", "lost-reply", "external-fan" })
        {
            var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
            provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
                provider.Mode, null, "RequestStateUnverified");
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            const string owner = "field-guard";
            await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
            var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
            await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
            provider.RejectFanRestoreWithoutMutation = change != "lost-reply";
            if (change == "unknown") provider.RejectedFanMutation = HardwareMutation.Unknown;
            if (change == "lost-reply") provider.AfterWrite = command =>
            {
                if (command is SetFanControlCommand) throw new IOException("Fan reply lost after dispatch.");
            };
            var first = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
            Assert.AreNotEqual(CommandState.Applied, first.State, change);
            Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
            if (change == "generation") runtime.Execution.InvalidateIntent();
            if (change == "external-fan") provider.FanPlan = new FanControlPlan([]) { Strategy = "Fixed", FixedRpm = 4700 };
            provider.RejectFanRestoreWithoutMutation = false;
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var retry = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
            Console.WriteLine(change + " first: " + JsonSerializer.Serialize(first));
            Console.WriteLine(change + " retry: " + JsonSerializer.Serialize(retry));
            Console.WriteLine(change + " commands: " + string.Join(",", commands.Select(command => command.GetType().Name)));
            Assert.AreEqual(CommandState.RecoveryRequired, retry.State, change);
            Assert.AreEqual(HardwareMutation.NoMutation, retry.Mutation, change);
            Assert.AreEqual(0, commands.Count, change);
            if (change == "external-fan")
            {
                Assert.AreEqual(4700, provider.FanPlan!.FixedRpm);
                Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
                Assert.AreEqual(branch.OperationId, retry.ExecutionState!.Fields["fan.strategy"].OwnerOperationId);
            }
        }
    }

    [TestMethod]
    public async Task Turbo_branch_retries_only_native_restore_after_confirmed_fields_and_rejected_mode()
    {
        foreach (bool priorManual in new[] { false, true })
        {
            var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
            provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
                provider.Mode, null, "RequestStateUnverified");
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            const string owner = "partial-branch";
            await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
                CancellationToken.None, userSid: owner);
            var manual = new SetCpuTuningBatchCommand(Guid.NewGuid(),
                [new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 3000 }], true);
            if (priorManual) await runtime.ExecuteAsync(manual, CancellationToken.None, userSid: owner);
            var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
            var applied = await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
            Assert.AreEqual(CommandState.Applied, applied.State);
            Assert.AreEqual(4500, provider.Ac);
            Assert.AreEqual(3300, provider.Dc);

            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            provider.RejectModeWithoutMutation = true;
            var failed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
                CancellationToken.None, userSid: owner);
            Console.WriteLine("First release: " + JsonSerializer.Serialize(failed));
            Console.WriteLine("First commands: " + string.Join(",", commands.Select(command => command.GetType().Name)));
            Assert.AreEqual(CommandState.Rejected, failed.State);
            Assert.AreEqual(ErrorCode.HardwareWriteFailed, failed.Error?.Code);
            Assert.AreEqual(priorManual ? 3000 : 3200, provider.Ac);
            Assert.AreEqual(2400, provider.Dc);
            Assert.AreEqual(4200, provider.FanPlan!.FixedRpm);
            Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
            Assert.IsInstanceOfType<SetPerformanceModeCommand>(commands.Last());
            Assert.AreEqual(PerformanceMode.Balanced, ((SetPerformanceModeCommand)commands.Last()).Mode);
            Assert.IsTrue(commands.Take(commands.Count - 1).Any(command => command is SetCpuTuningBatchCommand));
            Assert.IsFalse(failed.ExecutionState!.Fields.Values.Any(field => field.OwnerOperationId == branch.OperationId),
                "Confirmed field restoration must retire branch field ownership before native-mode retry.");
            if (priorManual) Assert.AreEqual(manual.OperationId, failed.ExecutionState.Fields["performance.acMaxFrequencyMhz"].OwnerOperationId);

            commands.Clear();
            provider.RejectModeWithoutMutation = false;
            var retried = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
                CancellationToken.None, userSid: owner);
            Console.WriteLine("Retry: " + JsonSerializer.Serialize(retried));
            Console.WriteLine("Retry commands: " + string.Join(",", commands.Select(command => command.GetType().Name)));
            Assert.AreEqual(CommandState.Applied, retried.State);
            Assert.AreEqual(PerformanceMode.Balanced, provider.Mode, "A failed final mode restore must remain retryable through the original branch.");
            Assert.AreEqual(1, commands.Count, "Retry must not rewrite already restored fields.");
            Assert.IsInstanceOfType<SetPerformanceModeCommand>(commands.Single());
            Assert.AreEqual(priorManual ? 3000 : 3200, provider.Ac);
            Assert.AreEqual(2400, provider.Dc);
            if (priorManual) Assert.AreEqual(manual.OperationId, retried.ExecutionState!.Fields["performance.acMaxFrequencyMhz"].OwnerOperationId);
            commands.Clear();
            var completed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
                CancellationToken.None, userSid: owner);
            Assert.AreEqual(CommandState.Applied, completed.State);
            Assert.AreEqual(HardwareMutation.NoMutation, completed.Mutation);
            Assert.AreEqual(0, commands.Count);
        }
    }

    [TestMethod]
    public async Task Turbo_branch_retry_retains_pending_when_native_read_is_missing_or_inconsistent()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        const string owner = "retry-read";
        await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
        provider.RejectModeWithoutMutation = true;
        var failed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Rejected, failed.State);
        provider.RejectModeWithoutMutation = false;
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        var noReader = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, noReader.State);
        Assert.AreEqual(HardwareMutation.NoMutation, noReader.Mutation);
        provider.NativeModeRead = () => throw new IOException("Native read interrupted.");
        var disconnected = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, disconnected.State);
        Assert.AreEqual(HardwareMutation.NoMutation, disconnected.Mutation);
        int reads = 0;
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", (byte)(++reads == 1 ? 1 : 0),
            PerformanceMode.Turbo, null, "RequestStateUnverified");
        var mismatch = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, mismatch.State);
        Assert.AreEqual(0, commands.Count);
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow.AddMinutes(-1), "native-read", 4, "01", 1,
            PerformanceMode.Turbo, null, "RequestStateUnverified");
        var stale = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, stale.State);
        Assert.AreEqual(0, commands.Count);
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
            PerformanceMode.Turbo, null, "RequestStateUnverified");
        var recovered = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, recovered.State, "Read-only refusal must not consume the original pending mode restoration.");
        Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
        Assert.AreEqual(1, commands.Count);
    }

    [TestMethod]
    public async Task Turbo_branch_retry_refuses_changed_context_and_uncertain_native_write()
    {
        foreach (string change in new[] { "generation", "epoch", "scheme", "unknown", "rolled-back", "unverified", "lost-reply" })
        {
            var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
            provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
                PerformanceMode.Turbo, null, "RequestStateUnverified");
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            const string owner = "retry-guard";
            await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
            var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
            await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
            provider.RejectModeWithoutMutation = true;
            if (change == "unknown") provider.RejectedModeMutation = HardwareMutation.Unknown;
            if (change == "rolled-back")
            {
                provider.RejectedModeMutation = HardwareMutation.Changed;
                provider.RejectedModeState = CommandState.RolledBack;
            }
            if (change == "unverified")
            {
                provider.RejectModeWithoutMutation = false;
                provider.ConfirmModes = false;
            }
            if (change == "lost-reply")
            {
                provider.RejectModeWithoutMutation = false;
                provider.AfterWrite = command =>
                {
                    if (command is SetPerformanceModeCommand) throw new IOException("Mode reply lost after dispatch.");
                };
            }
            var first = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
            Assert.AreNotEqual(CommandState.Applied, first.State, change);
            if (change == "generation") runtime.Execution.InvalidateIntent();
            if (change == "epoch") runtime.Execution.InvalidateHardware();
            if (change == "scheme") provider.Scheme = Guid.NewGuid();
            provider.RejectModeWithoutMutation = false;
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var retry = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
            Assert.AreEqual(CommandState.RecoveryRequired, retry.State, change);
            Assert.AreEqual(HardwareMutation.NoMutation, retry.Mutation, change);
            Assert.AreEqual(0, commands.Count, change);
        }
    }

    [TestMethod]
    public async Task Turbo_branch_reads_complete_OEM_baseline_for_apply_and_exact_release()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true, AllowOemSpl = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        var applied = await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: "oem-branch");
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.AreEqual(45, provider.Spl, "A verified OEM field must not be skipped by a lightweight baseline read.");
        Assert.AreEqual(ApplyState.Confirmed, applied.ExecutionState!.Fields["performance.splWatts"].WriteState);
        var released = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
            CancellationToken.None, userSid: "oem-branch");
        Assert.AreEqual(CommandState.Applied, released.State);
        Assert.AreEqual(65, provider.Spl, "Release must read the owned OEM value before restoring its exact baseline.");
    }

    [TestMethod]
    public async Task Turbo_branch_does_not_guess_unreadable_OEM_values_or_overwrite_external_edits()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true, AllowOemSpl = true, ReadOemSpl = false };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: "oem-branch");
        Assert.AreEqual(65, provider.Spl, "Unknown baseline never authorizes an OEM write.");
        await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
            CancellationToken.None, userSid: "oem-branch");
        provider.ReadOemSpl = true;
        branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
        await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: "oem-branch");
        Assert.AreEqual(45, provider.Spl);
        provider.Spl = 52;
        await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId),
            CancellationToken.None, userSid: "oem-branch");
        Assert.AreEqual(52, provider.Spl, "Later external edits survive release.");
    }

    [TestMethod]
    public async Task Turbo_branch_keeps_unreadable_owned_SPL_pending_until_fresh_read_allows_exact_release()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true, AllowOemSpl = true };
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
            provider.Mode, null, "RequestStateUnverified");
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        const string owner = "spl-read-loss";
        await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        var applied = await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.AreEqual(45, provider.Spl);
        var original = applied.ExecutionState!.Fields["performance.splWatts"];
        Assert.AreEqual(branch.OperationId, original.OwnerOperationId);
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        provider.ReadOemSpl = false;
        Assert.IsNull((await provider.ReadControlBaselineAsync(CancellationToken.None, includeCpuAdvanced: true)).CpuTuning!.SplWatts);
        var first = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Console.WriteLine("Read loss first: " + JsonSerializer.Serialize(first));
        Console.WriteLine("First commands: " + string.Join(",", commands.Select(command => command.GetType().Name)));
        Assert.AreEqual(CommandState.RecoveryRequired, first.State, "A missing getter is not an external edit or a completed restore.");
        Assert.AreEqual(45, provider.Spl);
        Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
        Assert.IsTrue(first.ExecutionState!.Pages.Any(page => page.SkippedFields.Contains("splWatts")));
        Assert.AreEqual(original, first.ExecutionState.Fields["performance.splWatts"]);
        Assert.AreEqual(3200, provider.Ac);
        Assert.AreEqual(2400, provider.Dc);
        Assert.AreEqual(4200, provider.FanPlan!.FixedRpm);
        Assert.IsFalse(commands.Any(command => command is SetPerformanceModeCommand));
        commands.Clear();
        var stillMissing = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, stillMissing.State);
        Assert.AreEqual(0, commands.Count);
        Assert.AreEqual(original, stillMissing.ExecutionState!.Fields["performance.splWatts"]);
        provider.ReadOemSpl = true;
        var retry = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Console.WriteLine("Read loss retry: " + JsonSerializer.Serialize(retry));
        Console.WriteLine("Retry commands: " + JsonSerializer.Serialize(commands, new JsonSerializerOptions { WriteIndented = false }));
        Assert.AreEqual(CommandState.Applied, retry.State);
        Assert.AreEqual(65, provider.Spl);
        Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
        Assert.IsFalse(retry.ExecutionState!.Fields.ContainsKey("performance.splWatts"));
        Assert.AreEqual(2, commands.Count);
        Assert.IsTrue(commands[0] is SetCpuTuningBatchCommand { Plans: [{ SplWatts: 65, AcMaxFrequencyMhz: null, DcMaxFrequencyMhz: null }] });
        Assert.IsTrue(commands[1] is SetPerformanceModeCommand);
        commands.Clear();
        var completed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, completed.State);
        Assert.AreEqual(0, commands.Count);
    }

    [TestMethod]
    public async Task Turbo_branch_retains_unverified_restore_and_confirms_fresh_target_without_replaying_field_write()
    {
        var provider = new PipelineProvider { AllowBranch = true, ConfirmModes = true, AllowOemSpl = true };
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
            provider.Mode, null, "RequestStateUnverified");
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        const string owner = "spl-post-write";
        await runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: owner);
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        var applied = await runtime.ExecuteAsync(branch, CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.AreEqual(45, provider.Spl);
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = command =>
        {
            commands.Add(command);
            if (command is SetCpuTuningBatchCommand batch && batch.Plans.Any(plan => plan.SplWatts == 65))
                provider.ReadOemSpl = false;
        };
        var release = new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId);
        var first = await runtime.ExecuteAsync(release, CancellationToken.None, userSid: owner);
        Console.WriteLine("Post-write first: " + JsonSerializer.Serialize(first));
        Assert.AreEqual(CommandState.RecoveryRequired, first.State);
        Assert.AreEqual(65, provider.Spl);
        Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
        Assert.IsFalse(commands.Any(command => command is SetPerformanceModeCommand));
        var pending = first.ExecutionState!.Fields["performance.splWatts"];
        Assert.AreEqual(release.OperationId, pending.OwnerOperationId);
        Assert.AreEqual(ApplyState.SubmittedUnverified, pending.WriteState);
        Assert.AreEqual(65, pending.Baseline!.Value!.Value.GetInt32());
        Assert.AreEqual(45, pending.WriteBefore!.Value!.Value.GetInt32());
        Assert.AreEqual(65, pending.LastWritten!.Value!.Value.GetInt32());
        commands.Clear();
        provider.AfterWrite = commands.Add;
        var missing = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, missing.State);
        Assert.AreEqual(pending, missing.ExecutionState!.Fields["performance.splWatts"]);
        Assert.AreEqual(0, commands.Count);
        provider.ReadOemSpl = true;
        provider.Spl = 52;
        var mismatch = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, mismatch.State);
        Assert.AreEqual(pending, mismatch.ExecutionState!.Fields["performance.splWatts"]);
        Assert.AreEqual(52, provider.Spl);
        Assert.AreEqual(0, commands.Count);
        provider.Spl = 65;
        provider.NativeModeRead = () => null;
        var missingMode = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.RecoveryRequired, missingMode.State);
        Assert.AreEqual(pending, missingMode.ExecutionState!.Fields["performance.splWatts"]);
        Assert.AreEqual(0, commands.Count);
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "01", 1,
            provider.Mode, null, "RequestStateUnverified");
        var settled = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Console.WriteLine("Post-write settled: " + JsonSerializer.Serialize(settled));
        Assert.AreEqual(CommandState.Applied, settled.State);
        Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
        Assert.AreEqual(65, provider.Spl);
        Assert.IsFalse(settled.ExecutionState!.Fields.ContainsKey("performance.splWatts"));
        Assert.AreEqual(1, commands.Count);
        Assert.IsTrue(commands[0] is SetPerformanceModeCommand);
        commands.Clear();
        var completed = await runtime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: owner);
        Assert.AreEqual(CommandState.Applied, completed.State);
        Assert.AreEqual(0, commands.Count);
    }

    [TestMethod]
    public async Task Exact_boost_preset_restores_indices_skips_repeated_requests_and_keeps_boolean_undo()
    {
        var provider = new PipelineProvider { ConfirmModes = true, AcBoost = 0, DcBoost = 0 };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = BoostPreset(provider, key, new(4, 1));
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: "boost-baseline")).State);
        async Task<CommandResult> Apply()
        {
            var id = Guid.NewGuid();
            return await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Performance, true, key, preset.Revision)])),
                CancellationToken.None, userSid: "boost-baseline");
        }
        Assert.AreEqual(ApplyState.Confirmed, (await Apply()).ExecutionState!.Pages.Single().State);
        Assert.AreEqual(4, provider.AcBoost);
        Assert.AreEqual(1, provider.DcBoost);
        int writes = provider.Writes;
        Assert.AreEqual(ApplyState.Confirmed, (await Apply()).ExecutionState!.Pages.Single().State);
        Assert.AreEqual(writes, provider.Writes, "Identical boost targets must not refresh the power policy repeatedly.");
        var off = await runtime.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
            new(null, null, null, null, false, null, null, null), true), CancellationToken.None, userSid: "boost-baseline");
        Assert.AreEqual(CommandState.Applied, off.State);
        Assert.IsFalse(off.ExecutionState!.Fields.ContainsKey("performance.isBoostEnabled"));
        await Apply();
        preset = preset with { Revision = 2, Settings = new() { ["isBoostEnabled"] = SettingRequest.Official } };
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 1, [preset]),
            CancellationToken.None, userSid: "boost-baseline")).State);
        Assert.AreEqual(ApplyState.Confirmed, (await Apply()).ExecutionState!.Pages.Single().State);
        Assert.AreEqual(0, provider.AcBoost);
        Assert.AreEqual(0, provider.DcBoost);
    }

    [TestMethod]
    public async Task Exact_boost_requires_both_readbacks_and_never_confirms_equal_booleans_with_wrong_indices()
    {
        foreach (bool missing in new[] { false, true })
        {
            var provider = new PipelineProvider { ConfirmModes = true, ReadBoostModes = !missing, AcBoost = 2, DcBoost = 2 };
            provider.AfterWrite = command =>
            { if (command is SetCpuTuningBatchCommand) { provider.AcBoost = 2; provider.DcBoost = 2; } };
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = BoostPreset(provider, key, new(4, 1));
            await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None, userSid: "boost-baseline");
            var id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Performance, true, key, 1)])),
                CancellationToken.None, userSid: "boost-baseline");
            if (missing)
            {
                Assert.AreEqual(CommandState.Rejected, result.State);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
                Assert.AreEqual(0, provider.Writes);
                Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
            }
            else
            {
                Assert.AreNotEqual(ApplyState.Confirmed, result.ExecutionState!.Pages.Single().State);
                CollectionAssert.DoesNotContain(result.ExecutionState.Pages.Single().ConfirmedFields, "isBoostEnabled");
            }
        }
    }

    private static PagePresetEnvelope BoostPreset(PipelineProvider provider, PresetKey key, CpuBoostPolicy policy) => new(
        PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "均衡恢复",
        JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
            acMaxFrequencyMhz = 3200, dcMaxFrequencyMhz = 2400, isBoostEnabled = policy.IsEnabled, boostPolicy = policy,
            windowsPowerSchemeId = provider.Scheme, acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null,
            negativeCurveOptimizer = (int?)null, advancedCpuTuning = (AdvancedCpuTuningPlan?)null, useOfficialCpuPolicy = true }),
        DateTimeOffset.UtcNow) { Revision = 1, Settings = new() { ["isBoostEnabled"] = SettingRequest.Set(policy) } };

    [TestMethod]
    public async Task Boolean_boost_off_disables_both_scopes_and_does_not_confirm_a_partial_disable()
    {
        foreach (bool corrupt in new[] { false, true })
        {
            var provider = new PipelineProvider { ConfirmModes = true, AcBoost = 0, DcBoost = 4 };
            if (corrupt) provider.AfterWrite = command =>
            { if (command is SetCpuTuningBatchCommand) { provider.AcBoost = 0; provider.DcBoost = 4; } };
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = BoostPreset(provider, key, new(0, 0));
            var payload = preset.Payload.EnumerateObject().Where(property => property.Name != "boostPolicy")
                .ToDictionary(property => property.Name, property => property.Value);
            preset = preset with { Payload = JsonSerializer.SerializeToElement(payload),
                Settings = new() { ["isBoostEnabled"] = SettingRequest.Set(false) } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                CancellationToken.None, userSid: "boost-baseline")).State);
            var id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Performance, true, key, 1)])),
                CancellationToken.None, userSid: "boost-baseline");
            if (corrupt) Assert.AreNotEqual(ApplyState.Confirmed, result.ExecutionState!.Pages.Single().State);
            else
            {
                Assert.AreEqual(ApplyState.Confirmed, result.ExecutionState!.Pages.Single().State);
                Assert.AreEqual(0, provider.AcBoost);
                Assert.AreEqual(0, provider.DcBoost);
            }
        }
    }

    [TestMethod]
    public async Task Preset_restores_per_core_co_and_pbo_without_smu_capability()
    {
        foreach (bool allowPbo in new[] { false, true })
        {
            var provider = new PipelineProvider { ConfirmModes = true, AllowPbo = allowPbo, Pbo = 7 };
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var cores = new Dictionary<int, int> { [0] = -5, [1] = -10 };
            var advanced = new AdvancedCpuTuningPlan(PboScalar: allowPbo ? 4 : null, PerCoreCurveOptimizer: cores);
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key, "均衡恢复",
                JsonSerializer.SerializeToElement(new { temperatureLimitC = 85, splWatts = 55, spptWatts = 65,
                    acMaxFrequencyMhz = 3200, dcMaxFrequencyMhz = 2400, isBoostEnabled = true,
                    windowsPowerSchemeId = provider.Scheme, acMinActiveCoresPercent = (int?)null, dcMinActiveCoresPercent = (int?)null,
                    negativeCurveOptimizer = (int?)null, advancedCpuTuning = advanced, useOfficialCpuPolicy = true }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(advanced),
                ["negativeCurveOptimizer"] = SettingRequest.Keep } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                CancellationToken.None, userSid: "cpu-baseline")).State);
            var id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Performance, true, key, 1)])),
                CancellationToken.None, userSid: "cpu-baseline");
            Assert.AreEqual(ApplyState.Confirmed, result.ExecutionState!.Pages.Single().State);
            Assert.AreEqual(allowPbo ? 4 : 7, provider.Pbo);
            CollectionAssert.AreEquivalent(cores.ToArray(), provider.Cores.ToArray());
            Assert.AreEqual(3200, provider.Ac);
            Assert.AreEqual(2400, provider.Dc);
        }
    }

    [TestMethod]
    public async Task Gpu_preset_restores_core_memory_and_curve_after_the_core_shifts_the_table()
    {
        foreach (var (beforeCore, targetCore) in new[] { (10000, 0), (0, 10000), (10000, -10000) })
        {
            var provider = new PipelineProvider { ConfirmModes = true, GpuCoreOffset = beforeCore, GpuMemoryOffset = 1000,
                GpuDeltas = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 30000).ToArray() };
            var targetCurve = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 5000).ToArray();
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "均衡恢复",
                JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = false,
                    memoryOffsetKhz = 2000, coreOffsetKhz = targetCore, vfOffsetsKhz = targetCurve }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["coreOffsetKhz"] = SettingRequest.Set(targetCore),
                ["memoryOffsetKhz"] = SettingRequest.Set(2000), ["vfOffsetsKhz"] = SettingRequest.Set(targetCurve) } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                CancellationToken.None, userSid: "gpu-baseline")).State);
            var id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Gpu, true, key, 1)])),
                CancellationToken.None, userSid: "gpu-baseline");
            Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Code.ToString());
            Assert.AreEqual(ApplyState.Confirmed, result.ExecutionState!.Pages.Single().State);
            Assert.AreEqual(targetCore, provider.GpuCoreOffset);
            Assert.AreEqual(2000, provider.GpuMemoryOffset);
            CollectionAssert.AreEqual(targetCurve, provider.GpuDeltas);
        }
    }

    [TestMethod]
    public async Task Performance_preset_keeps_fresh_selected_mode_and_only_applies_changed_cpu_fields()
    {
        foreach (bool sameValue in new[] { false, true })
        {
            var provider = new PipelineProvider { AcBoost = sameValue ? 0 : 4, DcBoost = sameValue ? 0 : 1 };
            provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "08", 8,
                provider.Mode, null, "RequestStateUnverified");
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var result = await ApplyPerformanceBoostPreset(provider);
            Assert.AreEqual(CommandState.Applied, result.State, result.Error?.Code.ToString());
            Assert.AreEqual(0, commands.OfType<SetPerformanceModeCommand>().Count());
            Assert.AreEqual(sameValue ? 0 : 1, commands.Count);
            Assert.AreEqual(0, provider.AcBoost);
            Assert.AreEqual(0, provider.DcBoost);
            Assert.IsNull(result.VerifiedPerformanceMode);
            Assert.AreEqual(ApplyState.SubmittedUnverified, result.ExecutionState!.NativeModeApplyState);
            Assert.AreEqual(ApplyState.Confirmed, result.ExecutionState.Pages.Single().State);
        }
    }

    [TestMethod]
    public async Task Performance_preset_requires_fresh_matching_raw_mode_to_skip_the_mode_write()
    {
        foreach (string scenario in new[] { "unknown", "stale", "future", "changed" })
        {
            var provider = new PipelineProvider();
            provider.NativeModeRead = () =>
            {
                if (scenario == "changed") provider.Mode = PerformanceMode.Quiet;
                return new(DateTimeOffset.UtcNow.AddSeconds(scenario == "stale" ? -10 : scenario == "future" ? 10 : 0),
                    "native-read", 4, "08", scenario == "unknown" ? null : (byte)8,
                    scenario == "unknown" ? null : provider.Mode, null, "RequestStateUnverified");
            };
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var result = await ApplyPerformanceBoostPreset(provider);
            Assert.AreEqual(CommandState.Applied, result.State, scenario);
            Assert.AreEqual(1, commands.OfType<SetPerformanceModeCommand>().Count(), scenario);
            Assert.AreEqual(PerformanceMode.Balanced, provider.Mode, scenario);
        }
    }

    [TestMethod]
    public async Task Same_mode_preset_rejects_a_mode_change_during_the_second_baseline_read_without_writes()
    {
        foreach (bool gpu in new[] { false, true })
        {
            var provider = new PipelineProvider();
            provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "08", 8,
                provider.Mode, null, "RequestStateUnverified");
            provider.AfterBaseline = () => { if (provider.NativeModeReads > 0) provider.Mode = PerformanceMode.Quiet; };
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var result = gpu ? await ApplyGpuMemoryPreset(provider) : await ApplyPerformanceBoostPreset(provider);
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(ErrorCode.CancelledBeforeApply, result.Error?.Code);
            Assert.IsEmpty(commands);
            Assert.AreEqual(4, provider.AcBoost);
            Assert.AreEqual(0, provider.GpuMemoryOffset);
        }
    }

    [TestMethod]
    public async Task Performance_manual_mode_still_submits_the_requested_native_mode()
    {
        var provider = new PipelineProvider();
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "08", 8,
            provider.Mode, null, "RequestStateUnverified");
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        var result = await ApplyPerformanceBoostPreset(provider, ControlOrigin.ManualMode);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(1, commands.OfType<SetPerformanceModeCommand>().Count());
    }

    private static async Task<CommandResult> ApplyPerformanceBoostPreset(PipelineProvider provider,
        ControlOrigin origin = ControlOrigin.PresetUse)
    {
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Gaming, 2);
        var preset = BoostPreset(provider, key, new(0, 0));
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: "cpu-mode-test")).State);
        var id = Guid.NewGuid();
        return await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, origin,
            ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Performance, true, key, 1)])),
            CancellationToken.None, userSid: "cpu-mode-test");
    }

    [TestMethod]
    public async Task Gpu_preset_keeps_fresh_selected_mode_without_claiming_effective_confirmation()
    {
        var provider = new PipelineProvider();
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "08", 8,
            provider.Mode, null, "RequestStateUnverified");
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        var result = await ApplyGpuMemoryPreset(provider);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(1000, provider.GpuMemoryOffset);
        Assert.AreEqual(2, provider.NativeModeReads);
        Assert.AreEqual(1, commands.Count);
        Assert.IsInstanceOfType<SetGpuMemoryOffsetCommand>(commands.Single());
        Assert.IsNull(result.VerifiedPerformanceMode);
        Assert.AreEqual(ApplyState.SubmittedUnverified, result.ExecutionState!.NativeModeApplyState);
        Assert.AreEqual(ApplyState.Confirmed, result.ExecutionState.Pages.Single().State);
    }

    [TestMethod]
    public async Task Gpu_preset_does_not_skip_requested_mode_for_unknown_stale_or_changed_readback()
    {
        foreach (string scenario in new[] { "unknown", "stale", "future", "changed" })
        {
            var provider = new PipelineProvider();
            provider.NativeModeRead = () =>
            {
                if (scenario == "changed") provider.Mode = PerformanceMode.Quiet;
                return new(DateTimeOffset.UtcNow.AddSeconds(scenario == "stale" ? -10 : scenario == "future" ? 10 : 0),
                    "native-read", 4, "08", scenario == "unknown" ? null : (byte)8,
                    scenario == "unknown" ? null : provider.Mode, null, "RequestStateUnverified");
            };
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var result = await ApplyGpuMemoryPreset(provider);
            Assert.AreEqual(CommandState.Applied, result.State, scenario);
            Assert.AreEqual(1, commands.OfType<SetPerformanceModeCommand>().Count(), scenario);
            Assert.AreEqual(PerformanceMode.Balanced, provider.Mode, scenario);
            Assert.AreEqual(1000, provider.GpuMemoryOffset, scenario);
            Assert.IsNull(result.VerifiedPerformanceMode, scenario);
        }
    }

    [TestMethod]
    public async Task Gpu_follow_rejects_unknown_stale_future_or_changed_mode_without_any_write()
    {
        foreach (string scenario in new[] { "unknown", "stale", "future", "changed" })
        {
            var provider = new PipelineProvider();
            provider.NativeModeRead = () =>
            {
                if (scenario == "changed") provider.Mode = PerformanceMode.Quiet;
                return new(DateTimeOffset.UtcNow.AddSeconds(scenario == "stale" ? -10 : scenario == "future" ? 10 : 0),
                    "native-read", 4, "08", scenario == "unknown" ? null : (byte)8,
                    scenario == "unknown" ? null : provider.Mode, null, "RequestStateUnverified");
            };
            var commands = new List<HardwareCommand>();
            provider.AfterWrite = commands.Add;
            var result = await ApplyGpuMemoryPreset(provider, ControlOrigin.ModeFollow);
            Assert.AreEqual(CommandState.Rejected, result.State, scenario);
            Assert.AreEqual(ErrorCode.CancelledBeforeApply, result.Error?.Code, scenario);
            Assert.IsEmpty(commands, scenario);
            Assert.AreEqual(0, provider.GpuMemoryOffset, scenario);
        }
    }

    [TestMethod]
    public async Task Gpu_follow_only_writes_selected_page_for_a_fresh_current_mode()
    {
        var provider = new PipelineProvider();
        provider.NativeModeRead = () => new(DateTimeOffset.UtcNow, "native-read", 4, "08", 8,
            provider.Mode, null, "RequestStateUnverified");
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        var result = await ApplyGpuMemoryPreset(provider, ControlOrigin.ModeFollow);
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(1, provider.NativeModeReads);
        Assert.HasCount(1, commands);
        Assert.IsInstanceOfType<SetGpuMemoryOffsetCommand>(commands.Single());
        Assert.AreEqual(1000, provider.GpuMemoryOffset);
        Assert.IsNull(result.VerifiedPerformanceMode);
    }

    [TestMethod]
    public async Task Follow_request_requires_a_selected_follow_page_before_any_write()
    {
        var provider = new PipelineProvider();
        var commands = new List<HardwareCommand>();
        provider.AfterWrite = commands.Add;
        var result = await ApplyGpuMemoryPreset(provider, ControlOrigin.ModeFollow, follow: false);
        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.IsEmpty(commands);
        Assert.AreEqual(0, provider.GpuMemoryOffset);
    }

    private static async Task<CommandResult> ApplyGpuMemoryPreset(PipelineProvider provider,
        ControlOrigin origin = ControlOrigin.PresetUse, bool follow = true)
    {
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Gaming, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "GPU only",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = false,
                memoryOffsetKhz = 1000, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["memoryOffsetKhz"] = SettingRequest.Set(1000) } };
        var publication = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: "gpu-mode-test");
        Assert.AreEqual(CommandState.Applied, publication.State, publication.Error?.Details.GetValueOrDefault("cause"));
        var id = Guid.NewGuid();
        return await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, origin,
            ControlModeId.Gaming, PerformanceMode.Balanced, [new(ControlPageId.Gpu, follow, key, 1)])),
            CancellationToken.None, userSid: "gpu-mode-test");
    }

    [TestMethod]
    public void Gpu_plan_preserves_the_original_curve_guard_and_rejects_unrestorable_intermediate_offsets()
    {
        var original = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : 30000).ToArray();
        var current = new HomeControlState(PerformanceMode.Balanced, false, [])
        { GpuVf = new(Enumerable.Range(0, 127).Select(index => new GpuVfNode(450 + index, 210, original[index])).ToArray(),
            -200000, 200000, null) { CoreOffsetKhz = 10000, MemoryOffsetKhz = 0 } };
        var commands = ControlPlanCompiler.Commands(ControlPageId.Gpu, new()
        { ["coreOffsetKhz"] = SettingRequest.Set(0), ["vfOffsetsKhz"] = SettingRequest.Set(new int[127]) }, current);
        CollectionAssert.AreEqual(original, ((SetGpuCoreOffsetCommand)commands[0]).ExpectedVfOffsetsKhz!);
        CollectionAssert.AreEqual(original.Select((value, index) => index == 0 ? value : value - 10000).ToArray(),
            ((SetGpuVfCurveCommand)commands[1]).ExpectedOffsetsKhz);
        var nearLimit = current with { GpuVf = current.GpuVf with
            { Nodes = current.GpuVf.Nodes.Select((node, index) => node with { OffsetKhz = index == 0 ? 0 : 200000 }).ToArray() } };
        Assert.ThrowsExactly<InvalidDataException>(() => ControlPlanCompiler.Commands(ControlPageId.Gpu,
            new() { ["coreOffsetKhz"] = SettingRequest.Set(20000), ["vfOffsetsKhz"] = SettingRequest.Set(new int[127]) }, nearLimit));
    }

    [TestMethod]
    public async Task Gpu_restore_rejects_external_curve_changes_and_preflights_before_mode_writes()
    {
        foreach (bool invalidIntermediate in new[] { false, true })
        {
            var provider = new PipelineProvider { ConfirmModes = true, GpuCoreOffset = invalidIntermediate ? 0 : 10000,
                GpuDeltas = Enumerable.Range(0, 127).Select(index => index == 0 ? 0 : invalidIntermediate ? 200000 : 30000).ToArray() };
            int curveWrites = 0;
            provider.AfterWrite = command =>
            {
                if (command is SetGpuCoreOffsetCommand) provider.GpuDeltas[1] += 1000;
                if (command is SetGpuVfCurveCommand) curveWrites++;
            };
            var runtime = new HomeServiceRuntime(provider);
            await runtime.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            int targetCore = invalidIntermediate ? 10000 : 0;
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "均衡恢复",
                JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = false,
                    memoryOffsetKhz = (int?)null, coreOffsetKhz = targetCore, vfOffsetsKhz = new int[127] }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["coreOffsetKhz"] = SettingRequest.Set(targetCore),
                ["vfOffsetsKhz"] = SettingRequest.Set(new int[127]) } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                CancellationToken.None, userSid: "gpu-baseline")).State);
            var id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, PerformanceMode.Turbo, [new(ControlPageId.Gpu, true, key, 1)])),
                CancellationToken.None, userSid: "gpu-baseline");
            Assert.AreEqual(0, curveWrites, "Neither invalid intermediates nor external changes may be overwritten.");
            if (invalidIntermediate)
            {
                Assert.AreEqual(CommandState.Rejected, result.State);
                Assert.AreEqual(ErrorCode.ValidationFailed, result.Error?.Code);
                Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
                Assert.AreEqual(0, provider.Writes);
                Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
            }
            else
            {
                Assert.AreEqual(ApplyState.Partial, result.ExecutionState!.Pages.Single().State);
                Assert.AreEqual(21000, provider.GpuDeltas[1], "Keep the external edit instead of rebasing the next command.");
                Assert.AreEqual(0, provider.GpuCoreOffset);
            }
        }
    }

    [TestMethod]
    public async Task Direct_gpu_tuning_return_to_owned_baseline_releases_manual_ownership()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-gpu-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new PipelineProvider();
            var runtime = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await runtime.InitializeAsync(CancellationToken.None);
            var beforeCurve = provider.GpuDeltas.ToArray();
            var targetCurve = beforeCurve.Select((value, index) => index == 0 ? value : value + 10_000).ToArray();
            (string Field, HardwareCommand Apply, HardwareCommand Restore)[] cases = [
                ("coreOffsetKhz", new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10_000, true),
                    new SetGpuCoreOffsetCommand(Guid.NewGuid(), 10_000, 0, true)),
                ("memoryOffsetKhz", new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 10_000, true),
                    new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 10_000, 0, true)),
                ("vfOffsetsKhz", new SetGpuVfCurveCommand(Guid.NewGuid(), beforeCurve, targetCurve, true),
                    new SetGpuVfCurveCommand(Guid.NewGuid(), targetCurve, beforeCurve, true))
            ];
            foreach (var (field, apply, restore) in cases)
            {
                var applied = await runtime.ExecuteAsync(apply, CancellationToken.None, userSid: "gpu-owner");
                Assert.AreEqual(CommandState.Applied, applied.State, field);
                Assert.IsTrue(applied.ExecutionState!.Fields.ContainsKey("gpu." + field), field);
                var restored = await runtime.ExecuteAsync(restore, CancellationToken.None, userSid: "gpu-owner");
                Assert.AreEqual(CommandState.Applied, restored.State, field);
                Assert.IsFalse(restored.ExecutionState!.Fields.ContainsKey("gpu." + field), field);
                Assert.IsFalse((await new ControlExecutionJournal(root).ReadAsync(CancellationToken.None))
                    .Current!.Fields.ContainsKey("gpu." + field), field);
            }
            await runtime.ExecuteAsync(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 10_000, true),
                CancellationToken.None, userSid: "gpu-owner");
            var changed = await runtime.ExecuteAsync(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 10_000, 20_000, true),
                CancellationToken.None, userSid: "gpu-owner");
            Assert.IsTrue(changed.ExecutionState!.Fields.ContainsKey("gpu.coreOffsetKhz"), "Non-baseline edits retain ownership.");
            var reset = await runtime.ExecuteAsync(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 20_000, 0, true),
                CancellationToken.None, userSid: "gpu-owner");
            Assert.IsFalse(reset.ExecutionState!.Fields.ContainsKey("gpu.coreOffsetKhz"));
            Assert.AreEqual(0, provider.GpuCoreOffset);
            Assert.AreEqual(0, provider.GpuMemoryOffset);
            CollectionAssert.AreEqual(beforeCurve, provider.GpuDeltas);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Official_cpu_frequency_at_baseline_skips_duplicate_frequency_write()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-same-frequency-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new PipelineProvider { ConfirmModes = true };
            var runtime = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await runtime.InitializeAsync(CancellationToken.None);
            var sameValues = new CpuTuningPlan(null, null, null, null, null, null, null, null)
            { AcMaxFrequencyMhz = provider.Ac, DcMaxFrequencyMhz = provider.Dc };
            var manual = await runtime.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(), sameValues, true),
                CancellationToken.None, userSid: "frequency-owner");
            Assert.AreEqual(CommandState.Applied, manual.State);
            Assert.IsFalse(manual.ExecutionState!.Fields.ContainsKey("performance.acMaxFrequencyMhz"));
            Assert.IsFalse((await new ControlExecutionJournal(root).ReadAsync(CancellationToken.None))
                .Current!.Fields.ContainsKey("performance.acMaxFrequencyMhz"));

            var key = PresetKey.Create(ControlModeId.Gaming, 2);
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance,
                key, "Official frequency", JsonSerializer.SerializeToElement(new
                { useOfficialCpuPolicy = false, temperatureLimitC = 95, splWatts = 45, spptWatts = 65,
                    acMaxFrequencyMhz = provider.Ac, dcMaxFrequencyMhz = provider.Dc, isBoostEnabled = true,
                    windowsPowerSchemeId = provider.Scheme, acMinActiveCoresPercent = (int?)null,
                    dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null,
                    advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Official,
                ["dcMaxFrequencyMhz"] = SettingRequest.Official } };
            Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
                new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                CancellationToken.None, userSid: "frequency-owner")).State);
            var confirmedMode = await runtime.ExecuteAsync(
                new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced),
                CancellationToken.None, userSid: "frequency-owner");
            Assert.AreEqual(PerformanceMode.Balanced, confirmedMode.VerifiedPerformanceMode);
            int writesBeforeRelease = provider.Writes;
            int frequencyWrites = 0;
            provider.AfterWrite = command => { if (command is SetCpuTuningBatchCommand) frequencyWrites++; };
            Guid id = Guid.NewGuid();
            var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue,
                ControlOrigin.PresetUse, ControlModeId.Gaming, PerformanceMode.Balanced,
                [new(ControlPageId.Performance, true, key, 1)])), CancellationToken.None, userSid: "frequency-owner");
            Assert.AreEqual(CommandState.Applied, result.State);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(writesBeforeRelease, provider.Writes, "Ownership-only release must not dispatch hardware writes.");
            Assert.AreEqual(0, frequencyWrites);
            Assert.AreEqual(3200, provider.Ac);
            Assert.AreEqual(2400, provider.Dc);
            CollectionAssert.Contains(result.ExecutionState!.Pages.Single().ConfirmedFields, "acMaxFrequencyMhz");
            CollectionAssert.Contains(result.ExecutionState.Pages.Single().ConfirmedFields, "dcMaxFrequencyMhz");
            Assert.IsFalse(result.ExecutionState.Fields.ContainsKey("performance.acMaxFrequencyMhz"));
            Assert.IsFalse(result.ExecutionState.Fields.ContainsKey("performance.dcMaxFrequencyMhz"));
            var persisted = await new ControlExecutionJournal(root).ReadAsync(CancellationToken.None);
            Assert.IsFalse(persisted.Current!.Fields.ContainsKey("performance.acMaxFrequencyMhz"));
            Assert.IsFalse(persisted.Current.Fields.ContainsKey("performance.dcMaxFrequencyMhz"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Suspend_during_preset_capture_or_write_stops_next_hardware_step_and_preserves_mutation_truth()
    {
        foreach (string stage in new[] { "capture", "mode", "cpu", "direct-mode", "rejected-mode" })
        {
            string journalFolder = Path.Combine(Path.GetTempPath(), "xisura-preset-suspend-" + Guid.NewGuid().ToString("N"));
            var journal = new ControlExecutionJournal(journalFolder);
            try
            {
                var provider = new PipelineProvider { RejectModeWithoutMutation = stage == "rejected-mode" };
                var runtime = new HomeServiceRuntime(provider, journal: journal);
                await runtime.InitializeAsync(CancellationToken.None);
                var key = PresetKey.Create(ControlModeId.Office, 1);
                var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance,
                    key, "Suspend boundary", JsonSerializer.SerializeToElement(new { useOfficialCpuPolicy = false, temperatureLimitC = 95,
                        splWatts = 45, spptWatts = 65, acMaxFrequencyMhz = 3000, dcMaxFrequencyMhz = 4500,
                        isBoostEnabled = true, windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                        dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null, advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
                { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Set(3000) } };
                var published = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
                    CancellationToken.None, userSid: "boundary-user");
                Assert.AreEqual(CommandState.Applied, published.State);
                int readsAtSuspend = -1;
                void Suspend()
                {
                    readsAtSuspend = provider.BaselineReads;
                    runtime.PreparePowerTransition(false);
                }
                if (stage == "capture") provider.AfterBaseline = () => { if (readsAtSuspend < 0) Suspend(); };
                else provider.AfterWrite = command =>
                {
                    if (readsAtSuspend < 0 && (stage is "mode" or "direct-mode" or "rejected-mode" && command is SetPerformanceModeCommand ||
                        stage == "cpu" && command is SetCpuTuningBatchCommand)) Suspend();
                };
                Guid id = Guid.NewGuid();
                HardwareCommand command = stage is "direct-mode" or "rejected-mode" ? new SetPerformanceModeCommand(id, PerformanceMode.Quiet)
                    : new ApplyControlRequestCommand(id, new(id, 0, ControlOrigin.PresetUse,
                        ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Performance, true, key, 1)]));
                var result = await runtime.ExecuteAsync(command, CancellationToken.None, userSid: "boundary-user");
                Assert.IsTrue(runtime.IsSuspended);
                bool noMutation = stage is "capture" or "rejected-mode";
                Assert.AreEqual(noMutation ? 0 : stage == "cpu" ? 2 : 1, provider.Writes,
                    "The next write must stop at the suspend boundary: " + stage);
                Assert.AreEqual(readsAtSuspend, provider.BaselineReads, "No follow-up read during suspend: " + stage);
                Assert.AreEqual(noMutation ? CommandState.Rejected : CommandState.RecoveryRequired, result.State);
                Assert.AreEqual(noMutation ? HardwareMutation.NoMutation : HardwareMutation.Unknown, result.Mutation);
                Assert.IsNotNull(result.Error);
                Assert.AreEqual("LifecycleGuard", result.Error.Details["stage"]);
                var saved = await journal.ReadAsync(CancellationToken.None);
                Assert.AreEqual(noMutation ? 0 : 1, saved.Unresolved.Length,
                    "Only zero-write failures may clear their pending journal entry.");
                Assert.AreEqual(result, await runtime.ExecuteAsync(command, CancellationToken.None, userSid: "boundary-user"),
                    "Same operation retries must not repeat a partial write.");
            }
            finally { if (Directory.Exists(journalFolder)) Directory.Delete(journalFolder, true); }
        }
    }

    [TestMethod]
    public async Task Unreadable_official_gpu_limit_without_owned_baseline_is_not_confirmed()
    {
        var provider = new PipelineProvider { ConfirmModes = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "GPU official",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = (int?)null, resetCoreFrequencyLimit = true,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Official } };
        var published = await runtime.ExecuteAsync(
            new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code + ": " +
            string.Join(", ", published.Error?.Details?.Select(pair => pair.Key + "=" + pair.Value) ?? []));
        Guid id = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        var page = result.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(ApplyState.Partial, page.State);
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
        CollectionAssert.DoesNotContain(page.UnverifiedFields, "coreFrequencyLimitMhz");
        CollectionAssert.DoesNotContain(page.ConfirmedFields, "coreFrequencyLimitMhz");
        Assert.AreEqual(1, provider.Writes, "No GPU reset is safe without a baseline.");
    }

    [TestMethod]
    public async Task Unreadable_gpu_clock_limit_is_skipped_for_follow_and_explicit_preset()
    {
        var provider = new PipelineProvider { ConfirmModes = true };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "Old GPU cap",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = 2280, resetCoreFrequencyLimit = false,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Set(2280) } };
        var published = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]),
            CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code.ToString());
        Guid id = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        var page = result.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(1, provider.Writes, "Only the native mode may be written on a followed transition.");
        Assert.AreEqual(ApplyState.Partial, page.State, JsonSerializer.Serialize(new { result.State, result.Mutation,
            page.SkippedFields, page.UnverifiedFields, page.ErrorCode, provider.Writes }));
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
        Assert.AreEqual(0, provider.GpuClockWrites);

        Guid explicitId = Guid.NewGuid();
        var explicitResult = await runtime.ExecuteAsync(new ApplyControlRequestCommand(explicitId, new(explicitId, long.MaxValue,
            ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
            [new PageSelection(ControlPageId.Gpu, true, key, 1)])),
            CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(0, provider.GpuClockWrites, "An explicit selection cannot make a write-only GPU cap recoverable.");
        CollectionAssert.Contains(explicitResult.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu).SkippedFields,
            "coreFrequencyLimitMhz");
    }

    [TestMethod]
    public async Task Unavailable_gpu_cap_in_saved_preset_does_not_block_native_mode()
    {
        var provider = new PipelineProvider { ConfirmModes = true, AllowGpuClock = false };
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Turbo, 1);
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Gpu, key, "Old GPU cap",
            JsonSerializer.SerializeToElement(new { coreFrequencyLimitMhz = 2280, resetCoreFrequencyLimit = false,
                memoryOffsetKhz = (int?)null, coreOffsetKhz = (int?)null, vfOffsetsKhz = (int[]?)null }),
            DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["coreFrequencyLimitMhz"] = SettingRequest.Set(2280) } };
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
            new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None, userSid: "user-a")).State);
        Guid operation = Guid.NewGuid();
        var applied = await runtime.ExecuteAsync(new ApplyControlRequestCommand(operation, new(operation,
            long.MaxValue, ControlOrigin.PresetUse, ControlModeId.Turbo, PerformanceMode.Turbo,
            [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, applied.State, applied.Error?.Details.GetValueOrDefault("cause"));
        Assert.AreEqual(PerformanceMode.Turbo, provider.Mode);
        Assert.AreEqual(0, provider.GpuClockWrites);
        var page = applied.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Gpu);
        Assert.AreEqual(ApplyState.Partial, page.State);
        CollectionAssert.Contains(page.SkippedFields, "coreFrequencyLimitMhz");
    }

    [TestMethod]
    public async Task Official_after_service_restart_reports_lost_ownership_instead_of_silent_success()
    {
        string root = Path.Combine(Path.GetTempPath(), "xisura-restart-ownership-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new PipelineProvider { ConfirmModes = true };
            var first = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await first.InitializeAsync(CancellationToken.None);
            var key = PresetKey.Create(ControlModeId.Office, 1);
            PagePresetEnvelope Preset(SettingRequest setting) => new(PagePresetEnvelope.CurrentSchemaVersion,
                ControlPageId.Performance, key, "CPU", JsonSerializer.SerializeToElement(new
                { useOfficialCpuPolicy = false, temperatureLimitC = 95, splWatts = 45, spptWatts = 65,
                    acMaxFrequencyMhz = 3000, dcMaxFrequencyMhz = 4500, isBoostEnabled = true,
                    windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                    dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null,
                    advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = setting } };
            async Task<CommandResult> Apply(HomeServiceRuntime runtime, SettingRequest setting)
            {
                Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(
                    new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [Preset(setting)]),
                    CancellationToken.None, userSid: "user-a")).State);
                Guid operation = Guid.NewGuid();
                return await runtime.ExecuteAsync(new ApplyControlRequestCommand(operation, new(operation,
                    long.MaxValue, ControlOrigin.PresetUse, ControlModeId.Office, PerformanceMode.Quiet,
                    [new(ControlPageId.Performance, true, key, 1)])), CancellationToken.None, userSid: "user-a");
            }
            Assert.AreEqual(CommandState.Applied, (await Apply(first, SettingRequest.Set(3000))).State);
            Assert.AreEqual(3000, provider.Ac);
            var second = new HomeServiceRuntime(provider, journal: new ControlExecutionJournal(root));
            await second.InitializeAsync(CancellationToken.None);
            var restored = await Apply(second, SettingRequest.Official);
            Assert.AreEqual(3000, provider.Ac, "A new process cannot assume ownership from an old hardware epoch.");
            var page = restored.ExecutionState!.Pages.Single(item => item.Page == ControlPageId.Performance);
            Assert.AreEqual(ApplyState.Partial, page.State);
            CollectionAssert.Contains(page.SkippedFields, "acMaxFrequencyMhz");
            CollectionAssert.DoesNotContain(page.ConfirmedFields, "acMaxFrequencyMhz");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Latest_intent_is_serial_and_duplicate_operation_never_repeats_a_write()
    {
        var provider = new HeldProvider();
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var initial = runtime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo), CancellationToken.None);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var latest = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet);
        var pending = Enumerable.Range(0, 19).Select(index => runtime.ExecuteAsync(
            index == 18 ? latest : new SetPerformanceModeCommand(Guid.NewGuid(), index % 2 == 0 ? PerformanceMode.Balanced : PerformanceMode.Turbo),
            CancellationToken.None)).ToArray();
        provider.Release.TrySetResult();
        await Task.WhenAll(pending.Append(initial)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, provider.MaximumConcurrent, "The same hardware cannot receive concurrent writes.");
        Assert.AreEqual(2, provider.Writes, "Only the in-flight write and the latest queued intent execute.");
        Assert.AreEqual(PerformanceMode.Quiet, provider.Mode);
        var original = await pending[^1];
        var repeated = await runtime.ExecuteAsync(latest, CancellationToken.None);
        Assert.AreEqual(original, repeated);
        Assert.AreEqual(2, provider.Writes);
        var key = PresetKey.Create(ControlModeId.Office, 2);
        var envelope = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance,
            key, "Official", JsonSerializer.SerializeToElement(new { useOfficialCpuPolicy = true, temperatureLimitC = 95,
                splWatts = 45, spptWatts = 65, acMaxFrequencyMhz = 4500, dcMaxFrequencyMhz = 4500,
                isBoostEnabled = true, windowsPowerSchemeId = Guid.Empty, acMinActiveCoresPercent = (int?)null,
                dcMinActiveCoresPercent = (int?)null, negativeCurveOptimizer = (int?)null, advancedCpuTuning = (object?)null }), DateTimeOffset.UtcNow)
        { Revision = 1, Settings = new() { ["acMaxFrequencyMhz"] = SettingRequest.Official } };
        var publication = new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [envelope]);
        Assert.IsTrue(PresetValidation.Validate(envelope).IsValid, string.Join(";", PresetValidation.Validate(envelope).Errors));
        var published = await runtime.ExecuteAsync(publication, CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, published.State, published.Error?.Code.ToString());
        Assert.AreEqual(1L, published.CatalogRevision);
        Assert.AreEqual(2, provider.Writes, "Catalog publication is a pure configuration operation.");
        Assert.AreEqual(0, runtime.Execution.Catalog.Resolve("user-b", []).Length);
        Assert.ThrowsExactly<InvalidDataException>(() => runtime.Execution.Catalog.Resolve("user-b", [new(ControlPageId.Performance, true, key, 1)]));
        var conflict = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [envelope]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, conflict.State);
        Assert.AreEqual(1L, conflict.CatalogRevision);
        Assert.AreEqual("CatalogPublish", conflict.Error!.Details.GetValueOrDefault("stage"));
        Assert.AreEqual("expectedCatalogRevisionMismatch", conflict.Error.Details.GetValueOrDefault("cause"));
        var advancedPublication = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 1, [envelope with { Revision = 2 }]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Applied, advancedPublication.State);
        var olderPublication = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 2, [envelope]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual("presetRevisionRegressed", olderPublication.Error!.Details.GetValueOrDefault("cause"));
        Assert.AreEqual("2", olderPublication.Error.Details[$"presetRevision:{(int)envelope.Page}:{(int)key.Mode}:{key.Slot}"]);
        Assert.AreEqual(HardwareMutation.NoMutation, olderPublication.Mutation);
        Assert.AreEqual(2, provider.Writes, "Catalog conflicts must never execute hardware commands.");
        var recoveryCatalog = new ControlPresetCatalog();
        var otherPage = envelope with { Key = PresetKey.Create(ControlModeId.Office, 3), Revision = 7 };
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope with { Revision = 5 }, otherPage]), "user-a").State);
        var floors = recoveryCatalog.Publish(new(Guid.NewGuid(), 1, [envelope, otherPage with { Revision = 1 }]), "user-a");
        Assert.AreEqual("5", floors.Error!.Details[$"presetRevision:{(int)envelope.Page}:{(int)key.Mode}:{key.Slot}"]);
        Assert.AreEqual("7", floors.Error.Details[$"presetRevision:{(int)otherPage.Page}:{(int)otherPage.Key.Mode}:{otherPage.Key.Slot}"]);
        Assert.AreEqual(1L, recoveryCatalog.Revision("user-a"));
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope]), "user-b").State, "Other user revisions remain isolated.");
        Assert.AreEqual(CommandState.Rejected, recoveryCatalog.Publish(new(Guid.NewGuid(), 0, [envelope with { Revision = 6 }]), "user-a").State, "Rebasing never bypasses catalog CAS.");
        Assert.AreEqual(CommandState.Applied, recoveryCatalog.Publish(new(Guid.NewGuid(), 1, [envelope with { Revision = 6 }, otherPage with { Revision = 8 }]), "user-a").State);
        var future = await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 1, [envelope with { SchemaVersion = 99 }]), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, future.State);
        Assert.AreEqual(2, provider.Writes);
        var changedReplay = await runtime.ExecuteAsync(latest with { Mode = PerformanceMode.Turbo }, CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, changedReplay.State);
        Assert.AreEqual(HardwareMutation.NoMutation, changedReplay.Mutation);
        Assert.AreEqual(2, provider.Writes);
        var pipelineProvider = new PipelineProvider();
        var pipeline = new HomeServiceRuntime(pipelineProvider);
        await pipeline.InitializeAsync(CancellationToken.None);
        long revision = 0;
        PagePresetEnvelope Fixture(SettingRequest setting, int payloadFrequency = 3000) => envelope with
        { Settings = new() { ["acMaxFrequencyMhz"] = setting }, Payload = JsonSerializer.SerializeToElement(
            envelope.Payload.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Name == "acMaxFrequencyMhz" ? JsonSerializer.SerializeToElement(payloadFrequency) : property.Value.Clone())) };
        async Task<CommandResult> Apply(PagePresetEnvelope preset)
        {
            preset = preset with { Revision = ++revision };
            var publishedPreset = await pipeline.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), revision - 1, [preset]), CancellationToken.None, userSid: "user-a");
            Assert.AreEqual(CommandState.Applied, publishedPreset.State, publishedPreset.Error?.Code.ToString());
            Guid id = Guid.NewGuid();
            return await pipeline.ExecuteAsync(new ApplyControlRequestCommand(id, new(id, long.MaxValue, ControlOrigin.PresetUse,
                ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Performance, true, key, revision)])), CancellationToken.None, userSid: "user-a");
        }
        var tuned = await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(CommandState.Applied, tuned.State);
        Assert.AreEqual(3000, pipelineProvider.Ac);
        Assert.AreEqual(2400, pipelineProvider.Dc, "An AC-only request preserves DC.");
        Assert.IsNull(tuned.VerifiedPerformanceMode, "Interface acknowledgement cannot become effective-mode evidence.");
        Assert.AreEqual(1L, tuned.ExecutionState!.Generation, "Generation is assigned by the service.");
        int writes = pipelineProvider.Writes;
        pipelineProvider.ConfirmModes = true;
        await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(writes + 1, pipelineProvider.Writes, "An unconfirmed acknowledgement cannot suppress a fresh native-mode request.");
        writes = pipelineProvider.Writes;
        await Apply(Fixture(SettingRequest.Set(3000)));
        Assert.AreEqual(writes, pipelineProvider.Writes, "Fresh independent mode confirmation and unchanged fields avoid repeat writes.");
        await Apply(Fixture(SettingRequest.Set(2200), 2200));
        var restored = await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(3200, pipelineProvider.Ac, "Repeated own writes preserve the first baseline.");
        Assert.AreEqual(ApplyState.Confirmed, restored.ExecutionState!.Pages[0].State);
        await Apply(Fixture(SettingRequest.Set(3000)));
        pipelineProvider.Ac = 4400;
        writes = pipelineProvider.Writes;
        var external = await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(writes, pipelineProvider.Writes, "Official cannot restore over external changes.");
        Assert.AreEqual(4400, pipelineProvider.Ac);
        Assert.AreEqual(ApplyState.Partial, external.ExecutionState!.Pages[0].State);
        writes = pipelineProvider.Writes;
        Guid forgedId = Guid.NewGuid();
        var forged = await pipeline.ExecuteAsync(new ApplyControlRequestCommand(forgedId, new(forgedId, 0, ControlOrigin.Adaptive,
            ControlModeId.Office, PerformanceMode.Turbo, [])), CancellationToken.None, userSid: "user-a");
        Assert.AreEqual(CommandState.Rejected, forged.State);
        Assert.AreEqual(writes, pipelineProvider.Writes, "Client origins cannot acquire service policy ownership.");
        var wrongCapability = Fixture(SettingRequest.Set(3000));
        pipelineProvider.AllowFrequency = false;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var denied = await Apply(wrongCapability);
        Assert.AreEqual(CommandState.Rejected, denied.State);
        Assert.AreEqual("Preflight", denied.Error!.Details["stage"]);
        Assert.AreEqual("fieldNotWritable:acMaxFrequencyMhz", denied.Error.Details["cause"]);
        Assert.AreEqual(writes, pipelineProvider.Writes, "Required capability failures precede every hardware write.");
        pipelineProvider.AllowFrequency = true;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var optional = Fixture(SettingRequest.Official);
        var advanced = new AdvancedCpuTuningPlan(PboScalar: 2);
        var optionalPayload = optional.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(advanced);
        optional = optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(advanced) }, Payload = JsonSerializer.SerializeToElement(optionalPayload) };
        var partial = await Apply(optional);
        Assert.AreEqual(CommandState.Applied, partial.State, "Unsupported optional advanced settings do not prevent native mode.");
        Assert.AreEqual(ApplyState.Partial, partial.ExecutionState!.Pages[0].State);
        Assert.IsTrue(partial.ExecutionState.Pages[0].SkippedFields.Contains("pboScalar"));
        pipelineProvider.AllowPbo = true; pipelineProvider.AllowCurve = false;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var mixedAdvanced = new AdvancedCpuTuningPlan(FastPptWatts: 65, PboScalar: 2, CurveOptimizerAll: -15,
            PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -15 });
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(mixedAdvanced);
        var mixed = await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(mixedAdvanced) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(2, pipelineProvider.Pbo, "An unavailable SMU/CO field cannot suppress independently verified PBO.");
        Assert.AreEqual(ApplyState.Partial, mixed.ExecutionState!.Pages[0].State);
        CollectionAssert.AreEquivalent(new[] { "advancedCpuTuning", "negativeCurveOptimizer" }, mixed.ExecutionState.Pages[0].SkippedFields);
        int writesBeforeConflict = pipelineProvider.Writes;
        var conflicting = mixedAdvanced with { PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -14 } };
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(conflicting);
        var conflictResult = await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(conflicting) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(CommandState.Rejected, conflictResult.State);
        Assert.AreEqual("conflictingAdvancedField:negativeCurveOptimizer", conflictResult.Error!.Details["cause"]);
        Assert.AreEqual(writesBeforeConflict, pipelineProvider.Writes, "Genuinely different targets remain a zero-write rejection.");
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Official } });
        Assert.AreEqual(1, pipelineProvider.Pbo, "Official restores the owned scalar independently of unsupported SMU fields.");
        pipelineProvider.AllowCurve = true;
        await pipeline.ReinitializeAsync(CancellationToken.None);
        var perCorePlan = new AdvancedCpuTuningPlan(PerCoreCurveOptimizer: new Dictionary<int, int> { [0] = -15 });
        optionalPayload["advancedCpuTuning"] = JsonSerializer.SerializeToElement(perCorePlan);
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Set(perCorePlan) },
            Payload = JsonSerializer.SerializeToElement(optionalPayload) });
        Assert.AreEqual(-15, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1], "Untouched per-core baseline is preserved.");
        await Apply(optional with { Settings = new() { ["advancedCpuTuning"] = SettingRequest.Official } });
        Assert.AreEqual(-10, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1]);
        await Apply(Fixture(SettingRequest.Set(3000)));
        pipeline.Execution.InvalidateHardware();
        await pipeline.ReinitializeAsync(CancellationToken.None);
        await Apply(Fixture(SettingRequest.Official));
        Assert.AreEqual(3000, pipelineProvider.Ac, "A prior hardware epoch cannot restore a baseline.");
        Assert.IsNull(CommandValidation.Validate(new SetCpuTuningBatchCommand(Guid.NewGuid(), [new(null, null, null, null, null, null, null, -30)], true)));
        int uncertainWrites = 0;
        var uncertain = new ControlExecutionCoordinator((request, token, connection) =>
        { uncertainWrites++; return Task.FromResult(new CommandResult(request.OperationId, CommandState.RecoveryRequired, null,
              RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Unknown }); },
            (_, _) => Task.CompletedTask, () => Task.CompletedTask);
        var unresolved = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Turbo);
        var unresolvedResult = await uncertain.ExecuteAsync(unresolved, CancellationToken.None);
        Assert.AreEqual(unresolvedResult, await uncertain.ExecuteAsync(unresolved, CancellationToken.None));
        Assert.AreEqual(1, uncertainWrites, "Unknown operations remain queryable and do not retry hardware.");
        FanPoint[] curvePoints = [new(30, 30), new(60, 60), new(90, 100)];
        var fanBaseline = new HomeControlState(PerformanceMode.Balanced, false, [])
        { ActiveFanControlPlan = new(curvePoints) { Strategy = "Curve" } };
        var fanCommands = ControlPlanCompiler.Commands(ControlPageId.Fan,
            new() { ["maximumRpm"] = SettingRequest.Set(5500) }, fanBaseline);
        var preservedFan = (SetFanControlCommand)fanCommands.Single();
        CollectionAssert.AreEqual(curvePoints, preservedFan.Plan.Points);
        Assert.IsNull(preservedFan.Plan.GpuPoints, "A shared curve remains shared when only its RPM limit changes.");
        Assert.IsNull(CommandValidation.Validate(preservedFan));
        var sharedCommands = ControlPlanCompiler.Commands(ControlPageId.Fan, new()
        { ["strategy"] = SettingRequest.Set("Curve"), ["isShared"] = SettingRequest.Set(true),
          ["shared"] = SettingRequest.Set(curvePoints.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray()) }, fanBaseline);
        Assert.IsNull(CommandValidation.Validate(sharedCommands.Single()));

        var boostFixture = Fixture(SettingRequest.Official) with { Settings = new() { ["isBoostEnabled"] = SettingRequest.Set(false) } };
        var boostPayload = boostFixture.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        boostPayload["isBoostEnabled"] = JsonSerializer.SerializeToElement(false);
        await Apply(boostFixture with { Payload = JsonSerializer.SerializeToElement(boostPayload) });
        Assert.AreEqual(0, pipelineProvider.AcBoost);
        await Apply(boostFixture with { Settings = new() { ["isBoostEnabled"] = SettingRequest.Official } });
        Assert.AreEqual(4, pipelineProvider.AcBoost); Assert.AreEqual(1, pipelineProvider.DcBoost);
        var coFixture = Fixture(SettingRequest.Official) with { Settings = new() { ["negativeCurveOptimizer"] = SettingRequest.Set(-25) } };
        var coPayload = coFixture.Payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        coPayload["negativeCurveOptimizer"] = JsonSerializer.SerializeToElement(-25);
        await Apply(coFixture with { Payload = JsonSerializer.SerializeToElement(coPayload) });
        await Apply(coFixture with { Settings = new() { ["negativeCurveOptimizer"] = SettingRequest.Official } });
        Assert.AreEqual(-10, pipelineProvider.Cores[0]); Assert.AreEqual(5, pipelineProvider.Cores[1], "Exact BIOS CO baseline may include positive offsets.");

        var branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
        var branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        await branchRuntime.ExecuteAsync(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None, userSid: "branch-a");
        var branch = new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet);
        var branchResult = await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, branchResult.State, branchResult.Error?.Code.ToString());
        Assert.AreEqual(4500, branchProvider.Ac); Assert.AreEqual(3300, branchProvider.Dc);
        Assert.IsTrue(branchResult.ExecutionState!.Pages.Single(page => page.Page == ControlPageId.Performance).SkippedFields.Contains("splWatts"));
        Assert.AreEqual(0L, branchRuntime.Execution.Catalog.Revision("branch-a"), "Transient branches never publish or change the stored catalog.");
        int branchWrites = branchProvider.Writes;
        Assert.AreEqual(branchResult, await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a"));
        Assert.AreEqual(branchWrites, branchProvider.Writes);
        Assert.AreEqual(CommandState.Rejected, (await branchRuntime.ExecuteAsync(branch with { Tier = TurboBranchTier.Extreme }, CancellationToken.None, userSid: "branch-a")).State);
        var alienRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-b");
        Assert.AreEqual(CommandState.Rejected, alienRelease.State); Assert.AreEqual(branchWrites, branchProvider.Writes);
        // Unauthorized release admission must not erase the original generation evidence.
        var released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, released.State, released.Error?.Code.ToString());
        Assert.AreEqual(3200, branchProvider.Ac); Assert.AreEqual(2400, branchProvider.Dc);
        Assert.AreEqual(PerformanceMode.Balanced, branchProvider.Mode);
        Assert.AreEqual(4200, branchProvider.FanPlan!.FixedRpm, "Confirmed fan baseline is restored on the service gate.");
        var repeatedRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, repeatedRelease.State, "A successfully released branch remains idempotent within this service process.");
        var alienRepeatedRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-b");
        Assert.AreEqual(CommandState.Rejected, alienRepeatedRelease.State);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, alienRepeatedRelease.Error?.Code);
        var unknownRelease = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.RecoveryRequired, unknownRelease.State);
        Assert.AreEqual(ErrorCode.ConflictDetected, unknownRelease.Error?.Code);
        Assert.AreEqual(HardwareMutation.NoMutation, unknownRelease.Mutation);
        await branchRuntime.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(),
            [new(null, null, null, null, null, null, null, null) { AcMaxFrequencyMhz = 3000 }], true), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(3000, branchProvider.Ac);
        branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
        await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        var restoredBranch = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(3000, branchProvider.Ac, "A temporary branch restores the actual pre-branch override, not its older OEM baseline.");
        Assert.IsTrue(restoredBranch.ExecutionState!.Fields.Values.Any(field => field.Origin == ControlOrigin.ManualLive),
            "Prior still-matching ownership survives a temporary branch.");

        branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
        await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        await branchRuntime.ExecuteAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(), [new(null, null, null, null, null, null, null, null)
            { AcMaxFrequencyMhz = 4400 }], true), CancellationToken.None, userSid: "branch-a");
        branchProvider.FanPlan = new FanControlPlan([]) { Strategy = "Fixed", FixedRpm = 4700 };
        released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(4400, branchProvider.Ac, "Later user CPU edits survive branch exit.");
        Assert.AreEqual(2400, branchProvider.Dc, "Unchanged branch-owned CPU fields restore independently.");
        Assert.AreEqual(4700, branchProvider.FanPlan!.FixedRpm, "Later fan edits survive branch exit.");
        Assert.AreEqual(PerformanceMode.Turbo, branchProvider.Mode, "An intervening generation retires native-mode restoration.");

        foreach (bool changeEpoch in new[] { false, true })
        {
            branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = true };
            branchRuntime = new HomeServiceRuntime(branchProvider);
            await branchRuntime.InitializeAsync(CancellationToken.None);
            branch = new(Guid.NewGuid(), TurboBranchTier.Quiet);
            await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
            if (changeEpoch) branchRuntime.Execution.InvalidateHardware(); else branchProvider.Scheme = Guid.NewGuid();
            branchWrites = branchProvider.Writes;
            released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
            Assert.AreEqual(branchWrites, branchProvider.Writes, "Epoch/scheme changes prohibit stale branch restoration.");
            Assert.IsTrue(released.ExecutionState!.Pages.Any(page => page.State == ApplyState.Partial));
        }
        branchProvider = new PipelineProvider { AllowBranch = true, ConfirmModes = false, ReadFan = false };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branch = new(Guid.NewGuid(), TurboBranchTier.Extreme);
        branchResult = await branchRuntime.ExecuteAsync(branch, CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(ApplyState.SubmittedUnverified, branchResult.ExecutionState!.NativeModeApplyState);
        Assert.AreEqual(ApplyState.SubmittedUnverified, branchResult.ExecutionState.Fields["fan.strongCooling"].WriteState);
        released = await branchRuntime.ExecuteAsync(new ReleaseTurboBranchCommand(Guid.NewGuid(), branch.OperationId), CancellationToken.None, userSid: "branch-a");
        Assert.IsTrue(branchProvider.StrongCooling, "Submission-only fan evidence never authorizes blind restoration.");
        Assert.AreEqual(PerformanceMode.Turbo, branchProvider.Mode, "WMI acknowledgements cannot restore an unknown native baseline.");
        branchProvider = new PipelineProvider { AllowBranch = true, AllowBoost = false };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Rejected, branchResult.State); Assert.AreEqual(0, branchProvider.Writes, "Required dependencies fail before the first mode write.");
        branchProvider = new PipelineProvider { AllowBranch = true, CancelBaselineOnce = true };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Quiet), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Rejected, branchResult.State);
        Assert.AreEqual(HardwareMutation.NoMutation, branchResult.Mutation, "Cancellation before provider dispatch cannot become Unknown.");
        Assert.AreEqual(0, branchProvider.Writes);
        branchProvider = new PipelineProvider { AllowBranch = true, CpuModel = "AMD Ryzen 9 7940HX" };
        branchRuntime = new HomeServiceRuntime(branchProvider);
        await branchRuntime.InitializeAsync(CancellationToken.None);
        branchResult = await branchRuntime.ExecuteAsync(new ApplyTurboBranchCommand(Guid.NewGuid(), TurboBranchTier.Extreme), CancellationToken.None, userSid: "branch-a");
        Assert.AreEqual(CommandState.Applied, branchResult.State);
        Assert.AreEqual(3200, branchProvider.Ac); Assert.AreEqual(2400, branchProvider.Dc, "Unknown CPU profiles retain official Windows frequency policy.");

        string journalRoot = Path.Combine(Path.GetTempPath(), "xisura-control-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ControlExecutionJournal(journalRoot);
            var uncertainState = tuned.ExecutionState! with { NativeModeApplyState = ApplyState.Unknown };
            var identity = new HardwareIdentity("fake", "fake", "fake", "fake");
            await journal.SaveAsync(uncertainState, identity, tuned.OperationId, true, CancellationToken.None, "user-a");
            var afterCrash = await new ControlExecutionJournal(journalRoot).ReadAsync(CancellationToken.None);
            Assert.AreEqual(1, afterCrash.Unresolved.Length);
            Assert.IsNull(afterCrash.LastConfirmedMode);
            Assert.AreEqual("user-a", afterCrash.Unresolved[0].OwnerSid);
            var zeroWriteId = Guid.NewGuid();
            await journal.SaveAsync(uncertainState, identity, zeroWriteId, true, CancellationToken.None, "user-a");
            await journal.ResolveNoMutationAsync(zeroWriteId, null, CancellationToken.None);
            var clearedZeroWrite = await journal.ReadAsync(CancellationToken.None);
            Assert.IsNull(clearedZeroWrite.Current);
            Assert.AreEqual(tuned.OperationId, clearedZeroWrite.Unresolved.Single().OperationId,
                "A zero-write operation with no prior state clears only its own marker and retains older uncertainty.");
            var restartProvider = new PipelineProvider();
            var restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(journalRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            var replay = await restarted.ExecuteAsync(new SetCpuTuningCommand(tuned.OperationId,
                new(null, null, null, 3000, null, null, null, null), false), CancellationToken.None, userSid: "user-a");
            Assert.AreEqual(CommandState.RecoveryRequired, replay.State);
            Assert.AreEqual(HardwareMutation.NoMutation, replay.Mutation);
            Assert.AreEqual(0, restartProvider.Writes, "A durable uncertain operation must not be replayed after restart.");
            await journal.SaveAsync(tuned.ExecutionState!, identity, tuned.OperationId, false, CancellationToken.None);
            Assert.AreEqual(0, (await journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
            string lifecycleRoot = Path.Combine(journalRoot, "lifecycle");
            restartProvider = new PipelineProvider { StrongCooling = true, UncertainFanRelease = true };
            restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(lifecycleRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            await restarted.HandleLifecycleAsync(false, CancellationToken.None);
            Assert.AreEqual(1, restartProvider.Writes);
            var lifecycleJournal = await new ControlExecutionJournal(lifecycleRoot).ReadAsync(CancellationToken.None);
            Assert.AreEqual(nameof(ReleaseFanControlCommand), lifecycleJournal.Unresolved.Single().CommandKind);
            restarted = new HomeServiceRuntime(restartProvider, journal: new ControlExecutionJournal(lifecycleRoot));
            await restarted.InitializeAsync(CancellationToken.None);
            await restarted.HandleLifecycleAsync(true, CancellationToken.None);
            Assert.AreEqual(1, restartProvider.Writes, "Resume/restart must not repeat an uncertain EC handoff.");
            Assert.IsFalse(restarted.IsSuspended);
            pipelineProvider = new PipelineProvider();
            pipeline = new HomeServiceRuntime(pipelineProvider, journal: journal);
            revision = 0;
            await pipeline.InitializeAsync(CancellationToken.None);
            await Apply(Fixture(SettingRequest.Set(3000)));
            string journalPath = Path.Combine(journalRoot, "control-execution.json");
            using (var blocked = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                int beforeWrites = pipelineProvider.Writes;
                var rejectedWrite = await pipeline.ExecuteAsync(new SetCpuTuningCommand(Guid.NewGuid(),
                    new(null, null, null, 2000, null, null, null, null), false), CancellationToken.None);
                Assert.AreEqual(HardwareMutation.NoMutation, rejectedWrite.Mutation);
                Assert.AreEqual(beforeWrites, pipelineProvider.Writes);
            }
            await Apply(Fixture(SettingRequest.Set(2500), 2500));
            string diagnosticSummary = JsonSerializer.Serialize(await pipeline.ReadControlDiagnosticsAsync(CancellationToken.None, "user-b"));
            Assert.IsFalse(diagnosticSummary.Contains("Baseline", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(diagnosticSummary.Contains("user-a", StringComparison.OrdinalIgnoreCase));
            await Apply(Fixture(SettingRequest.Official));
            Assert.AreEqual(3200, pipelineProvider.Ac, "Failed pre-write journaling must retain the original 3200 baseline.");
        }
        finally { if (Directory.Exists(journalRoot)) Directory.Delete(journalRoot, true); }
    }

    private sealed class HeldProvider : IHomeHardwareProvider
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;
        public int MaximumConcurrent;
        private int active;
        public PerformanceMode Mode = PerformanceMode.Balanced;
        private HomeHardwareState Snapshot => new(new([new("performanceMode", CapabilityState.Available, null)]),
            DeviceSupportState.Ready, null, new("fake", "fake", "fake", "fake"), null)
        { Controls = new(Mode, false, []) };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Snapshot.Controls);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", 40, 40, 10, 10));
        public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            int concurrency = Interlocked.Increment(ref active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, concurrency);
            int sequence = Interlocked.Increment(ref Writes);
            try
            {
                if (sequence == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
                Mode = ((SetPerformanceModeCommand)command).Mode;
                return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed, VerifiedPerformanceMode = Mode };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class PipelineProvider : IHomeHardwareProvider
    {
        public int Ac = 3200, Dc = 2400, Writes, GpuClockWrites, GpuCoreOffset, GpuMemoryOffset;
        public int[] GpuDeltas = new int[127];
        public int BaselineReads;
        public Action? AfterBaseline;
        public Action<HardwareCommand>? AfterWrite;
        public bool RejectModeWithoutMutation;
        public bool RejectFanRestoreWithoutMutation;
        public HardwareMutation RejectedFanMutation = HardwareMutation.NoMutation;
        public HardwareMutation RejectedModeMutation = HardwareMutation.NoMutation;
        public CommandState RejectedModeState = CommandState.Rejected;
        public bool AllowFrequency = true;
        public bool AllowGpuClock = true;
        public bool AllowBranch, AllowBoost = true, ReadFan = true;
        public bool AllowOemSpl, ReadOemSpl = true;
        public int Spl = 65;
        public bool CancelBaselineOnce;
        public bool UncertainFanRelease;
        public string CpuModel = "AMD Ryzen 9 7745HX";
        public bool StrongCooling;
        public FanControlPlan? FanPlan = new([new(40, 60), new(95, 100)]) { Strategy = "Fixed", FixedRpm = 4200 };
        public bool AllowPbo, AllowCurve = true;
        public int Pbo = 1;
        public bool ConfirmModes;
        public Func<PerformanceModeEvidence?>? NativeModeRead;
        public int NativeModeReads;
        public int AcBoost = 4, DcBoost = 1;
        public bool ReadBoostModes = true;
        public Dictionary<int, int> Cores = new() { [0] = -10, [1] = 5 };
        public PerformanceMode Mode = PerformanceMode.Balanced;
        public Guid Scheme = Guid.NewGuid();
        private HomeControlState Controls => new(Mode, StrongCooling, [])
        { ActiveFanControlPlan = FanPlan, CpuTuning = new(null, AllowOemSpl && ReadOemSpl ? Spl : null, null, Ac, AcBoost != 0 && DcBoost != 0, Cores.Count, Scheme,
            Cores.Values.Distinct().Count() == 1 ? Cores.Values.First() : null, "hardwareReadback")
          { AcMaxFrequencyMhz = Ac, DcMaxFrequencyMhz = Dc, AcBoostMode = ReadBoostModes ? AcBoost : null, DcBoostMode = ReadBoostModes ? DcBoost : null,
            PerCoreCurveOptimizer = new Dictionary<int, int>(Cores), PboScalar = Pbo },
          GpuClockLimit = new(210, 3105, null, false, null),
          GpuVf = new(Enumerable.Range(0, 127).Select(index => new GpuVfNode(400 + index * 5, 1500, GpuDeltas[index])).ToArray(),
              -200_000, 200_000, null) { CoreOffsetKhz = GpuCoreOffset, MemoryOffsetKhz = GpuMemoryOffset } };
        private HomeHardwareState Snapshot => new(new([new("performanceMode", CapabilityState.Available, null), new("cpuTuning", CapabilityState.Available, null), new("gpuFrequencyLimit", CapabilityState.Available, null), new("gpuCoreOffset", CapabilityState.Available, null), new("gpuMemoryOffset", CapabilityState.Available, null), new("gpuVfCurve", CapabilityState.Available, null), new("fanControl", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, null), new("strongCooling", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, null)])
        { IsOnAcPower = true,
          Fields = [new("splWatts", AllowOemSpl ? CapabilityState.Available : CapabilityState.Unavailable, AllowOemSpl, AllowOemSpl,
            1, 200, 1, "W", "verifiedOem", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("dcMaxFrequencyMhz", AllowFrequency ? CapabilityState.Available : CapabilityState.ReadOnly, true, AllowFrequency,
            0, 6000, 1, "MHz", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("strategy", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("strongCooling", AllowBranch ? CapabilityState.Available : CapabilityState.Unavailable, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("cpu", CapabilityState.Available, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("shared", CapabilityState.Available, ReadFan, AllowBranch, null, null, null, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("fixedRpm", CapabilityState.Available, ReadFan, AllowBranch, 1500, 6000, 1, "", "verifiedFan", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("acMaxFrequencyMhz", AllowFrequency ? CapabilityState.Available : CapabilityState.ReadOnly, true, AllowFrequency,
            0, 6000, 1, "MHz", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("isBoostEnabled", AllowBoost ? CapabilityState.Available : CapabilityState.Unavailable, true, AllowBoost, null, null, null, "", "windowsPowerApi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("negativeCurveOptimizer", AllowCurve ? CapabilityState.Available : CapabilityState.Unavailable, AllowCurve, AllowCurve, -30, 0, 1, "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("pboScalar", AllowPbo ? CapabilityState.Available : CapabilityState.Unavailable, AllowPbo, AllowPbo, 1, 10, 1, "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("coreOffsetKhz", CapabilityState.Available, true, true, -200_000, 200_000, 1, "kHz", "nvapi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("memoryOffsetKhz", CapabilityState.Available, true, true, -200_000, 200_000, 1, "kHz", "nvapi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("vfOffsetsKhz", CapabilityState.Available, true, true, -200_000, 200_000, 1, "kHz", "nvapi", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0),
            new("coreFrequencyLimitMhz", AllowGpuClock ? CapabilityState.Available : CapabilityState.Unavailable, false, AllowGpuClock,
                210, 3105, 1, "MHz", "driverSubmission", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)] },
            DeviceSupportState.Ready, null, new("fake", "fake", AllowBranch ? CpuModel : "fake", "fake"), null) { Controls = Controls };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token)
        { NativeModeReads++; return Task.FromResult(NativeModeRead?.Invoke()); }
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Controls);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        {
            BaselineReads++;
            if (CancelBaselineOnce) { CancelBaselineOnce = false; throw new OperationCanceledException(); }
            AfterBaseline?.Invoke();
            var controls = Controls;
            return Task.FromResult(includeCpuAdvanced ? controls : controls with
                { CpuTuning = controls.CpuTuning! with { SplWatts = null } });
        }
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", 40, 40, 10, 10));
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            bool staleGpu = command switch
            {
                SetGpuCoreOffsetCommand core => core.ExpectedOffsetKhz != GpuCoreOffset ||
                    core.ExpectedVfOffsetsKhz is { } expectedCurve && !expectedCurve.SequenceEqual(GpuDeltas),
                SetGpuMemoryOffsetCommand memory => memory.ExpectedOffsetKhz != GpuMemoryOffset,
                SetGpuVfCurveCommand curve => !curve.ExpectedOffsetsKhz.SequenceEqual(GpuDeltas),
                _ => false
            };
            if (staleGpu) return Task.FromResult(new CommandResult(command.OperationId, CommandState.Rejected, null,
                RequiredUserAction.None, ServiceError.Create(ErrorCode.ReadBackMismatch, command.OperationId, false), false)
                { Mutation = HardwareMutation.NoMutation });
            if (RejectModeWithoutMutation && command is SetPerformanceModeCommand)
            {
                AfterWrite?.Invoke(command);
                return Task.FromResult(new CommandResult(command.OperationId, RejectedModeState, null, RequiredUserAction.None,
                    Jiaolong.Contracts.Errors.ServiceError.Create(Jiaolong.Contracts.Errors.ErrorCode.HardwareWriteFailed, command.OperationId, false), false)
                    { Mutation = RejectedModeMutation });
            }
            if (RejectFanRestoreWithoutMutation && command is SetFanControlCommand { Plan: { Strategy: "Fixed", FixedRpm: 4200 } })
            {
                AfterWrite?.Invoke(command);
                return Task.FromResult(new CommandResult(command.OperationId,
                    RejectedFanMutation == HardwareMutation.Unknown ? CommandState.RecoveryRequired : CommandState.Rejected, null,
                    RequiredUserAction.None, ServiceError.Create(ErrorCode.HardwareWriteFailed, command.OperationId, false), false)
                    { Mutation = RejectedFanMutation });
            }
            Writes++;
            if (command is ReleaseFanControlCommand && UncertainFanRelease)
                return Task.FromResult(new CommandResult(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                    Jiaolong.Contracts.Errors.ServiceError.Create(Jiaolong.Contracts.Errors.ErrorCode.HardwareWriteFailed, command.OperationId, false), false)
                    { Mutation = HardwareMutation.Unknown });
            switch (command)
            { case SetPerformanceModeCommand request: Mode = request.Mode; break;
              case SetGpuFrequencyLimitCommand: GpuClockWrites++; break;
              case SetGpuCoreOffsetCommand gpu:
                int delta = gpu.OffsetKhz - GpuCoreOffset;
                GpuDeltas = GpuDeltas.Select((value, index) => index == 0 ? value : checked(value + delta)).ToArray();
                GpuCoreOffset = gpu.OffsetKhz; break;
              case SetGpuMemoryOffsetCommand gpu: GpuMemoryOffset = gpu.OffsetKhz; break;
              case SetGpuVfCurveCommand gpu: GpuDeltas = gpu.OffsetsKhz.ToArray(); break;
              case SetFanControlCommand fan: FanPlan = fan.Plan; StrongCooling = false; break;
              case ReleaseFanControlCommand: FanPlan = null; StrongCooling = false; break;
              case SetStrongCoolingCommand cooling: StrongCooling = cooling.Enabled; break;
              case SetCpuTuningBatchCommand batch: foreach (var plan in batch.Plans) ApplyCpu(plan); break;
              case SetCpuTuningCommand single: ApplyCpu(single.Plan); break; }
            AfterWrite?.Invoke(command);
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
            { Mutation = HardwareMutation.Changed, VerifiedPerformanceMode = command is SetPerformanceModeCommand && ConfirmModes ? Mode : null });
        }
        private void ApplyCpu(CpuTuningPlan plan)
        {
            Spl = plan.SplWatts ?? Spl;
            Ac = plan.AcMaxFrequencyMhz ?? Ac; Dc = plan.DcMaxFrequencyMhz ?? Dc;
            if (plan.BoostPolicy is { } policy) { AcBoost = policy.AcMode; DcBoost = policy.DcMode; }
            else if (plan.BoostEnabled is bool boost) { AcBoost = boost ? AcBoost == 0 ? 2 : AcBoost : 0; DcBoost = boost ? DcBoost == 0 ? 2 : DcBoost : 0; }
            if (plan.NegativeCurveOptimizer is int offset) Cores = Cores.Keys.ToDictionary(core => core, _ => offset);
            if (plan.Advanced?.PboScalar is int scalar) Pbo = scalar;
            if (plan.Advanced?.PerCoreCurveOptimizer is { } perCore)
                foreach (var pair in perCore) { Assert.IsTrue(pair.Value <= 0); Cores[pair.Key] = pair.Value; }
        }

        public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline, CpuFieldRestorationValue expected, CancellationToken token)
        {
            Assert.IsTrue(HomeServiceRuntime.SameRestoration(HomeServiceRuntime.CaptureCpuRestoration(field, Controls.CpuTuning), expected));
            if (field == "isBoostEnabled") { AcBoost = baseline.AcBoostMode!.Value; DcBoost = baseline.DcBoostMode!.Value; }
            else Cores = new(baseline.PerCoreCurveOptimizer!);
            Writes++;
            return Task.FromResult(new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed });
        }
    }
}
