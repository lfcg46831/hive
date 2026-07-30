using System.Net;
using System.Net.Http.Json;
using Hive.Api.Auditing;
using Hive.Contracts.Audit;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class DirectiveAuditExportEndpointTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000016"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000016"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000016"));
    private static readonly DateTimeOffset At =
        new(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Endpoint_returns_the_bounded_v1_page_and_terminal_canonical_result()
    {
        var reader = new RecordingReader((organization, thread, directive, cursor) =>
            new DirectiveAuditExportPageData(
                organization,
                thread,
                directive,
                cursor,
                [
                    new DirectiveAuditExportEventData(
                        cursor + 1,
                        AuditRecord(organization, thread, directive)),
                ],
                isTerminal: true,
                new DirectiveAuditExportResultData(
                    organization,
                    thread,
                    directive,
                    PositionId.From("bug-triage"),
                    "Report",
                    1,
                    """{"schema_version":1,"type":"Report"}""")));
        await using var app = BuildApp(reader);
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync(
            $"/api/v1/organizations/acme/threads/{Thread.Value}/directives/{Directive.Value}/audit-export?after_sequence=9");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<DirectiveAuditExportPage>();
        Assert.NotNull(page);
        Assert.Equal(AuditExportContract.Name, page.ContractName);
        Assert.Equal(AuditExportContract.Version, page.ContractVersion);
        Assert.Equal(9, page.AfterSequence);
        Assert.Equal(10, page.NextAfterSequence);
        Assert.True(page.IsTerminal);
        Assert.Equal(100, reader.LastPageSize);
        Assert.Equal(Organization, reader.LastOrganization);
        Assert.Equal(Thread, reader.LastThread);
        Assert.Equal(Directive, reader.LastDirective);

        var exportedEvent = Assert.Single(page.Events);
        Assert.Equal(10, exportedEvent.Sequence);
        Assert.Equal("safe-value", exportedEvent.Attributes["status"]);
        Assert.DoesNotContain("prompt", exportedEvent.Attributes.Keys);
        Assert.DoesNotContain("raw_output", exportedEvent.Attributes.Keys);
        Assert.DoesNotContain("reasoning_trace", exportedEvent.Attributes.Keys);
        Assert.DoesNotContain("short_memory", exportedEvent.Attributes.Keys);
        Assert.Equal("pricing-v1", exportedEvent.Cost!.PricingVersion);
        Assert.Equal(1_000, exportedEvent.Cost.PricingTokenUnit);

        Assert.NotNull(page.Result);
        Assert.Equal("Report", page.Result.MessageType);
        Assert.Equal(AuditExportContract.ResultMediaType, page.Result.MediaType);
        Assert.Equal("""{"schema_version":1,"type":"Report"}""", page.Result.Content);
    }

    [Fact]
    public async Task Endpoint_rejects_a_negative_cursor_before_reading_storage()
    {
        var reader = new RecordingReader((organization, thread, directive, cursor) =>
            new DirectiveAuditExportPageData(
                organization,
                thread,
                directive,
                cursor,
                [],
                isTerminal: false));
        await using var app = BuildApp(reader);
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync(
            $"/api/v1/organizations/acme/threads/{Thread.Value}/directives/{Directive.Value}/audit-export?after_sequence=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, reader.CallCount);
    }

    private static JourneyAuditRecord AuditRecord(
        OrganizationId organization,
        ThreadId thread,
        DirectiveId directive) =>
        new(
            Guid.Parse("dddddddd-0000-0000-0000-000000000016"),
            At,
            JourneyAuditStage.AgentDecided,
            JourneyAuditOutcome.Succeeded,
            organization,
            thread,
            Message,
            directive,
            PositionId.From("bug-triage"),
            messageType: "Report",
            provider: new AiProviderMetadata("stub", "triage"),
            usage: new AiTokenUsage(10, 5, 15, isEstimated: false),
            cost: new AiCostMetadata(0.015m, "usd", isEstimated: false),
            latency: TimeSpan.FromMilliseconds(125),
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "safe-value",
                ["prompt"] = "must-not-leave-runtime",
                ["raw_output"] = "must-not-leave-runtime",
                ["reasoning_trace"] = "must-not-leave-runtime",
                ["short_memory"] = "must-not-leave-runtime",
                ["pricingVersion"] = "pricing-v1",
                ["pricingTokenUnit"] = "1000",
                ["inputPricePerTokenUnit"] = "0.10",
                ["outputPricePerTokenUnit"] = "0.20",
            },
            persistedAtUtc: At.AddMilliseconds(5));

    private static WebApplication BuildApp(IDirectiveAuditExportReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHiveDirectiveAuditExportApi();
        builder.Services.AddSingleton<IDirectiveAuditExportReader>(reader);
        var app = builder.Build();
        app.MapHiveDirectiveAuditExportApi();
        return app;
    }

    private sealed class RecordingReader(
        Func<OrganizationId, ThreadId, DirectiveId, long, DirectiveAuditExportPageData> page)
        : IDirectiveAuditExportReader
    {
        public int CallCount { get; private set; }

        public int LastPageSize { get; private set; }

        public OrganizationId? LastOrganization { get; private set; }

        public ThreadId? LastThread { get; private set; }

        public DirectiveId? LastDirective { get; private set; }

        public ValueTask<DirectiveAuditExportPageData> ReadAsync(
            OrganizationId organizationId,
            ThreadId threadId,
            DirectiveId directiveId,
            long afterSequence,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastOrganization = organizationId;
            LastThread = threadId;
            LastDirective = directiveId;
            LastPageSize = pageSize;
            return ValueTask.FromResult(page(
                organizationId,
                threadId,
                directiveId,
                afterSequence));
        }
    }
}
