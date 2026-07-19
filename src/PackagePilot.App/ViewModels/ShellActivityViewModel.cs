using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.ViewModels;

internal enum ShellActivitySeverity
{
    Informational,
    Success,
    Warning,
    Error
}

internal sealed record ShellActivityState
{
    public static ShellActivityState Hidden { get; } = new();

    public bool IsVisible { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public ShellActivitySeverity Severity { get; init; }
    public double Progress { get; init; }
    public bool IsIndeterminate { get; init; }
    public bool ShowProgress { get; init; }
    public bool IsPersistent { get; init; }
    public Guid? OperationId { get; init; }
    public DateTimeOffset? RefreshAt { get; init; }
}

/// <summary>
/// Projects operation state into the shell's compact activity surface. The projection is pure
/// apart from the explicit reviewed set, which lets a failure remain visible until the user opens
/// Activity without introducing polling or background work.
/// </summary>
internal sealed class ShellActivityViewModel
{
    internal static readonly TimeSpan SuccessVisibility = TimeSpan.FromSeconds(4);
    private readonly HashSet<Guid> _reviewed = [];

    public ShellActivityState Project(
        OperationQueueSnapshot queue,
        IReadOnlyList<MutationVerificationMarker> pendingVerification,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pendingVerification);

        if (queue.Current is { } current)
        {
            var queued = queue.Pending.Count;
            return new ShellActivityState
            {
                IsVisible = true,
                Summary = $"{DisplayName(current.Operation)} — {Phase(current.Progress.State)}",
                Detail = queued switch
                {
                    0 => current.Progress.Message ?? "Package operation in progress",
                    1 => "1 operation queued next",
                    _ => $"{queued} operations queued next"
                },
                Severity = ShellActivitySeverity.Informational,
                Progress = current.Progress.Percent ?? 0,
                IsIndeterminate = current.Progress.Percent is null,
                ShowProgress = true,
                OperationId = current.Operation.Id
            };
        }

        if (queue.Pending.Count > 0)
        {
            var next = queue.Pending[0];
            return new ShellActivityState
            {
                IsVisible = true,
                Summary = queue.Pending.Count == 1
                    ? $"{DisplayName(next.Operation)} — queued"
                    : $"{queue.Pending.Count} package operations queued",
                Detail = queue.Pending.Count == 1
                    ? "Waiting to start"
                    : $"Next: {DisplayName(next.Operation)}",
                Severity = ShellActivitySeverity.Informational,
                Progress = 0,
                IsIndeterminate = false,
                ShowProgress = true,
                OperationId = next.Operation.Id
            };
        }

        var verification = pendingVerification
            .Where(marker => !_reviewed.Contains(marker.OperationId))
            .OrderByDescending(marker => marker.RecordedAt)
            .FirstOrDefault();
        if (verification is not null)
        {
            var restart = verification.Phase is MutationVerificationPhase.RestartRequired
                or MutationVerificationPhase.ApplicationRestartPending;
            return new ShellActivityState
            {
                IsVisible = true,
                Summary = restart
                    ? $"{verification.Package.Name} needs attention"
                    : $"Verifying {verification.Package.Name}",
                Detail = verification.Phase switch
                {
                    MutationVerificationPhase.ApplicationRestartPending =>
                        "Close and reopen the updated app, then check again.",
                    MutationVerificationPhase.RestartRequired =>
                        "Restart Windows to finish verifying this change.",
                    _ => "Confirming the installed package state."
                },
                Severity = restart ? ShellActivitySeverity.Warning : ShellActivitySeverity.Informational,
                Progress = 95,
                IsIndeterminate = !restart,
                ShowProgress = !restart,
                IsPersistent = true,
                OperationId = verification.OperationId
            };
        }

        var terminal = queue.History
            .OrderByDescending(result => result.CompletedAt)
            .FirstOrDefault();
        if (terminal is null || _reviewed.Contains(terminal.OperationId))
        {
            return ShellActivityState.Hidden;
        }

        if (!terminal.IsSuccess)
        {
            return new ShellActivityState
            {
                IsVisible = true,
                Summary = $"{TargetName(terminal)} could not be {PastTense(terminal.Kind)}",
                Detail = terminal.Error?.Message ?? "Review Activity for operation details.",
                Severity = ShellActivitySeverity.Error,
                IsPersistent = true,
                OperationId = terminal.OperationId
            };
        }

        if (terminal.RebootRequired || terminal.State == PackageOperationState.RebootRequired)
        {
            return new ShellActivityState
            {
                IsVisible = true,
                Summary = $"Restart required for {TargetName(terminal)}",
                Detail = "Restart Windows to finish applying this change.",
                Severity = ShellActivitySeverity.Warning,
                IsPersistent = true,
                OperationId = terminal.OperationId
            };
        }

        var expiresAt = terminal.CompletedAt + SuccessVisibility;
        if (expiresAt <= now)
        {
            return ShellActivityState.Hidden;
        }

        return new ShellActivityState
        {
            IsVisible = true,
            Summary = $"{TargetName(terminal)} is current",
            Detail = "Package operation completed successfully.",
            Severity = ShellActivitySeverity.Success,
            Progress = 100,
            ShowProgress = true,
            OperationId = terminal.OperationId,
            RefreshAt = expiresAt
        };
    }

    public void MarkReviewed(OperationQueueSnapshot queue, IEnumerable<MutationVerificationMarker> pending)
    {
        foreach (var result in queue.History)
        {
            _reviewed.Add(result.OperationId);
        }

        foreach (var marker in pending)
        {
            _reviewed.Add(marker.OperationId);
        }
    }

    private static string DisplayName(PackageOperation operation) =>
        string.IsNullOrWhiteSpace(operation.DisplayName)
            ? operation.EffectiveTarget?.Id ?? operation.Package.Id
            : operation.DisplayName;

    private static string TargetName(OperationResult result) =>
        result.EffectiveTarget?.Id ?? result.Package.Id;

    private static string Phase(PackageOperationState state) => state switch
    {
        PackageOperationState.Resolving => "preparing",
        PackageOperationState.Downloading => "downloading",
        PackageOperationState.Installing => "installing",
        PackageOperationState.Upgrading => "installing update",
        PackageOperationState.Uninstalling => "uninstalling",
        _ => state.ToString().ToLowerInvariant()
    };

    private static string PastTense(PackageOperationKind kind) => kind switch
    {
        PackageOperationKind.Install => "installed",
        PackageOperationKind.Upgrade => "updated",
        PackageOperationKind.Uninstall => "removed",
        _ => "changed"
    };
}
