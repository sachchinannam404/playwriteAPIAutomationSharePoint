using System.Collections.Concurrent;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;
using Microsoft.Extensions.Logging;

namespace ApiAutomation.Data.Repositories;

/// <summary>
/// Decorator that buffers PersistAsync calls and flushes them in parallel batches.
/// CreateRunAsync / CompleteRunAsync pass through immediately; FlushAsync drains the buffer.
/// Use for larger suites to reduce Graph write pressure; still subject to Graph throttling.
/// </summary>
public sealed class BatchedResultRepository : IBatchedResultRepository
{
    private readonly IResultRepository _inner;
    private readonly int _batchSize;
    private readonly ConcurrentQueue<TestExecutionResult> _buffer = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ILogger _log;

    public BatchedResultRepository(IResultRepository inner, int batchSize = 25)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _batchSize = Math.Max(1, batchSize);
        _log = FrameworkLogging.CreateLogger(nameof(BatchedResultRepository));
    }

    public Task CreateRunAsync(string executionId, ExecutionFilter filter, CancellationToken cancellationToken) =>
        _inner.CreateRunAsync(executionId, filter, cancellationToken);

    public async Task PersistAsync(TestExecutionResult result, CancellationToken cancellationToken)
    {
        _buffer.Enqueue(result);
        if (_buffer.Count >= _batchSize)
            await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(ExecutionSummary summary, CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _inner.CompleteRunAsync(summary, cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<TestExecutionResult>();
            while (_buffer.TryDequeue(out var item))
                batch.Add(item);

            if (batch.Count == 0) return;

            _log.LogInformation("Flushing {Count} buffered result writes", batch.Count);
            var tasks = batch.Select(r => _inner.PersistAsync(r, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }
}
