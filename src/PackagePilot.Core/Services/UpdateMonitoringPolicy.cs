using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>Central cadence, freshness, and retry policy for automatic update discovery.</summary>
public static class UpdateMonitoringPolicy
{
    public static readonly TimeSpan DailyFreshness = TimeSpan.FromHours(24);
    public static readonly TimeSpan SixHourFreshness = TimeSpan.FromHours(6);
    public static readonly TimeSpan FailureRetryDelay = TimeSpan.FromHours(1);

    public static UpdateMonitoringCadence ParseCadence(string? value) => value switch
    {
        "manual" => UpdateMonitoringCadence.Manual,
        "sixHours" => UpdateMonitoringCadence.EverySixHours,
        _ => UpdateMonitoringCadence.Daily
    };

    public static string ToSettingValue(UpdateMonitoringCadence cadence) => cadence switch
    {
        UpdateMonitoringCadence.Manual => "manual",
        UpdateMonitoringCadence.EverySixHours => "sixHours",
        _ => "daily"
    };

    public static TimeSpan GetFreshness(UpdateMonitoringCadence cadence) => cadence switch
    {
        UpdateMonitoringCadence.EverySixHours => SixHourFreshness,
        _ => DailyFreshness
    };

    public static UpdateCheckState GetState(
        UpdateSnapshot? snapshot,
        UpdateMonitoringCadence cadence,
        DateTimeOffset now,
        TimeSpan? freshnessOverride = null)
    {
        if (snapshot is null)
        {
            return UpdateCheckState.NotChecked;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastError)
            && snapshot.LastAttemptAt is { } failedAt
            && (snapshot.LastSuccessAt is null || failedAt >= snapshot.LastSuccessAt.Value))
        {
            return UpdateCheckState.Failed;
        }

        if (snapshot.LastSuccessAt is not { } succeededAt)
        {
            return UpdateCheckState.NotChecked;
        }

        var freshness = freshnessOverride ?? GetFreshness(cadence);
        return now >= succeededAt + freshness
            ? UpdateCheckState.Stale
            : UpdateCheckState.Current;
    }

    public static bool ShouldAutomaticallyCheck(
        UpdateSnapshot? snapshot,
        UpdateMonitoringCadence cadence,
        DateTimeOffset now,
        TimeSpan? freshnessOverride = null,
        TimeSpan? failureRetryDelayOverride = null)
    {
        if (cadence == UpdateMonitoringCadence.Manual)
        {
            return false;
        }

        var state = GetState(snapshot, cadence, now, freshnessOverride);
        var failureRetryDelay = failureRetryDelayOverride ?? FailureRetryDelay;
        if (state == UpdateCheckState.Failed
            && snapshot?.LastAttemptAt is { } attemptedAt
            && now < attemptedAt + failureRetryDelay)
        {
            return false;
        }

        return state is UpdateCheckState.NotChecked or UpdateCheckState.Stale or UpdateCheckState.Failed;
    }
}
