namespace Hive.Application.Directives;

/// <summary>
/// Application seam that owns directive execution coordination without exposing actor or provider
/// implementation types.
/// </summary>
public interface IDirectiveExecutionCoordinator
{
    ValueTask<DirectiveExecutionResult> ExecuteAsync(
        DirectiveExecutionRequest request,
        CancellationToken cancellationToken = default);
}
