using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jiaolong.Service.Ipc;

public sealed record PipeAce(string Identity, string Access, bool Allow);

public static class PipeAclFactory
{
    public static IReadOnlyList<PipeAce> Create() =>
    [
        new("SYSTEM", "FullControl", true),
        new("BUILTIN\\Administrators", "ReadWriteSynchronizeCreateNewInstance", true),
        new("INTERACTIVE", "ReadWriteSynchronizeCreateNewInstance", true),
        new("ANONYMOUS LOGON", "FullControl", false),
        new("NETWORK", "FullControl", false)
    ];

    public static PipeSecurity CreateSecurityDescriptor()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        return security;
    }
}
