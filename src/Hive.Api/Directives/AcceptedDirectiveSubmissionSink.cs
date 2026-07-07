using Hive.Domain.Messaging;

namespace Hive.Api.Directives;

internal sealed class AcceptedDirectiveSubmissionSink : IDirectiveSubmissionSink
{
    public ValueTask<DirectiveSubmissionResult> SubmitAsync(
        Directive directive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directive);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(DirectiveSubmissionResult.Accepted(directive));
    }
}
