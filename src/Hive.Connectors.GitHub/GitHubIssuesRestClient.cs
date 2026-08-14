using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesRestErrorCodes
{
    public const string AuthenticationFailed = "github-authentication-failed";
    public const string Forbidden = "github-forbidden";
    public const string ResourceNotFound = "github-resource-not-found";
    public const string ResourceGone = "github-resource-gone";
    public const string RequestInvalid = "github-request-invalid";
    public const string ResponseInvalid = "github-response-invalid";
    public const string Unavailable = "github-unavailable";
    public const string RateLimited = "github-rate-limited";
}

internal sealed class GitHubIssuesRestClientException : Exception
{
    public GitHubIssuesRestClientException(
        string errorCode,
        bool isRetryable,
        DateTimeOffset? rateLimitNotBeforeUtc = null)
        : base($"GitHub Issues REST request failed with code '{errorCode}'.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (rateLimitNotBeforeUtc is { Offset: not { Ticks: 0 } })
        {
            throw new ArgumentException(
                "Rate-limit timestamp must use a UTC offset.",
                nameof(rateLimitNotBeforeUtc));
        }

        ErrorCode = errorCode;
        IsRetryable = isRetryable;
        RateLimitNotBeforeUtc = rateLimitNotBeforeUtc;
    }

    public string ErrorCode { get; }

    public bool IsRetryable { get; }

    public DateTimeOffset? RateLimitNotBeforeUtc { get; }
}

