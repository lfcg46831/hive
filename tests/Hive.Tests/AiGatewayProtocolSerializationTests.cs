using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.Serialization;
using Hive.Actors;
using Hive.Actors.Gateway;
using Hive.Actors.Serialization;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F1-05-T07: the sharded AI gateway protocol travels in the versionable ADR-007 JSON
/// format under stable manifests, so a call between an agents node and a gateway node never falls
/// back to Akka's default .NET serialization and no CLR type name reaches the wire.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class AiGatewayProtocolSerializationTests
{
    private const int ExpectedSerializerId = 0x48494147;
    private const string ExpectedSerializerType =
        "Hive.Actors.Serialization.AiGatewayProtocolJsonSerializer";

    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread = ThreadId.From(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message = MessageId.From(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static TheoryData<string, object> ProtocolSamples
    {
        get
        {
            var data = new TheoryData<string, object>();
            foreach (var (manifest, value) in Samples())
            {
                data.Add(manifest, value);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProtocolSamples))]
    public void Gateway_protocol_values_round_trip_under_their_manifest(
        string expectedManifest,
        object value)
    {
        Assert.Equal(expectedManifest, AiGatewayProtocolManifests.ForType(value.GetType()));

        var payload = AiGatewayProtocolJsonFormat.Serialize(value);
        var json = Encoding.UTF8.GetString(payload);
        var restored = AiGatewayProtocolJsonFormat.Deserialize(expectedManifest, payload);

        Assert.IsType(value.GetType(), restored);
        Assert.Equal(payload, AiGatewayProtocolJsonFormat.Serialize(restored));
        Assert.DoesNotContain("Hive.Domain", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Hive.Actors", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Assembly", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Request_survives_the_wire_with_policy_chain_tools_and_context()
    {
        var call = CompleteCall();

        var restored = Assert.IsType<CompleteAiGatewayCall>(
            AiGatewayProtocolJsonFormat.Deserialize(
                "complete-ai-gateway-call",
                AiGatewayProtocolJsonFormat.Serialize(call)));

        var request = restored.Request;
        Assert.Equal(call.CorrelationId, restored.CorrelationId);
        Assert.Equal(Organization, request.OrganizationId);
        Assert.Equal(Position, request.PositionId);
        Assert.Equal(Thread, request.ThreadId);
        Assert.Equal(Message, request.MessageId);
        Assert.Equal("Classify the incoming directive.", request.Content);
        Assert.Equal("openai", request.Provider!.ProviderId);
        Assert.Equal("gpt-5.6-luna", request.Provider.ModelId);
        Assert.Equal(AiProcessingMode.Interactive, request.ProcessingMode);
        Assert.Equal(TimeSpan.FromSeconds(45), request.Timeout);
        Assert.Equal("triage", Assert.Single(request.Tools).Name);
        Assert.Equal(
            AiGatewayMessageRole.Assistant,
            Assert.Single(request.ContextMessages).Role);
        Assert.Equal("anthropic", Assert.Single(request.Policy!.Fallback).ProviderId);
        Assert.Equal(2, request.Policy.AuthorizedModels.Length);
        Assert.Equal(4096, request.Policy.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromMinutes(1), request.Policy.MaxTimeout);
        Assert.Equal("triage", Assert.Single(request.Policy.AuthorizedTools));
        Assert.True(request.Policy.HasAvailableBudget);
    }

    [Fact]
    public void Failed_response_preserves_code_reason_and_diagnostics()
    {
        var completed = new AiGatewayCallCompleted("corr-2", FailedResponse());

        var restored = Assert.IsType<AiGatewayCallCompleted>(
            AiGatewayProtocolJsonFormat.Deserialize(
                "ai-gateway-call-completed",
                AiGatewayProtocolJsonFormat.Serialize(completed)));

        var error = restored.Response.Error!;
        Assert.True(restored.Response.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.Equal(AiGatewayErrorReason.FallbackExhausted, error.Reason);
        Assert.True(error.IsRetryable);
        Assert.Equal("openai", error.Provider!.ProviderId);
        Assert.Equal(503, error.Diagnostics!.ProviderStatusCode);
    }

    [Fact]
    public void Successful_response_preserves_usage_cost_and_tool_calls()
    {
        var completed = new AiGatewayCallCompleted("corr-3", SucceededResponse());

        var restored = Assert.IsType<AiGatewayCallCompleted>(
            AiGatewayProtocolJsonFormat.Deserialize(
                "ai-gateway-call-completed",
                AiGatewayProtocolJsonFormat.Serialize(completed)));

        var response = restored.Response;
        Assert.True(response.IsSuccess);
        Assert.Equal("classified", response.Text);
        Assert.Equal(AiFinishReason.Stop, response.FinishReason);
        Assert.Equal("openai", response.Provider!.ProviderId);
        Assert.Equal(120, response.Usage!.TotalTokens);
        Assert.Equal(0.42m, response.Cost!.Amount);
        Assert.Equal("EUR", response.Cost.Currency);
        Assert.Equal("triage", Assert.Single(response.ToolCalls).Name);
    }

    [Fact]
    public void Envelope_embeds_the_command_with_an_explicit_manifest()
    {
        var envelope = new AiGatewayEnvelope("openai", CompleteCall());

        var node = JsonNode.Parse(AiGatewayProtocolJsonFormat.Serialize(envelope))!.AsObject();

        Assert.Equal("openai", node["ProviderKey"]!.GetValue<string>());
        Assert.Equal("complete-ai-gateway-call", node["Command"]!["manifest"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_manifest_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => AiGatewayProtocolManifests.ForManifest("ai-gateway-unknown"));
        Assert.Throws<ArgumentException>(
            () => AiGatewayProtocolManifests.ForType(typeof(AiGatewayProtocolSerializationTests)));
    }

    [Fact]
    public void Undefined_enum_wire_values_are_rejected()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"CorrelationId\":\"corr-4\",\"Request\":{\"OrganizationId\":\"acme\"," +
            "\"PositionId\":\"delivery-lead\",\"ThreadId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"MessageId\":\"22222222-2222-2222-2222-222222222222\",\"Content\":\"hi\"," +
            "\"ProcessingMode\":\"telepathy\"}}");

        Assert.Throws<JsonException>(
            () => AiGatewayProtocolJsonFormat.Deserialize("complete-ai-gateway-call", payload));
    }

    [Fact]
    public async Task Gateway_protocol_types_bind_to_the_versionable_json_serializer()
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

            var envelopeSerializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(
                system.Serialization.FindSerializerForType(typeof(AiGatewayEnvelope)));
            var envelope = new AiGatewayEnvelope("openai", CompleteCall());

            Assert.Equal("ai-gateway-envelope", envelopeSerializer.Manifest(envelope));
            Assert.IsType<AiGatewayEnvelope>(envelopeSerializer.FromBinary(
                envelopeSerializer.ToBinary(envelope),
                "ai-gateway-envelope"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IEnumerable<Type> BoundTypes()
    {
        yield return typeof(AiGatewayEnvelope);
        yield return typeof(AiGatewayProviderCommand);
        yield return typeof(CompleteAiGatewayCall);
        yield return typeof(CancelAiGatewayCall);
        yield return typeof(AiGatewayCallCompleted);
        yield return typeof(AiGatewayCallCanceled);
    }

    private static IEnumerable<(string Manifest, object Value)> Samples()
    {
        yield return ("ai-gateway-envelope", new AiGatewayEnvelope("openai", CompleteCall()));
        yield return ("complete-ai-gateway-call", CompleteCall());
        yield return ("cancel-ai-gateway-call", new CancelAiGatewayCall("corr-1"));
        yield return (
            "ai-gateway-call-completed",
            new AiGatewayCallCompleted("corr-1", SucceededResponse()));
        yield return (
            "ai-gateway-call-completed",
            new AiGatewayCallCompleted("corr-1", FailedResponse()));
        yield return ("ai-gateway-call-canceled", new AiGatewayCallCanceled("corr-1"));
    }

    private static CompleteAiGatewayCall CompleteCall() =>
        new(
            "corr-1",
            new AiGatewayRequest(
                Organization,
                Position,
                Thread,
                Message,
                "Classify the incoming directive.",
                systemInstruction: "You are the delivery lead.",
                contextMessages: new[]
                {
                    new AiGatewayMessage(AiGatewayMessageRole.Assistant, "Previous answer."),
                },
                tools: new[] { new AiToolDefinition("triage", "Triage a bug report.") },
                modelParameters: new AiModelParameters(temperature: 0.2m, maxOutputTokens: 512),
                metadata: new Dictionary<string, string> { ["directive"] = "triage" },
                provider: new AiProviderMetadata("openai", "gpt-5.6-luna"),
                processingMode: AiProcessingMode.Interactive,
                timeout: TimeSpan.FromSeconds(45),
                policy: new AiGatewayPolicy(
                    new[]
                    {
                        new AiProviderMetadata("openai", "gpt-5.6-luna"),
                        new AiProviderMetadata("anthropic", "claude-sonnet"),
                    },
                    hasAvailableBudget: true,
                    maxOutputTokens: 4096,
                    maxTimeout: TimeSpan.FromMinutes(1),
                    allowedProcessingModes: new[] { AiProcessingMode.Interactive },
                    authorizedTools: new[] { "triage" },
                    fallback: new[] { new AiProviderMetadata("anthropic", "claude-sonnet") })));

    private static AiGatewayResponse SucceededResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "classified",
            AiFinishReason.Stop,
            new AiProviderMetadata("openai", "gpt-5.6-luna"),
            new[] { new AiToolCall("call-1", "triage") },
            new AiTokenUsage(inputTokens: 100, outputTokens: 20, totalTokens: 120),
            new AiCostMetadata(0.42m, "EUR", isEstimated: false));

    private static AiGatewayResponse FailedResponse() =>
        AiGatewayResponse.Failed(new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderUnavailable,
            "AI provider is unavailable.",
            isRetryable: true,
            new AiProviderMetadata("openai", "gpt-5.6-luna"),
            new AiGatewayFailureDiagnostics(providerStatusCode: 503),
            AiGatewayErrorReason.FallbackExhausted));

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
            ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
                OccupantChannelCorrelationTokenTests.SigningKey(),
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
