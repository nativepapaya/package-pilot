using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.Storage;

namespace PackagePilot.Background;

/// <summary>
/// Best-effort diagnostics for failures that happen outside BackgroundUpdateRunner. This path
/// must never keep the COM host alive or replace a valid cached update snapshot.
/// </summary>
internal static class BackgroundHostStatusReporter
{
    internal static Task TryRecordAsync(
        BackgroundUpdateRunState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return TryRecordAsync(state, Describe(exception));
    }

    internal static async Task TryRecordAsync(
        BackgroundUpdateRunState state,
        string message)
    {
        try
        {
            var store = new JsonBackgroundUpdateRunStatusStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "background-update-status.json"));
            var previous = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var status = new BackgroundUpdateRunStatus
            {
                State = state,
                AttemptedAt = now,
                CompletedAt = now,
                LastSuccessfulRunAt = previous?.LastSuccessfulRunAt,
                UpdateCount = previous?.UpdateCount ?? 0,
                ForegroundFallbackRequired = true,
                NotificationRetryPending = previous?.NotificationRetryPending == true,
                Message = Normalize(message)
            };
            await store.SaveAsync(status, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are best effort; the host watchdog remains authoritative.
        }
    }

    private static string Describe(Exception exception)
    {
        var type = exception.GetType().Name;
        var hresult = $"0x{unchecked((uint)exception.HResult):X8}";
        var detail = string.Join(' ', (exception.Message ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "No error detail was provided by Windows.";
        }

        return Normalize($"{type} ({hresult}): {detail}");
    }

    private static string Normalize(string? message)
    {
        var value = string.Join(' ', (message ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(value))
        {
            return "The background host failed before update discovery could complete.";
        }

        return value.Length <= 512 ? value : value[..512];
    }
}
