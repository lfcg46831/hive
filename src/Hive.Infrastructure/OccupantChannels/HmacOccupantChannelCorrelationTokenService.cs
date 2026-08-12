using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class HmacOccupantChannelCorrelationTokenService
    : IOccupantChannelCorrelationTokenService
{
    private const int CurrentVersion = 1;
    private const int HmacSizeBytes = 32;
    private const int MaximumTokenLength = 4096;
    private const string EnvelopePrefix = "hive-oc1";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly byte[] _signingKey;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly IOccupantChannelDecisionTokenUseStore _useStore;

    public HmacOccupantChannelCorrelationTokenService(
        IOptions<OccupantChannelCorrelationTokenOptions> options,
        TimeProvider timeProvider,
        IOccupantChannelDecisionTokenUseStore useStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _useStore = useStore ?? throw new ArgumentNullException(nameof(useStore));

        var configured = options.Value;
        if (configured.Lifetime < TimeSpan.FromSeconds(1) ||
            configured.Lifetime > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException(
                "Occupant-channel correlation token lifetime was not validated.");
        }

        if (configured.SigningKey is null ||
            !OccupantChannelCorrelationTokenOptionsValidator.TryDecodeKey(
                configured.SigningKey,
                out var keyLength) ||
            keyLength < HmacSizeBytes)
        {
            throw new InvalidOperationException(
                "Occupant-channel correlation token signing key was not validated.");
        }

        _signingKey = Convert.FromBase64String(configured.SigningKey);
        _lifetime = configured.Lifetime;
    }

    public OccupantChannelCorrelationToken Issue(
        OccupantChannelCorrelationTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issuedAtUtc = CurrentSecond();
        var expiresAtUtc = issuedAtUtc.Add(_lifetime);
        var payload = new TokenPayload
        {
            Version = CurrentVersion,
            TokenId = Guid.NewGuid().ToString("N"),
            OrganizationId = request.OrganizationId.Value,
            PositionId = request.PositionId.Value,
            MessageId = request.MessageId.Value.ToString("N"),
            ThreadId = request.ThreadId.Value.ToString("N"),
            RequestId = request.RequestId?.Value.ToString("N"),
            IssuedAtUnixSeconds = issuedAtUtc.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds = expiresAtUtc.ToUnixTimeSeconds(),
        };
        var payloadSegment = Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonOptions));
        var signingInput = $"{EnvelopePrefix}.{payloadSegment}";
        var signature = HMACSHA256.HashData(
            _signingKey,
            Encoding.ASCII.GetBytes(signingInput));
        return OccupantChannelCorrelationToken.From(
            $"{signingInput}.{Base64UrlEncode(signature)}");
    }

    public OccupantChannelCorrelationTokenValidation Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > MaximumTokenLength ||
            !string.Equals(token, token.Trim(), StringComparison.Ordinal))
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        var segments = token.Split('.');
        if (segments.Length != 3 ||
            segments.Any(static segment => segment.Length == 0))
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        if (!string.Equals(segments[0], EnvelopePrefix, StringComparison.Ordinal))
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.UnsupportedVersion);
        }

        var suppliedSignature = Base64UrlDecode(segments[2]);
        if (suppliedSignature is null || suppliedSignature.Length != HmacSizeBytes)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        var signingInput = $"{segments[0]}.{segments[1]}";
        var expectedSignature = HMACSHA256.HashData(
            _signingKey,
            Encoding.ASCII.GetBytes(signingInput));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.InvalidSignature);
        }

        var payloadBytes = Base64UrlDecode(segments[1]);
        if (payloadBytes is null)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes, PayloadJsonOptions);
        }
        catch (JsonException)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        if (payload is null)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        if (payload.Version != CurrentVersion)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.UnsupportedVersion);
        }

        if (!TryClaims(payload, out var claims))
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Malformed);
        }

        var nowUtc = CurrentSecond();
        if (claims!.IssuedAtUtc > nowUtc)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.NotYetValid);
        }

        if (claims.ExpiresAtUtc <= nowUtc)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Expired);
        }

        return OccupantChannelCorrelationTokenValidation.Valid(claims);
    }

    public async ValueTask<OccupantChannelCorrelationTokenValidation> RedeemDecisionAsync(
        string? token,
        CancellationToken cancellationToken = default) =>
        await RedeemDecisionAsync(token, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<OccupantChannelCorrelationTokenValidation> RedeemDecisionAsync(
        string? token,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Decision-token redemption operation id cannot be empty.",
                nameof(operationId));
        }

        var validation = Validate(token);
        if (!validation.IsValid)
        {
            return validation;
        }

        var claims = validation.Claims!;
        if (!claims.IsDecision)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.NotDecision);
        }

        var consumedAtUtc = CurrentSecond();
        if (claims.ExpiresAtUtc <= consumedAtUtc)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.Expired);
        }

        try
        {
            var use = await _useStore.TryConsumeAsync(
                claims.TokenId,
                operationId,
                claims.ExpiresAtUtc,
                consumedAtUtc,
                cancellationToken).ConfigureAwait(false);
            return use is OccupantChannelDecisionTokenUseResult.Consumed
                or OccupantChannelDecisionTokenUseResult.AlreadyConsumedByOperation
                    ? validation
                    : Invalid(OccupantChannelCorrelationTokenFailure.AlreadyUsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Invalid(OccupantChannelCorrelationTokenFailure.UseStoreUnavailable);
        }
    }

    private static bool TryClaims(
        TokenPayload payload,
        out OccupantChannelCorrelationTokenClaims? claims)
    {
        claims = null;
        try
        {
            if (!Guid.TryParseExact(payload.TokenId, "N", out var tokenId) ||
                tokenId == Guid.Empty ||
                !Guid.TryParseExact(payload.MessageId, "N", out var messageId) ||
                !Guid.TryParseExact(payload.ThreadId, "N", out var threadId) ||
                payload.OrganizationId is null ||
                payload.PositionId is null)
            {
                return false;
            }

            MessageId? requestId = null;
            if (payload.RequestId is not null)
            {
                if (!Guid.TryParseExact(payload.RequestId, "N", out var parsedRequestId))
                {
                    return false;
                }

                requestId = MessageId.From(parsedRequestId);
            }

            claims = new OccupantChannelCorrelationTokenClaims(
                tokenId,
                OrganizationId.From(payload.OrganizationId),
                PositionId.From(payload.PositionId),
                MessageId.From(messageId),
                ThreadId.From(threadId),
                requestId,
                DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnixSeconds),
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private DateTimeOffset CurrentSecond() =>
        DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());

    private static OccupantChannelCorrelationTokenValidation Invalid(
        OccupantChannelCorrelationTokenFailure failure) =>
        OccupantChannelCorrelationTokenValidation.Invalid(failure);

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[]? Base64UrlDecode(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Contains('=', StringComparison.Ordinal) ||
            value.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return null;
        }

        var remainder = value.Length % 4;
        if (remainder == 1)
        {
            return null;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += remainder switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };

        try
        {
            var decoded = Convert.FromBase64String(padded);
            return string.Equals(Base64UrlEncode(decoded), value, StringComparison.Ordinal)
                ? decoded
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class TokenPayload
    {
        [JsonPropertyName("v"), JsonPropertyOrder(0)]
        public int Version { get; set; }

        [JsonPropertyName("j"), JsonPropertyOrder(1)]
        public string? TokenId { get; set; }

        [JsonPropertyName("o"), JsonPropertyOrder(2)]
        public string? OrganizationId { get; set; }

        [JsonPropertyName("p"), JsonPropertyOrder(3)]
        public string? PositionId { get; set; }

        [JsonPropertyName("m"), JsonPropertyOrder(4)]
        public string? MessageId { get; set; }

        [JsonPropertyName("t"), JsonPropertyOrder(5)]
        public string? ThreadId { get; set; }

        [JsonPropertyName("r"), JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RequestId { get; set; }

        [JsonPropertyName("iat"), JsonPropertyOrder(7)]
        public long IssuedAtUnixSeconds { get; set; }

        [JsonPropertyName("exp"), JsonPropertyOrder(8)]
        public long ExpiresAtUnixSeconds { get; set; }
    }
}
