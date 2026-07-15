namespace PackagePilot.Core.Models;

/// <summary>A safe, UI-neutral destination produced by an external activation.</summary>
public enum AppDestination
{
    Discover,
    Installed,
    Updates,
    Activity,
    Settings,
    Sources
}

/// <summary>
/// An allowlisted request that may be honored by the foreground application. External
/// activation deliberately cannot describe a package or source mutation.
/// </summary>
public sealed record AppActivationRequest
{
    public AppDestination Destination { get; init; } = AppDestination.Discover;
    public string? SearchQuery { get; init; }
    public bool CheckForUpdates { get; init; }
}

public sealed record ActivationParseResult
{
    public AppActivationRequest? Request { get; init; }
    public string? Error { get; init; }

    public bool IsAccepted => Request is not null;

    public static ActivationParseResult Accepted(AppActivationRequest request) => new()
    {
        Request = request
    };

    public static ActivationParseResult Rejected(string error) => new()
    {
        Error = error
    };
}
