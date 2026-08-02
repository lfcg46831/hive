using Hive.Domain.Auditing;

namespace Hive.Infrastructure.Auditing;

internal sealed class ScopedDirectiveAuditExportReader :
    IDirectiveAuditExportReader
{
    private readonly DirectiveAuditExportScopeCatalog _catalog;
    private readonly IDirectiveAuditExportReader _inner;

    public ScopedDirectiveAuditExportReader(
        DirectiveAuditExportScopeCatalog catalog,
        IDirectiveAuditExportReader inner)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<DirectiveAuditExportPageData> ReadAsync(
        Hive.Domain.Identity.OrganizationId organizationId,
        Hive.Domain.Identity.ThreadId threadId,
        Hive.Domain.Identity.DirectiveId directiveId,
        long afterSequence,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _inner.ReadAsync(
                organizationId,
                threadId,
                directiveId,
                afterSequence,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
        var events = page.Events
            .Where(item =>
                item.Record.PositionId is { } position &&
                _catalog.Allows(organizationId, position))
            .ToArray();
        var result = page.Result is { } candidate &&
            _catalog.Allows(organizationId, candidate.SourcePositionId)
                ? candidate
                : null;
        var isAuthorizedPage = events.Length > 0 || result is not null;

        return new DirectiveAuditExportPageData(
            organizationId,
            threadId,
            directiveId,
            afterSequence,
            events,
            page.IsTerminal && isAuthorizedPage && (
                page.Result is null || result is not null),
            result);
    }
}

internal sealed class ScopedDirectiveAuditExportResultSink :
    IDirectiveAuditExportResultSink
{
    private readonly DirectiveAuditExportScopeCatalog _catalog;
    private readonly IDirectiveAuditExportResultSink _inner;

    public ScopedDirectiveAuditExportResultSink(
        DirectiveAuditExportScopeCatalog catalog,
        IDirectiveAuditExportResultSink inner)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public ValueTask StoreAsync(
        DirectiveAuditExportResultCaptureData capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return _catalog.Allows(
                capture.Result.OrganizationId,
                capture.Result.SourcePositionId)
            ? _inner.StoreAsync(capture, cancellationToken)
            : ValueTask.CompletedTask;
    }
}
