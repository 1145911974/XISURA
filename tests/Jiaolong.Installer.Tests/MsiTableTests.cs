using System.Runtime.InteropServices;
using System.Xml.Linq;
using Jiaolong.Service.Installation;

namespace Jiaolong.Installer.Tests;

[TestClass]
public sealed class MsiTableTests
{
    [TestMethod]
    public void Msi_is_per_machine_x64_and_contains_no_oem_binary_or_firewall_row()
    {
        using var msi = MsiFixture.OpenCandidate();

        Assert.AreEqual("x64", msi.TemplatePlatform);
        Assert.IsTrue(msi.IsPerMachine);
        Assert.IsFalse(msi.FileNames.Any(MsiFixture.IsOemBinary));
        Assert.IsFalse(msi.TableExists("WixFirewallException"));
    }

    [TestMethod]
    public void Msi_launch_condition_queries_native_machine()
    {
        using var msi = MsiFixture.OpenCandidate();

        Assert.IsTrue(msi.TableContainsValue("LaunchCondition", "WIX_NATIVE_MACHINE"));
        Assert.IsTrue(msi.TableContainsValue("CustomAction", "NativeMachine"));
    }

    [TestMethod]
    public void Msi_uses_deferred_recovery_configurator_instead_of_failure_action_table()
    {
        using var msi = MsiFixture.OpenCandidate();

        Assert.IsFalse(msi.TableExists("MsiServiceConfigFailureActions"));
        Assert.IsTrue(msi.TableContainsValue("CustomAction", "JiaolongConfigureServiceRecovery"));
        Assert.IsTrue(msi.TableContainsValue("CustomAction", "--configure-service-recovery"));
    }

    [TestMethod]
    public void Msi_removes_program_data_only_when_explicitly_requested()
    {
        using var msi = MsiFixture.OpenCandidate();

        Assert.IsTrue(msi.TableContainsValue("CustomAction", "JiaolongRemoveProgramData"));
        Assert.IsTrue(msi.TableContainsValue("CustomAction", "--remove-data"));
        Assert.IsTrue(msi.TableContainsValue("InstallExecuteSequence", "REMOVE_DATA=1"));
    }

    [TestMethod]
    public void Service_is_localsystem_delayed_auto_unrestricted()
    {
        using var msi = MsiFixture.OpenCandidate();
        var service = msi.GetService("JiaolongControlService");

        Assert.AreEqual("LocalSystem", service.Account);
        Assert.IsTrue(service.IsDelayedAutoStart);
        Assert.AreEqual("unrestricted", service.ServiceSid);
        CollectionAssert.AreEqual(new[] { "SeChangeNotifyPrivilege" }, service.RequiredPrivileges);
    }

    [TestMethod]
    public void Service_recovery_plan_is_three_restarts_at_one_five_and_thirty_seconds()
    {
        CollectionAssert.AreEqual(
            new[] { 1000u, 5000u, 30000u },
            ServiceRecoveryPlan.Actions.Select(action => action.DelayMilliseconds).ToArray());
        CollectionAssert.AreEqual(
            new[] { ServiceRecoveryPlan.RestartActionType, ServiceRecoveryPlan.RestartActionType, ServiceRecoveryPlan.RestartActionType },
            ServiceRecoveryPlan.Actions.Select(action => action.Type).ToArray());
        Assert.AreEqual(86400u, ServiceRecoveryPlan.ResetPeriodSeconds);
    }

    [TestMethod]
    public void Service_configuration_is_nested_under_service_install()
    {
        var source = MsiFixture.FindSource("Service.wxs");
        var document = XDocument.Load(source);
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var serviceInstall = document.Descendants(wix + "ServiceInstall").Single();
        var component = document.Descendants(wix + "Component").Single();

        Assert.IsNotNull(serviceInstall.Element(wix + "ServiceConfig"));
        Assert.IsNull(serviceInstall.Element(wix + "ServiceConfigFailureActions"));
        Assert.IsNull(component.Element(wix + "ServiceConfig"));
        Assert.IsNull(component.Element(wix + "ServiceConfigFailureActions"));
    }
}

