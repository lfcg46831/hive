using System.Collections.Immutable;

namespace Hive.Domain.Ai;

public sealed record AiGatewayPolicy
{
    public AiGatewayPolicy(
        IEnumerable<AiProviderMetadata> authorizedModels,
        bool hasAvailableBudget = true,
        int? maxOutputTokens = null,
        TimeSpan? maxTimeout = null,
        IEnumerable<AiProcessingMode>? allowedProcessingModes = null,
        IEnumerable<string>? authorizedTools = null)
    {
        var modelSnapshot = AiContractGuards.Snapshot(
            authorizedModels,
            nameof(authorizedModels));
        if (modelSnapshot.IsEmpty)
        {
            throw new ArgumentException(
                "At least one authorized provider/model pair is required.",
                nameof(authorizedModels));
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                maxOutputTokens,
                "Max output tokens policy must be greater than zero.");
        }

        if (maxTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTimeout),
                maxTimeout,
                "Max timeout policy must be greater than zero.");
        }

        AuthorizedModels = modelSnapshot;
        HasAvailableBudget = hasAvailableBudget;
        MaxOutputTokens = maxOutputTokens;
        MaxTimeout = maxTimeout;
        AllowedProcessingModes = SnapshotProcessingModes(
            allowedProcessingModes,
            nameof(allowedProcessingModes));
        AuthorizedTools = SnapshotToolNames(authorizedTools, nameof(authorizedTools));
    }

    public ImmutableArray<AiProviderMetadata> AuthorizedModels { get; }

    public bool HasAvailableBudget { get; }

    public int? MaxOutputTokens { get; }

    public TimeSpan? MaxTimeout { get; }

    public ImmutableArray<AiProcessingMode> AllowedProcessingModes { get; }

    public ImmutableArray<string> AuthorizedTools { get; }

    private static ImmutableArray<AiProcessingMode> SnapshotProcessingModes(
        IEnumerable<AiProcessingMode>? values,
        string parameterName)
    {
        if (values is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AiProcessingMode>();
        foreach (var value in values)
        {
            builder.Add(AiProcessingModeContract.RequireDefined(value, parameterName));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> SnapshotToolNames(
        IEnumerable<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var value in values)
        {
            builder.Add(AiContractGuards.RequireText(value, parameterName));
        }

        return builder.ToImmutable();
    }
}
