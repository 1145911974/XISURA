using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Commands;
using Jiaolong.Service.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ClientIdentityVerifierTests
{
    [TestMethod]
    public void Identity_policy_requires_local_interactive_administrator()
    {
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalInteractive("S-1-5-21-1-2-3-4", 1)));
        Assert.IsTrue(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalAdministrator("S-1-5-21-1-2-3-4", 1)));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Network("S-1-5-21-1-2-3-4")));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Anonymous()));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.NonInteractive("S-1-5-21-1-2-3-4")));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalAdministrator("S-1-5-21-1-2-3-4", -1)));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalAdministrator("S-1-5-21-1-2-3-4", 1) with { IsLocal = false }));
    }

    [TestMethod]
    public async Task Standard_user_cannot_dispatch_hardware_write_but_administrator_can()
    {
        var orchestrator = new RecordingOrchestrator();
        var dispatcher = new RequestDispatcher(commandOrchestrator: orchestrator);
        HardwareCommand command = new SetLidLogoCommand(Guid.NewGuid(), true);
        var request = new RequestEnvelope(new ProtocolVersion(1, 1), Guid.NewGuid(), DateTimeOffset.UtcNow,
            command.OperationId, "executeCommand", DateTimeOffset.UtcNow.AddMinutes(1),
            JsonSerializer.SerializeToElement(command, ProtocolJsonContext.Default.HardwareCommand));

        var denied = await dispatcher.DispatchAsync(request, ClientIdentity.LocalInteractive("S-1-5-21-test", 1), CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Error, denied.Status);
        Assert.AreEqual(ErrorCode.UnauthorizedClient, denied.Error?.Code);
        Assert.AreEqual(0, orchestrator.Calls);

        var allowed = await dispatcher.DispatchAsync(request, ClientIdentity.LocalAdministrator("S-1-5-21-test", 1), CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Success, allowed.Status);
        Assert.AreEqual(1, orchestrator.Calls);
    }

    [TestMethod]
    public void Deny_only_administrators_sid_does_not_authorize_process_token()
    {
        using var current = WindowsIdentity.GetCurrent();
        var administratorSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sidBytes = new byte[administratorSid.BinaryLength];
        administratorSid.GetBinaryForm(sidBytes, 0);
        var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
        try
        {
            Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
            var disabledGroup = new SidAndAttributes { Sid = sidPointer };
            if (!CreateRestrictedToken(current.AccessToken, 0, 1, ref disabledGroup, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (restricted)
            {
                var check = typeof(ClientIdentityVerifier).GetMethod("IsAdministrator", BindingFlags.Static | BindingFlags.NonPublic)!;
                Assert.IsFalse((bool)check.Invoke(null, [restricted.DangerousGetHandle()])!,
                    "A disabled/deny-only Administrators SID must not grant hardware-write authority.");
            }
        }
        finally { Marshal.FreeHGlobal(sidPointer); }
    }

    private sealed class RecordingOrchestrator : ICommandOrchestrator
    {
        public int Calls { get; private set; }
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CommandContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, ref SidAndAttributes disableSid, uint deletePrivilegeCount, IntPtr deletePrivileges,
        uint restrictedSidCount, IntPtr restrictedSids, out SafeAccessTokenHandle newToken);
}
