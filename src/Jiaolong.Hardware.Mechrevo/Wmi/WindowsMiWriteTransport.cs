using System.Management;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

public sealed class WindowsMiWriteTransport : IMiWriteTransport
{
    public Task<byte[]?> WriteAsync(
        VerifiedWmiBinding binding,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Length != 32 ||
            !IsSafeWmiPathPart(binding.Namespace) ||
            !IsSafeWmiPathPart(binding.Class) ||
            !IsSafeWmiPathPart(binding.InstanceName))
        {
            return Task.FromResult<byte[]?>(null);
        }

        try
        {
            using var managementObject = new ManagementObject(
                binding.Namespace,
                $"{binding.Class}.InstanceName='{binding.InstanceName}'",
                null);
            var parameters = managementObject.GetMethodParameters("MiInterface");
            parameters.SetPropertyValue("InData", request.ToArray());
            var result = managementObject.InvokeMethod("MiInterface", parameters, null);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null)
                return Task.FromResult<byte[]?>(null);

            var output = result.Properties.Cast<PropertyData>()
                .FirstOrDefault(property => string.Equals(property.Name, "OutData", StringComparison.Ordinal))
                ?.Value as byte[];
            return Task.FromResult<byte[]?>(output ?? Array.Empty<byte>());
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
