using System.ComponentModel;
using System.Runtime.CompilerServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.App.ViewModels;

internal sealed record OperationDiagnosticSelection(
    OperationResult? Completed,
    OperationQueueEntry? Live)
{
    public bool IsLive => Live is not null;
}

/// <summary>
/// Owns the selected diagnostic's bounded read lifecycle. Selecting another operation or closing
/// the pane cancels the prior read immediately; no provider log is touched without a selection.
/// </summary>
internal sealed class OperationDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IOperationDiagnosticsService _service;
    private readonly Func<Guid, OperationDiagnosticSelection?> _resolve;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private CancellationTokenSource? _selectionCancellation;
    private Task? _refreshTask;
    private Guid? _operationId;
    private OperationDiagnosticDocument? _document;
    private bool _isLoading;
    private string _error = string.Empty;
    private string _refreshStatus = "Select an activity to view its diagnostic log.";

    public OperationDetailsViewModel(
        IOperationDiagnosticsService service,
        Func<Guid, OperationDiagnosticSelection?> resolve)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public Guid? OperationId
    {
        get => _operationId;
        private set => SetProperty(ref _operationId, value);
    }

    public OperationDiagnosticDocument? Document
    {
        get => _document;
        private set
        {
            if (SetProperty(ref _document, value))
            {
                OnPropertyChanged(nameof(Lines));
            }
        }
    }

    public IReadOnlyList<OperationDiagnosticLine> Lines =>
        Document?.StructuredLines ?? Array.Empty<OperationDiagnosticLine>();

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string RefreshStatus
    {
        get => _refreshStatus;
        private set => SetProperty(ref _refreshStatus, value);
    }

    internal Task? CurrentRefreshTask => _refreshTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task SelectAsync(Guid operationId)
    {
        Close();
        OperationId = operationId;
        var cancellation = new CancellationTokenSource();
        _selectionCancellation = cancellation;
        IsLoading = true;
        Error = string.Empty;
        RefreshStatus = "Loading operation diagnostics...";
        var initialRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshTask = RunRefreshLoopAsync(operationId, cancellation.Token, initialRead);

        try
        {
            await initialRead.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    public async Task RefreshNowAsync()
    {
        if (OperationId is not { } operationId
            || _selectionCancellation is not { } cancellation
            || cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ReadOnceAsync(operationId, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowReadError(exception);
        }
    }

    public void Close()
    {
        var cancellation = Interlocked.Exchange(ref _selectionCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        OperationId = null;
        Document = null;
        IsLoading = false;
        Error = string.Empty;
        RefreshStatus = "Select an activity to view its diagnostic log.";
    }

    public void Dispose()
    {
        Close();
        _readGate.Dispose();
    }

    private async Task RunRefreshLoopAsync(
        Guid operationId,
        CancellationToken cancellationToken,
        TaskCompletionSource initialRead)
    {
        var first = true;
        try
        {
            var refreshLoop = new OperationDiagnosticRefreshLoop();
            var result = await refreshLoop.RunAsync(
                async token =>
                {
                    try
                    {
                        return await ReadOnceAsync(operationId, token).ConfigureAwait(true);
                    }
                    finally
                    {
                        if (first)
                        {
                            first = false;
                            initialRead.TrySetResult();
                        }
                    }
                },
                cancellationToken).ConfigureAwait(true);
            if (result == OperationDiagnosticRefreshLoopResult.LimitReached
                && !cancellationToken.IsCancellationRequested)
            {
                RefreshStatus = "Live auto-refresh paused after five minutes. Refresh manually to continue.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            initialRead.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            initialRead.TrySetResult();
            if (OperationId == operationId)
            {
                ShowReadError(exception);
            }
        }
        finally
        {
            if (first)
            {
                initialRead.TrySetResult();
            }
        }
    }

    private async Task<bool> ReadOnceAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var selection = _resolve(operationId)
                ?? throw new InvalidOperationException("This activity is no longer retained.");
            IsLoading = Document is null;
            var document = selection.Completed is not null
                ? await _service.ReadAsync(selection.Completed, cancellationToken).ConfigureAwait(true)
                : await _service.ReadLiveAsync(selection.Live!, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (OperationId != operationId)
            {
                return false;
            }

            Document = document;
            Error = string.Empty;
            IsLoading = false;
            RefreshStatus = selection.IsLive
                ? $"Live — refreshes every {OperationDiagnosticRefreshLoop.AutomaticRefreshInterval.TotalSeconds:0} seconds while selected"
                : "Completed — refresh manually to re-read retained logs";
            return selection.IsLive;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private void ShowReadError(Exception exception)
    {
        IsLoading = false;
        Error = $"The operation log could not be read (0x{exception.HResult:X8}).";
        RefreshStatus = "Automatic refresh paused. Refresh manually to retry.";
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
