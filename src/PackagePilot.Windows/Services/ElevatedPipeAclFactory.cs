using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Creates a one-client pipe whose protected DACL grants access only to the initiating Windows
/// user SID. The elevated helper retains that SID across UAC integrity levels, unlike the
/// managed same-token pipe option.
/// </summary>
public static class ElevatedPipeAclFactory
{
    public static NamedPipeServerStream CreateServerForCurrentUser(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The initiating Windows user SID is unavailable.");
        return CreateServer(pipeName, user);
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
    }

    internal static NamedPipeServerStream CreateServer(
        string pipeName,
        SecurityIdentifier initiatingUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(initiatingUser);
        ValidatePipeName(pipeName);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(initiatingUser);
        security.AddAccessRule(new PipeAccessRule(
            initiatingUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None,
            additionalAccessRights: 0);
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!PackagePilot.Core.Services.SourceAdminPipeProtocol.IsValidPipeName(pipeName)
            && !PackagePilot.Core.Services.PackageAdminPipeProtocol.IsValidPipeName(pipeName))
        {
            throw new ArgumentException(
                "The elevated helper pipe name is invalid.",
                nameof(pipeName));
        }
    }
}
