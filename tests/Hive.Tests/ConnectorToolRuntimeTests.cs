using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Positions;
using Hive.Infrastructure.Connectors;

namespace Hive.Tests;

public sealed class ConnectorToolRuntimeTests
{
    [Fact]
    public async Task Retained_tool_reuses_canonical_call_and_runtime_operation_key()
    {
        var tool = new RecordingTool("issues.comment");
        var runtime = new ConnectorToolRuntime(new ConnectorToolRegistry([tool]));
        var action = new PersistedRetainedAction(
            RetainedActionId.New(),
            ActionFingerprint.From(
                "sha256:0000000000000000000000000000000000000000000000000000000000000001"),
            RetainedActionKind.Tool,
            "issues.comment",
            "{\"Arguments\":{\"body\":\"Approved response\"},\"Id\":\"call-42\",\"Name\":\"issues.comment\"}",
            "{}",
            "directive:approved",
            OrganizationId.From("acme"),
            PositionId.From("bug-triage"),
            ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            parentDirectiveId: null,
            "action-gate-objective-human-approval",
            new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));

        var first = await runtime.ExecuteAsync(
            new RetainedActionExecutionRequest(action, MessageId.New()));
        var second = await runtime.ExecuteAsync(
            new RetainedActionExecutionRequest(action, MessageId.New()));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, tool.Invocations.Count);
        Assert.Equal(tool.Invocations[0].OperationKey, tool.Invocations[1].OperationKey);
        Assert.Equal(
            ConnectorToolOperationKey.Create(
                action.OrganizationId.Value,
                action.DirectiveId.Value,
                tool.Invocations[0].ToolCall),
            tool.Invocations[0].OperationKey);
        Assert.Equal("Approved response",
            ((System.Text.Json.JsonElement)tool.Invocations[0].ToolCall.Arguments["body"]!).GetString());
    }

    [Fact]
    public void Registry_rejects_duplicate_tool_names()
    {
        Assert.Throws<InvalidOperationException>(() => new ConnectorToolRegistry(
        [
            new RecordingTool("issues.comment"),
            new RecordingTool("issues.comment"),
        ]));
    }

    private sealed class RecordingTool : IConnectorTool
    {
        public RecordingTool(string name)
        {
            Definition = new AiToolDefinition(name, "Test connector tool.");
        }

        public AiToolDefinition Definition { get; }

        public List<ConnectorToolInvocation> Invocations { get; } = [];

        public ValueTask<ConnectorToolResult> ExecuteAsync(
            ConnectorToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            return ValueTask.FromResult(ConnectorToolResult.Succeeded());
        }
    }
}