/// <summary>
/// Private GitHub REST adapter. GitHub response shapes, authentication and pagination remain inside
/// the plugin; only the existing inbound/outbound seams cross this boundary.
/// </summary>
internal sealed class GitHubIssuesRestClient :
    IGitHubIssuesInboundClient,
    IGitHubIssuesOutboundClient
{
    internal const string HttpClientName = "hive-github-issues-rest";
    internal const string ApiVersion = "2026-03-10";
    internal const string UserAgent = "HIVE-GitHub-Issues-Connector/1.0";

    private const int CursorVersion = 1;
    private const string CursorPrefix = "gh1.";
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private const int OutboundCommentPageSize = 100;
    private static readonly Uri ApiBaseAddress = new("https://api.github.com/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions CursorJsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNamingPolicy = null,
    };

    private readonly HttpClient _httpClient;
    private readonly GitHubIssuesConnectorConfigurationCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rateLimits =
        new(StringComparer.Ordinal);

    public GitHubIssuesRestClient(
        HttpClient httpClient,
        GitHubIssuesConnectorConfigurationCatalog catalog,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ConfigureHttpClient(_httpClient);
    }

    internal static void ConfigureHttpClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.BaseAddress = ApiBaseAddress;
        client.Timeout = RequestTimeout;
    }

    public async Task<GitHubIssuesInboundBatch> FetchBatchAsync(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string repository,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }

        var configuredInstance = _catalog.FindInstance(instance.InstanceId);
        if (configuredInstance is null
            || !GitHubIssuesScopePolicy.AuthorizeInbound(
                configuredInstance,
                repository).IsAllowed)
        {
            throw new GitHubIssuesScopeDeniedException();
        }

        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var state = DecodeCursor(cursor);
        if (TryReadActiveRateLimit(instance.InstanceId, out var activeRateLimit))
        {
            return new GitHubIssuesInboundBatch(
                instance.InstanceId,
                repository,
                cursor,
                [],
                activeRateLimit);
        }

        var token = _catalog.GetToken(instance.InstanceId);
        var observedAtUtc = _timeProvider.GetUtcNow();
        var issues = await FetchStreamAsync(
                instance.InstanceId,
                repository,
                token,
                RestStreamKind.Issues,
                state.Issues,
                pageSize,
                observedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        StreamFetchResult comments;
        if (issues.WasRateLimited
            || TryReadActiveRateLimit(instance.InstanceId, out _))
        {
            comments = StreamFetchResult.RateLimited(state.Comments, issues.RateLimitNotBeforeUtc);
        }
        else
        {
            comments = await FetchStreamAsync(
                    instance.InstanceId,
                    repository,
                    token,
                    RestStreamKind.Comments,
                    state.Comments,
                    pageSize,
                    observedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var nextState = new RestCursor(CursorVersion, issues.Cursor, comments.Cursor);
        var nextCursor = EncodeCursor(nextState);
        var events = issues.Events.AddRange(comments.Events);
        var rateLimit = Later(issues.RateLimitNotBeforeUtc, comments.RateLimitNotBeforeUtc);
        return new GitHubIssuesInboundBatch(
            instance.InstanceId,
            repository,
            nextCursor,
            events,
            rateLimit);
    }

    public async Task<GitHubIssuesOutboundClientResult> ExecuteAsync(
        GitHubIssuesOutboundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesScope(request))
        {
            return GitHubIssuesOutboundClientResult.Failed(
                GitHubIssuesScopePolicy.ScopeDeniedCode,
                retryable: false);
        }

        if (TryReadActiveRateLimit(request.Instance.InstanceId, out _))
        {
            return GitHubIssuesOutboundClientResult.Failed(
                GitHubIssuesRestErrorCodes.RateLimited,
                retryable: true);
        }

        try
        {
            var token = _catalog.GetToken(request.Instance.InstanceId);
            return request.Operation.Name switch
            {
                GitHubIssuesOutboundOperations.Comment =>
                    await PublishCommentAsync(request, token, cancellationToken).ConfigureAwait(false),
                GitHubIssuesOutboundOperations.UpdateState =>
                    await UpdateStateAsync(request, token, cancellationToken).ConfigureAwait(false),
                GitHubIssuesOutboundOperations.UpdateLabels =>
                    await UpdateLabelsAsync(request, token, cancellationToken).ConfigureAwait(false),
                _ => GitHubIssuesOutboundClientResult.Failed(
                    GitHubIssuesRestErrorCodes.RequestInvalid,
                    retryable: false),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubIssuesRestClientException exception)
        {
            return GitHubIssuesOutboundClientResult.Failed(
                exception.ErrorCode,
                exception.IsRetryable);
        }
        catch (Exception)
        {
            return GitHubIssuesOutboundClientResult.Failed(
                GitHubIssuesRestErrorCodes.Unavailable,
                retryable: true);
        }
    }

    private async Task<StreamFetchResult> FetchStreamAsync(
        string instanceId,
        string repository,
        string token,
        RestStreamKind kind,
        RestStreamCursor initialCursor,
        int pageSize,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var boundary = initialCursor.BoundaryUtc;
        var seenAtBoundary = initialCursor.BoundaryIds.ToHashSet();
        var acceptedSourceIds = new HashSet<long>();
        var accepted = new List<SourceItem>();
        DateTimeOffset? rateLimitNotBeforeUtc = null;
        var reachedEnd = false;

        for (var page = 1; accepted.Count < pageSize; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(
                        instanceId,
                        CreateRequest(
                            HttpMethod.Get,
                            StreamPath(repository, kind, boundary, pageSize, page),
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubIssuesRestClientException exception)
                when (exception.ErrorCode == GitHubIssuesRestErrorCodes.RateLimited)
            {
                return new StreamFetchResult(
                    initialCursor,
                    accepted.Where(item => item.Event is not null).Select(item => item.Event!).ToImmutableArray(),
                    exception.RateLimitNotBeforeUtc,
                    WasRateLimited: true);
            }

            using (response)
            {
                rateLimitNotBeforeUtc = Later(
                    rateLimitNotBeforeUtc,
                    ReadSuccessfulRateLimit(instanceId, response));
                var pageItems = await ReadSourceItemsAsync(
                        response.Content,
                        repository,
                        kind,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var item in pageItems.OrderBy(value => value.UpdatedAtUtc).ThenBy(value => value.Id))
                {
                    if (boundary is { } boundaryValue
                        && (item.UpdatedAtUtc < boundaryValue
                            || (item.UpdatedAtUtc == boundaryValue
                                && seenAtBoundary.Contains(item.Id))))
                    {
                        continue;
                    }

                    if (acceptedSourceIds.Add(item.Id))
                    {
                        accepted.Add(item);
                    }

                    if (accepted.Count == pageSize)
                    {
                        break;
                    }
                }

                reachedEnd = pageItems.Length < pageSize;
            }

            if (reachedEnd || TryReadActiveRateLimit(instanceId, out _))
            {
                break;
            }
        }

        var nextCursor = AdvanceCursor(
            initialCursor,
            accepted,
            observedAtUtc,
            reachedEnd);
        return new StreamFetchResult(
            nextCursor,
            accepted.Where(item => item.Event is not null).Select(item => item.Event!).ToImmutableArray(),
            rateLimitNotBeforeUtc,
            WasRateLimited: rateLimitNotBeforeUtc is not null && TryReadActiveRateLimit(instanceId, out _));
    }

    private async Task<GitHubIssuesOutboundClientResult> PublishCommentAsync(
        GitHubIssuesOutboundRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        var marker = OperationMarker(request.OperationKey);
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                    request.Instance.InstanceId,
                    CreateRequest(
                        HttpMethod.Get,
                        IssueCommentsPath(
                            request.Issue.Repository,
                            request.Issue.IssueNumber,
                            OutboundCommentPageSize,
                            page),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            ReadSuccessfulRateLimit(request.Instance.InstanceId, response);
            var comments = await ReadCommentReceiptsAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            var existing = comments.FirstOrDefault(comment =>
                comment.Body.EndsWith(marker, StringComparison.Ordinal));
            if (existing is not null)
            {
                return GitHubIssuesOutboundClientResult.Success(CommentReceipt(existing.Id));
            }

            if (comments.Length < OutboundCommentPageSize)
            {
                break;
            }

            if (TryReadActiveRateLimit(request.Instance.InstanceId, out var notBefore))
            {
                throw RateLimited(notBefore);
            }
        }

        if (TryReadActiveRateLimit(request.Instance.InstanceId, out var activeRateLimit))
        {
            throw RateLimited(activeRateLimit);
        }

        var body = request.Operation.Body + "\n\n" + marker;
        using var createResponse = await SendAsync(
                request.Instance.InstanceId,
                CreateRequest(
                    HttpMethod.Post,
                    IssueCommentsPath(request.Issue.Repository, request.Issue.IssueNumber),
                    token,
                    JsonContent(new { body })),
                cancellationToken)
            .ConfigureAwait(false);
        ReadSuccessfulRateLimit(request.Instance.InstanceId, createResponse);
        if (createResponse.StatusCode != HttpStatusCode.Created)
        {
            throw InvalidResponse();
        }

        var commentId = await ReadPositiveIdAsync(createResponse.Content, cancellationToken)
            .ConfigureAwait(false);
        return GitHubIssuesOutboundClientResult.Success(CommentReceipt(commentId));
    }

    private async Task<GitHubIssuesOutboundClientResult> UpdateStateAsync(
        GitHubIssuesOutboundRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
                request.Instance.InstanceId,
                CreateRequest(
                    HttpMethod.Patch,
                    IssuePath(request.Issue.Repository, request.Issue.IssueNumber),
                    token,
                    JsonContent(new { state = request.Operation.State })),
                cancellationToken)
            .ConfigureAwait(false);
        ReadSuccessfulRateLimit(request.Instance.InstanceId, response);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw InvalidResponse();
        }

        return GitHubIssuesOutboundClientResult.Success(OperationReceipt(request.OperationKey));
    }

    private async Task<GitHubIssuesOutboundClientResult> UpdateLabelsAsync(
        GitHubIssuesOutboundRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
                request.Instance.InstanceId,
                CreateRequest(
                    HttpMethod.Put,
                    IssueLabelsPath(request.Issue.Repository, request.Issue.IssueNumber),
                    token,
                    JsonContent(new { labels = request.Operation.Labels })),
                cancellationToken)
            .ConfigureAwait(false);
        ReadSuccessfulRateLimit(request.Instance.InstanceId, response);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw InvalidResponse();
        }

        return GitHubIssuesOutboundClientResult.Success(OperationReceipt(request.OperationKey));
    }

    private async Task<HttpResponseMessage> SendAsync(
        string instanceId,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or OperationCanceledException)
            {
                throw new GitHubIssuesRestClientException(
                    GitHubIssuesRestErrorCodes.Unavailable,
                    isRetryable: true);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            try
            {
                var notBefore = ReadRateLimitNotBefore(response);
                var isRateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
                    || (response.StatusCode == HttpStatusCode.Forbidden
                        && (notBefore is not null
                            || await IndicatesSecondaryRateLimitAsync(
                                    response.Content,
                                    cancellationToken)
                                .ConfigureAwait(false)));
                if (isRateLimited)
                {
                    notBefore ??= SafeAdd(_timeProvider.GetUtcNow(), TimeSpan.FromMinutes(1));
                    UpdateRateLimit(instanceId, notBefore.Value);
                    throw RateLimited(notBefore.Value);
                }

                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => Error(
                        GitHubIssuesRestErrorCodes.AuthenticationFailed,
                        retryable: false),
                    HttpStatusCode.Forbidden => Error(
                        GitHubIssuesRestErrorCodes.Forbidden,
                        retryable: false),
                    HttpStatusCode.NotFound => Error(
                        GitHubIssuesRestErrorCodes.ResourceNotFound,
                        retryable: false),
                    HttpStatusCode.Gone => Error(
                        GitHubIssuesRestErrorCodes.ResourceGone,
                        retryable: false),
                    HttpStatusCode.UnprocessableEntity => Error(
                        GitHubIssuesRestErrorCodes.RequestInvalid,
                        retryable: false),
                    HttpStatusCode.RequestTimeout => Error(
                        GitHubIssuesRestErrorCodes.Unavailable,
                        retryable: true),
                    >= HttpStatusCode.InternalServerError => Error(
                        GitHubIssuesRestErrorCodes.Unavailable,
                        retryable: true),
                    _ => Error(
                        GitHubIssuesRestErrorCodes.RequestInvalid,
                        retryable: false),
                };
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private DateTimeOffset? ReadSuccessfulRateLimit(
        string instanceId,
        HttpResponseMessage response)
    {
        if (!TryReadHeaderInt64(response.Headers, "X-RateLimit-Remaining", out var remaining)
            || remaining != 0)
        {
            return null;
        }

        var notBefore = ReadRateLimitNotBefore(response);
        if (notBefore is not null)
        {
            UpdateRateLimit(instanceId, notBefore.Value);
        }

        return notBefore;
    }

    private DateTimeOffset? ReadRateLimitNotBefore(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return SafeAdd(now, delta <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delta);
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date.ToUniversalTime() > now
                ? date.ToUniversalTime()
                : SafeAdd(now, TimeSpan.FromSeconds(1));
        }

        if (TryReadHeaderInt64(response.Headers, "X-RateLimit-Remaining", out var remaining)
            && remaining == 0
            && TryReadHeaderInt64(response.Headers, "X-RateLimit-Reset", out var reset))
        {
            try
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(reset).AddSeconds(1);
                return resetAt > now ? resetAt : SafeAdd(now, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<bool> IndicatesSecondaryRateLimitAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedAsync(
                    content,
                    MaximumErrorResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = message.GetString() ?? string.Empty;
            return value.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                || value.Contains("abuse detection", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or GitHubIssuesRestClientException)
        {
            return false;
        }
    }

    private async Task<ImmutableArray<SourceItem>> ReadSourceItemsAsync(
        HttpContent content,
        string repository,
        RestStreamKind kind,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(content, MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse();
            }

            var items = ImmutableArray.CreateBuilder<SourceItem>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                items.Add(kind == RestStreamKind.Issues
                    ? ParseIssue(element)
                    : ParseComment(element, repository));
            }

            return items.ToImmutable();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static SourceItem ParseIssue(JsonElement element)
    {
        var id = ReadPositiveInt64(element, "id");
        var updatedAtUtc = ReadUtcTimestamp(element, "updated_at");
        if (element.TryGetProperty("pull_request", out _))
        {
            return new SourceItem(id, updatedAtUtc, Event: null);
        }

        var number = ReadPositiveInt64(element, "number");
        var title = ReadString(element, "title", allowNull: false)!;
        var body = ReadString(element, "body", allowNull: true);
        var payload = JsonSerializer.Serialize(new { number, title, body });
        return new SourceItem(
            id,
            updatedAtUtc,
            new GitHubIssuesInboundEvent(
                $"issue:{id.ToString(CultureInfo.InvariantCulture)}",
                GitHubIssuesInboundEventKinds.Issue,
                payload));
    }

    private static SourceItem ParseComment(JsonElement element, string repository)
    {
        var id = ReadPositiveInt64(element, "id");
        var updatedAtUtc = ReadUtcTimestamp(element, "updated_at");
        var issueUrl = ReadString(element, "issue_url", allowNull: false)!;
        var issueNumber = ParseIssueNumber(issueUrl, repository);
        var htmlUrl = ReadString(element, "html_url", allowNull: false)!;
        var isIssueComment = ReadIsIssueComment(htmlUrl, repository, issueNumber, id);
        var body = ReadString(element, "body", allowNull: true);
        GitHubIssuesInboundEvent? inboundEvent = null;
        if (isIssueComment && body is not null && !IsOperationMarkerEcho(body))
        {
            var payload = JsonSerializer.Serialize(new { issue_number = issueNumber, id, body });
            inboundEvent = new GitHubIssuesInboundEvent(
                $"comment:{id.ToString(CultureInfo.InvariantCulture)}",
                GitHubIssuesInboundEventKinds.Comment,
                payload);
        }

        return new SourceItem(id, updatedAtUtc, inboundEvent);
    }

    private async Task<ImmutableArray<RemoteComment>> ReadCommentReceiptsAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(content, MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse();
            }

            var comments = ImmutableArray.CreateBuilder<RemoteComment>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                comments.Add(new RemoteComment(
                    ReadPositiveInt64(element, "id"),
                    ReadString(element, "body", allowNull: true) ?? string.Empty));
            }

            return comments.ToImmutable();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<long> ReadPositiveIdAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(content, MaximumErrorResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return ReadPositiveInt64(document.RootElement, "id");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw InvalidResponse();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw InvalidResponse();
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Error(GitHubIssuesRestErrorCodes.Unavailable, retryable: true);
        }
    }

    private static RestStreamCursor AdvanceCursor(
        RestStreamCursor initial,
        IReadOnlyList<SourceItem> accepted,
        DateTimeOffset observedAtUtc,
        bool reachedEnd)
    {
        var maximumAccepted = accepted.Count == 0
            ? (DateTimeOffset?)null
            : accepted.Max(item => item.UpdatedAtUtc);
        var safeObservedAtUtc = initial.BoundaryUtc is { } currentBoundary
            && currentBoundary > observedAtUtc
                ? currentBoundary
                : observedAtUtc;
        if (reachedEnd
            && (maximumAccepted is null || maximumAccepted <= safeObservedAtUtc))
        {
            return new RestStreamCursor(safeObservedAtUtc, []);
        }

        if (maximumAccepted is null)
        {
            return initial;
        }

        var ids = accepted
            .Where(item => item.UpdatedAtUtc == maximumAccepted)
            .Select(item => item.Id)
            .Concat(initial.BoundaryUtc == maximumAccepted ? initial.BoundaryIds : [])
            .Distinct()
            .Order()
            .ToImmutableArray();
        return new RestStreamCursor(maximumAccepted, ids);
    }

    private static RestCursor DecodeCursor(string? value)
    {
        if (value is null)
        {
            return RestCursor.Empty;
        }

        try
        {
            if (!value.StartsWith(CursorPrefix, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }

            var encoded = value[CursorPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var cursor = JsonSerializer.Deserialize<RestCursor>(
                Convert.FromBase64String(encoded),
                CursorJsonOptions);
            if (cursor is null
                || cursor.Version != CursorVersion
                || cursor.Issues is null
                || cursor.Comments is null
                || !cursor.Issues.IsValid()
                || !cursor.Comments.IsValid())
            {
                throw InvalidCursor();
            }

            return cursor.Normalize();
        }
        catch (Exception exception)
            when (exception is FormatException or JsonException or ArgumentException)
        {
            throw InvalidCursor();
        }
    }

    private static string EncodeCursor(RestCursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor.Normalize(), CursorJsonOptions);
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var value = CursorPrefix + encoded;
        if (value.Length > 4096)
        {
            throw InvalidResponse();
        }

        return value;
    }

    private bool TryReadActiveRateLimit(string instanceId, out DateTimeOffset notBeforeUtc)
    {
        if (_rateLimits.TryGetValue(instanceId, out notBeforeUtc))
        {
            if (notBeforeUtc > _timeProvider.GetUtcNow())
            {
                return true;
            }

            _rateLimits.TryRemove(
                new KeyValuePair<string, DateTimeOffset>(instanceId, notBeforeUtc));
        }

        notBeforeUtc = default;
        return false;
    }

    private void UpdateRateLimit(string instanceId, DateTimeOffset notBeforeUtc) =>
        _rateLimits.AddOrUpdate(
            instanceId,
            notBeforeUtc,
            (_, existing) => existing >= notBeforeUtc ? existing : notBeforeUtc);

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        string token,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, relativePath) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string StreamPath(
        string repository,
        RestStreamKind kind,
        DateTimeOffset? boundary,
        int pageSize,
        int page)
    {
        var path = kind == RestStreamKind.Issues
            ? $"repos/{RepositoryPath(repository)}/issues?state=open&sort=updated&direction=asc"
            : $"repos/{RepositoryPath(repository)}/issues/comments?sort=updated&direction=asc";
        if (boundary is { } value)
        {
            var inclusiveSince = SafeSubtract(value, TimeSpan.FromSeconds(1));
            path += "&since=" + Uri.EscapeDataString(
                inclusiveSince.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        return path
            + $"&per_page={pageSize.ToString(CultureInfo.InvariantCulture)}"
            + $"&page={page.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string IssuePath(string repository, long issueNumber) =>
        $"repos/{RepositoryPath(repository)}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}";

    private static string IssueCommentsPath(string repository, long issueNumber) =>
        IssuePath(repository, issueNumber) + "/comments";

    private static string IssueCommentsPath(
        string repository,
        long issueNumber,
        int pageSize,
        int page) =>
        IssueCommentsPath(repository, issueNumber)
        + $"?sort=created&direction=desc&per_page={pageSize.ToString(CultureInfo.InvariantCulture)}"
        + $"&page={page.ToString(CultureInfo.InvariantCulture)}";

    private static string IssueLabelsPath(string repository, long issueNumber) =>
        IssuePath(repository, issueNumber) + "/labels";

    private static string RepositoryPath(string repository)
    {
        var separator = repository.IndexOf('/');
        return Uri.EscapeDataString(repository[..separator])
            + "/"
            + Uri.EscapeDataString(repository[(separator + 1)..]);
    }

    private static long ParseIssueNumber(string issueUrl, string repository)
    {
        if (!Uri.TryCreate(issueUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, ApiBaseAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var repositorySegments = repository.Split('/');
        if (segments.Length != 5
            || !string.Equals(segments[0], "repos", StringComparison.Ordinal)
            || !string.Equals(segments[1], repositorySegments[0], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], repositorySegments[1], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[3], "issues", StringComparison.Ordinal)
            || !long.TryParse(
                segments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var issueNumber)
            || issueNumber <= 0)
        {
            throw InvalidResponse();
        }

        return issueNumber;
    }

    private static bool ReadIsIssueComment(
        string htmlUrl,
        string repository,
        long issueNumber,
        long commentId)
    {
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse();
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var repositorySegments = repository.Split('/');
        var expectedFragment =
            $"#issuecomment-{commentId.ToString(CultureInfo.InvariantCulture)}";
        if (segments.Length != 4
            || !string.Equals(segments[0], repositorySegments[0], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], repositorySegments[1], StringComparison.OrdinalIgnoreCase)
            || segments[2] is not ("issues" or "pull")
            || !long.TryParse(
                segments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var urlIssueNumber)
            || urlIssueNumber != issueNumber
            || !string.Equals(uri.Fragment, expectedFragment, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }

        return segments[2] == "issues";
    }

    private static long ReadPositiveInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value)
            || value <= 0)
        {
            throw InvalidResponse();
        }

        return value;
    }

    private static string? ReadString(
        JsonElement element,
        string propertyName,
        bool allowNull)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            throw InvalidResponse();
        }

        if (allowNull && property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        return property.GetString()!;
    }

    private static DateTimeOffset ReadUtcTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName, allowNull: false);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw InvalidResponse();
        }

        return parsed.ToUniversalTime();
    }

    private static bool TryReadHeaderInt64(
        HttpResponseHeaders headers,
        string name,
        out long value)
    {
        value = default;
        return headers.TryGetValues(name, out var values)
            && long.TryParse(
                values.FirstOrDefault(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }

    private bool MatchesScope(GitHubIssuesOutboundRequest request)
    {
        var configuredInstance = _catalog.FindInstance(request.Instance.InstanceId);
        return configuredInstance is not null
            && GitHubIssuesScopePolicy.AuthorizeOutbound(
                configuredInstance,
                request.Issue,
                request.Operation.Name).IsAllowed;
    }

    private static string OperationMarker(string operationKey) =>
        $"<!-- hive-operation:v1:{operationKey} -->";

    private static bool IsOperationMarkerEcho(string body)
    {
        const string prefix = "<!-- hive-operation:v1:";
        const string suffix = " -->";
        if (body.Length < prefix.Length + 64 + suffix.Length
            || !body.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var markerStart = body.Length - prefix.Length - 64 - suffix.Length;
        return markerStart >= 0
            && body.AsSpan(markerStart, prefix.Length).SequenceEqual(prefix)
            && body.AsSpan(markerStart + prefix.Length, 64).ToString().All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string CommentReceipt(long id) =>
        $"github-comment:{id.ToString(CultureInfo.InvariantCulture)}";

    private static string OperationReceipt(string operationKey) =>
        $"github-operation:{operationKey}";

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null || first >= second ? first : second;

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan delay)
    {
        try
        {
            return value.Add(delay).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue.ToUniversalTime();
        }
    }

    private static DateTimeOffset SafeSubtract(DateTimeOffset value, TimeSpan delay)
    {
        try
        {
            return value.Subtract(delay).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue.ToUniversalTime();
        }
    }

    private static GitHubIssuesRestClientException Error(string code, bool retryable) =>
        new(code, retryable);

    private static GitHubIssuesRestClientException RateLimited(DateTimeOffset notBeforeUtc) =>
        new(GitHubIssuesRestErrorCodes.RateLimited, isRetryable: true, notBeforeUtc);

    private static GitHubIssuesRestClientException InvalidResponse() =>
        Error(GitHubIssuesRestErrorCodes.ResponseInvalid, retryable: false);

    private static GitHubIssuesRestClientException InvalidCursor() =>
        Error(GitHubIssuesRestErrorCodes.RequestInvalid, retryable: false);

    private enum RestStreamKind
    {
        Issues,
        Comments,
    }

    private sealed record SourceItem(
        long Id,
        DateTimeOffset UpdatedAtUtc,
        GitHubIssuesInboundEvent? Event);

    private sealed record RemoteComment(long Id, string Body);

    private sealed record StreamFetchResult(
        RestStreamCursor Cursor,
        ImmutableArray<GitHubIssuesInboundEvent> Events,
        DateTimeOffset? RateLimitNotBeforeUtc,
        bool WasRateLimited)
    {
        public static StreamFetchResult RateLimited(
            RestStreamCursor cursor,
            DateTimeOffset? notBeforeUtc) =>
            new(cursor, [], notBeforeUtc, WasRateLimited: true);
    }

    private sealed record RestCursor(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("i")] RestStreamCursor Issues,
        [property: JsonPropertyName("c")] RestStreamCursor Comments)
    {
        public static RestCursor Empty { get; } =
            new(CursorVersion, RestStreamCursor.Empty, RestStreamCursor.Empty);

        public RestCursor Normalize() =>
            this with
            {
                Issues = Issues.Normalize(),
                Comments = Comments.Normalize(),
            };
    }

    private sealed record RestStreamCursor(
        [property: JsonPropertyName("t")] DateTimeOffset? BoundaryUtc,
        [property: JsonPropertyName("ids")] ImmutableArray<long> BoundaryIds)
    {
        public static RestStreamCursor Empty { get; } = new(null, []);

        public bool IsValid() =>
            (BoundaryUtc is null || BoundaryUtc.Value.Offset == TimeSpan.Zero)
            && !BoundaryIds.IsDefault
            && BoundaryIds.All(id => id > 0)
            && BoundaryIds.Distinct().Count() == BoundaryIds.Length;

        public RestStreamCursor Normalize() =>
            this with { BoundaryIds = BoundaryIds.Distinct().Order().ToImmutableArray() };
    }
}
