namespace PackagePilot.Core.Models;

/// <summary>
/// Identifies the supported diagnostic surface for a package operation. The
/// reference is intentionally opaque: persisted history never contains log text or a path.
/// </summary>
public sealed record OperationDiagnosticReference
{
    public OperationDiagnosticProvider Provider { get; init; }
    public Guid ReferenceId { get; init; }
}

public enum OperationDiagnosticProvider
{
    Winget = 1,
    WindowsDeployment = 2
}

/// <summary>A bounded, plain-text diagnostic document loaded only when the user requests it.</summary>
public sealed record OperationDiagnosticDocument
{
    public string Title { get; init; } = "Operation diagnostics";
    public string ProviderLabel { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Notice { get; init; } = string.Empty;
    public bool IsTruncated { get; init; }
    public bool HasProviderLog { get; init; }
    public bool HasInstallerLog { get; init; }
    public bool IsLive { get; init; }
}
