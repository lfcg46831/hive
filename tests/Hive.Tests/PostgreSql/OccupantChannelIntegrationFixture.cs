using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Hive.Actors;
using Hive.Actors.OccupantChannels;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Identity;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests.PostgreSql;

internal sealed class OccupantChannelIntegrationFixture : IAsyncDisposable
{
    public static readonly OrganizationId Organization = OrganizationId.From("acme");
    public static readonly PositionId HumanPosition = PositionId.From("delivery-lead");
    public static readonly PositionId SuperiorPosition = PositionId.From("owner");
    public static readonly UnitId Unit = UnitId.From("delivery");
    public static readonly PositionEntityId Entity = PositionEntityId.From(
        Organization,
        HumanPosition);
    public static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    public static readonly UserId User = UserId.From(
        Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    public static readonly OccupantChannelBindingId Binding = OccupantChannelBindingId.From(
        Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    public static readonly DateTimeOffset At = new(
        2026,
        8,
        13,
        9,
        0,
        0,
        TimeSpan.Zero);
    public const string Endpoint = "person@example.test";

    private readonly string _connectionString;
    private readonly MutablePositionConfigurationProvider _configurationProvider;
    private readonly bool _terminalEscalationTarget;
    private readonly PostgreSqlOccupantChannelDecisionTokenUseStore _tokenUseStore;
    private IHost? _host;
    private IActorRef? _position;

    private OccupantChannelIntegrationFixture(
        string connectionString,
        PositionRuntimeConfiguration configuration,
        bool terminalEscalationTarget)
    {
        _connectionString = connectionString;
        _configurationProvider = new MutablePositionConfigurationProvider(configuration);
        _terminalEscalationTarget = terminalEscalationTarget;
        Clock = new MutableTimeProvider(At);
        _tokenUseStore = new PostgreSqlOccupantChannelDecisionTokenUseStore(connectionString);
        Tokens = new HmacOccupantChannelCorrelationTokenService(
            Options.Create(new OccupantChannelCorrelationTokenOptions
            {
                SigningKey = OccupantChannelCorrelationTokenTests.SigningKey(),
                Lifetime = TimeSpan.FromHours(1),
            }),
            Clock,
            _tokenUseStore);
        Bindings = new MutableIdentityBindings();
        Transport = new RecordingSmtpTransport();
        Scheduler = new RecordingResponseScheduler();
        Emitter = new RecordingMessageEmitter();
        KillSwitch = new RecordingKillSwitch();
        Projections = new RecordingProjectionPublisher();
    }

    public MutableTimeProvider Clock { get; }

    public HmacOccupantChannelCorrelationTokenService Tokens { get; }

    public MutableIdentityBindings Bindings { get; }

    public RecordingSmtpTransport Transport { get; }

    public RecordingResponseScheduler Scheduler { get; }

    public RecordingMessageEmitter Emitter { get; }

    public RecordingKillSwitch KillSwitch { get; }

    public RecordingProjectionPublisher Projections { get; }

    public IActorRef Position => _position
        ?? throw new InvalidOperationException("The position actor has not been started.");

    public static async Task<OccupantChannelIntegrationFixture> StartAsync(
        PostgreSqlFixture postgres,
        bool activeLink = true,
        bool responsePolicy = false,
        OccupantAbsenceAction? absenceAction = null,
        bool terminalEscalationTarget = false)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        await postgres.ResetRegistryAsync();
        await postgres.ResetPersistenceAsync();
        await ResetOccupantChannelAsync(postgres);
        await using (var dataSource = postgres.CreateDataSource())
        {
            await new PostgreSqlOccupantChannelTokenMigrator(dataSource).MigrateAsync();
        }

        var fixture = new OccupantChannelIntegrationFixture(
            postgres.ConnectionString,
            Configuration(activeLink, responsePolicy, absenceAction),
            terminalEscalationTarget);
        if (activeLink)
        {
            fixture.Bindings.Activate();
        }

        try
        {
            await fixture.StartPositionAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task RestartPositionAsync(
        bool? activeLink = null,
        bool? responsePolicy = null,
        OccupantAbsenceAction? absenceAction = null)
    {
        if (activeLink is not null || responsePolicy is not null || absenceAction is not null)
        {
            var current = _configurationProvider.Configuration;
            var effectiveActive = activeLink
                ?? current.Occupant.HumanIdentity is not null;
            var effectivePolicy = responsePolicy
                ?? current.Occupant.ResponsePolicy is not null;
            _configurationProvider.Configuration = Configuration(
                effectiveActive,
                effectivePolicy,
                absenceAction);
        }

        await StopPositionAsync();
        await StartPositionAsync();
    }

    public async Task<AcceptMessageResult> AcceptAsync(OrgMessage message) =>
        await Position.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());

    public async Task<PositionState> StateAsync() =>
        await Position.Ask<PositionState>(GetPositionState.Instance, Timeout());

    public async Task<bool> HasHumanProxyAsync()
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{OccupantType.Human}:{Occupant.Value}:{User.Value:N}")))[..16]
            .ToLowerInvariant();
        var childName = $"occupant-human-{hash}";
        var system = _host?.Services.GetRequiredService<ActorSystem>()
            ?? throw new InvalidOperationException("The actor host has not been started.");
        var identity = await system
            .ActorSelection($"{Position.Path}/{childName}")
            .Ask<ActorIdentity>(new Identify(childName), TimeSpan.FromSeconds(2));
        return identity.Subject is not null;
    }

    public async Task<SmtpOutboundMessage> WaitForEmailAsync(MessageId messageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = Transport.Messages.FirstOrDefault(message =>
                string.Equals(
                    message.HiveMessageId,
                    messageId.Value.ToString("D"),
                    StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"SMTP message '{messageId}' was not recorded.");
    }

    public async Task<PositionState> WaitForNotificationAsync(
        MessageId messageId,
        OccupantNotificationDeliveryStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await StateAsync();
            if (state.OccupantNotifications.TryGetValue(messageId, out var notification)
                && notification.Status == status)
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Occupant notification '{messageId}' did not reach {status}.");
    }

    public async Task<PositionState> WaitForTimeoutAsync(MessageId messageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await StateAsync();
            if (state.OccupantNotifications.TryGetValue(messageId, out var notification)
                && notification.ResponseTimeout is not null)
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Occupant response timeout '{messageId}' was not persisted.");
    }

    public async Task<PositionState> WaitForAbsenceAsync(MessageId messageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await StateAsync();
            if (state.OccupantAbsenceEscalations.ContainsKey(messageId))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Occupant absence result '{messageId}' was not persisted.");
    }

    public HmacOccupantChannelCorrelationTokenService CreateTokenService() =>
        Tokens;

    public PostgreSqlImapInboundEmailStore CreateInboundStore() => new(_connectionString);

    public InboundOccupantEmailParser CreateInboundParser(
        IOccupantChannelCorrelationTokenService tokens) =>
        new(tokens, Bindings);

    public InboundOccupantEmailProcessor CreateInboundProcessor(
        IImapInboundEmailStore store,
        IInboundOccupantEmailParser parser) =>
        new(store, parser, Options.Create(ImapOptions()), Clock);

    public InboundOccupantEmailReplyProcessor CreateReplyProcessor(
        IImapInboundEmailStore store) =>
        new(store, new DirectReplyEmitter(Position), Options.Create(ImapOptions()), Clock);

    public InboundOccupantEmailDecisionProcessor CreateDecisionProcessor(
        IImapInboundEmailStore store) =>
        new(store, new DirectDecisionEmitter(Position), Options.Create(ImapOptions()), Clock);

    public static ImapInboundEmailOptions ImapOptions() => new()
    {
        SourceId = "occupant-replies",
        Mailbox = "INBOX",
        BatchSize = 50,
    };

    public static byte[] ReplyMessage(string sender, string body)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(sender));
        message.To.Add(MailboxAddress.Parse("hive@example.test"));
        message.Subject = "Occupant reply";
        message.Body = new TextPart("plain") { Text = body };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    public static OrgDirective Directive(
        Guid messageId,
        Guid threadId,
        Priority priority = Priority.High) =>
        new OrgDirective(
            MessageId.From(messageId),
            Organization,
            new PositionEndpointRef(SuperiorPosition),
            new PositionEndpointRef(HumanPosition),
            ThreadId.From(threadId),
            priority,
            schemaVersion: 1,
            At,
            deadline: null,
            DirectiveId.From(messageId),
            parentDirectiveId: null,
            objective: "Make the delivery decision.",
            context: "A human response is required.");

    public static ApprovalRequest ApprovalRequest(
        Guid messageId,
        Guid threadId,
        DateTimeOffset? deadline = null) =>
        new(
            MessageId.From(messageId),
            Organization,
            new PositionEndpointRef(SuperiorPosition),
            new PositionEndpointRef(HumanPosition),
            ThreadId.From(threadId),
            Priority.Critical,
            schemaVersion: 1,
            At,
            deadline,
            "Publish the external release.",
            "The release is ready for publication.",
            ApprovalPolicyRef.From("comms.external-official"));

    public async Task AssertEndpointAbsentFromPersistenceAsync(string endpoint = Endpoint)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM persistence.event_journal journal_entry
                WHERE to_jsonb(journal_entry)::text ILIKE @endpoint
            ) OR EXISTS (
                SELECT 1
                FROM persistence.snapshot_store snapshot_entry
                WHERE to_jsonb(snapshot_entry)::text ILIKE @endpoint
            );
            """);
        command.Parameters.AddWithValue("endpoint", $"%{endpoint}%");
        var containsEndpoint = (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Persistence privacy query returned no result."));
        Assert.False(containsEndpoint);
    }

    public async Task<string?> ReadInboundRejectionCodeAsync(uint uid)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT failure_code
            FROM occupant_channel.imap_inbound_emails
            WHERE source_id = 'occupant-replies'
              AND mailbox = 'INBOX'
              AND uid_validity = 7
              AND uid = @uid;
            """);
        command.Parameters.AddWithValue("uid", (long)uid);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopPositionAsync();
        }
        finally
        {
            await _tokenUseStore.DisposeAsync();
        }
    }

    private async Task StartPositionAsync()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = GetFreeTcpPort().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["Hive:Node:Roles:0"] = NodeRoleNames.Api,
            ["Hive:Organizations:RootPath"] = Path.Combine(
                RepositoryRoot,
                "config",
                "organizations"),
            ["ConnectionStrings:PostgreSql"] = _connectionString,
        });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        _host = builder.Build();
        await _host.StartAsync();

        var system = _host.Services.GetRequiredService<ActorSystem>();
        var tokens = CreateTokenService();
        var smtp = new SmtpOccupantChannel(
            Bindings,
            Transport,
            new SmtpOccupantEmailRenderer(),
            ImmediateSmtpRetryDelay.Instance,
            Options.Create(SmtpOptions()));
        var occupantFactory = new PositionOccupantFactory(
            smtp,
            new SignedOccupantChannelDeliveryRequestFactory(tokens));
        var relations = Relations();
        _position = system.ActorOf(
            Props.Create(() => new PositionActor(
                Entity.Value,
                _configurationProvider,
                occupantFactory,
                Projections,
                () => Clock.GetUtcNow(),
                null,
                new OccupantReplyMessageValidator(relations, Clock),
                Emitter,
                Scheduler,
                _terminalEscalationTarget
                    ? NoEscalationTargetResolver.Instance
                    : new OrganizationRelationsOccupantResponseEscalationTargetResolver(relations),
                KillSwitch)),
            $"occupant-channel-position-{Guid.NewGuid():N}");
        await WaitForReadyAsync(_position);
    }

    private async Task StopPositionAsync()
    {
        if (_position is not null)
        {
            await _position.GracefulStop(Timeout());
            _position = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    private static async Task WaitForReadyAsync(IActorRef actor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await actor.Ask<PositionRuntimeStatus>(
                    GetPositionRuntimeStatus.Instance,
                    TimeSpan.FromSeconds(1));
                if (status.OperationalState == PositionOperationalState.Ready)
                {
                    return;
                }
            }
            catch (AskTimeoutException)
            {
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The occupant-channel PositionActor did not reach Ready.");
    }

    private static async Task ResetOccupantChannelAsync(PostgreSqlFixture postgres)
    {
        await using var dataSource = postgres.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "DROP SCHEMA IF EXISTS occupant_channel CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static PositionRuntimeConfiguration Configuration(
        bool activeLink,
        bool responsePolicy,
        OccupantAbsenceAction? absenceAction)
    {
        var fingerprint = string.Join(
            '-',
            activeLink ? "linked" : "unlinked",
            responsePolicy ? "policy" : "no-policy",
            absenceAction?.ToString().ToLowerInvariant() ?? "available");
        return new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(1, $"sha256:occupant-channel-{fingerprint}"),
            Organization,
            HumanPosition,
            new PositionRuntimeDescriptor(
                Unit,
                reportsTo: SuperiorPosition,
                name: "Delivery lead",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.Human,
                configuredIdentity: Occupant,
                humanIdentity: activeLink
                    ? new HumanOccupantRuntimeIdentity(User, Binding)
                    : null,
                responsePolicy: responsePolicy
                    ? new OccupantResponsePolicyRuntimeConfiguration(
                        1,
                        TimeSpan.FromHours(1),
                        TimeSpan.FromHours(2),
                        "Europe/Lisbon",
                        new TimeOnly(9, 0),
                        new TimeOnly(18, 0))
                    : null,
                absence: absenceAction is { } action
                    ? new OccupantAbsenceConfiguration(action)
                    : null),
            new PositionAuthorityRuntimeConfiguration([]));
    }

    private static MaterializedOrganizationRelations Relations()
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            Organization,
            new OrganizationOwnerEndpointRef());
        builder.AddPosition(SuperiorPosition, Unit);
        builder.AddPosition(HumanPosition, Unit, SuperiorPosition);
        return new MaterializedOrganizationRelations(builder.Build());
    }

    private static SmtpOccupantChannelOptions SmtpOptions() => new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        Port = 587,
        Security = "start-tls",
        FromAddress = "hive@example.test",
        FromName = "HIVE",
        ReplyToAddress = "replies@example.test",
        SubjectPrefix = "[HIVE]",
        MaxAttempts = 1,
        InitialBackoff = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(1),
        AttemptTimeout = TimeSpan.FromSeconds(5),
    };

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(20);

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    internal sealed class MutableIdentityBindings :
        IOccupantEmailBindingResolver,
        IInboundOccupantEmailIdentityResolver
    {
        private OccupantEmailBindingResolutionStatus _status =
            OccupantEmailBindingResolutionStatus.Missing;

        public List<OccupantEmailBindingQuery> OutboundQueries { get; } = [];

        public List<InboundOccupantEmailIdentityQuery> InboundQueries { get; } = [];

        public void Activate() => _status = OccupantEmailBindingResolutionStatus.Active;

        public void Revoke() => _status = OccupantEmailBindingResolutionStatus.Revoked;

        public Task<OccupantEmailBindingResolution> ResolveActiveAsync(
            OccupantEmailBindingQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboundQueries.Add(query);
            return Task.FromResult(_status switch
            {
                OccupantEmailBindingResolutionStatus.Active =>
                    OccupantEmailBindingResolution.Active(Endpoint),
                OccupantEmailBindingResolutionStatus.Revoked =>
                    OccupantEmailBindingResolution.Revoked(),
                _ => OccupantEmailBindingResolution.Missing(),
            });
        }

        public Task<InboundOccupantEmailIdentityResolution> ResolveActiveAsync(
            InboundOccupantEmailIdentityQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InboundQueries.Add(query);
            return Task.FromResult(_status switch
            {
                OccupantEmailBindingResolutionStatus.Active =>
                    InboundOccupantEmailIdentityResolution.Active(
                        Occupant,
                        User,
                        Binding,
                        Endpoint),
                OccupantEmailBindingResolutionStatus.Revoked =>
                    InboundOccupantEmailIdentityResolution.BindingRevoked(),
                _ => InboundOccupantEmailIdentityResolution.BindingMissing(),
            });
        }
    }

    internal sealed class RecordingSmtpTransport : ISmtpOccupantTransport
    {
        public ConcurrentQueue<SmtpOutboundMessage> Messages { get; } = new();

        public Task<OccupantChannelDeliveryResult> SendAsync(
            SmtpOutboundMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Enqueue(message);
            return Task.FromResult(OccupantChannelDeliveryResult.Succeeded());
        }
    }

    internal sealed class RecordingResponseScheduler : IOccupantResponseScheduler
    {
        public ConcurrentQueue<object> Commands { get; } = new();

        public void Schedule(
            IActorContext context,
            IActorRef receiver,
            object command,
            TimeSpan delay) =>
            Commands.Enqueue(command);

        public async Task<T> WaitForAsync<T>(Func<T, bool> predicate)
        {
            var deadline = DateTimeOffset.UtcNow.Add(Timeout());
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = Commands.OfType<T>().FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Scheduled command '{typeof(T).Name}' was not recorded.");
        }
    }

    internal sealed class RecordingMessageEmitter : IPositionMessageEmitter
    {
        public ConcurrentQueue<OrgMessage> Messages { get; } = new();

        public void Emit(ActorSystem system, OrgMessage message) => Messages.Enqueue(message);

        public async Task<T> WaitForAsync<T>(Func<T, bool>? predicate = null)
            where T : OrgMessage
        {
            var deadline = DateTimeOffset.UtcNow.Add(Timeout());
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = Messages.OfType<T>()
                    .FirstOrDefault(item => predicate?.Invoke(item) ?? true);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Emitted message '{typeof(T).Name}' was not recorded.");
        }
    }

    internal sealed class RecordingKillSwitch : IOccupantResponseKillSwitch
    {
        public ConcurrentQueue<OccupantResponseKillSwitchRequest> Requests { get; } = new();

        public void Request(ActorSystem system, OccupantResponseKillSwitchRequest request) =>
            Requests.Enqueue(request);
    }

    internal sealed class RecordingProjectionPublisher : IPositionProjectionPublisher
    {
        public ConcurrentQueue<PositionProjectionEvent> Events { get; } = new();

        public void Publish(PositionProjectionEvent @event) => Events.Enqueue(@event);
    }

    private sealed class MutablePositionConfigurationProvider(
        PositionRuntimeConfiguration configuration) : IPositionConfigurationProvider
    {
        public PositionRuntimeConfiguration Configuration { get; set; } = configuration;

        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(Configuration));
    }

    private sealed class DirectReplyEmitter(IActorRef position)
        : IInboundOccupantEmailReplyEmitter
    {
        public async ValueTask<OccupantReplyEmissionResult> EmitAsync(
            PositionEntityId target,
            EmitCorrelatedOccupantReply command,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Entity, target);
            return await position.Ask<OccupantReplyEmissionResult>(
                command,
                Timeout(),
                cancellationToken);
        }
    }

    private sealed class DirectDecisionEmitter(IActorRef position)
        : IInboundOccupantEmailDecisionEmitter
    {
        public async ValueTask<OccupantReplyEmissionResult> EmitAsync(
            PositionEntityId target,
            EmitOccupantApprovalDecision command,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Entity, target);
            return await position.Ask<OccupantReplyEmissionResult>(
                command,
                Timeout(),
                cancellationToken);
        }
    }

    private sealed class ImmediateSmtpRetryDelay : ISmtpRetryDelay
    {
        public static ImmediateSmtpRetryDelay Instance { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoEscalationTargetResolver :
        IOccupantResponseEscalationTargetResolver
    {
        public static NoEscalationTargetResolver Instance { get; } = new();

        public ValueTask<EndpointRef?> ResolveAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<EndpointRef?>(null);
    }
}
