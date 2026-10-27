using System.Text.Json;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ExitDeviceDefaultsStoreTests
{
    private static readonly HardwareIdentity Identity = new("MRID6-23", "V39", "CPU", "GPU");
    private const string Machine = "11111111-2222-3333-4444-555555555555";
    private const string Firmware = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static string Key => DeviceRecoveryKey.Create(Machine, Firmware, Identity)!;

    [TestMethod]
    public void Device_key_distinguishes_same_model_machines_and_never_contains_raw_identifiers()
    {
        Assert.HasCount(64, Key);
        Assert.AreEqual(Key, DeviceRecoveryKey.Create(Machine.ToUpperInvariant(), Firmware.ToUpperInvariant(), Identity));
        Assert.AreNotEqual(Key, DeviceRecoveryKey.Create("22222222-2222-3333-4444-555555555555", Firmware, Identity));
        Assert.AreNotEqual(Key, DeviceRecoveryKey.Create(Machine, "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee", Identity));
        Assert.AreNotEqual(Key, DeviceRecoveryKey.Create(Machine, Firmware, Identity with { BiosVersion = "V40" }));
        Assert.IsFalse(Key.Contains(Machine, StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(DeviceRecoveryKey.Create(null, Firmware, Identity));
        Assert.IsNull(DeviceRecoveryKey.Create(Machine, Guid.Empty.ToString(), Identity));
        Assert.IsNull(DeviceRecoveryKey.Create(Machine, "ffffffff-ffff-ffff-ffff-ffffffffffff", Identity));
        Assert.IsNull(DeviceRecoveryKey.Create(Machine, Firmware, Identity with { CpuModel = "" }));
    }

    [TestMethod]
    public void Missing_profile_has_no_global_co_default_and_local_override_is_device_bound()
    {
        var paths = new RecordingPathProvider();
        var store = new ExitDeviceDefaultsStore(paths);
        Assert.IsNull(store.Load(Key));
        Assert.IsNull(store.Load(null));
        store.Save(new(1, Key, -30));
        Assert.AreEqual(-30, new ExitDeviceDefaultsStore(paths).Load(Key)!.CurveOptimizerAll);
        string other = DeviceRecoveryKey.Create("22222222-2222-3333-4444-555555555555", Firmware, Identity)!;
        Assert.IsNull(store.Load(other), "The local override must not become a default for another computer.");
        string file = Directory.GetFiles(Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "exit-defaults"), "*.json").Single();
        string json = File.ReadAllText(file);
        Assert.IsFalse(json.Contains(Machine, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(Firmware, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Invalid_values_and_unknown_or_transplanted_profiles_preserve_the_existing_file()
    {
        var paths = new RecordingPathProvider();
        var store = new ExitDeviceDefaultsStore(paths);
        store.Save(new(1, Key, -30));
        string file = Directory.GetFiles(Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "exit-defaults"), "*.json").Single();
        string original = File.ReadAllText(file);
        foreach (var value in new[] { -51, 1 })
            Assert.ThrowsExactly<ArgumentException>(() => store.Save(new(1, Key, value)));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new(1, "../elsewhere", -30)));
        Assert.AreEqual(original, File.ReadAllText(file));
        foreach (string invalid in new[]
        {
            "{broken", JsonSerializer.Serialize(new ExitDeviceDefaults(2, Key, -30)),
            JsonSerializer.Serialize(new ExitDeviceDefaults(1, new string('A', 64), -30))
        })
        {
            File.WriteAllText(file, invalid);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Load(Key));
            Assert.ThrowsExactly<InvalidDataException>(() => store.Save(new(1, Key, -20)));
            Assert.AreEqual(invalid, File.ReadAllText(file));
        }
    }

    [TestMethod]
    public void Explicit_update_and_removing_the_override_leave_no_temporary_file_or_magic_default()
    {
        var paths = new RecordingPathProvider();
        var store = new ExitDeviceDefaultsStore(paths);
        store.Save(new(1, Key, -30));
        store.Save(new(1, Key, -20));
        Assert.AreEqual(-20, store.Load(Key)!.CurveOptimizerAll);
        store.Save(new(1, Key, null));
        Assert.IsNull(store.Load(Key)!.CurveOptimizerAll);
        string folder = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "exit-defaults");
        Assert.HasCount(1, Directory.GetFiles(folder));
        Assert.IsFalse(Directory.Exists(paths.ProgramDataRoot), "User-specific defaults must not populate a service/global default.");
    }
}
