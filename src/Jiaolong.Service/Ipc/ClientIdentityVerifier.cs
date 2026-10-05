using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Jiaolong.Service.Ipc;

public enum ClientTokenKind
{
    Interactive,
    Network,
    Anonymous,
    NonInteractive
}

public sealed record ClientIdentity(
    string UserSid,
    int SessionId,
    bool IsLocal,
    bool IsAdministrator,
    ClientTokenKind TokenKind)
{
    public Guid ConnectionId { get; init; }
    public static ClientIdentity LocalInteractive(string sid, int sessionId) => new(sid, sessionId, true, false, ClientTokenKind.Interactive);

    public static ClientIdentity LocalAdministrator(string sid, int sessionId) => new(sid, sessionId, true, true, ClientTokenKind.Interactive);

    public static ClientIdentity Network(string sid) => new(sid, -1, false, false, ClientTokenKind.Network);

    public static ClientIdentity Anonymous() => new(string.Empty, -1, false, false, ClientTokenKind.Anonymous);

    public static ClientIdentity NonInteractive(string sid) => new(sid, -1, true, false, ClientTokenKind.NonInteractive);
}

public interface IClientIdentityVerifier
{
    ValueTask<ClientIdentity> VerifyAsync(SafePipeHandle pipeHandle, CancellationToken cancellationToken);
}

public sealed class ClientIdentityVerifier : IClientIdentityVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static bool IsAuthorized(ClientIdentity identity) =>
        identity.IsLocal &&
        identity.SessionId >= 0 &&
        (identity.IsAdministrator || identity.TokenKind == ClientTokenKind.Interactive) &&
        identity.TokenKind == ClientTokenKind.Interactive;

    public ValueTask<ClientIdentity> VerifyAsync(SafePipeHandle pipeHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pipeHandle is null || pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            throw new UnauthorizedAccessException("Invalid pipe client identity.");
        }

        if (!GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out var processId) || processId == 0)
        {
            throw new UnauthorizedAccessException("Pipe client identity could not be established.");
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Pipe client process could not be opened.");
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token) || token == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException("Pipe client token could not be opened.");
            }

            try
            {
                var sid = ReadTokenSid(token);
                var sessionId = ReadTokenInt32(token, TokenInformationClass.TokenSessionId);
                var tokenType = (TokenType)ReadTokenInt32(token, TokenInformationClass.TokenType);
                var isAdministrator = IsAdministrator(token);
                var kind = tokenType == TokenType.Primary && sessionId > 0
                    ? ClientTokenKind.Interactive
                    : ClientTokenKind.NonInteractive;
                var result = new ClientIdentity(sid, sessionId, true, isAdministrator, kind);
                if (!IsAuthorized(result))
                {
                    throw new UnauthorizedAccessException("Pipe client is not an allowed local interactive identity.");
                }

                return ValueTask.FromResult(result);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string ReadTokenSid(IntPtr token)
    {
        var bufferLength = 0u;
        _ = GetTokenInformation(token, TokenInformationClass.TokenUser, IntPtr.Zero, 0, out bufferLength);
        if (bufferLength == 0)
        {
            throw new UnauthorizedAccessException("Pipe client SID is unavailable.");
        }

        var buffer = Marshal.AllocHGlobal((int)bufferLength);
        try
        {
            if (!GetTokenInformation(token, TokenInformationClass.TokenUser, buffer, bufferLength, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Pipe client SID is unavailable.");
            }

            var sidPointer = Marshal.ReadIntPtr(buffer);
            return new SecurityIdentifier(sidPointer).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadTokenInt32(IntPtr token, TokenInformationClass informationClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, sizeof(int), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Pipe client token information is unavailable.");
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsAdministrator(IntPtr token)
    {
        var bufferLength = 0u;
        _ = GetTokenInformation(token, TokenInformationClass.TokenGroups, IntPtr.Zero, 0, out bufferLength);
        if (bufferLength == 0)
        {
            throw new UnauthorizedAccessException("Pipe client group information is unavailable.");
        }

        var buffer = Marshal.AllocHGlobal((int)bufferLength);
        try
        {
            if (!GetTokenInformation(token, TokenInformationClass.TokenGroups, buffer, bufferLength, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Pipe client group information is unavailable.");
            }

            var groupCount = Marshal.ReadInt32(buffer);
            var firstGroupOffset = IntPtr.Size;
            var groupSize = IntPtr.Size * 2;
            var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
            for (var index = 0; index < groupCount; index++)
            {
                var sidPointer = Marshal.ReadIntPtr(buffer, firstGroupOffset + (index * groupSize));
                if (new SecurityIdentifier(sidPointer).Value == administratorSid)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups = 2,
        TokenSessionId = 12,
        TokenType = 8
    }

    private enum TokenType
    {
        Primary = 1,
        Impersonation = 2
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass informationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
