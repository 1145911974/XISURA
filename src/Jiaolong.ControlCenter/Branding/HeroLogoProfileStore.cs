using System.Text.Json;

namespace Jiaolong_ControlCenter.Branding;

public static class HeroLogoProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static HeroLogoProfileLoadResult LoadOrDefault(string path)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<HeroLogoProfile>(File.ReadAllText(path), Json)
                ?? throw new JsonException("profile is empty");
            var errors = profile.Validate();
            return errors.Count == 0
                ? new(profile, null)
                : new(HeroLogoProfile.Default, string.Join("; ", errors));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(HeroLogoProfile.Default, error.Message);
        }
    }

    public static void SaveAtomic(string path, HeroLogoProfile profile)
    {
        var errors = profile.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors), nameof(profile));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Json));
            var verified = LoadOrDefault(temporary);
            if (verified.Error is not null || verified.Profile != profile)
                throw new InvalidDataException(verified.Error ?? "profile round-trip changed values");
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string OutputProfilePath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "HeroLogoProfile.json");

    public static string EditableProfilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;
        return directory is null
            ? OutputProfilePath()
            : Path.Combine(directory.FullName, "src", "Jiaolong.ControlCenter", "Assets", "Brand", "HeroLogoProfile.json");
    }
}
