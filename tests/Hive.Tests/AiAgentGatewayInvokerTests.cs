using Akka.Actor;
using Hive.Actors;
using Hive.Actors.Positions;
using Hive.Actors.Sharding;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class AiAgentGatewayInvokerTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111112"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222223"));

    [Fact]
    public async Task InvokeAsync_delegates_once_and_preserves_correlation_request_and_cancellation()
    {
        var request = Request();
        var response = SuccessResponse();
        var gateway = new RecordingGateway(response);
        var invoker = new AiAgentGatewayInvoker(gateway);
        using var cancellation = new CancellationTokenSource();

        var result = await invoker.InvokeAsync(
            new AiAgentGatewayInvocation("message:22222222-2222-2222-2222-222222222223", request),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Null(result.FailureReason);
        Assert.Equal("message:22222222-2222-2222-2222-222222222223", result.CorrelationId);
        Assert.Same(response, result.Response);
        Assert.Equal(1, gateway.CallCount);
        Assert.Same(request, gateway.Request);
        Assert.Equal(cancellation.Token, gateway.CancellationToken);
        Assert.Equal(Organization, result.Response.OrganizationId);
        Assert.Equal(Position, result.Response.PositionId);
        Assert.Equal(Thread, result.Response.ThreadId);
        Assert.Equal(Message, result.Response.MessageId);
    }

    [Fact]
    public async Task InvokeAsync_exposes_gateway_error_as_structured_failure_reason()
    {
        var provider = new AiProviderMetadata("openai", "gpt-4.1");
        var request = Request(provider);
        var error = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderRejected,
            "Provider rejected the request.",
            isRetryable: false,
            provider);
        var gateway = new RecordingGateway(AiGatewayResponse.Failed(error));
        var invoker = new AiAgentGatewayInvoker(gateway);

        var result = await invoker.InvokeAsync(new AiAgentGatewayInvocation("thread:triage", request));

        Assert.True(result.IsFailure);
        Assert.Same(error, result.FailureReason);
        Assert.Equal(AiGatewayErrorCode.ProviderRejected, result.FailureReason!.Code);
        Assert.Equal("thread:triage", result.CorrelationId);
    }

    [Fact]
    public async Task InvokeAsync_passes_precanceled_token_to_gateway_and_propagates_cancellation()
    {
        var gateway = new CancelingGateway();
        var invoker = new AiAgentGatewayInvoker(gateway);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await invoker.InvokeAsync(
                new AiAgentGatewayInvocation("thread:canceled", Request()),
                cancellation.Token));

        Assert.Equal(1, gateway.CallCount);
        Assert.Equal(cancellation.Token, gateway.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_converts_unexpected_gateway_exception_to_sanitized_structured_failure()
    {
        var request = Request(new AiProviderMetadata("openai", "gpt-4.1"));
        var gateway = new ThrowingGateway(
            new InvalidOperationException("leaked type and secret sk-live-123456789"));
        var invoker = new AiAgentGatewayInvoker(gateway);

        var result = await invoker.InvokeAsync(
            new AiAgentGatewayInvocation("thread:unexpected", request));

        Assert.True(result.IsFailure);
        Assert.Equal("thread:unexpected", result.CorrelationId);
        var failure = Assert.IsType<AiGatewayError>(result.FailureReason);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, failure.Code);
        Assert.True(failure.IsRetryable);
        Assert.Equal(Organization, failure.OrganizationId);
        Assert.Equal(Position, failure.PositionId);
        Assert.Equal(Thread, failure.ThreadId);
        Assert.Equal(Message, failure.MessageId);
        Assert.Equal("openai", failure.Provider!.ProviderId);
        Assert.Contains("AI gateway invocation failed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiAgentActor_invokes_gateway_through_injected_invoker()
    {
        var invoker = new RecordingInvoker();
        var invocation = new AiAgentGatewayInvocation("actor:gateway", Request());
        var system = ActorSystem.Create($"ai-agent-gateway-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(OccupantId.From("agent-7"), invoker)),
                "agent");

            var result = await actor.Ask<AiAgentGatewayInvocationResult>(
                invocation,
                Timeout());

            Assert.True(result.IsSuccess);
            Assert.Equal("actor:gateway", result.CorrelationId);
            Assert.Equal(1, invoker.CallCount);
            Assert.Same(invocation, invoker.Invocation);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void AddHiveActorSystem_composes_ai_occupant_factory_outside_position_entity_props()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = "0",
            ["Hive:Node:Roles:0"] = NodeRoleNames.Agents,
        });

        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        using var host = builder.Build();

        Assert.IsType<AiAgentGatewayInvoker>(
            host.Services.GetRequiredService<IAiAgentGatewayInvoker>());
        Assert.IsType<FailClosedAiAgentActionGate>(
            host.Services.GetRequiredService<IAiAgentActionGate>());
        Assert.IsType<PositionOccupantFactory>(
            host.Services.GetRequiredService<IPositionOccupantFactory>());

        var props = host.Services.GetRequiredService<IPositionEntityProps>();
        Assert.IsType<Props>(props.Create("acme-delivery/triage-agent"));
    }

    [Fact]
    public void PositionEntityProps_depends_on_neutral_occupant_factory_not_ai_contracts()
    {
        var parameters = typeof(PositionEntityProps)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IPositionOccupantFactory), parameters);
        Assert.DoesNotContain(typeof(IAiAgentGatewayInvoker), parameters);
        Assert.DoesNotContain(typeof(IAiAgentActionGate), parameters);
    }

    [Fact]
    public void Actors_project_does_not_introduce_ai_gateway_actor()
    {
        var gatewayActors = typeof(AiAgentActor).Assembly
            .GetTypes()
            .Where(type => type.Name.Contains("AiGatewayActor", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(gatewayActors);
    }

    private static AiGatewayRequest Request(AiProviderMetadata? provider = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify the incoming directive.",
            provider: provider);

    private static AiGatewayResponse SuccessResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The directive is actionable.",
            AiFinishReason.Stop);

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class RecordingGateway(AiGatewayResponse response) : IAiGateway
    {
        public int CallCount { get; private set; }

        public AiGatewayRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            return Task.FromResult(response);
        }
    }

    private sealed class CancelingGateway : IAiGateway
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(SuccessResponse());
        }
    }

    private sealed class ThrowingGateway(Exception exception) : IAiGateway
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AiGatewayResponse>(exception);
    }

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public AiAgentGatewayInvocation? Invocation { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Invocation = invocation;

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                SuccessResponse()));
        }
    }
}
