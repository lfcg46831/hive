using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectivePromptTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateInitialRequest_rejects_null_context()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AiDirectivePrompt.CreateInitialRequest(null!));
    }

    [Fact]
    public void CreateInitialRequest_creates_provider_neutral_request_without_assuming_bug_triage_role()
    {
        var processingRequest = Request(includeOptionalContext: true);
        var context = AiDirectiveExecutionContext.From(processingRequest);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(processingRequest.OrganizationId, request.OrganizationId);
        Assert.Equal(processingRequest.PositionId, request.PositionId);
        Assert.Equal(processingRequest.ThreadId, request.ThreadId);
        Assert.Equal(processingRequest.MessageId, request.MessageId);
        Assert.Equal("stub", request.Provider!.ProviderId);
        Assert.Equal("triage", request.Provider.ModelId);
        Assert.Equal(256, request.ModelParameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        Assert.Equal(AiProcessingMode.Batch, request.ProcessingMode);
        var requestTool = Assert.Single(request.Tools);
        Assert.Equal("jira", requestTool.Name);
        Assert.Equal(
            "Authorized HIVE connector 'jira' with scopes: issues/read, issues/comment.",
            requestTool.Description);
        Assert.Empty(requestTool.ParametersSchema);

        Assert.NotNull(request.SystemInstruction);
        Assert.Contains("current position", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Escalate work outside this position's authority", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Return JSON only", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("\"intent\"", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Report", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Escalation", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Directive", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Do not invent routing", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "Directive only when a permitted downward target is explicit",
            request.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bug triage agent", request.SystemInstruction, StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"CorrelationId: {processingRequest.CorrelationId}", request.Content, StringComparison.Ordinal);
        Assert.Contains("IdentityPromptRef: triage-v1", request.Content, StringComparison.Ordinal);
        Assert.Contains("IdentityPrompt:", request.Content, StringComparison.Ordinal);
        Assert.Contains("Path: prompts/triage-v1.md", request.Content, StringComparison.Ordinal);
        Assert.Contains("You are responsible for triaging incoming bugs.", request.Content, StringComparison.Ordinal);
        Assert.Contains("Objective: Triage checkout regression", request.Content, StringComparison.Ordinal);
        Assert.Contains("Context: Customer reports checkout failures.", request.Content, StringComparison.Ordinal);
        Assert.Contains("DirectiveId: cccccccc-0000-0000-0000-000000000904", request.Content, StringComparison.Ordinal);
        Assert.Contains("ParentDirectiveId: cccccccc-0000-0000-0000-000000000099", request.Content, StringComparison.Ordinal);
        Assert.Contains("Deadline: 2026-07-01T18:00:00.0000000+00:00", request.Content, StringComparison.Ordinal);
        Assert.Contains("ReportsTo: delivery-lead", request.Content, StringComparison.Ordinal);
        Assert.Contains("CanDecide: bug.triage", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorityOverrides:", request.Content, StringComparison.Ordinal);
        Assert.Contains("- comms.external-official: human-approval (approver: delivery-lead)", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("MustEscalate", request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("RequiresHumanApproval", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorizedTools:", request.Content, StringComparison.Ordinal);
        Assert.Contains("- jira: issues/read, issues/comment", request.Content, StringComparison.Ordinal);
        AssertContainsInOrder(request.Content, "- alpha: first", "- zeta: last");
        AssertContainsInOrder(request.Content, "First task", "Second task");
        Assert.DoesNotContain("```", request.SystemInstruction + request.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bug triage directive context", request.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInitialRequest_represents_missing_optional_context_explicitly()
    {
        var processingRequest = Request(includeOptionalContext: false);
        var context = AiDirectiveExecutionContext.From(processingRequest);

        var request = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Contains("IdentityPromptRef: triage-v1", request.Content, StringComparison.Ordinal);
        Assert.Contains("You are responsible for triaging incoming bugs.", request.Content, StringComparison.Ordinal);
        Assert.Contains("ParentDirectiveId: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("Deadline: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("ReportsTo: <none>", request.Content, StringComparison.Ordinal);
        Assert.Contains("ShortMemory: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("OpenTasks: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("RecentHistory: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("CanDecide: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorityOverrides: <empty>", request.Content, StringComparison.Ordinal);
        Assert.Contains("AuthorizedTools: <empty>", request.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiAgentActor_invokes_gateway_after_initial_prompt_with_policy_and_limits()
    {
        var processingRequest = Request(
            includeOptionalContext: true,
            maxIterations: 3);
        var invoker = new RecordingInvoker();
        var system = ActorSystem.Create($"ai-agent-prompt-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            var promptResult = await WaitForPromptAsync(actor, processingRequest.CorrelationId);
            var invocation = await invoker.Invoked.Task.WaitAsync(Timeout());
            var snapshotResult = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(processingRequest.CorrelationId),
                Timeout());
            var gatewayResult = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation(processingRequest.CorrelationId),
                Timeout());
            var gatewayRequest = invocation.Request;

            Assert.True(promptResult.Found);
            Assert.Equal(processingRequest.CorrelationId, promptResult.CorrelationId);
            Assert.Equal(processingRequest.MessageId, promptResult.Request!.MessageId);
            Assert.Contains("Return JSON only", promptResult.Request.SystemInstruction, StringComparison.Ordinal);
            Assert.Same(promptResult.Request, gatewayRequest);
            Assert.Equal(1, invoker.CallCount);
            Assert.Equal(processingRequest.CorrelationId, invocation.CorrelationId);
            Assert.Equal(processingRequest.OrganizationId, gatewayRequest.OrganizationId);
            Assert.Equal(processingRequest.PositionId, gatewayRequest.PositionId);
            Assert.Equal(processingRequest.ThreadId, gatewayRequest.ThreadId);
            Assert.Equal(processingRequest.MessageId, gatewayRequest.MessageId);
            Assert.Equal(TimeSpan.FromSeconds(15), gatewayRequest.Timeout);
            Assert.Equal(256, gatewayRequest.ModelParameters.MaxOutputTokens);
            Assert.Equal("3", gatewayRequest.Metadata["max_iterations"]);
            Assert.True(invoker.CancellationToken.CanBeCanceled);

            var policy = Assert.IsType<AiGatewayPolicy>(gatewayRequest.Policy);
            Assert.True(policy.HasAvailableBudget);
            Assert.Equal(TimeSpan.FromSeconds(15), policy.MaxTimeout);
            Assert.Equal(256, policy.MaxOutputTokens);
            var authorizedModel = Assert.Single(policy.AuthorizedModels);
            Assert.Equal("stub", authorizedModel.ProviderId);
            Assert.Equal("triage", authorizedModel.ModelId);
            Assert.Equal([AiProcessingMode.Batch], policy.AllowedProcessingModes.ToArray());
            Assert.Equal(["jira"], policy.AuthorizedTools.ToArray());
            var gatewayTool = Assert.Single(gatewayRequest.Tools);
            Assert.Equal("jira", gatewayTool.Name);
            Assert.Equal(
                "Authorized HIVE connector 'jira' with scopes: issues/read, issues/comment.",
                gatewayTool.Description);
            Assert.Empty(gatewayTool.ParametersSchema);
            Assert.True(gatewayResult.Found);
            Assert.True(gatewayResult.Result!.IsSuccess);
            Assert.Equal(processingRequest.CorrelationId, gatewayResult.Result.CorrelationId);
            Assert.Contains("\"intent\":\"Report\"", gatewayResult.Result.Response.Text, StringComparison.Ordinal);

            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshotResult.Snapshot!.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                    AiDirectiveProcessingStatus.GatewayRequested,
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    AiDirectiveProcessingStatus.ResultEmitted,
                ],
                snapshotResult.Snapshot.History.Select(transition => transition.Status).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_fails_processing_when_gateway_timeout_cancels_invocation()
    {
        var processingRequest = Request(
            includeOptionalContext: true,
            timeout: TimeSpan.FromMilliseconds(50));
        var invoker = new WaitForCancellationInvoker();
        var system = ActorSystem.Create($"ai-agent-gateway-timeout-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            await invoker.Started.Task.WaitAsync(Timeout());
            var snapshotResult = await WaitForSnapshotAsync(actor, processingRequest.CorrelationId);

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshotResult.Snapshot!.Status);
            Assert.Contains("timeout", snapshotResult.Snapshot.TerminalReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(invoker.CancellationToken.IsCancellationRequested);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_fails_closed_when_identity_prompt_is_not_resolved()
    {
        var processingRequest = Request(includeOptionalContext: false, includeIdentityPrompt: false);
        var invoker = new ThrowingInvoker();
        var system = ActorSystem.Create($"ai-agent-prompt-fail-closed-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(processingRequest.Occupant, invoker)),
                "agent");

            actor.Tell(processingRequest);

            var snapshotResult = await WaitForSnapshotAsync(actor, processingRequest.CorrelationId);
            var promptResult = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt(processingRequest.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshotResult.Snapshot!.Status);
            Assert.Contains(
                "identity prompt",
                snapshotResult.Snapshot.TerminalReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(promptResult.Found);
            Assert.Equal(0, invoker.CallCount);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_bug_triage_prompt_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-prompt-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new ThrowingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Request);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_gateway_invocation_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-gateway-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new ThrowingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Result);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveInitialPromptQueryResult> WaitForPromptAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive initial prompt was not built.");
    }

    private static async Task<AiDirectiveProcessingSnapshotQueryResult> WaitForSnapshotAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive processing snapshot was not recorded.");
    }

    private static AiDirectiveProcessingRequest Request(
        bool includeOptionalContext,
        bool includeIdentityPrompt = true,
        TimeSpan? timeout = null,
        int? maxIterations = null)
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(11, "sha256:t05");
        var parentDirective = includeOptionalContext
            ? DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000099"))
            : null;
        var directive = new OrgDirective(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000904")),
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000904")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: includeOptionalContext ? At.AddHours(2) : null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000904")),
            parentDirective,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");

        var configuration = new PositionRuntimeConfiguration(
            stamp,
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: includeOptionalContext ? PositionId.From("delivery-lead") : null,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: includeIdentityPrompt ? "triage-v1" : null,
                tools: includeOptionalContext
                    ? [new ToolConfiguration("jira", ["issues/read", "issues/comment"])]
                    : null,
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: timeout ?? TimeSpan.FromSeconds(15),
                    processingMode: AiProcessingMode.Batch,
                    maxIterations: maxIterations),
                identityPrompt: includeIdentityPrompt
                    ? new IdentityPromptRuntimeConfiguration(
                        "triage-v1",
                        "prompts/triage-v1.md",
                        "You are responsible for triaging incoming bugs.")
                    : null),
            includeOptionalContext
                ? new PositionAuthorityRuntimeConfiguration(
                    canDecide: ["bug.triage"],
                    overrides:
                    [
                        new PositionAuthorityOverrideRuntimeConfiguration(
                            "comms.external-official",
                            ActionDomainGate.HumanApproval,
                            "delivery-lead"),
                    ])
                : new PositionAuthorityRuntimeConfiguration());

        var state = includeOptionalContext
            ? PositionState.Restore(new PositionSnapshot(
                At,
                occupant,
                OccupantType.AiAgent,
                openTasks:
                [
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                        directive.Thread,
                        "Second task",
                        Priority.Normal,
                        At),
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                        directive.Thread,
                        "First task",
                        Priority.High,
                        At),
                ],
                shortMemory: new Dictionary<string, string>
                {
                    ["zeta"] = "last",
                    ["alpha"] = "first",
                },
                recentHistory:
                [
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000002")),
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
                ],
                lastConfigurationStamp: stamp))
            : PositionState.Restore(new PositionSnapshot(At, lastConfigurationStamp: stamp));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            occupant,
            directive);
    }

    private static void AssertContainsInOrder(string text, string first, string second)
    {
        var firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = text.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Expected to find '{first}'.");
        Assert.True(secondIndex >= 0, $"Expected to find '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class ThrowingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Gateway must not be invoked by T05.");
        }
    }

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public TaskCompletionSource<AiAgentGatewayInvocation> Invoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            Invoked.TrySetResult(invocation);

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}",
                    AiFinishReason.Stop)));
        }
    }

    private sealed class WaitForCancellationInvoker : IAiAgentGatewayInvoker
    {
        public CancellationToken CancellationToken { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            Started.TrySetResult();

            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token should stop the wait.");
        }
    }
}
