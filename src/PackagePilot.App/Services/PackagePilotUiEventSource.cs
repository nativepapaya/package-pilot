using System.Diagnostics.Tracing;

namespace PackagePilot.App.Services;

/// <summary>Local ETW timings. EventSource is inert unless a local listener enables it.</summary>
[EventSource(Name = "PackagePilot-UI")]
internal sealed class PackagePilotUiEventSource : EventSource
{
    public static PackagePilotUiEventSource Log { get; } = new();

    [Event(1, Level = EventLevel.Informational)]
    public void FirstFrame(double milliseconds) => WriteEvent(1, milliseconds);

    [Event(2, Level = EventLevel.Informational)]
    public void DestinationActivated(string destination, double milliseconds) =>
        WriteEvent(2, destination, milliseconds);

    [Event(3, Level = EventLevel.Informational)]
    public void FirstContent(string destination, int itemCount) =>
        WriteEvent(3, destination, itemCount);

    [Event(4, Level = EventLevel.Verbose)]
    public void FilterCompleted(string destination, int itemCount, double milliseconds) =>
        WriteEvent(4, destination, itemCount, milliseconds);

    [Event(5, Level = EventLevel.Verbose)]
    public void SearchResultsPresented(int itemCount, double milliseconds) =>
        WriteEvent(5, itemCount, milliseconds);

    [Event(6, Level = EventLevel.Verbose)]
    public void OperationProgressPresented(string operationId, string state) =>
        WriteEvent(6, operationId, state);
}
