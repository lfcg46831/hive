using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// Runtime occupant configuration projected from the registry/read model for one position
/// (US-F0-06-T08a).
/// </summary>
public sealed record OccupantRuntimeConfiguration
{
    public OccupantRuntimeConfiguration(
        OccupantType type,
        string? identityPromptRef = null,
        AiConfiguration? ai = null,
        WorkingHoursConfiguration? workingHours = null,
        IEnumerable<SubscriptionConfiguration>? subscriptions = null,
        IEnumerable<ToolConfiguration>? tools = null,
        AiPositionRuntimeConfiguration? aiGateway = null,
        IdentityPromptRuntimeConfiguration? identityPrompt = null,
        OccupantId? configuredIdentity = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Occupant type must be AiAgent or Human.");
        }

        Type = type;
        IdentityPromptRef = identityPromptRef is null
            ? null
            : CommandText.RequireContent(identityPromptRef, nameof(identityPromptRef));
        if (identityPrompt is not null
            && !string.Equals(identityPrompt.Id, IdentityPromptRef, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Resolved identity prompt must match the occupant identity prompt reference.",
                nameof(identityPrompt));
        }

        Ai = ai;
        AiGateway = aiGateway;
        IdentityPrompt = identityPrompt;
        ConfiguredIdentity = configuredIdentity;
        WorkingHours = workingHours;
        Subscriptions = ToValidatedArray(subscriptions, nameof(subscriptions));
        Tools = ToValidatedArray(tools, nameof(tools));
    }

    /// <summary>Whether the configured occupant is an AI agent or human.</summary>
    public OccupantType Type { get; }

    /// <summary>The identity prompt reference into the prompt catalog, when declared.</summary>
    public string? IdentityPromptRef { get; }

    /// <summary>The resolved identity prompt content, when materialized for an AI occupant.</summary>
    public IdentityPromptRuntimeConfiguration? IdentityPrompt { get; }

    /// <summary>The AI runtime configuration, when declared for this occupant.</summary>
    public AiConfiguration? Ai { get; }

    /// <summary>The gateway-facing AI runtime projection, when declared for this occupant.</summary>
    public AiPositionRuntimeConfiguration? AiGateway { get; }

    /// <summary>
    /// Stable occupant identity supplied by the configuration provider for initial
    /// materialization. A missing value means the position must remain unoccupied.
    /// </summary>
    public OccupantId? ConfiguredIdentity { get; }

    /// <summary>The configured working-hours window, when declared.</summary>
    public WorkingHoursConfiguration? WorkingHours { get; }

    /// <summary>The event subscriptions relevant to this position.</summary>
    public ImmutableArray<SubscriptionConfiguration> Subscriptions { get; }

    /// <summary>The tools authorized for this occupant.</summary>
    public ImmutableArray<ToolConfiguration> Tools { get; }

    private static ImmutableArray<T> ToValidatedArray<T>(IEnumerable<T>? source, string parameterName)
        where T : class
    {
        if (source is null)
        {
            return ImmutableArray<T>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<T>();
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Collection cannot contain null items.", parameterName);
            }

            builder.Add(item);
        }

        return builder.ToImmutable();
    }
}
