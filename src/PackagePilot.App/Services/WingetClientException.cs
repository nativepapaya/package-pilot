using PackagePilot.Core.Models;

namespace PackagePilot.App.Services;

/// <summary>
/// Carries a UI-safe WinGet failure without leaking COM or WinRT types outside the app adapter.
/// </summary>
public sealed class WingetClientException : Exception
{
    public WingetClientException(WingetError error, IReadOnlyList<PackageAgreement>? agreements = null)
        : base(error.Message)
    {
        Error = error;
        Agreements = agreements ?? Array.Empty<PackageAgreement>();
        HResult = error.HResult ?? HResult;
    }

    public WingetClientException(
        WingetError error,
        Exception innerException,
        IReadOnlyList<PackageAgreement>? agreements = null)
        : base(error.Message, innerException)
    {
        Error = error;
        Agreements = agreements ?? Array.Empty<PackageAgreement>();
        HResult = error.HResult ?? innerException.HResult;
    }

    public WingetError Error { get; }

    /// <summary>Agreements that must be presented before retrying an operation.</summary>
    public IReadOnlyList<PackageAgreement> Agreements { get; }
}
