using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace PackagePilot.Windows.Services;

internal interface IWindowsDeploymentEventLogReader
{
    Task<WindowsDeploymentEventLogResult> ReadAsync(
        Guid activityId,
        CancellationToken cancellationToken);
}

internal sealed record WindowsDeploymentEventLogResult(
    Guid ActivityId,
    string ChannelName,
    string Text,
    int EventCount,
    bool IsTruncated,
    bool IsComplete,
    int? NativeErrorCode,
    string? UnavailableMessage)
{
    public bool HasEvents => EventCount > 0;
}

internal static class AppxDeploymentEventLogQuery
{
    public static string Create(Guid activityId)
    {
        if (activityId == Guid.Empty)
        {
            throw new ArgumentException("A deployment activity identifier is required.", nameof(activityId));
        }

        // Activity identifiers in event XML use the braced, uppercase GUID representation.
        // Formatting a Guid here keeps all user-controlled text out of the XPath expression.
        var formattedActivityId = activityId.ToString("B").ToUpperInvariant();
        return $"*[System/Correlation[@ActivityID='{formattedActivityId}']]";
    }
}

internal static class AppxDeploymentRemovalCompletionQuery
{
    internal const int SuccessfulDeploymentEventId = 400;
    internal const string RemoveOperationValue = "2";
    internal const string CallingProcess = "PackagePilot.App.exe";

    public static string Create(string packageFullName, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        if (packageFullName.Length > 255
            || packageFullName.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                "The package full name contains unsupported characters.",
                nameof(packageFullName));
        }

        var systemTime = startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return $"*[System[(EventID={SuccessfulDeploymentEventId}) and " +
            $"TimeCreated[@SystemTime >= '{systemTime}']] and " +
            "EventData[Data[@Name='DeploymentOperation']='" + RemoveOperationValue + "' and " +
            $"Data[@Name='PackageFullName']='{packageFullName}' and " +
            $"Data[@Name='CallingProcess']='{CallingProcess}']]";
    }
}

/// <summary>
/// Reads the AppX deployment events for one DeploymentResult.ActivityId. The query is fixed to
/// the AppX deployment operational channel and bounded so opening diagnostics cannot enumerate
/// unrelated event logs or allocate an unbounded result.
/// </summary>
internal sealed class WindowsDeploymentEventLogReader : IWindowsDeploymentEventLogReader
{
    internal const string ChannelName = "Microsoft-Windows-AppXDeploymentServer/Operational";
    internal const int MaximumEventCount = 200;
    internal const int MaximumRenderedBytes = 1024 * 1024;

    private const int EventBatchSize = 16;
    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryForwardDirection = 0x100;
    private const int EvtQueryReverseDirection = 0x200;
    private const int EvtRenderEventXml = 0x1;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int EvtNextTimeoutMilliseconds = 2000;
    private const int Utf16NewLineBytes = 4;

    public Task<WindowsDeploymentEventLogResult> ReadAsync(
        Guid activityId,
        CancellationToken cancellationToken)
    {
        _ = AppxDeploymentEventLogQuery.Create(activityId);

        return Task.Run(
            () => ReadCore(activityId, cancellationToken),
            cancellationToken);
    }

    internal static Task<Guid?> FindSuccessfulRemovalAsync(
        string packageFullName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        _ = AppxDeploymentRemovalCompletionQuery.Create(packageFullName, startedAt);
        return Task.Run(
            () => FindSuccessfulRemovalCore(packageFullName, startedAt, cancellationToken),
            cancellationToken);
    }

    private static Guid? FindSuccessfulRemovalCore(
        string packageFullName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var queryHandle = EvtQuery(
            nint.Zero,
            ChannelName,
            AppxDeploymentRemovalCompletionQuery.Create(packageFullName, startedAt),
            EvtQueryChannelPath | EvtQueryReverseDirection);
        if (queryHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var handles = new nint[1];
        if (!EvtNext(queryHandle, handles.Length, handles, 0, 0, out var returned))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNoMoreItems)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        try
        {
            if (returned <= 0)
            {
                return null;
            }

            var rendered = RenderEvent(handles[0]);
            if (rendered.ErrorCode is int renderError)
            {
                throw new Win32Exception(renderError);
            }

            return TryGetSuccessfulRemovalActivityId(rendered.Xml, packageFullName);
        }
        finally
        {
            CloseEventHandles(handles, returned);
        }
    }

