using System.Security.Cryptography;

namespace Jiaolong.DeviceAcceptance.Stages;

public static class AcceptancePrompt
{
    public static async Task<int> RunAsync(string stage, CancellationToken cancellationToken)
    {
        var phrase = $"JIAOLONG-{RandomNumberGenerator.GetInt32(100000, 1000000)}";
        Console.WriteLine($"Stage: {stage}");
        Console.WriteLine("Fingerprint: unknown (read-only evidence required)");
        Console.WriteLine("Dependency: unknown (service/driver/signature must be shown)");
        Console.WriteLine("Current values: unknown until the physically present user runs the read-only probe");
        Console.WriteLine("No WMI/EC call or hardware write is performed by this software build.");
        Console.WriteLine($"Type the one-time confirmation phrase exactly: {phrase}");
        var entered = await Console.In.ReadLineAsync(cancellationToken);
        if (!string.Equals(entered, phrase, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Confirmation failed; stage stopped before any device action.");
            return 3;
        }

        Console.WriteLine("Confirmation accepted. Live device action remains disabled in this build; no write was issued.");
        return 0;
    }
}
