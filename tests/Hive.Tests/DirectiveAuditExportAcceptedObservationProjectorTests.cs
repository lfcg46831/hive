using Hive.Domain.Auditing;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class DirectiveAuditExportAcceptedObservationProjectorTests
{
    [Fact]
    public void Projector_retains_only_the_bounded_canonical_observation()
    {
        var projection = DirectiveAuditExportAcceptedObservationProjector.TryProject(
            Message(
                "Private triage assessment.\n" +
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\"],\"missing-information\":[\"environment\"]}}"));

        Assert.NotNull(projection);
        Assert.Equal(1, projection.ContractVersion);
        Assert.Equal(
            "{\"dimensions\":{\"missing-information\":[\"environment\"],\"severity\":[\"medium\"]}}",
            projection.Content);
        Assert.DoesNotContain(
            "Private triage assessment",
            projection.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hive-evaluation-v1",
            projection.Content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No observation marker.")]
    [InlineData("hive-evaluation-v1:{\"dimensions\":{},\"extra\":true}")]
    [InlineData("hive-evaluation-v1:{\"dimensions\":{\"severity\":\"medium\"}}")]
    [InlineData("hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\",\"medium\"]}}")]
    [InlineData("hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\"]}}\nhive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"]}}")]
    public void Projector_fails_closed_without_retaining_rejected_values(string body)
    {
        Assert.Null(
            DirectiveAuditExportAcceptedObservationProjector.TryProject(Message(body)));
    }

    private static DirectiveAuditExportMessageData Message(string body) =>
        new(
            "Report",
            1,
            System.Text.Json.JsonSerializer.Serialize(new { Body = body }));
}
