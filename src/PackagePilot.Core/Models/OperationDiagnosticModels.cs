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
    public Guid RevisionId { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "Operation diagnostics";
    public string ProviderLabel { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Notice { get; init; } = string.Empty;
    public bool IsTruncated { get; init; }
    public bool HasProviderLog { get; init; }
    public bool HasInstallerLog { get; init; }
    public bool IsLive { get; init; }

    public IReadOnlyList<OperationDiagnosticLine> StructuredLines =>
        OperationDiagnosticLine.Parse(Text);
}

public enum OperationDiagnosticSeverity
{
    Trace,
    Information,
    Warning,
    Error
}

/// <summary>A presentation-safe line from an already redacted diagnostic document.</summary>
public sealed record OperationDiagnosticLine
{
    public int Index { get; init; }
    public string Text { get; init; } = string.Empty;
    public OperationDiagnosticSeverity Severity { get; init; }
    public string Category { get; init; } = string.Empty;

    public static IReadOnlyList<OperationDiagnosticLine> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<OperationDiagnosticLine>();
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select((line, index) => new OperationDiagnosticLine
            {
                Index = index,
                Text = line,
                Severity = DetectSeverity(line),
                Category = DetectCategory(line)
            })
            .ToArray();
    }

    private static OperationDiagnosticSeverity DetectSeverity(string line)
    {
        if (line.Contains("<E>", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" error ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return OperationDiagnosticSeverity.Error;
        }

        if (line.Contains("<W>", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" warning ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
        {
            return OperationDiagnosticSeverity.Warning;
        }

        return string.IsNullOrWhiteSpace(line)
            ? OperationDiagnosticSeverity.Trace
            : OperationDiagnosticSeverity.Information;
    }

    private static string DetectCategory(string line)
    {
        var start = line.IndexOf('[', StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = line.IndexOf(']', start + 1);
        return end > start && end - start <= 16
            ? line[(start + 1)..end].Trim()
            : string.Empty;
    }
}
