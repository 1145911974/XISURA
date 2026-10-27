using System.Runtime.InteropServices;

namespace Jiaolong.Service.Home;

public sealed record PowerContinuityEvidence(ulong LastWakeTime, ulong LastSleepTime)
{
    internal static bool Valid(PowerContinuityEvidence? value) => value is not null && value.LastWakeTime >= value.LastSleepTime;
}

// Only the two documented read-only levels are admitted; never pass a power-policy input buffer.
public sealed class WindowsPowerContinuityProbe
{
    private readonly Func<int, (uint Status, ulong Value)> read;
    public WindowsPowerContinuityProbe() : this(ReadNative) { }
    internal WindowsPowerContinuityProbe(Func<int, (uint Status, ulong Value)> read) => this.read = read;

    public Task<PowerContinuityEvidence?> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            PowerContinuityEvidence? ReadPair()
            {
                token.ThrowIfCancellationRequested();
                var wake = read(14);
                token.ThrowIfCancellationRequested();
                var sleep = read(15);
                token.ThrowIfCancellationRequested();
                var value = new PowerContinuityEvidence(wake.Value, sleep.Value);
                return wake.Status == 0 && sleep.Status == 0 && PowerContinuityEvidence.Valid(value) ? value : null;
            }
            var first = ReadPair();
            var second = first is null ? null : ReadPair();
            return Task.FromResult(first is not null && first == second ? first : null);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<PowerContinuityEvidence?>(null); }
    }

    private static (uint Status, ulong Value) ReadNative(int level)
    {
        if (level is not (14 or 15)) throw new ArgumentOutOfRangeException(nameof(level));
        uint status = CallNtPowerInformation(level, IntPtr.Zero, 0, out ulong value, sizeof(ulong));
        return (status, value);
    }

    [DllImport("PowrProf.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out ulong output, uint outputLength);
}
