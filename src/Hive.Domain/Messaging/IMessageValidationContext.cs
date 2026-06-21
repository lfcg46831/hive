using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public interface IMessageValidationContext
{
    ValueTask<Directive?> FindDirectiveAsync(
        DirectiveId directiveId,
        CancellationToken cancellationToken = default);
}
