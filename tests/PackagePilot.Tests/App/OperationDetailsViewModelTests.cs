using PackagePilot.App.ViewModels;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Tests.App;

public sealed class OperationDetailsViewModelTests
{
    [Fact]
    public void StructuredLines_ClassifyProviderSeverityAndCategory()
    {
        var lines = OperationDiagnosticLine.Parse(
            "2026 <I> [CORE] Started\r\n2026 <W> [CLI ] Warning\r\n2026 <E> [FAIL] Error");

        Assert.Equal(3, lines.Count);
        Assert.Equal(OperationDiagnosticSeverity.Information, lines[0].Severity);
        Assert.Equal("CORE", lines[0].Category);
        Assert.Equal(OperationDiagnosticSeverity.Warning, lines[1].Severity);
        Assert.Equal(OperationDiagnosticSeverity.Error, lines[2].Severity);
    }

    [Fact]
    public async Task ChangingSelection_CancelsThePriorLiveRead()
    {
        var service = new BlockingDiagnosticService();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var viewModel = new OperationDetailsViewModel(
            service,
            id => new OperationDiagnosticSelection(null, Entry(id)));

        var firstSelection = viewModel.SelectAsync(firstId);
        await service.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondSelection = viewModel.SelectAsync(secondId);

        await service.FirstReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.ReleaseSecondRead.TrySetResult();
        await Task.WhenAll(firstSelection, secondSelection).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(secondId, viewModel.OperationId);
        Assert.Equal("second", viewModel.Document?.Text);
    }

    [Fact]
    public async Task ClosingPane_CancelsTheSelectedReadImmediately()
    {
        var service = new BlockingDiagnosticService();
        using var viewModel = new OperationDetailsViewModel(
            service,
            id => new OperationDiagnosticSelection(null, Entry(id)));

        var selection = viewModel.SelectAsync(Guid.NewGuid());
        await service.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Close();

        await service.FirstReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await selection.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(viewModel.OperationId);
    }

    private static OperationQueueEntry Entry(Guid id) => new(
        new PackageOperation
        {
            Id = id,
            DisplayName = "Test",
            Package = new PackageKey("Test.App", "source")
        },
        new OperationProgress
        {
            OperationId = id,
            State = PackageOperationState.Installing
        });

    private sealed class BlockingDiagnosticService : IOperationDiagnosticsService
    {
        private int _readCount;
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationDiagnosticDocument> ReadAsync(
            OperationResult operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OperationDiagnosticDocument { Text = "completed" });

        public async Task<OperationDiagnosticDocument> ReadLiveAsync(
            OperationQueueEntry operation,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstReadCancelled.TrySetResult();
                    throw;
                }
            }

            await ReleaseSecondRead.Task.WaitAsync(cancellationToken);
            return new OperationDiagnosticDocument { Text = "second", IsLive = false };
        }

        public Task DeleteOwnedLogsAsync(
            IReadOnlyCollection<OperationDiagnosticReference> diagnostics,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
