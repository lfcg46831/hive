using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Tests;

public sealed class HorizontalRoutingValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("horizontal-routing");
    private static readonly UnitId Root = UnitId.From("root");
    private static readonly UnitId Delivery = UnitId.From("delivery");

    public static IEnumerable<object[]> InitiationMatrix()
    {
        // Explicit routing expectations: 0 = implicit, 1 = declared direction, 2 = undeclared reverse.
        (string From, string To, int Route)[] routes =
        [
            ("ceo", "assistant", 0), ("assistant", "ceo", 0),
            ("delivery-lead", "engineer", 0), ("engineer", "delivery-lead", 0),
            ("ceo", "delivery-lead", 0), ("delivery-lead", "ceo", 0),
            ("ceo", "engineer", 1), ("assistant", "delivery-lead", 1), ("assistant", "engineer", 1),
            ("engineer", "ceo", 2), ("delivery-lead", "assistant", 2), ("engineer", "assistant", 2),
        ];
        foreach (var (from, to, route) in routes)
        foreach (var request in new[] { false, true })
        foreach (var types in new[] { "absent", "memo", "peer-request", "both" })
        {
            var allowedType = types == "both" || types == (request ? "peer-request" : "memo");
            string? error = route == 0 ? null
                : route == 2 || types == "absent" ? "peer-channel-required"
                : allowedType ? null : "peer-type-not-allowed";
            yield return [from, to, request, types, error!, request && route == 1 && allowedType];
        }
    }

    [Theory]
    [MemberData(nameof(InitiationMatrix))]
    public async Task Initiation_and_limit_resolution_agree_for_every_direction_role_and_type(
        string from, string to, bool request, string types, string? error, bool limited)
    {
        var relations = new MaterializedOrganizationRelations(RelationsSnapshot(Org));
        var channel = types == "absent" ? null : new PeerChannelConfiguration(Root,
            types == "both" ? [PeerChannelMessageType.Memo, PeerChannelMessageType.PeerRequest]
                : [types == "memo" ? PeerChannelMessageType.Memo : PeerChannelMessageType.PeerRequest],
            7, PeerChannelRejectionAction.Escalate);
        var contracts = new MaterializedPeerChannelContracts(ContractsSnapshot(Org, channel));
        var message = Message(request, Position(from), Position(to));

        var result = await Validate(new HorizontalRoutingValidator(relations, contracts), message);

        if (error is null)
            Assert.Same(ValidationResult.Valid, result);
        else
            Assert.Equal(new ValidationError(error,
                error == "peer-type-not-allowed" ? "type" : "to.positionId", RejectionReason.InvalidRoute),
                Assert.Single(result.Errors));

        if (message is PeerRequest peerRequest)
        {
            var capacity = await new PeerRequestLimitResolver(relations, contracts).ResolveAsync(peerRequest);
            Assert.Equal(limited ? new PeerRequestChannel(Root, Delivery, 7) : null, capacity);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_unit_accepts_without_leadership_or_contract_queries(bool request)
    {
        var queries = new Queries();
        var validator = Validator(queries);

        var result = await Validate(validator, request, "engineer", "delivery-lead");

        Assert.Same(ValidationResult.Valid, result);
        Assert.Equal(["position:engineer", "position:delivery-lead"], queries.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leaders_in_both_directions_accept_without_contract_queries(bool request)
    {
        foreach (var (from, to) in new[] { ("ceo", "delivery-lead"), ("delivery-lead", "ceo") })
        {
            var queries = new Queries { FailAt = "contract", Failure = new InvalidOperationException() };

            Assert.True((await Validate(Validator(queries), request, from, to)).IsValid);

            Assert.Equal(4, queries.Calls.Count);
            Assert.DoesNotContain("contract", queries.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_unit_routes_require_a_directional_contract_even_if_one_endpoint_is_a_leader(bool request)
    {
        foreach (var (from, to) in new[]
        {
            ("assistant", "engineer"), ("ceo", "engineer"), ("assistant", "delivery-lead"),
        })
        {
            var queries = new Queries();
            var validator = Validator(queries, Channel(Root, request));

            Assert.True((await Validate(validator, request, from, to)).IsValid);
            Assert.Equal("contract", queries.Calls.Last());

            Assert.Equal(
                [RoutingValidationCatalog.PeerChannelRequired()],
                (await Validate(validator, request, to, from)).Errors);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_channel_is_a_canonical_rejection(bool request)
    {
        var result = await Validate(Validator(new Queries()), request, "assistant", "engineer");

        Assert.Equal(
            [new ValidationError("peer-channel-required", "to.positionId", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channel_allowing_only_the_other_type_rejects(bool request)
    {
        var result = await Validate(
            Validator(new Queries(), Channel(Root, !request)), request, "assistant", "engineer");

        Assert.Equal(
            [new ValidationError("peer-type-not-allowed", "type", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channel_with_both_types_accepts_each_type(bool request)
    {
        var channel = new PeerChannelConfiguration(
            Root, [PeerChannelMessageType.Memo, PeerChannelMessageType.PeerRequest], 1,
            PeerChannelRejectionAction.Escalate);

        Assert.True((await Validate(Validator(new Queries(), channel), request, "assistant", "engineer")).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_endpoint_variants_are_aggregated_before_any_queries(bool request)
    {
        EndpointRef[] invalid = [new OrganizationOwnerEndpointRef(), new SystemEndpointRef(SystemEndpointKind.Scheduler)];
        foreach (var endpoint in invalid)
        {
            var queries = new Queries();
            var validator = Validator(queries);

            Assert.Equal(
                [RoutingValidationCatalog.EndpointNotAllowed("from"), RoutingValidationCatalog.EndpointNotAllowed("to")],
                (await Validate(validator, Message(request, endpoint, endpoint))).Errors);
            Assert.Equal(
                [RoutingValidationCatalog.EndpointNotAllowed("from")],
                (await Validate(validator, Message(request, endpoint, Position("ghost")))).Errors);
            Assert.Equal(
                [RoutingValidationCatalog.EndpointNotAllowed("to")],
                (await Validate(validator, Message(request, Position("ghost"), endpoint))).Errors);
            Assert.Empty(queries.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_positions_are_aggregated_before_leadership_or_contract_queries(bool request)
    {
        foreach (var (from, to) in new[] { ("ghost", "missing"), ("ghost", "engineer"), ("assistant", "missing") })
        {
            var queries = new Queries();
            var expected = new List<ValidationError>();
            if (from == "ghost") expected.Add(RoutingValidationCatalog.PositionNotFound("from.positionId"));
            if (to == "missing") expected.Add(RoutingValidationCatalog.PositionNotFound("to.positionId"));

            Assert.Equal(expected, (await Validate(Validator(queries), request, from, to)).Errors);
            Assert.Equal([$"position:{from}", $"position:{to}"], queries.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_organization_is_a_canonical_rejection(bool request)
    {
        var queries = new Queries();
        var message = Message(request, Position("assistant"), Position("engineer"), OrganizationId.From("unknown"));

        Assert.Equal(
            [RoutingValidationCatalog.OrganizationNotFound()],
            (await Validate(Validator(queries), message)).Errors);
        Assert.Equal(["position:assistant"], queries.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organization_missing_at_second_probe_takes_precedence_over_missing_source(bool request)
    {
        var queries = new Queries
        {
            FailAt = "position:engineer",
            Failure = OrganizationRelationNotFoundException.ForOrganization(Org),
        };

        Assert.Equal(
            [RoutingValidationCatalog.OrganizationNotFound()],
            (await Validate(Validator(queries), request, "ghost", "engineer")).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identical_unit_and_position_ids_in_other_organization_do_not_share_contracts(bool request)
    {
        var other = OrganizationId.From("other");
        var relations = new MaterializedOrganizationRelations([RelationsSnapshot(Org), RelationsSnapshot(other)]);
        var contracts = new MaterializedPeerChannelContracts([
            ContractsSnapshot(Org, Channel(Root, request)), ContractsSnapshot(other),
        ]);
        var validator = new HorizontalRoutingValidator(relations, contracts);

        Assert.True((await Validate(validator, request, "assistant", "engineer")).IsValid);
        Assert.Equal(
            [RoutingValidationCatalog.PeerChannelRequired()],
            (await Validate(validator, Message(request, Position("assistant"), Position("engineer"), other))).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_validation_does_not_query_seams(bool request)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var queries = new Queries();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Validate(
            Validator(queries), Message(request, Position("assistant"), Position("engineer")), cancellation.Token));
        Assert.Empty(queries.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organization_and_cancellation_token_reach_every_query(bool request)
    {
        using var cancellation = new CancellationTokenSource();
        var queries = new Queries();
        var result = await Validate(Validator(queries, Channel(Root, request)),
            Message(request, Position("assistant"), Position("engineer")), cancellation.Token);

        Assert.True(result.IsValid);
        Assert.Equal(["position:assistant", "position:engineer", "leadership:root", "leadership:delivery", "contract"], queries.Calls);
        Assert.All(queries.Organizations, organization => Assert.Equal(Org, organization));
        Assert.All(queries.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Theory]
    [InlineData("position:assistant")]
    [InlineData("position:engineer")]
    [InlineData("leadership:root")]
    [InlineData("leadership:delivery")]
    [InlineData("contract")]
    public async Task Technical_failures_and_cancellation_propagate_at_every_lookup(string failAt)
    {
        foreach (var request in new[] { false, true })
        foreach (var failure in new Exception[] { new InvalidOperationException("Unavailable"), new OperationCanceledException() })
        {
            var queries = new Queries { FailAt = failAt, Failure = failure };

            var thrown = await Record.ExceptionAsync(() => Validate(Validator(queries), request, "assistant", "engineer"));

            Assert.Same(failure, thrown);
            Assert.Equal(failAt, queries.Calls.Last());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structural_failures_after_position_probes_are_not_misreported_as_missing_channels(bool request)
    {
        foreach (var (failAt, failure) in new (string, Exception)[]
        {
            ("leadership:root", OrganizationRelationNotFoundException.ForUnit(Org, Root)),
            ("leadership:delivery", OrganizationRelationNotFoundException.ForUnit(Org, Delivery)),
            ("leadership:root", OrganizationRelationNotFoundException.ForOrganization(Org)),
            ("contract", PeerChannelContractNotFoundException.ForUnit(Org, Root)),
            ("contract", PeerChannelContractNotFoundException.ForUnit(Org, Delivery)),
            ("contract", PeerChannelContractNotFoundException.ForOrganization(Org)),
        })
        {
            var queries = new Queries { FailAt = failAt, Failure = failure };

            Assert.Same(failure, await Record.ExceptionAsync(() => Validate(Validator(queries), request, "assistant", "engineer")));
            Assert.Equal(failAt, queries.Calls.Last());
        }
    }

    [Fact]
    public async Task Missing_dependencies_or_messages_are_api_misuse()
    {
        var relations = new MaterializedOrganizationRelations(RelationsSnapshot(Org));
        var contracts = new MaterializedPeerChannelContracts(ContractsSnapshot(Org));
        Assert.Throws<ArgumentNullException>(() => new HorizontalRoutingValidator(null!, contracts));
        Assert.Throws<ArgumentNullException>(() => new HorizontalRoutingValidator(relations, null!));
        var validator = new HorizontalRoutingValidator(relations, contracts);
        await Assert.ThrowsAsync<ArgumentNullException>(() => validator.ValidateAsync((Memo)null!).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => validator.ValidateAsync((PeerRequest)null!).AsTask());
    }

    private static HorizontalRoutingValidator Validator(Queries queries, PeerChannelConfiguration? channel = null) =>
        new(new RecordingRelations(new MaterializedOrganizationRelations(RelationsSnapshot(Org)), queries),
            new RecordingContracts(new MaterializedPeerChannelContracts(ContractsSnapshot(Org, channel)), queries));

    private static OrganizationRelationsSnapshot RelationsSnapshot(OrganizationId organization) =>
        OrganizationRelationsSnapshot.CreateBuilder(organization, new OrganizationOwnerEndpointRef())
            .AddPosition(PositionId.From("ceo"), Root)
            .AddPosition(PositionId.From("assistant"), Root, PositionId.From("ceo"))
            .AddPosition(PositionId.From("delivery-lead"), Delivery, PositionId.From("ceo"))
            .AddPosition(PositionId.From("engineer"), Delivery, PositionId.From("delivery-lead"))
            .AddUnitLeadership(Delivery, PositionId.From("delivery-lead"))
            .Build();

    private static PeerChannelContractsSnapshot ContractsSnapshot(
        OrganizationId organization, PeerChannelConfiguration? channel = null) =>
        PeerChannelContractsSnapshot.CreateBuilder(organization)
            .AddUnit(Root, []).AddUnit(Delivery, channel is null ? [] : [channel]).Build();

    private static PeerChannelConfiguration Channel(UnitId from, bool request) =>
        new(from, [request ? PeerChannelMessageType.PeerRequest : PeerChannelMessageType.Memo], 1, PeerChannelRejectionAction.None);

    private static PositionEndpointRef Position(string value) => new(PositionId.From(value));

    private static OrgMessage Message(bool request, EndpointRef from, EndpointRef to, OrganizationId? organization = null) =>
        request
            ? new PeerRequest(MessageId.New(), organization ?? Org, from, to, ThreadId.New(), Priority.Normal, 1,
                DateTimeOffset.UnixEpoch, null, "Help with delivery")
            : new Memo(MessageId.New(), organization ?? Org, from, to, ThreadId.New(), Priority.Normal, 1,
                DateTimeOffset.UnixEpoch, null, "Delivery context");

    private static Task<ValidationResult> Validate(HorizontalRoutingValidator validator, bool request, string from, string to) =>
        Validate(validator, Message(request, Position(from), Position(to)));

    private static Task<ValidationResult> Validate(
        HorizontalRoutingValidator validator, OrgMessage message, CancellationToken cancellationToken = default) =>
        message switch
        {
            Memo memo => validator.ValidateAsync(memo, cancellationToken).AsTask(),
            PeerRequest request => validator.ValidateAsync(request, cancellationToken).AsTask(),
            _ => throw new NotSupportedException(),
        };

    private sealed class Queries
    {
        public List<string> Calls { get; } = [];
        public List<OrganizationId> Organizations { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public string? FailAt { get; init; }
        public Exception? Failure { get; init; }

        public void Record(string call, OrganizationId organization, CancellationToken token)
        {
            Calls.Add(call);
            Organizations.Add(organization);
            Tokens.Add(token);
            if (call == FailAt) throw Failure!;
        }
    }

    private sealed class RecordingContracts(IPeerChannelContracts inner, Queries queries) : IPeerChannelContracts
    {
        public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(
            OrganizationId organizationId, UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default)
        {
            queries.Record("contract", organizationId, cancellationToken);
            return inner.ResolveChannelAsync(organizationId, fromUnitId, toUnitId, cancellationToken);
        }
    }

    private sealed class RecordingRelations(IOrganizationRelations inner, Queries queries) : IOrganizationRelations
    {
        public ValueTask<UnitId?> GetUnitOfPositionAsync(
            OrganizationId organizationId, PositionId positionId, CancellationToken cancellationToken = default)
        {
            queries.Record($"position:{positionId.Value}", organizationId, cancellationToken);
            return inner.GetUnitOfPositionAsync(organizationId, positionId, cancellationToken);
        }

        public ValueTask<PositionId> GetUnitLeadershipAsync(
            OrganizationId organizationId, UnitId unitId, CancellationToken cancellationToken = default)
        {
            queries.Record($"leadership:{unitId.Value}", organizationId, cancellationToken);
            return inner.GetUnitLeadershipAsync(organizationId, unitId, cancellationToken);
        }

        public ValueTask<PositionId?> GetDirectSuperiorAsync(
            OrganizationId organizationId, PositionId positionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
            OrganizationId organizationId, PositionId positionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PositionId> GetRootUnitLeadershipAsync(
            OrganizationId organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
            OrganizationId organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
