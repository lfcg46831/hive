using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.Serialization;
using Hive.Actors;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T05b: PositionActor sharded messages, commands, persisted events and snapshots
/// are bound to a versionable HIVE serializer instead of Akka's default .NET serializers.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class PositionProtocolSerializerBindingTests
{
    private const int ExpectedSerializerId = 0x48495650;
    private const string ExpectedSerializerType = "Hive.Actors.Serialization.PositionProtocolJsonSerializer";
    private static readonly DateTimeOffset At = new(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, object> PositionProtocolSamples
    {
        get
        {
            var data = new TheoryData<string, object>();
            foreach (var (manifest, message) in Samples())
            {
                data.Add(manifest, message);
            }

            return data;
        }
    }

    [Fact]
    public async Task Position_protocol_types_bind_to_the_versionable_json_serializer()
    {
        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();

            foreach (var type in BoundTypes())
            {
                var serializer = system.Serialization.FindSerializerForType(type);

                Assert.Equal(ExpectedSerializerType, serializer.GetType().FullName);
                Assert.Equal(ExpectedSerializerId, serializer.Identifier);
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [MemberData(nameof(PositionProtocolSamples))]
    public async Task Position_protocol_values_round_trip_with_stable_manifests(
        string expectedManifest,
        object value)
    {
        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var serializer = system.Serialization.FindSerializerForType(value.GetType());
            var withManifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer);

            Assert.Equal(expectedManifest, withManifest.Manifest(value));

            var payload = withManifest.ToBinary(value);
            var json = System.Text.Encoding.UTF8.GetString(payload);
            var restored = withManifest.FromBinary(payload, expectedManifest);

            Assert.IsType(value.GetType(), restored);
            Assert.Equal(payload, withManifest.ToBinary(restored));
            Assert.DoesNotContain("Hive.Domain", json);
            Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Assembly", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Position_envelope_embeds_the_command_with_an_explicit_manifest()
    {
        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var serializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(
                system.Serialization.FindSerializerForType(typeof(PositionEnvelope)));
            var node = JsonNode.Parse(serializer.ToBinary(SampleEnvelope()))!.AsObject();

            Assert.Equal("acme/delivery-lead", node["Position"]!.GetValue<string>());
            Assert.Equal("accept-message", node["Command"]!["manifest"]!.GetValue<string>());
            Assert.Equal("memo", node["Command"]!["payload"]!["Message"]!["manifest"]!.GetValue<string>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Older_position_snapshot_without_retained_actions_remains_readable()
    {
        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var serializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(
                system.Serialization.FindSerializerForType(typeof(PositionSnapshot)));
            var node = JsonNode.Parse(serializer.ToBinary(new PositionSnapshot(At)))!.AsObject();
            node.Remove("RetainedActions");

            var restored = Assert.IsType<PositionSnapshot>(serializer.FromBinary(
                System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()),
                "position-snapshot"));

            Assert.Empty(restored.RetainedActions);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IEnumerable<Type> BoundTypes()
    {
        yield return typeof(PositionEnvelope);
        yield return typeof(PositionCommand);
        yield return typeof(AcceptMessage);
        yield return typeof(OpenTask);
        yield return typeof(UpdateTask);
        yield return typeof(CompleteTask);
        yield return typeof(UpdateShortMemory);
        yield return typeof(ChangeOccupant);
        yield return typeof(RequestPassivation);
        yield return typeof(RetainAction);
        yield return typeof(AuthorizeRetainedAction);
        yield return typeof(DenyRetainedAction);
        yield return typeof(ConsumeRetainedAction);
        yield return typeof(ExpireRetainedAction);
        yield return typeof(ReturnRetainedAction);
        yield return typeof(PositionEvent);
        yield return typeof(MessageReceived);
        yield return typeof(TaskCreated);
        yield return typeof(TaskUpdated);
        yield return typeof(TaskCompleted);
        yield return typeof(ShortMemoryUpdated);
        yield return typeof(OccupantChanged);
        yield return typeof(MessageDispatched);
        yield return typeof(MessageProcessingCompleted);
        yield return typeof(PositionPassivated);
        yield return typeof(PositionConfigurationApplied);
        yield return typeof(ActionRetained);
        yield return typeof(RetainedActionAuthorized);
        yield return typeof(RetainedActionDenied);
        yield return typeof(RetainedActionConsumed);
        yield return typeof(RetainedActionExpired);
        yield return typeof(RetainedActionReturned);
        yield return typeof(PositionSnapshot);
    }

    private static IEnumerable<(string Manifest, object Value)> Samples()
    {
        var retained = SampleRetainedAction();
        var grant = SampleAuthorizationGrant(retained);
        var denial = SampleAuthorizationDenial(retained);
        yield return ("position-envelope", SampleEnvelope());
        yield return ("accept-message", new AcceptMessage(SampleMessage()));
        yield return ("open-task", new OpenTask(TaskId(), ThreadId(), "triage incoming bug", Priority.High, At.AddHours(2), MessageId()));
        yield return ("update-task", new UpdateTask(TaskId(), "reproduced locally", Priority.Critical, At.AddHours(1)));
        yield return ("complete-task", new CompleteTask(TaskId(), "fixed"));
        yield return ("update-short-memory", new UpdateShortMemory("current-thread", "customer-impact"));
        yield return ("change-occupant", new ChangeOccupant(OccupantId.From("agent-7"), OccupantType.AiAgent));
        yield return ("request-passivation", new RequestPassivation("idle"));
        yield return ("retain-action", new RetainAction(retained));
        yield return ("authorize-retained-action", new AuthorizeRetainedAction(grant));
        yield return ("deny-retained-action", new DenyRetainedAction(denial));
        yield return ("consume-retained-action", new ConsumeRetainedAction(retained.Id, grant.Id));
        yield return ("expire-retained-action", new ExpireRetainedAction(retained.Id, grant.Id, "authorization-expired"));
        yield return ("return-retained-action", new ReturnRetainedAction(retained.Id, grant.Id, "policy-tightened"));
        yield return ("message-received", new MessageReceived(SampleMessage(), At));
        yield return ("task-created", new TaskCreated(TaskId(), ThreadId(), "triage incoming bug", Priority.High, At, At.AddHours(2), MessageId()));
        yield return ("task-updated", new TaskUpdated(TaskId(), "reproduced locally", At, Priority.Critical, At.AddHours(1)));
        yield return ("task-completed", new TaskCompleted(TaskId(), At, "fixed"));
        yield return ("short-memory-updated", new ShortMemoryUpdated("current-thread", "customer-impact", At));
        yield return ("occupant-changed", new OccupantChanged(OccupantId.From("agent-7"), OccupantType.AiAgent, At));
        yield return ("message-dispatched", new MessageDispatched(MessageId(), ThreadId(), OccupantId.From("agent-7"), OccupantType.AiAgent, At));
        yield return ("message-processing-completed", new MessageProcessingCompleted("message:completed", MessageId(), ThreadId(), MessageProcessingCompletionStatus.Completed, At));
        yield return ("position-passivated", new PositionPassivated(At, "idle"));
        yield return ("position-configuration-applied", new PositionConfigurationApplied(ConfigurationStamp(), At));
        yield return ("action-retained", new ActionRetained(retained));
        yield return ("retained-action-authorized", new RetainedActionAuthorized(grant, At.AddMinutes(1)));
        yield return ("retained-action-denied", new RetainedActionDenied(denial, At.AddMinutes(1)));
        yield return ("retained-action-consumed", new RetainedActionConsumed(retained.Id, grant.Id, At.AddMinutes(2)));
        yield return ("retained-action-expired", new RetainedActionExpired(retained.Id, grant.Id, "authorization-expired", At.AddMinutes(2)));
        yield return ("retained-action-returned", new RetainedActionReturned(retained.Id, grant.Id, "policy-tightened", At.AddMinutes(2)));
        yield return ("position-snapshot", SampleSnapshot());
    }

    private static PositionEnvelope SampleEnvelope() =>
        PositionEnvelope.For(
            PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("delivery-lead")),
            new AcceptMessage(SampleMessage()));

    private static PositionSnapshot SampleSnapshot()
    {
        var retained = SampleRetainedAction();
        var authorized = retained.Authorize(SampleAuthorizationGrant(retained), At.AddMinutes(1));
        return new PositionSnapshot(
            At,
            OccupantId.From("agent-7"),
            OccupantType.AiAgent,
            new[] { SampleMessage() },
            new[]
            {
                new PersistedTask(TaskId(), ThreadId(), "triage incoming bug", Priority.High, At, At.AddHours(2), MessageId()),
            },
            new Dictionary<string, string> { ["current-thread"] = "customer-impact" },
            new[] { MessageId() },
            new[] { MessageId() },
            ConfigurationStamp(),
            new[] { authorized });
    }

    private static PersistedRetainedAction SampleRetainedAction() =>
        new(
            RetainedActionId.From(new Guid("dddddddd-0000-0000-0000-000000000001")),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000001"),
            RetainedActionKind.OrganizationalMessage,
            "Report",
            "{\"body\":\"retained\"}",
            "{}",
            "directive:retained",
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId(),
            MessageId(),
            DirectiveId.From(new Guid("eeeeeeee-0000-0000-0000-000000000001")),
            null,
            "action-gate-escalation-required",
            At,
            governanceMessages: new[] { SampleMessage() });

    private static AuthorizationGrant SampleAuthorizationGrant(PersistedRetainedAction action) =>
        new(
            Hive.Domain.Identity.MessageId.From(new Guid("fa000000-0000-0000-0000-000000000001")),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At.AddMinutes(1),
            null,
            Hive.Domain.Identity.MessageId.From(new Guid("fa000000-0000-0000-0000-000000000002")),
            action.Id,
            action.Fingerprint,
            Hive.Domain.Governance.AuthorityKey.From("governance.authorize-retained-action"),
            At.AddHours(1),
            null);

    private static AuthorizationDenial SampleAuthorizationDenial(PersistedRetainedAction action) =>
        new(
            Hive.Domain.Identity.MessageId.From(new Guid("fb000000-0000-0000-0000-000000000001")),
            action.OrganizationId,
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(action.PositionId),
            action.ThreadId,
            Priority.High,
            1,
            At.AddMinutes(1),
            null,
            Hive.Domain.Identity.MessageId.From(new Guid("fb000000-0000-0000-0000-000000000002")),
            action.Id,
            "Denied by organization owner.");

    private static Memo SampleMessage() =>
        new(
            MessageId(),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            ThreadId(),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static MessageId MessageId() =>
        Hive.Domain.Identity.MessageId.From(new Guid("aaaaaaaa-0000-0000-0000-000000000001"));

    private static ThreadId ThreadId() =>
        Hive.Domain.Identity.ThreadId.From(new Guid("bbbbbbbb-0000-0000-0000-000000000001"));

    private static PositionTaskId TaskId() =>
        PositionTaskId.From(new Guid("cccccccc-0000-0000-0000-000000000001"));

    private static PositionConfigurationStamp ConfigurationStamp() =>
        new(5, "sha256:runtime-v5");

    private static IHost BuildHost(int port)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Hive:Node:Roles:0"] = NodeRoleNames.Agents,
        });

        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        return builder.Build();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
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
}
