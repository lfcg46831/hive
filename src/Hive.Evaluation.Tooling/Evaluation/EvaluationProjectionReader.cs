namespace Hive.Evaluation.Tooling.Evaluation;

public interface IEvaluationProjectionReader : IAsyncDisposable
{
    Task<EvaluationPrediction?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken);
}

public sealed class NoopEvaluationProjectionReader : IEvaluationProjectionReader
{
    public static NoopEvaluationProjectionReader Instance { get; } = new();

    private NoopEvaluationProjectionReader()
    {
    }

    public Task<EvaluationPrediction?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<EvaluationPrediction?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
