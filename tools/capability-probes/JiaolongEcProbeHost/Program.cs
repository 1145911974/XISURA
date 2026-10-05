using System.Reflection;
using System.Runtime.Loader;
using System.Security.Principal;
using System.Text.Json;

const string install = @"F:\JiaoLong7.3";
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Probe must run elevated.");

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(install, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

var appDll = Directory.GetFiles(install, "*.dll")
    .Single(path => Path.GetFileName(path).Contains("蛟龙", StringComparison.Ordinal));
Directory.SetCurrentDirectory(install);
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(appDll);
var smuType = assembly.GetType("RyzenSmu.Smu", true)!;
var smu = Activator.CreateInstance(smuType, [true])!;
var read = smuType.GetMethod("Read_EC_EX", BindingFlags.Public | BindingFlags.Instance)!;
var write = smuType.GetMethod("Write_EC_EX", BindingFlags.Public | BindingFlags.Instance)!;
var deinitialize = smuType.GetMethod("Deinitialize", BindingFlags.Public | BindingFlags.Instance)!;

byte Read(ushort address) => (byte)read.Invoke(smu, [address])!;
bool Write(ushort address, byte value) => (bool)write.Invoke(smu, [address, value])!;

var addresses = new ushort[] { 0xC830, 0xC831, 0xC832, 0xC833, 0xC83C, 0xC83D };
var before = addresses.ToDictionary(a => $"0x{a:X4}", Read);
var cpuWrite = Write(0xC83C, before["0xC83C"]);
var gpuWrite = Write(0xC83D, before["0xC83D"]);
var after = addresses.ToDictionary(a => $"0x{a:X4}", Read);
deinitialize.Invoke(smu, null);

var result = new
{
    timestamp = DateTimeOffset.Now,
    elevated = true,
    before,
    writeSucceeded = new { cpu = cpuWrite, gpu = gpuWrite },
    after,
    equal = addresses.All(a => before[$"0x{a:X4}"] == after[$"0x{a:X4}"])
};
await File.WriteAllTextAsync(args.Single(), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
