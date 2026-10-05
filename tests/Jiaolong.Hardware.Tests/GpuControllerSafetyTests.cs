using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using System.IO;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class GpuControllerSafetyTests
{
    [TestMethod]
    public async Task Mux_readback_mismatch_restores_and_verifies_previous_mode()
    {
        var transport = new SequenceMuxTransport(MuxMode.Hybrid, MuxMode.Hybrid, MuxMode.Hybrid);
        var controller = new GpuController(transport);

        var result = await controller.ApplyMuxAsync(MuxMode.Discrete, CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(MuxMode.Hybrid, result.VerifiedState?.MuxMode);
        Assert.AreEqual(ErrorCode.ReadBackMismatch, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { MuxMode.Discrete, MuxMode.Hybrid }, transport.Writes);
    }

    [TestMethod]
    public async Task Mux_failed_restore_requires_recovery_and_reports_latest_readback()
    {
        var transport = new SequenceMuxTransport(MuxMode.Hybrid, MuxMode.Hybrid, MuxMode.Discrete);
        var controller = new GpuController(transport);

        var result = await controller.ApplyMuxAsync(MuxMode.Discrete, CancellationToken.None);

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(MuxMode.Discrete, result.VerifiedState?.MuxMode);
        Assert.AreEqual(ErrorCode.RollbackFailed, result.Error?.Code);
    }

    [TestMethod]
    public async Task Mux_write_failure_attempts_a_verified_restore()
    {
        var transport = new WriteThenFailMuxTransport();
        var controller = new GpuController(transport);

        var result = await controller.ApplyMuxAsync(MuxMode.Discrete, CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(MuxMode.Hybrid, result.VerifiedState?.MuxMode);
        Assert.AreEqual(ErrorCode.HardwareWriteFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { MuxMode.Discrete, MuxMode.Hybrid }, transport.Writes);
    }

    [TestMethod]
    public async Task Gpu_limit_readback_mismatch_restores_and_verifies_previous_limit()
    {
        var transport = new GpuMismatchTransport(failRestore: false);
        var controller = new GpuController(limitTransport: transport);

        var result = await controller.ApplyFrequencyLimitAsync(new GpuLimitPlan(1800), CancellationToken.None);

        Assert.AreEqual(CommandState.RolledBack, result.State);
        Assert.AreEqual(1700, result.VerifiedLimitMhz);
        Assert.AreEqual(ErrorCode.ReadBackMismatch, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { 1800, 1700 }, transport.AppliedLimits);
    }

    [TestMethod]
    public async Task Gpu_limit_restore_failure_requires_recovery()
    {
        var transport = new GpuMismatchTransport(failRestore: true);
        var controller = new GpuController(limitTransport: transport);

        var result = await controller.ApplyFrequencyLimitAsync(new GpuLimitPlan(1800), CancellationToken.None);

        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(ErrorCode.RollbackFailed, result.Error?.Code);
        CollectionAssert.AreEqual(new[] { 1800, 1700 }, transport.AppliedLimits);
    }

    private sealed class SequenceMuxTransport(params MuxMode[] reads) : IMuxTransport
    {
        private readonly Queue<MuxMode> readbacks = new(reads);
        public List<MuxMode> Writes { get; } = [];

        public Task<MuxMode> ReadMuxAsync(CancellationToken cancellationToken) =>
            Task.FromResult(readbacks.Dequeue());

        public Task WriteMuxAsync(MuxMode mode, CancellationToken cancellationToken)
        {
            Writes.Add(mode);
            return Task.CompletedTask;
        }
    }

    private sealed class WriteThenFailMuxTransport : IMuxTransport
    {
        private MuxMode current = MuxMode.Hybrid;
        private bool failed;
        public List<MuxMode> Writes { get; } = [];

        public Task<MuxMode> ReadMuxAsync(CancellationToken cancellationToken) => Task.FromResult(current);

        public Task WriteMuxAsync(MuxMode mode, CancellationToken cancellationToken)
        {
            Writes.Add(mode);
            current = mode;
            if (mode == MuxMode.Discrete && !failed)
            {
                failed = true;
                throw new IOException("simulated write acknowledgement failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class GpuMismatchTransport(bool failRestore) : IGpuLimitTransport
    {
        private int? currentLimit = 1700;
        public List<int> AppliedLimits { get; } = [];

        public Task<GpuFrequencyRange> ReadRangeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GpuFrequencyRange(800, 3000));

        public Task ApplyCoreLimitAsync(int minimumMhz, int maximumMhz, CancellationToken cancellationToken)
        {
            AppliedLimits.Add(maximumMhz);
            if (maximumMhz == 1800) currentLimit = 1750;
            else if (failRestore) currentLimit = 1750;
            else currentLimit = maximumMhz;
            return Task.CompletedTask;
        }

        public Task ResetCoreLimitAsync(CancellationToken cancellationToken)
        {
            currentLimit = null;
            return Task.CompletedTask;
        }

        public Task<int?> ReadCoreLimitAsync(CancellationToken cancellationToken) => Task.FromResult(currentLimit);
    }
}
