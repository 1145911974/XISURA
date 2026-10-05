namespace Jiaolong.Contracts.Protocol;

public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;
}
