using System.Management;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed class WindowsMiReadTransport : IMiReadTransport
{
    public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSafeWmiPathPart(binding.Namespace) ||
            !IsSafeWmiPathPart(binding.Class) ||
            !IsSafeWmiPathPart(binding.InstanceName))
        {
            return Task.FromResult<byte[]?>(null);
        }

        try
        {
            var request = new byte[32];
            request[1] = checked((byte)binding.ReadType);
            request[3] = binding.MethodName;

            using var managementObject = new ManagementObject(
                binding.Namespace,
                $"{binding.Class}.InstanceName='{binding.InstanceName}'",
                null);
            // Microsoft documents this WMI flow as GetMethodParameters -> set input -> InvokeMethod.
            // https://learn.microsoft.com/en-us/dotnet/api/system.management.managementobject.getmethodparameters
            var parameters = managementObject.GetMethodParameters("MiInterface");
            parameters.SetPropertyValue("InData", request);
            var result = managementObject.InvokeMethod("MiInterface", parameters, null);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result?.GetPropertyValue("OutData") as byte[]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    private static bool IsSafeWmiPathPart(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => char.IsLetterOrDigit(character) || character is '\\' or '_' or '-' or '.');
}
