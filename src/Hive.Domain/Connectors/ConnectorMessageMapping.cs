using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Messaging;

namespace Hive.Domain.Connectors;

public enum ConnectorContentTrust
{
    UntrustedExternal = 1,
}

/// <summary>
/// Transport-neutral external message. Content is always untrusted data; a connector cannot mark
/// external text as an instruction or as trusted organizational authority.
/// </summary>
public sealed record ConnectorExternalMessage
{
    public ConnectorExternalMessage(
        string externalId,
        string kind,
        string? subject,
        string? content,
        IReadOnlyDictionary<string, ActionAttributeValue>? attributes = null)
    {
        ExternalId = ConnectorContractGuards.RequireText(externalId, nameof(externalId));
        Kind = ConnectorContractGuards.RequireToken(kind, nameof(kind));
        Subject = ConnectorContractGuards.OptionalContent(subject, nameof(subject));
        Content = ConnectorContractGuards.OptionalContent(content, nameof(content));
        if (Subject is null && Content is null)
        {
            throw new ArgumentException(
                "An external message must contain a subject or content.",
                nameof(content));
        }

        Attributes = ConnectorContractGuards.SnapshotAttributes(attributes, nameof(attributes));
    }

    public string ExternalId { get; }

    public string Kind { get; }

    public string? Subject { get; }

    public string? Content { get; }

    public ConnectorContentTrust Trust => ConnectorContentTrust.UntrustedExternal;

    public IReadOnlyDictionary<string, ActionAttributeValue> Attributes { get; }
}

/// <summary>Deterministic structural mapping from external data to a canonical HIVE message.</summary>
public interface IConnectorInboundMessageMapper
{
    ConnectorInboundMappingResult Map(ConnectorExternalMessage message);
}

/// <summary>Deterministic structural mapping from a canonical HIVE message to external data.</summary>
public interface IConnectorOutboundMessageMapper
{
    ConnectorOutboundMappingResult Map(OrgMessage message);
}

public sealed record ConnectorInboundMappingResult
{
    private ConnectorInboundMappingResult(OrgMessage? message, ConnectorError? error)
    {
        Message = message;
        Error = error;
    }

    public bool IsSuccess => Message is not null;

    public bool IsFailure => Error is not null;

    public OrgMessage? Message { get; }

    public ConnectorError? Error { get; }

    public static ConnectorInboundMappingResult Succeeded(OrgMessage message) =>
        new(message ?? throw new ArgumentNullException(nameof(message)), error: null);

    public static ConnectorInboundMappingResult Failed(ConnectorError error) =>
        new(message: null, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed record ConnectorOutboundMappingResult
{
    private ConnectorOutboundMappingResult(
        ConnectorExternalMessage? message,
        ConnectorError? error)
    {
        Message = message;
        Error = error;
    }

    public bool IsSuccess => Message is not null;

    public bool IsFailure => Error is not null;

    public ConnectorExternalMessage? Message { get; }

    public ConnectorError? Error { get; }

    public static ConnectorOutboundMappingResult Succeeded(ConnectorExternalMessage message) =>
        new(message ?? throw new ArgumentNullException(nameof(message)), error: null);

    public static ConnectorOutboundMappingResult Failed(ConnectorError error) =>
        new(message: null, error ?? throw new ArgumentNullException(nameof(error)));
}
