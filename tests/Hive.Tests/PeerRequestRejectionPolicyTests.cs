using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Tests;

public sealed class PeerRequestRejectionPolicyTests
{
    private static readonly OrganizationId Org = OrganizationId.From("rejection-policy");
    private static readonly UnitId Source = UnitId.From("source");
    private static readonly UnitId Destination = UnitId.From("destination");

    public static IEnumerable<object[]> StructuralRejections()
    {
        foreach (var error in new[] { RoutingValidationCatalog.PeerChannelRequired(),
            RoutingValidationCatalog.PeerTypeNotAllowed(), RoutingValidationCatalog.PeerChannelLimitExceeded() })
        foreach (var policy in new[] { "absent", "none", "escalate" })
            yield return [error, policy];
    }

    [Theory]
    [MemberData(nameof(StructuralRejections))]
    public async Task Each_structural_reason_requires_an_explicit_directional_escalation_rule(
        ValidationError error, string action)
    {
        using var cancellation = new CancellationTokenSource();
        var contracts = new Contracts(action);
        var policy = new PeerRequestRejectionPolicy(Relations(), contracts);
        var request = Request("requester", "responder");

        Assert.Equal(action == "escalate", await policy.ShouldEscalateAsync(
            request, Rejection(request, error), cancellation.Token));
        Assert.Equal((Org, Source, Destination, cancellation.Token), Assert.Single(contracts.Queries));

        var reverse = Request("responder", "requester");
        Assert.False(await policy.ShouldEscalateAsync(reverse, Rejection(reverse, error), cancellation.Token));
        Assert.Equal((Org, Destination, Source, cancellation.Token), contracts.Queries[1]);
    }

    [Theory]
    [InlineData("requester", "source-lead")]
    [InlineData("missing", "responder")]
    [InlineData("requester", "missing")]
    public async Task Same_unit_or_unknown_positions_do_not_consult_contracts(string from, string to)
    {
        var contracts = new Contracts("escalate") { Failure = new IOException("unavailable") };
        var request = Request(from, to);

        Assert.False(await new PeerRequestRejectionPolicy(Relations(), contracts).ShouldEscalateAsync(
            request, Rejection(request, RoutingValidationCatalog.PeerChannelRequired())));
        Assert.Empty(contracts.Queries);
    }

    [Fact]
    public async Task Correlation_errors_and_invalid_endpoints_never_trigger_policy_queries()
    {
        var contracts = new Contracts("escalate") { Failure = new IOException("unavailable") };
        var policy = new PeerRequestRejectionPolicy(Relations(), contracts);
        var request = Request("requester", "responder");
        foreach (var error in new[] { RoutingValidationCatalog.PeerRequestNotFound(),
            RoutingValidationCatalog.PeerResponderRequired(), RoutingValidationCatalog.PeerRequesterRequired(),
            RoutingValidationCatalog.PeerThreadMismatch(), RoutingValidationCatalog.PeerResponseDuplicate(),
            RoutingValidationCatalog.PeerRequestNotOpen(), RoutingValidationCatalog.PeerRequestExpired() })
            Assert.False(await policy.ShouldEscalateAsync(request, Rejection(request, error)));

        EndpointRef[] invalid = [new OrganizationOwnerEndpointRef(), new SystemEndpointRef(SystemEndpointKind.Scheduler)];
        foreach (var endpoint in invalid)
        foreach (var from in new[] { false, true })
        {
            var malformed = new PeerRequest(MessageId.New(), Org, from ? endpoint : request.From,
                from ? request.To : endpoint, request.Thread, Priority.Normal, 1,
                DateTimeOffset.UnixEpoch, null, "Request");
            Assert.False(await policy.ShouldEscalateAsync(malformed,
                Rejection(malformed, RoutingValidationCatalog.PeerChannelRequired())));
        }
        Assert.Empty(contracts.Queries);
    }

    [Fact]
    public async Task Superior_resolution_uses_direct_command_relation_and_preserves_scope()
    {
        var policy = new PeerRequestRejectionPolicy(Relations(), new Contracts("escalate"));
        Assert.Equal(PositionId.From("destination-lead"),
            await policy.GetSuperiorAsync(Org, PositionId.From("responder")));
        Assert.Null(await policy.GetSuperiorAsync(Org, PositionId.From("source-lead")));
        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(() =>
            policy.GetSuperiorAsync(OrganizationId.From("other"), PositionId.From("responder")).AsTask());
    }

    [Fact]
    public async Task Canceled_policy_check_does_not_resolve_a_channel()
    {
        var contracts = new Contracts("escalate");
        var policy = new PeerRequestRejectionPolicy(Relations(), contracts);
        var request = Request("requester", "responder");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ShouldEscalateAsync(request, Rejection(request, RoutingValidationCatalog.PeerChannelRequired()),
                cancellation.Token).AsTask());
        Assert.Empty(contracts.Queries);
    }

    private static RoutingRejection Rejection(OrgMessage message, ValidationError error) =>
        RoutingRejection.Create(RoutingValidationContext.ForMessage(message), ValidationResult.Create([error]));

    private static PeerRequest Request(string from, string to) => new(MessageId.New(), Org,
        new PositionEndpointRef(PositionId.From(from)), new PositionEndpointRef(PositionId.From(to)),
        ThreadId.New(), Priority.Normal, 1, DateTimeOffset.UnixEpoch, null, "Request");

    private static MaterializedOrganizationRelations Relations() => new(OrganizationRelationsSnapshot
        .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
        .AddPosition(PositionId.From("source-lead"), Source)
        .AddPosition(PositionId.From("requester"), Source, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("destination-lead"), Destination, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("responder"), Destination, PositionId.From("destination-lead"))
        .AddUnitLeadership(Destination, PositionId.From("destination-lead")).Build());

    private sealed class Contracts(string action) : IPeerChannelContracts
    {
        public List<(OrganizationId, UnitId, UnitId, CancellationToken)> Queries { get; } = [];
        public Exception? Failure { get; init; }

        public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
            UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default)
        {
            Queries.Add((organizationId, fromUnitId, toUnitId, cancellationToken));
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult<PeerChannelConfiguration?>(
                action == "absent" || fromUnitId != Source || toUnitId != Destination ? null
                : new(Source, [PeerChannelMessageType.Memo], 1,
                    action == "escalate" ? PeerChannelRejectionAction.Escalate : PeerChannelRejectionAction.None));
        }
    }
}
