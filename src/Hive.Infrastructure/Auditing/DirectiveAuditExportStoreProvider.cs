using Hive.Domain.Auditing;
using Hive.Infrastructure.Auditing.PostgreSql;

namespace Hive.Infrastructure.Auditing;

internal sealed class DirectiveAuditExportStoreProvider : IAsyncDisposable
{
    private readonly IAsyncDisposable? _ownedStore;

    public DirectiveAuditExportStoreProvider(
        DirectiveAuditExportScopeCatalog catalog,
        string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Count == 0)
        {
            Reader = NoopDirectiveAuditExportStore.Instance;
            ResultSink = NoopDirectiveAuditExportStore.Instance;
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "An enabled audit/export profile requires ConnectionStrings:PostgreSql.");
        }

        var store = new PostgreSqlDirectiveAuditExportStore(connectionString);
        Reader = store;
        ResultSink = store;
        _ownedStore = store;
    }

    public IDirectiveAuditExportReader Reader { get; }

    public IDirectiveAuditExportResultSink ResultSink { get; }

    public async ValueTask DisposeAsync()
    {
        if (_ownedStore is not null)
        {
            await _ownedStore.DisposeAsync().ConfigureAwait(false);
        }
    }
}
