using System.Collections.Immutable;

namespace Hive.Domain.Connectors;

public sealed record ConnectorContractValidationError(string Code, string Path);

public sealed record ConnectorContractValidationResult
{
    private ConnectorContractValidationResult(
        ImmutableArray<ConnectorContractValidationError> errors) => Errors = errors;

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ConnectorContractValidationError> Errors { get; }

    internal static ConnectorContractValidationResult Create(
        IEnumerable<ConnectorContractValidationError> errors) =>
        new(
            errors
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal)
                .ToImmutableArray());
}

/// <summary>Fail-closed validation for an <see cref="IConnector"/> implementation at registration.</summary>
public static class ConnectorContractValidator
{
    public static ConnectorContractValidationResult Validate(IConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        var errors = new List<ConnectorContractValidationError>();
        RequireReference(connector.Id, "connector-id-missing", nameof(IConnector.Id), errors);
        RequireReference(
            connector.Version,
            "connector-version-missing",
            nameof(IConnector.Version),
            errors);
        RequireReference(
            connector.ConfigurationSchema,
            "connector-configuration-schema-missing",
            nameof(IConnector.ConfigurationSchema),
            errors);

        var capabilitiesValid = ValidateCapabilities(connector.Capabilities, errors);
        var actions = SnapshotActions(connector.OutboundActions, errors);
        if (capabilitiesValid)
        {
            ValidateOptionalCapability(
                connector.Capabilities,
                ConnectorCapability.InboundMessages,
                connector.InboundMessageMapper,
                nameof(IConnector.InboundMessageMapper),
                errors);
            ValidateOptionalCapability(
                connector.Capabilities,
                ConnectorCapability.OutboundMessages,
                connector.OutboundMessageMapper,
                nameof(IConnector.OutboundMessageMapper),
                errors);

            var declaresActions = connector.Capabilities.Contains(ConnectorCapability.OutboundActions);
            if (declaresActions != (actions.Count > 0))
            {
                errors.Add(
                    new ConnectorContractValidationError(
                        declaresActions
                            ? "connector-outbound-actions-missing"
                            : "connector-outbound-actions-undeclared",
                        nameof(IConnector.OutboundActions)));
            }
        }

        var duplicateAction = actions
            .GroupBy(action => action.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAction is not null)
        {
            errors.Add(
                new ConnectorContractValidationError(
                    "connector-outbound-action-duplicate",
                    nameof(IConnector.OutboundActions)));
        }

        return ConnectorContractValidationResult.Create(errors);
    }

    private static bool ValidateCapabilities(
        ConnectorCapability capabilities,
        ICollection<ConnectorContractValidationError> errors)
    {
        try
        {
            ConnectorCapabilityContract.RequireSupported(capabilities, nameof(capabilities));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            errors.Add(
                new ConnectorContractValidationError(
                    "connector-capabilities-invalid",
                    nameof(IConnector.Capabilities)));
            return false;
        }
    }

    private static IReadOnlyList<ConnectorOutboundAction> SnapshotActions(
        IReadOnlyList<ConnectorOutboundAction>? actions,
        ICollection<ConnectorContractValidationError> errors)
    {
        if (actions is null)
        {
            errors.Add(
                new ConnectorContractValidationError(
                    "connector-outbound-actions-missing",
                    nameof(IConnector.OutboundActions)));
            return [];
        }

        if (actions.Any(action => action is null))
        {
            errors.Add(
                new ConnectorContractValidationError(
                    "connector-outbound-action-null",
                    nameof(IConnector.OutboundActions)));
        }

        return actions.Where(action => action is not null).ToImmutableArray();
    }

    private static void ValidateOptionalCapability<T>(
        ConnectorCapability capabilities,
        ConnectorCapability capability,
        T? implementation,
        string path,
        ICollection<ConnectorContractValidationError> errors)
        where T : class
    {
        var declared = capabilities.Contains(capability);
        if (declared == (implementation is not null))
        {
            return;
        }

        errors.Add(
            new ConnectorContractValidationError(
                declared
                    ? "connector-capability-implementation-missing"
                    : "connector-capability-implementation-undeclared",
                path));
    }

    private static void RequireReference<T>(
        T? value,
        string code,
        string path,
        ICollection<ConnectorContractValidationError> errors)
        where T : class
    {
        if (value is null)
        {
            errors.Add(new ConnectorContractValidationError(code, path));
        }
    }
}
