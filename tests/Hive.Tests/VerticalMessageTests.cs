using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class VerticalMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Directive_preserves_payload_and_derives_vertical_channel()
    {
        var directiveId = DirectiveId.New();
        var parentDirectiveId = DirectiveId.New();

        var message = CreateDirective(directiveId, parentDirectiveId);

        Assert.Equal(directiveId, message.DirectiveId);
        Assert.Equal(parentDirectiveId, message.ParentDirectiveId);
        Assert.Equal("Triage the reported bug", message.Objective);
        Assert.Equal("Customer impact is under investigation", message.Context);
        Assert.Equal(MessageChannel.Vertical, message.Channel);
    }

    [Fact]
    public void Directive_allows_root_lineage()
    {
        var message = CreateDirective(DirectiveId.New(), parentDirectiveId: null);

        Assert.Null(message.ParentDirectiveId);
    }

    [Fact]
    public void Directive_rejects_self_parenting()
    {
        var directiveId = DirectiveId.New();

        Assert.Throws<ArgumentException>(() => CreateDirective(directiveId, directiveId));
    }

    [Fact]
    public void Directive_rejects_missing_directive_identity()
    {
        Assert.Throws<ArgumentNullException>(() => CreateDirective(null!, parentDirectiveId: null));
    }

    [Fact]
    public void Report_preserves_payload_and_derives_vertical_channel()
    {
        var aboutDirectiveId = DirectiveId.New();

        var message = CreateReport(aboutDirectiveId, ReportKind.Progress);

        Assert.Equal(aboutDirectiveId, message.AboutDirectiveId);
        Assert.Equal(ReportKind.Progress, message.Kind);
        Assert.Equal("Reproduction confirmed", message.Body);
        Assert.Equal(MessageChannel.Vertical, message.Channel);
    }

    [Fact]
    public void Report_rejects_undefined_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateReport(DirectiveId.New(), (ReportKind)0));
    }

    [Fact]
    public void Report_rejects_missing_directive_reference()
    {
        Assert.Throws<ArgumentNullException>(
            () => CreateReport(null!, ReportKind.Progress));
    }

    [Fact]
    public void Escalation_preserves_payload_and_derives_vertical_channel()
    {
        var options = new[] { "Rollback", "Prepare a hotfix" };

        var message = CreateEscalation(options);

        Assert.Equal("Production deployment is blocked", message.Issue);
        Assert.Equal("The deployment credential has expired", message.Context);
        Assert.Equal(options, message.OptionsConsidered);
        Assert.Equal(MessageChannel.Vertical, message.Channel);
    }

    [Fact]
    public void Escalation_snapshots_considered_options()
    {
        var options = new[] { "Rollback", "Prepare a hotfix" };
        var message = CreateEscalation(options);

        options[0] = "Ignore the failure";

        Assert.Equal("Rollback", message.OptionsConsidered[0]);
    }

    [Fact]
    public void Escalation_rejects_missing_considered_options()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => CreateEscalation(null!));

        Assert.Equal("optionsConsidered", exception.ParamName);
    }

    [Fact]
    public void Vertical_payload_properties_are_get_only()
    {
        AssertGetOnly<Directive>(
            nameof(Directive.DirectiveId),
            nameof(Directive.ParentDirectiveId),
            nameof(Directive.Objective),
            nameof(Directive.Context),
            nameof(Directive.Channel));
        AssertGetOnly<Report>(
            nameof(Report.AboutDirectiveId),
            nameof(Report.Kind),
            nameof(Report.Body),
            nameof(Report.Channel));
        AssertGetOnly<Escalation>(
            nameof(Escalation.Issue),
            nameof(Escalation.Context),
            nameof(Escalation.OptionsConsidered),
            nameof(Escalation.Channel));
    }

    private static Directive CreateDirective(
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("delivery-lead"),
            Position("bug-triage"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            SentAt.AddHours(4),
            directiveId,
            parentDirectiveId,
            "Triage the reported bug",
            "Customer impact is under investigation");

    private static Report CreateReport(DirectiveId aboutDirectiveId, ReportKind kind) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            Position("delivery-lead"),
            ThreadId.New(),
            Priority.Normal,
            1,
            SentAt,
            null,
            aboutDirectiveId,
            kind,
            "Reproduction confirmed");

    private static Escalation CreateEscalation(IEnumerable<string> optionsConsidered) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            Position("delivery-lead"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            "Production deployment is blocked",
            "The deployment credential has expired",
            optionsConsidered);

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static void AssertGetOnly<T>(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = typeof(T).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }
}
