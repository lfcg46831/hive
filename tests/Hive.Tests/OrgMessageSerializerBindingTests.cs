using Akka.Actor;
using Akka.Serialization;
using Hive.Actors;
using Hive.Actors.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

/// <summary>
/// Confirms that the Akka bootstrap (US-F0-03-T08) actually binds the organizational message
/// protocol to <see cref="OrgMessageJsonSerializer"/>, overriding Akka's default serializer, and
/// that a message round-trips through the registered serializer instance.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class OrgMessageSerializerBindingTests
{
    private static readonly DateTimeOffset SentAt = new(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Org_messages_bind_to_the_versionable_json_serializer()
    {
        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var serializer = system.Serialization.FindSerializerForType(typeof(Domain.Messaging.Directive));

            Assert.IsType<OrgMessageJsonSerializer>(serializer);
            Assert.Equal(OrgMessageJsonSerializer.SerializerId, serializer.Identifier);

            var message = CreateDirective();
            var withManifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer);
            var restored = withManifest.FromBinary(
                withManifest.ToBinary(message),
                withManifest.Manifest(message));

            Assert.IsType<Domain.Messaging.Directive>(restored);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static Domain.Messaging.Directive CreateDirective() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            DirectiveId.New(),
            null,
            "Triage the reported bug",
            "Customer impact is under investigation");

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