    internal static Guid? TryGetSuccessfulRemovalActivityId(
        string eventXml,
        string packageFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        if (string.IsNullOrWhiteSpace(eventXml))
        {
            return null;
        }

        var document = XDocument.Parse(eventXml, LoadOptions.None);
        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System");
        var eventData = root.Element(ns + "EventData");
        if (system is null || eventData is null
            || !string.Equals(
                system.Element(ns + "EventID")?.Value,
                AppxDeploymentRemovalCompletionQuery.SuccessfulDeploymentEventId.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return null;
        }

        var values = eventData.Elements(ns + "Data")
            .Where(element => element.Attribute("Name") is not null)
            .ToDictionary(
                element => element.Attribute("Name")!.Value,
                element => element.Value,
                StringComparer.Ordinal);
        if (!values.TryGetValue("DeploymentOperation", out var operation)
            || operation != AppxDeploymentRemovalCompletionQuery.RemoveOperationValue
            || !values.TryGetValue("PackageFullName", out var recordedPackage)
            || !string.Equals(recordedPackage, packageFullName, StringComparison.OrdinalIgnoreCase)
            || !values.TryGetValue("CallingProcess", out var caller)
            || !string.Equals(
                caller,
                AppxDeploymentRemovalCompletionQuery.CallingProcess,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var activity = system.Element(ns + "Correlation")?.Attribute("ActivityID")?.Value;
        return Guid.TryParse(activity, out var activityId) && activityId != Guid.Empty
            ? activityId
            : null;
    }

    private static WindowsDeploymentEventLogResult ReadCore(
        Guid activityId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var queryHandle = EvtQuery(
                nint.Zero,
                ChannelName,
                AppxDeploymentEventLogQuery.Create(activityId),
                EvtQueryChannelPath | EvtQueryForwardDirection);

            if (queryHandle.IsInvalid)
            {
                return Failure(activityId, Marshal.GetLastWin32Error());
            }

            var renderedEvents = new StringBuilder();
            var renderedBytes = 0;
            var eventCount = 0;

            while (eventCount < MaximumEventCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handles = new nint[Math.Min(EventBatchSize, MaximumEventCount - eventCount)];
                if (!EvtNext(
                        queryHandle,
                        handles.Length,
                        handles,
                        EvtNextTimeoutMilliseconds,
                        0,
                        out var returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    return error == ErrorNoMoreItems
                        ? Success(activityId, renderedEvents, eventCount, isTruncated: false)
                        : Failure(activityId, error, renderedEvents, eventCount);
                }

                if (returned <= 0)
                {
                    return Success(activityId, renderedEvents, eventCount, isTruncated: false);
                }

                try
                {
                    for (var index = 0; index < returned; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var rendered = RenderEvent(handles[index]);
                        if (rendered.ErrorCode is int renderError)
                        {
                            return Failure(activityId, renderError, renderedEvents, eventCount);
                        }

                        var separatorBytes = eventCount == 0 ? 0 : Utf16NewLineBytes;
                        if (rendered.ByteCount > MaximumRenderedBytes - renderedBytes - separatorBytes)
                        {
                            return Success(activityId, renderedEvents, eventCount, isTruncated: true);
                        }

                        if (eventCount > 0)
                        {
                            renderedEvents.AppendLine();
                            renderedBytes += Utf16NewLineBytes;
                        }

                        renderedEvents.Append(rendered.Xml);
                        renderedBytes += rendered.ByteCount;
                        eventCount++;
                    }
                }
                finally
                {
                    CloseEventHandles(handles, returned);
                }
            }

            var hasMore = HasAnotherEvent(queryHandle, cancellationToken, out var continuationError);
            if (continuationError is int errorCode)
            {
                return Failure(activityId, errorCode, renderedEvents, eventCount);
            }

            return Success(activityId, renderedEvents, eventCount, isTruncated: hasMore);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException)
        {
            return new WindowsDeploymentEventLogResult(
                activityId,
                ChannelName,
                string.Empty,
                0,
                IsTruncated: false,
                IsComplete: false,
                NativeErrorCode: null,
                $"Windows deployment diagnostics are unavailable: {exception.Message}");
        }
    }

    private static bool HasAnotherEvent(
        SafeEvtHandle queryHandle,
        CancellationToken cancellationToken,
        out int? errorCode)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var handles = new nint[1];
        if (!EvtNext(
                queryHandle,
                handles.Length,
                handles,
                EvtNextTimeoutMilliseconds,
                0,
                out var returned))
        {
            var error = Marshal.GetLastWin32Error();
            errorCode = error == ErrorNoMoreItems ? null : error;
            return false;
        }

        CloseEventHandles(handles, returned);
        errorCode = null;
        return returned > 0;
    }

    private static EventRenderResult RenderEvent(nint eventHandle)
    {
        _ = EvtRender(
            nint.Zero,
            eventHandle,
            EvtRenderEventXml,
            0,
            nint.Zero,
            out var requiredBytes,
            out _);

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
        {
            return new EventRenderResult(string.Empty, 0, error);
        }

        if (requiredBytes <= 0)
        {
            return new EventRenderResult(string.Empty, 0, null);
        }

        // Do not allocate an individual native render buffer larger than the total result cap.
        if (requiredBytes > MaximumRenderedBytes)
        {
            return new EventRenderResult(string.Empty, requiredBytes, null);
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!EvtRender(
                    nint.Zero,
                    eventHandle,
                    EvtRenderEventXml,
                    requiredBytes,
                    buffer,
                    out var renderedBytes,
                    out _))
            {
                return new EventRenderResult(
                    string.Empty,
                    0,
                    Marshal.GetLastWin32Error());
            }

            var charCount = renderedBytes / sizeof(char);
            var xml = Marshal.PtrToStringUni(buffer, charCount)?.TrimEnd('\0') ?? string.Empty;
            return new EventRenderResult(xml, renderedBytes, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CloseEventHandles(nint[] handles, int returned)
    {
        for (var index = 0; index < returned; index++)
        {
            if (handles[index] != nint.Zero)
            {
                _ = EvtClose(handles[index]);
            }
        }
    }

    private static WindowsDeploymentEventLogResult Success(
        Guid activityId,
        StringBuilder renderedEvents,
        int eventCount,
        bool isTruncated) =>
        new(
            activityId,
            ChannelName,
            renderedEvents.ToString(),
            eventCount,
            isTruncated,
            IsComplete: true,
            NativeErrorCode: null,
            UnavailableMessage: null);

    private static WindowsDeploymentEventLogResult Failure(
        Guid activityId,
        int errorCode,
        StringBuilder? renderedEvents = null,
        int eventCount = 0) =>
        new(
            activityId,
            ChannelName,
            renderedEvents?.ToString() ?? string.Empty,
            eventCount,
            IsTruncated: false,
            IsComplete: false,
            errorCode,
            $"Windows could not read deployment diagnostics: {new Win32Exception(errorCode).Message}");

    private readonly record struct EventRenderResult(
        string Xml,
        int ByteCount,
        int? ErrorCode);

    private sealed class SafeEvtHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeEvtHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport(
        "wevtapi.dll",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeEvtHandle EvtQuery(
        nint session,
        string path,
        string query,
        int flags);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(
        SafeEvtHandle resultSet,
        int eventArraySize,
        [Out] nint[] eventArray,
        int timeout,
        int flags,
        out int returned);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(
        nint context,
        nint fragment,
        int flags,
        int bufferSize,
        nint buffer,
        out int bufferUsed,
        out int propertyCount);

    [DllImport("wevtapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint objectHandle);
}
