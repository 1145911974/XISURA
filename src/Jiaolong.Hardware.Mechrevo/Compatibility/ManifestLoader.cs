using System.Text;
using System.Text.Json;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Hardware.Mechrevo.Compatibility;

public sealed record LoadedCompatibilityManifest(
    byte[] Content,
    byte[] Signature,
    CompatibilityManifest Manifest);

public sealed class ManifestLoadException(string message) : Exception(message);

public sealed class ManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public LoadedCompatibilityManifest Load(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        var contentCopy = content.ToArray();
        var signatureCopy = signature.ToArray();
        CompatibilityManifest manifest;
        try
        {
            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(contentCopy);
            manifest = JsonSerializer.Deserialize<CompatibilityManifest>(json, JsonOptions)
                ?? throw new ManifestLoadException("Manifest JSON is null.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new ManifestLoadException($"Manifest is not strict UTF-8: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new ManifestLoadException($"Manifest JSON is invalid: {exception.Message}");
        }

        Validate(manifest);
        return new LoadedCompatibilityManifest(contentCopy, signatureCopy, manifest);
    }

    public LoadedCompatibilityManifest LoadFromFiles(string manifestPath, string signaturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        return Load(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath));
    }

    public static byte[] Serialize(CompatibilityManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

    public static LoadedCompatibilityManifest LoadEmbedded()
    {
        var assembly = typeof(ManifestLoader).Assembly;
        var manifest = ReadResource(assembly, ".Compatibility.Manifests.MRID6_23.MRID6_23_P_V39.json");
        var signature = ReadResource(assembly, ".Compatibility.Manifests.MRID6_23.MRID6_23_P_V39.json.p7s");
        return new ManifestLoader().Load(manifest, signature);
    }

    private static byte[] ReadResource(System.Reflection.Assembly assembly, string suffix)
    {
        var name = assembly.GetManifestResourceNames()
            .Single(resourceName => resourceName.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{suffix}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Validate(CompatibilityManifest manifest)
    {
        if (manifest.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.ManifestId) ||
            string.IsNullOrWhiteSpace(manifest.DeviceFamily) ||
            manifest.BoardProductsExact.Length == 0 ||
            manifest.BiosVersionsExact.Length == 0 ||
            string.IsNullOrWhiteSpace(manifest.Cpu.Vendor) ||
            string.IsNullOrWhiteSpace(manifest.Cpu.ModelContains) ||
            manifest.Gpus.Length == 0 ||
            manifest.WmiProvider is null ||
            manifest.Safety.CpuCriticalC <= 0 ||
            manifest.Safety.GpuCriticalC <= 0)
        {
            throw new ManifestLoadException("Manifest is missing required fields.");
        }

        if (manifest.Gpus.Any(gpu => string.IsNullOrWhiteSpace(gpu.VendorId) || string.IsNullOrWhiteSpace(gpu.NameContains)))
        {
            throw new ManifestLoadException("GPU compatibility evidence is incomplete.");
        }

        var forbidden = new[] { "methodName", "ecAddress", "rawPayload" };
        if (!forbidden.All(key => manifest.Migration.ForbiddenLowLevelKeys.Contains(key, StringComparer.Ordinal)))
        {
            throw new ManifestLoadException("Manifest migration rules must forbid low-level keys.");
        }
    }
}
