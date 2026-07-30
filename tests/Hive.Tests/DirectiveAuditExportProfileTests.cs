using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Infrastructure.Auditing;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DirectiveAuditExportProfileTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId AllowedPosition = PositionId.From("bug-triage");
    private static readonly PositionId OtherPosition = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000216"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000216"));

    [Fact]
    public async Task Normal_runtime_resolves_noop_export_ports_without_an_enabled_profile()
    {
        using var host = BuildHost(new Dictionary<string, string?>(
            StringComparer.Ordinal));

        Assert.Same(
            NoopDirectiveAuditExportStore.Instance,
            host.Services.GetRequiredService<IDirectiveAuditExportReader>());
        Assert.Same(
            NoopDirectiveAuditExportStore.Instance,
            host.Services.GetRequiredService<IDirectiveAuditExportResultSink>());
        await host.StopAsync();
    }

    [Fact]
    public void Enabled_profile_requires_explicit_storage_configuration()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Hive:Evaluation:Profiles:lab:Enabled"] = "true",
            ["Hive:Evaluation:Profiles:lab:OrganizationId"] = Organization.Value,
            ["Hive:Evaluation:Profiles:lab:PositionId"] = AllowedPosition.Value,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            host.Services.GetRequiredService<IDirectiveAuditExportReader>());
        Assert.Contains(
            "ConnectionStrings:PostgreSql",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_adapters_forward_only_the_enabled_organization_and_position()
    {
        var catalog = DirectiveAuditExportScopeCatalog.Load(new DirectiveAuditExportOptions
        {
            Profiles = new Dictionary<string, DirectiveAuditExportProfileOptions>(
                StringComparer.Ordinal)
            {
                ["lab"] = new()
                {
                    Enabled = true,
                    OrganizationId = Organization.Value,
                    PositionId = AllowedPosition.Value,
                },
            },
        });
        var storage = new RecordingStore();
        var reader = new ScopedDirectiveAuditExportReader(catalog, storage);
        var sink = new ScopedDirectiveAuditExportResultSink(catalog, storage);

        await sink.StoreAsync(Result(AllowedPosition));
        await sink.StoreAsync(Result(OtherPosition));
        var page = await reader.ReadAsync(
            Organization,
            Thread,
            Directive,
            0,
            100);

        Assert.Single(storage.StoredResults);
        Assert.Equal(AllowedPosition, storage.StoredResults[0].SourcePositionId);
        var exported = Assert.Single(page.Events);
        Assert.Equal(AllowedPosition, exported.Record.PositionId);
        Assert.True(page.IsTerminal);
        Assert.NotNull(page.Result);
    }

    private static IHost BuildHost(
        IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder.Build();
    }

    private static DirectiveAuditExportResultData Result(PositionId position) =>
        new(
            Organization,
            Thread,
            Directive,
            position,
            "Report",
            1,
            """{"schema_version":1}""");

    private sealed class RecordingStore :
        IDirectiveAuditExportReader,
        IDirectiveAuditExportResultSink
    {
        public List<DirectiveAuditExportResultData> StoredResults { get; } = [];

        public ValueTask<DirectiveAuditExportPageData> ReadAsync(
            OrganizationId organizationId,
            ThreadId threadId,
            DirectiveId directiveId,
            long afterSequence,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DirectiveAuditExportPageData(
                organizationId,
                threadId,
                directiveId,
                afterSequence,
                [
                    Event(afterSequence + 1, AllowedPosition),
                    Event(afterSequence + 2, OtherPosition),
                ],
                isTerminal: true,
                Result(AllowedPosition)));

        public ValueTask StoreAsync(
            DirectiveAuditExportResultData result,
            CancellationToken cancellationToken = default)
        {
            StoredResults.Add(result);
            return ValueTask.CompletedTask;
        }

        private static DirectiveAuditExportEventData Event(
            long sequence,
            PositionId position) =>
            new(
                sequence,
                JourneyAuditRecord.Create(
                    JourneyAuditStage.ResultMessageCreated,
                    JourneyAuditOutcome.Succeeded,
                    Organization,
                    Thread,
                    MessageId.From(Guid.NewGuid()),
                    Directive,
                    position,
                    messageType: "Report"));
    }
}
