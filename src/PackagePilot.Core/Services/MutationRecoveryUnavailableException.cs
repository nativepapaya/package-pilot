namespace PackagePilot.Core.Services;

/// <summary>Raised when Package Pilot cannot durably record mutation safety state.</summary>
public sealed class MutationRecoveryUnavailableException : InvalidOperationException
{
    public MutationRecoveryUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
