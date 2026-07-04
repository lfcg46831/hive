using Hive.Actors.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveIntegrationFixtureTests
{
    [Fact]
    public async Task Fixture_dispatches_directive_through_position_actor_ai_agent_and_configured_gateway_stub()
    {
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            AiDirectiveIntegrationScenario.Create(configureStub: options =>
            {
                options.ModelId = "fixture-scenario";
                options.Text = ValidReportOutput();
            }));

        var result = await fixture.ProcessDirectiveAsync();

        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        Assert.Equal("result-emitted", result.Audit.TerminalCode);
        Assert.Equal("fixture-scenario", result.GatewayInvocation.Response.Provider!.ModelId);
        Assert.Contains(result.Directive.Id, result.PositionState.ProcessedMessages);
        Assert.Equal(
            "Report Done: Integration report complete.",
            result.PositionState.ShortMemory[
                $"directive:{result.Directive.DirectiveId.Value:N}:result"]);
    }

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "Integration report complete."
          }
        }
        """;
}
