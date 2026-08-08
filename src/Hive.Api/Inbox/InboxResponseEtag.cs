using System.Security.Cryptography;
using System.Text.Json;
using Hive.Contracts.Inbox;
using Microsoft.Net.Http.Headers;

namespace Hive.Api.Inbox;

internal static class InboxResponseEtag
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IResult OkOrNotModified(HttpContext context, InboxPage page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page);

        var tag = CreateTag(new
        {
            page.LastEventAppliedAtUtc,
            page.PageSize,
            page.NextCursor,
            page.Items,
        });
        return Result(context, tag, page);
    }

    public static IResult OkOrNotModified(HttpContext context, InboxItemResponse response)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        var tag = CreateTag(new
        {
            response.LastEventAppliedAtUtc,
            response.Item,
            response.DraftText,
        });
        return Result(context, tag, response);
    }

    private static IResult Result<T>(HttpContext context, string tag, T response)
    {
        context.Response.Headers.ETag = tag;
        context.Response.Headers.CacheControl = "private, no-cache";
        return Matches(context, tag)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(response);
    }

    private static bool Matches(HttpContext context, string tag)
    {
        IList<EntityTagHeaderValue>? requested;
        try
        {
            requested = context.Request.GetTypedHeaders().IfNoneMatch;
        }
        catch (FormatException)
        {
            return false;
        }

        if (requested is null || requested.Count == 0)
        {
            return false;
        }

        var opaqueTag = tag[2..];
        return requested.Any(candidate =>
            string.Equals(candidate.Tag.Value, "*", StringComparison.Ordinal) ||
            string.Equals(candidate.Tag.Value, opaqueTag, StringComparison.Ordinal));
    }

    private static string CreateTag<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"W/\"sha256-{hash}\"";
    }
}
