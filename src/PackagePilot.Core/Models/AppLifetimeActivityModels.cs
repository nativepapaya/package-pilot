namespace PackagePilot.Core.Models;

/// <summary>Foreground work that must finish before Package Pilot exits.</summary>
public enum AppLifetimeActivityKind
{
    SourceRefresh,
    SourceMutation,
    WindowsIntegration
}

/// <summary>A thread-safe point-in-time view of exit-sensitive foreground work.</summary>
public sealed record AppLifetimeActivitySnapshot
{
    public AppLifetimeActivityKind? ActiveKind { get; init; }
    public bool IsShutdownCommitted { get; init; }
    public bool HasSourceActivity => ActiveKind is
        AppLifetimeActivityKind.SourceRefresh or
        AppLifetimeActivityKind.SourceMutation;
}
