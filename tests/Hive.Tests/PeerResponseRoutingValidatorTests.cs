using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class PeerResponseRoutingValidatorTests
{
    internal static readonly DateTimeOffset At = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MessageState.Received)]
    [InlineData(MessageState.Accepted)]
    [InlineData(MessageState.Processing)]
    public async Task Open_request_accepts_response_including_refusal_without_channel_queries(MessageState state)
    {
        var request = Request();
        var response = Response(request);
        var log = new Log(new PeerRequestRecord(request, state));
        var validator = new PeerResponseRoutingValidator(log, new Clock(At));

        Assert.Same(ValidationResult.Valid, await validator.ValidateAsync(response));
        Assert.Equal((request.OrganizationId, ((PositionEndpointRef)request.From).PositionId, request.Id),
            log.Query);
        Assert.Equal("Cannot take this on.", response.Body);
    }

    [Fact]
    public async Task Endpoint_errors_are_aggregated_before_reading_the_log()
    {
        var request = Request();
        var log = new Log(null) { Failure = new InvalidOperationException() };
        var response = Response(request, from: new OrganizationOwnerEndpointRef(),
            to: new OrganizationOwnerEndpointRef());
        var result = await new PeerResponseRoutingValidator(log).ValidateAsync(response);
        Assert.Equal([RoutingValidationCatalog.EndpointNotAllowed("from"),
            RoutingValidationCatalog.EndpointNotAllowed("to")], result.Errors);
        Assert.Null(log.Query);
    }

    [Fact]
    public async Task Missing_request_is_confirmed_absence()
    {
        var result = await new PeerResponseRoutingValidator(new Log(null)).ValidateAsync(Response(Request()));
        Assert.Equal([RoutingValidationCatalog.PeerRequestNotFound()], result.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Record_with_wrong_organization_or_request_id_is_not_a_correlation(bool organization)
    {
        var request = Request();
        var wrong = Request(organization: organization ? OrganizationId.From("other") : request.OrganizationId,
            id: organization ? request.Id : MessageId.New(), thread: request.Thread);
        var result = await new PeerResponseRoutingValidator(new Log(new PeerRequestRecord(wrong,
            MessageState.Accepted))).ValidateAsync(Response(request));
        Assert.Equal([RoutingValidationCatalog.PeerRequestNotFound()], result.Errors);
    }

    [Fact]
    public async Task Thread_and_both_original_endpoints_are_checked_independently()
    {
        var request = Request();
        var result = await new PeerResponseRoutingValidator(
            new Log(new PeerRequestRecord(request, MessageState.Accepted)), new Clock(At))
            .ValidateAsync(Response(request, from: Position("wrong-from"), to: Position("wrong-to"),
                thread: ThreadId.New()));
        Assert.Equal(ValidationResult.Create([
            RoutingValidationCatalog.PeerResponderRequired(),
            RoutingValidationCatalog.PeerRequesterRequired(),
            RoutingValidationCatalog.PeerThreadMismatch()]).Errors, result.Errors);
    }

    [Theory]
    [InlineData(MessageState.Completed, "peer-response-duplicate", RejectionReason.Duplicate)]
    [InlineData(MessageState.Rejected, "peer-request-not-open", RejectionReason.InvalidRoute)]
    [InlineData(MessageState.Failed, "peer-request-not-open", RejectionReason.InvalidRoute)]
    [InlineData(MessageState.Accepted, "peer-request-expired", RejectionReason.Expired)]
    public async Task Terminal_state_precedes_expiration(MessageState state, string code, RejectionReason reason)
    {
        var request = Request(deadline: At);
        var result = await new PeerResponseRoutingValidator(
            new Log(new PeerRequestRecord(request, state)), new Clock(At)).ValidateAsync(Response(request));
        Assert.Equal(new ValidationError(code, "inReplyTo", reason), Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Deadline_is_exclusive_and_uses_validation_clock(int ticks, bool valid)
    {
        var request = Request(deadline: At.AddTicks(ticks));
        Assert.Equal(valid, (await new PeerResponseRoutingValidator(
            new Log(new PeerRequestRecord(request, MessageState.Accepted)), new Clock(At))
            .ValidateAsync(Response(request))).IsValid);
    }

    [Fact]
    public async Task No_deadline_remains_open()
    {
        var request = Request();
        Assert.True((await new PeerResponseRoutingValidator(
            new Log(new PeerRequestRecord(request, MessageState.Accepted)), new Clock(At.AddYears(1)))
            .ValidateAsync(Response(request))).IsValid);
    }

    [Theory]
    [InlineData(MessageState.Received)]
    [InlineData(MessageState.Accepted)]
    [InlineData(MessageState.Processing)]
    [InlineData(MessageState.Completed)]
    [InlineData(MessageState.Rejected)]
    [InlineData(MessageState.Failed)]
    public async Task Correlation_mismatches_are_reported_alongside_state_errors_in_both_validation_paths(
        MessageState state)
    {
        var request = Request(deadline: At);
        var record = new PeerRequestRecord(request, state);
        var response = Response(request, from: Position("intruder"), to: Position("another-requester"),
            thread: ThreadId.New());
        var expected = ValidationResult.Create([
            new("peer-responder-required", "from.positionId", RejectionReason.InvalidRoute),
            new("peer-requester-required", "to.positionId", RejectionReason.InvalidRoute),
            new("peer-thread-mismatch", "threadId", RejectionReason.InvalidRoute),
            state switch
            {
                MessageState.Completed => new("peer-response-duplicate", "inReplyTo", RejectionReason.Duplicate),
                MessageState.Rejected or MessageState.Failed => new("peer-request-not-open", "inReplyTo", RejectionReason.InvalidRoute),
                _ => new("peer-request-expired", "inReplyTo", RejectionReason.Expired),
            },
        ]);

        var result = await new PeerResponseRoutingValidator(new Log(record), new Clock(At)).ValidateAsync(response);

        Assert.Equal(expected.Errors, result.Errors);
        Assert.Equal(expected.Errors, PeerResponseRoutingValidator.Validate(response, record, At).Errors);
        var rejection = RoutingRejection.Create(RoutingValidationContext.ForMessage(response), result);
        Assert.All(rejection.PublicResult.Errors, error => Assert.Equal("$", error.Path));
        Assert.DoesNotContain(rejection.PublicResult.Errors, error => error.Code.StartsWith("peer-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failures_and_cancellation_propagate_without_becoming_absence()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("unavailable");
        var log = new Log(null) { Failure = failure };
        var validator = new PeerResponseRoutingValidator(log);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.ValidateAsync(Response(Request()), cancellation.Token).AsTask()));
        Assert.Equal(cancellation.Token, log.Token);

        log.Failure = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => validator.ValidateAsync(Response(Request()), cancellation.Token).AsTask());
        cancellation.Cancel();
        log.Query = null;
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => validator.ValidateAsync(Response(Request()), cancellation.Token).AsTask());
        Assert.Null(log.Query);
    }

    [Fact]
    public async Task Null_arguments_and_undefined_states_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PeerResponseRoutingValidator(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new PeerResponseRoutingValidator(new Log(null)).ValidateAsync(null!).AsTask());
        Assert.Throws<ArgumentNullException>(() => new PeerRequestRecord(null!, MessageState.Accepted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerRequestRecord(Request(), (MessageState)99));
    }

    internal static PeerRequest Request(DateTimeOffset? deadline = null, OrganizationId? organization = null,
        MessageId? id = null, ThreadId? thread = null) => new(
        id ?? MessageId.New(), organization ?? OrganizationId.From("peer-test"),
        Position("requester"), Position("responder"), thread ?? ThreadId.New(), Priority.Normal, 1,
        At.AddMinutes(-1), deadline, "Can you review this?");

    internal static PeerResponse Response(PeerRequest request, EndpointRef? from = null, EndpointRef? to = null,
        ThreadId? thread = null) => new(MessageId.New(), request.OrganizationId, from ?? request.To,
        to ?? request.From, thread ?? request.Thread, Priority.Normal, 1, At.AddMinutes(-1), null,
        request.Id, "Cannot take this on.");

    private static PositionEndpointRef Position(string id) => new(PositionId.From(id));
    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed class Log(PeerRequestRecord? record) : IPeerRequestLog
    {
        public (OrganizationId, PositionId, MessageId)? Query { get; set; }
        public CancellationToken Token { get; private set; }
        public Exception? Failure { get; set; }
        public ValueTask<PeerRequestRecord?> FindRequestAsync(OrganizationId organizationId, PositionId requester,
            MessageId requestId, CancellationToken cancellationToken = default)
        {
            Query = (organizationId, requester, requestId);
            Token = cancellationToken;
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(record);
        }
    }
}
