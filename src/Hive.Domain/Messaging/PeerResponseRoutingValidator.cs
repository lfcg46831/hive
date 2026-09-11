namespace Hive.Domain.Messaging;

/// <summary>Validates a response using only the original request, never current channel policy.</summary>
public sealed class PeerResponseRoutingValidator
{
    private readonly IPeerRequestLog _requestLog;
    private readonly TimeProvider _timeProvider;

    public PeerResponseRoutingValidator(IPeerRequestLog requestLog, TimeProvider? timeProvider = null)
    {
        _requestLog = requestLog ?? throw new ArgumentNullException(nameof(requestLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        PeerResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        var errors = EndpointErrors(response);
        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        var record = await _requestLog.FindRequestAsync(
            response.OrganizationId,
            ((PositionEndpointRef)response.To).PositionId,
            response.InReplyTo,
            cancellationToken);
        return Validate(response, record, _timeProvider.GetUtcNow());
    }

    /// <summary>Also used when folding a persisted response, with its persisted receipt time.</summary>
    public static ValidationResult Validate(
        PeerResponse response, PeerRequestRecord? record, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(response);
        var errors = EndpointErrors(response);
        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        if (record is null || record.Request.OrganizationId != response.OrganizationId ||
            record.Request.Id != response.InReplyTo)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PeerRequestNotFound()]);
        }

        var request = record.Request;
        if (response.Thread != request.Thread)
        {
            errors.Add(RoutingValidationCatalog.PeerThreadMismatch());
        }
        if (response.From != request.To)
        {
            errors.Add(RoutingValidationCatalog.PeerResponderRequired());
        }
        if (response.To != request.From)
        {
            errors.Add(RoutingValidationCatalog.PeerRequesterRequired());
        }

        if (record.State == MessageState.Completed)
        {
            errors.Add(RoutingValidationCatalog.PeerResponseDuplicate());
        }
        else if (record.State is MessageState.Rejected or MessageState.Failed)
        {
            errors.Add(RoutingValidationCatalog.PeerRequestNotOpen());
        }
        else if (request.Deadline is { } deadline && deadline <= at)
        {
            errors.Add(RoutingValidationCatalog.PeerRequestExpired());
        }

        return ValidationResult.Create(errors);
    }

    private static List<ValidationError> EndpointErrors(PeerResponse response)
    {
        var errors = new List<ValidationError>();
        if (response.From is not PositionEndpointRef)
        {
            errors.Add(RoutingValidationCatalog.EndpointNotAllowed("from"));
        }
        if (response.To is not PositionEndpointRef)
        {
            errors.Add(RoutingValidationCatalog.EndpointNotAllowed("to"));
        }
        return errors;
    }
}
