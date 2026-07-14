using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed class WingetException : Exception
{
    public WingetException(WingetError error)
        : base(error?.Message)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public WingetException(WingetError error, Exception innerException)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public WingetError Error { get; }
}
