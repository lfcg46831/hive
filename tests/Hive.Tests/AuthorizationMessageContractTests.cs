using System.Reflection;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class AuthorizationMessageContractTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly MessageContractValidator _validator = new();
    private readonly EmptyValidationContext _context = new();

    [Fact]
    public async Task Valid_authorization_messages_pass_structural_validation()
    {
        var grantResult = await _validator.ValidateAsync(Grant(), _context);
        var denialResult = await _validator.ValidateAsync(Denial("Action is outside the change window"), _context);

        Assert.True(grantResult.IsValid);
        Assert.True(denialResult.IsValid);
    }

    [Fact]
    public async Task Denial_requires_a_non_empty_reason()
    {
        var result = await _validator.ValidateAsync(Denial(" "), _context);

        Assert.Equal(
            [new ValidationError("required-field", "reason", RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Grant_expiration_must_be_strictly_after_sent_at(int offsetSeconds)
    {
        var result = await _validator.ValidateAsync(
            Grant(expiresAt: SentAt.AddSeconds(offsetSeconds)),
            _context);

        Assert.Equal(
            [new ValidationError("invalid-expiration", "expiresAt", RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Grant_requires_a_non_default_expiration()
    {
        var result = await _validator.ValidateAsync(Grant(expiresAt: default), _context);

        Assert.Equal(
            [new ValidationError("required-field", "expiresAt", RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public async Task Authorization_origin_must_be_a_position_or_organization_owner()
    {
        var result = await _validator.ValidateAsync(
            Grant(from: new SystemEndpointRef(SystemEndpointKind.Scheduler)),
            _context);

        Assert.Equal(
            [new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Authorization_destination_must_be_a_position()
    {
        var result = await _validator.ValidateAsync(
            Grant(to: new OrganizationOwnerEndpointRef()),
            _context);

        Assert.Equal(
            [new ValidationError("endpoint-not-allowed", "to", RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Theory]
    [InlineData(typeof(AuthorizationGrant))]
    [InlineData(typeof(AuthorizationDenial))]
    public void Authorization_contracts_are_public_sealed_immutable_records(Type messageType)
    {
        Assert.True(messageType.IsPublic);
        Assert.True(messageType.IsSealed);
        Assert.Equal(typeof(OrgMessage), messageType.BaseType);
        Assert.All(
            messageType.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void Authorization_payload_exposes_only_the_canonical_properties()
    {
        Assert.Equal(
            ["Channel", "ExpiresAt", "Fingerprint", "InReplyTo", "Key", "Reason", "RetainedActionId"],
            DeclaredPropertyNames<AuthorizationGrant>());
        Assert.Equal(
            ["Channel", "InReplyTo", "Reason", "RetainedActionId"],
            DeclaredPropertyNames<AuthorizationDenial>());
    }

    [Fact]
    public void Typed_authorization_payload_references_cannot_be_null()
    {
        var fingerprint = ActionFingerprint.From($"sha256:{new string('c', 64)}");
        var key = AuthorityKey.From("delivery.bug-triage");

        Assert.Throws<ArgumentNullException>(() => GrantPayload(
            null!, RetainedActionId.New(), fingerprint, key));
        Assert.Throws<ArgumentNullException>(() => GrantPayload(
            MessageId.New(), null!, fingerprint, key));
        Assert.Throws<ArgumentNullException>(() => GrantPayload(
            MessageId.New(), RetainedActionId.New(), null!, key));
        Assert.Throws<ArgumentNullException>(() => GrantPayload(
            MessageId.New(), RetainedActionId.New(), fingerprint, null!));
        Assert.Throws<ArgumentNullException>(() => DenialPayload(null!, RetainedActionId.New()));
        Assert.Throws<ArgumentNullException>(() => DenialPayload(MessageId.New(), null!));
    }

    private static AuthorizationGrant Grant(
        EndpointRef? from = null,
        EndpointRef? to = null) =>
        Grant(SentAt.AddHours(24), from, to);

    private static AuthorizationGrant Grant(
        DateTimeOffset expiresAt,
        EndpointRef? from = null,
        EndpointRef? to = null) =>
        new(
            MessageId.New(), OrganizationId.From("acme"), from ?? Position("lead"),
            to ?? Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            MessageId.New(), RetainedActionId.New(),
            ActionFingerprint.From($"sha256:{new string('c', 64)}"),
            AuthorityKey.From("delivery.bug-triage"),
            expiresAt, reason: null);

    private static AuthorizationDenial Denial(string reason) =>
        new(
            MessageId.New(), OrganizationId.From("acme"), new OrganizationOwnerEndpointRef(),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            MessageId.New(), RetainedActionId.New(), reason);

    private static AuthorizationGrant GrantPayload(
        MessageId inReplyTo,
        RetainedActionId retainedActionId,
        ActionFingerprint fingerprint,
        AuthorityKey key) =>
        new(
            MessageId.New(), OrganizationId.From("acme"), Position("lead"),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            inReplyTo, retainedActionId, fingerprint, key, SentAt.AddHours(1), null);

    private static AuthorizationDenial DenialPayload(
        MessageId inReplyTo,
        RetainedActionId retainedActionId) =>
        new(
            MessageId.New(), OrganizationId.From("acme"), new OrganizationOwnerEndpointRef(),
            Position("developer"), ThreadId.New(), Priority.High, 1, SentAt, null,
            inReplyTo, retainedActionId, "Denied.");

    private static string[] DeclaredPropertyNames<T>() =>
        typeof(T).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private sealed class EmptyValidationContext : IMessageValidationContext
    {
        public ValueTask<Directive?> FindDirectiveAsync(
            DirectiveId directiveId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Directive?>(null);
    }
}