internal sealed record MsiServiceInfo(
    string Account,
    bool IsDelayedAutoStart,
    string ServiceSid,
    string[] RequiredPrivileges);

internal sealed class MsiFixture : IDisposable
{
    private readonly dynamic database;

    private MsiFixture(dynamic database) => this.database = database;

    public static MsiFixture OpenCandidate()
    {
        var path = FindCandidate();
        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("Windows Installer COM is unavailable.");
        dynamic installer = Activator.CreateInstance(installerType)
            ?? throw new InvalidOperationException("Windows Installer could not be created.");
        return new MsiFixture(installer.OpenDatabase(path, 0));
    }

    public string TemplatePlatform
    {
        get
        {
            dynamic summary = database.SummaryInformation(20);
            return Convert.ToString(summary.Property[7], System.Globalization.CultureInfo.InvariantCulture)?.Split(';')[0]
                ?? string.Empty;
        }
    }

    public bool IsPerMachine => QueryTable("Property").Any(row =>
        string.Equals(row.FirstOrDefault(), "ALLUSERS", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.Skip(1).FirstOrDefault(), "1", StringComparison.Ordinal));

    public IReadOnlyList<string> FileNames => QueryTable("File").Select(row => row.Skip(1).FirstOrDefault() ?? string.Empty).ToArray();

    public MsiServiceInfo GetService(string name)
    {
        var serviceRow = QueryTable("ServiceInstall").FirstOrDefault(row => row.Any(value => string.Equals(value, name, StringComparison.Ordinal)));
        if (serviceRow is null) throw new InvalidDataException($"Service '{name}' is absent from ServiceInstall.");

        var configRows = QueryTable("MsiServiceConfig");
        return new MsiServiceInfo(
            serviceRow.FirstOrDefault(value => string.Equals(value, "LocalSystem", StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
            configRows.Any(row => row.Count >= 5 && row[3] == "3" && row[4] == "1"),
            configRows.Any(row => row.Count >= 5 && row[3] == "5" && row[4] == "1") ? "unrestricted" :
            configRows.Any(row => row.Count >= 5 && row[3] == "5" && row[4] == "3") ? "restricted" : string.Empty,
            configRows.SelectMany(row => row)
                .Where(value => value.EndsWith("Privilege", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public bool TableExists(string table)
    {
        try
        {
            _ = QueryTable(table);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool TableContainsValue(string table, string value) =>
        QueryTable(table).Any(row => row.Any(item => item.Contains(value, StringComparison.OrdinalIgnoreCase)));

    public static bool IsOemBinary(string fileName) =>
        fileName.Contains("ControlCenterX", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".ocx", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Marshal.FinalReleaseComObject(database);

    private IReadOnlyList<IReadOnlyList<string>> QueryTable(string table)
    {
        dynamic view = database.OpenView($"SELECT * FROM {table}");
        view.Execute();
        var rows = new List<IReadOnlyList<string>>();
        while (true)
        {
            dynamic? record = view.Fetch();
            if (record is null) break;
            var values = new List<string>();
            for (var index = 1; index <= 32; index++)
            {
                try
                {
                    var value = Convert.ToString(record.StringData(index), System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(value)) values.Add(value);
                }
                catch { break; }
            }
            rows.Add(values);
            Marshal.FinalReleaseComObject(record);
        }

        Marshal.FinalReleaseComObject(view);
        return rows;
    }

    private static string FindCandidate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relativePath in new[]
            {
                Path.Combine("bin", "x64", "Release"),
                Path.Combine("bin", "x64", "Debug"),
                Path.Combine("bin", "Release"),
                Path.Combine("bin", "Debug")
            })
            {
                var candidate = Path.Combine(directory.FullName, "installer", "Jiaolong.Installer", relativePath, "Jiaolong.ControlCenter.msi");
                if (File.Exists(candidate)) return candidate;
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException("MSI candidate does not exist; build installer first.");
    }

    public static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "installer", "Jiaolong.Installer", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Installer source does not exist: {fileName}");
    }
}
