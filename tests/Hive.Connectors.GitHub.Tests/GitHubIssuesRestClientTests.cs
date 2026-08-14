using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesRestClientTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
    private const string Token = "test-only-github-token";
    private const string OperationKey =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Inbound_uses_authenticated_versioned_feeds_and_replays_cursor_without_emitting_prs_or_echoes()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    """
                    [
                      {
                        "id": 101,
                        "number": 7,
                        "title": "Payment failed",
                        "body": "Customer-visible failure",
                        "updated_at": "2026-08-14T09:00:00Z"
                      },
                      {
                        "id": 102,
                        "number": 8,
                        "title": "A pull request",
                        "body": "Not an issue event",
                        "updated_at": "2026-08-14T09:01:00Z",
                        "pull_request": { "url": "https://api.github.com/repos/acme/payments/pulls/8" }
                      }
                    ]
                    """);
            }

            return JsonResponse(
                HttpStatusCode.OK,
                $$"""
                [
                  {
                    "id": 201,
                    "body": "Please review this detail.",
                    "issue_url": "https://api.github.com/repos/acme/payments/issues/7",
                    "html_url": "https://github.com/acme/payments/issues/7#issuecomment-201",
                    "updated_at": "2026-08-14T09:02:00Z"
                  },
                  {
                    "id": 202,
                    "body": "Published by HIVE\n\n<!-- hive-operation:v1:{{OperationKey}} -->",
                    "issue_url": "https://api.github.com/repos/acme/payments/issues/7",
                    "html_url": "https://github.com/acme/payments/issues/7#issuecomment-202",
                    "updated_at": "2026-08-14T09:03:00Z"
                  },
                  {
                    "id": 203,
                    "body": "Pull request conversation, not an issue directive.",
                    "issue_url": "https://api.github.com/repos/acme/payments/issues/8",
                    "html_url": "https://github.com/acme/payments/pull/8#issuecomment-203",
                    "updated_at": "2026-08-14T09:04:00Z"
                  }
                ]
                """);
        });
        var (client, instance) = Client(handler);

        var first = await client.FetchBatchAsync(instance, "acme/payments", null, 100);
        var replay = await client.FetchBatchAsync(
            instance,
            "acme/payments",
            first.NextCursor,
            100);

        Assert.NotNull(first.NextCursor);
        Assert.Equal(["issue:101", "comment:201"],
            first.Events.Select(value => value.ExternalEventId));
        Assert.Empty(replay.Events);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal(Token, request.AuthorizationParameter);
            Assert.Equal(GitHubIssuesRestClient.ApiVersion, request.ApiVersion);
            Assert.Equal(GitHubIssuesRestClient.UserAgent, request.UserAgent);
            Assert.Contains("application/vnd.github+json", request.Accept);
            Assert.Equal("api.github.com", request.Uri.Host);
        });
        Assert.Contains("state=open", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("sort=updated", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("sort=updated", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("since=", handler.Requests[2].Uri.Query, StringComparison.Ordinal);

        using var issuePayload = JsonDocument.Parse(first.Events[0].PayloadJson);
        Assert.Equal(
            ["body", "number", "title"],
            issuePayload.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order()
                .ToArray());
        using var commentPayload = JsonDocument.Parse(first.Events[1].PayloadJson);
        Assert.Equal(7, commentPayload.RootElement.GetProperty("issue_number").GetInt64());
        Assert.Equal(201, commentPayload.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Rate_limit_is_returned_to_inbound_and_suppresses_follow_up_http_until_window()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse(
                HttpStatusCode.TooManyRequests,
                "{\"message\":\"rate limited\"}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                At.AddMinutes(10).ToUnixTimeSeconds().ToString());
            return response;
        });
        var (client, instance) = Client(
            handler,
            [GitHubIssuesOutboundOperations.Comment]);

        var inbound = await client.FetchBatchAsync(instance, "acme/payments", null, 100);
        var outbound = await client.ExecuteAsync(OutboundRequest(
            instance,
            GitHubIssuesOutboundOperations.Comment,
            new Dictionary<string, object?> { ["body"] = "Do not call GitHub yet." }));

        Assert.Empty(inbound.Events);
        Assert.Equal(At.AddMinutes(2), inbound.RateLimitNotBeforeUtc);
        Assert.False(outbound.Succeeded);
        Assert.True(outbound.Retryable);
        Assert.Equal(GitHubIssuesRestErrorCodes.RateLimited, outbound.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Successful_response_with_exhausted_primary_limit_persists_reset_without_second_feed_call()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                At.AddMinutes(5).ToUnixTimeSeconds().ToString());
            return response;
        });
        var (client, instance) = Client(handler);

        var batch = await client.FetchBatchAsync(instance, "acme/payments", null, 100);

        Assert.Equal(At.AddMinutes(5).AddSeconds(1), batch.RateLimitNotBeforeUtc);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/issues", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inclusive_cursor_tracks_ids_at_equal_timestamp_and_reads_later_pages()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/issues/comments", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, "[]");
            }

            var page = QueryValue(request.Uri, "page");
            return page switch
            {
                "1" => JsonResponse(HttpStatusCode.OK, IssueJson(101, 7)),
                "2" => JsonResponse(HttpStatusCode.OK, IssueJson(102, 8)),
                _ => JsonResponse(HttpStatusCode.OK, "[]"),
            };
        });
        var (client, instance) = Client(handler);

        var first = await client.FetchBatchAsync(instance, "acme/payments", null, pageSize: 1);
        var second = await client.FetchBatchAsync(
            instance,
            "acme/payments",
            first.NextCursor,
            pageSize: 1);
        var completedBoundary = await client.FetchBatchAsync(
            instance,
            "acme/payments",
            second.NextCursor,
            pageSize: 1);

        Assert.Equal("issue:101", Assert.Single(first.Events).ExternalEventId);
        Assert.Equal("issue:102", Assert.Single(second.Events).ExternalEventId);
        Assert.Empty(completedBoundary.Events);
        Assert.Contains(
            handler.Requests,
            request => request.Uri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)
                && QueryValue(request.Uri, "page") == "2"
                && request.Uri.Query.Contains("since=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authentication_failure_is_structured_and_never_exposes_body_or_token()
    {
        const string diagnostic = "remote-secret-bearing-diagnostic";
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new { message = diagnostic })));
        var (client, instance) = Client(handler);

        var exception = await Assert.ThrowsAsync<GitHubIssuesRestClientException>(() =>
            client.FetchBatchAsync(instance, "acme/payments", null, 100));

        Assert.Equal(GitHubIssuesRestErrorCodes.AuthenticationFailed, exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.DoesNotContain(diagnostic, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_with_remaining_quota_is_terminal_and_invalid_cursor_never_calls_http()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse(HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4999");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                At.AddMinutes(10).ToUnixTimeSeconds().ToString());
            return response;
        });
        var (client, instance) = Client(handler);

        var invalidCursor = await Assert.ThrowsAsync<GitHubIssuesRestClientException>(() =>
            client.FetchBatchAsync(instance, "acme/payments", "not-a-cursor", 100));
        var forbidden = await Assert.ThrowsAsync<GitHubIssuesRestClientException>(() =>
            client.FetchBatchAsync(instance, "acme/payments", null, 100));

        Assert.Equal(GitHubIssuesRestErrorCodes.RequestInvalid, invalidCursor.ErrorCode);
        Assert.Equal(GitHubIssuesRestErrorCodes.Forbidden, forbidden.ErrorCode);
        Assert.False(forbidden.IsRetryable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Comment_marker_closes_crash_window_by_finding_existing_remote_receipt()
    {
        string? postedBody = null;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return postedBody is null
                    ? JsonResponse(HttpStatusCode.OK, "[]")
                    : JsonResponse(
                        HttpStatusCode.OK,
                        JsonSerializer.Serialize(new[]
                        {
                            new { id = 55L, body = postedBody },
                        }));
            }

            using var document = JsonDocument.Parse(request.Body!);
            postedBody = document.RootElement.GetProperty("body").GetString();
            return JsonResponse(HttpStatusCode.Created, "{\"id\":55}");
        });
        var (client, instance) = Client(
            handler,
            [GitHubIssuesOutboundOperations.Comment]);
        var request = OutboundRequest(
            instance,
            GitHubIssuesOutboundOperations.Comment,
            new Dictionary<string, object?> { ["body"] = "Published after approval." });

        var first = await client.ExecuteAsync(request);
        var replayAfterUnknownCommit = await client.ExecuteAsync(request);

        Assert.True(first.Succeeded);
        Assert.True(replayAfterUnknownCommit.Succeeded);
        Assert.Equal("github-comment:55", first.Receipt);
        Assert.Equal(first.Receipt, replayAfterUnknownCommit.Receipt);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Get],
            handler.Requests.Select(value => value.Method));
        Assert.StartsWith("Published after approval.\n\n", postedBody, StringComparison.Ordinal);
        Assert.EndsWith(
            $"<!-- hive-operation:v1:{OperationKey} -->",
            postedBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_and_labels_use_repeatable_minimal_rest_operations()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        var (client, instance) = Client(
            handler,
            [
                GitHubIssuesOutboundOperations.UpdateState,
                GitHubIssuesOutboundOperations.UpdateLabels,
            ]);

        var state = await client.ExecuteAsync(OutboundRequest(
            instance,
            GitHubIssuesOutboundOperations.UpdateState,
            new Dictionary<string, object?> { ["state"] = "closed" }));
        var labels = await client.ExecuteAsync(OutboundRequest(
            instance,
            GitHubIssuesOutboundOperations.UpdateLabels,
            new Dictionary<string, object?> { ["labels"] = new[] { "urgent", "bug" } }));

        Assert.True(state.Succeeded);
        Assert.True(labels.Succeeded);
        Assert.Equal([HttpMethod.Patch, HttpMethod.Put],
            handler.Requests.Select(value => value.Method));
        Assert.EndsWith("/repos/acme/payments/issues/7",
            handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/repos/acme/payments/issues/7/labels",
            handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("closed",
            JsonDocument.Parse(handler.Requests[0].Body!).RootElement
                .GetProperty("state").GetString());
        Assert.Equal(
            ["bug", "urgent"],
            JsonDocument.Parse(handler.Requests[1].Body!).RootElement
                .GetProperty("labels").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
    }

    private static (GitHubIssuesRestClient Client, GitHubIssuesConnectorInstanceConfiguration Instance)
        Client(
            HttpMessageHandler handler,
            IReadOnlyList<string>? outboundOperations = null)
    {
        var instance = new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            OrganizationId.From("acme"),
            ["acme/payments"],
            PositionId.From("bug-triage"),
            outboundOperations ?? [],
            new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(30), 100));
        var options = Options.Create(new GitHubIssuesConnectorOptions
        {
            Instances =
            [
                new GitHubIssuesConnectorInstanceOptions
                {
                    InstanceId = instance.InstanceId,
                    OrganizationId = instance.OrganizationId.Value,
                    Repositories = instance.Repositories.ToArray(),
                    InboundDirectiveTarget = instance.InboundDirectiveTarget.Value,
                    OutboundOperations = instance.OutboundOperations.ToArray(),
                    Polling = new GitHubIssuesPollingOptions
                    {
                        Interval = "PT30S",
                        PageSize = 100,
                    },
                },
            ],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = instance.InstanceId,
                    Token = Token,
                },
            ],
        });
        var catalog = new GitHubIssuesConnectorConfigurationCatalog(options);
        var client = new GitHubIssuesRestClient(
            new HttpClient(handler, disposeHandler: true),
            catalog,
            new FixedTimeProvider(At));
        return (client, instance);
    }

    private static GitHubIssuesOutboundRequest OutboundRequest(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string operationName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        Assert.True(GitHubIssuesOutboundOperation.TryParse(
            new AiToolCall("call-7", operationName, arguments),
            out var operation,
            out _));
        return new GitHubIssuesOutboundRequest(
            OperationKey,
            instance,
            new GitHubIssueCorrelation(
                instance.InstanceId,
                instance.OrganizationId,
                "acme/payments",
                7,
                ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                DirectiveId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"))),
            operation!);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string IssueJson(long id, long number) =>
        $$"""
        [
          {
            "id": {{id}},
            "number": {{number}},
            "title": "Issue {{number}}",
            "body": "Body {{number}}",
            "updated_at": "2026-08-14T09:00:00Z"
          }
        ]
        """;

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0
                && string.Equals(pair[..separator], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private sealed class RecordingHandler(
        Func<CapturedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.GetValues("X-GitHub-Api-Version").Single(),
                request.Headers.UserAgent.ToString(),
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return respond(captured);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string ApiVersion,
        string UserAgent,
        string Accept,
        string? Body);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
