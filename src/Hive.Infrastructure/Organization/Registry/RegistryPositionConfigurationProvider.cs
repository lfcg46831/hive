using System.Globalization;
using System.Xml;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Organization.Registry;

/// <summary>
/// Loads the PositionActor runtime configuration from the materialized organization registry/read model
/// (US-F0-06-T08b).
/// </summary>
public sealed class RegistryPositionConfigurationProvider : IPositionConfigurationProvider
{
    private readonly IOrganizationRegistryReader _registryReader;
    private readonly string _organizationsRoot;

    public RegistryPositionConfigurationProvider(IOrganizationRegistryReader registryReader)
        : this(registryReader, Path.Combine("config", "organizations"))
    {
    }

    public RegistryPositionConfigurationProvider(
        IOrganizationRegistryReader registryReader,
        string organizationsRoot)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationsRoot);
        _organizationsRoot = organizationsRoot;
    }

    public async Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityId);

        OrganizationRegistrySnapshot? snapshot;
        try
        {
            snapshot = await _registryReader
                .FindSnapshotAsync(entityId.Organization, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PositionRuntimeConfigurationLoadResult.TechnicalFailure(exception);
        }

        if (snapshot is null)
        {
            return PositionRuntimeConfigurationLoadResult.Missing(
                $"Organization '{entityId.Organization.Value}' was not found in the registry.");
        }

        return Project(snapshot, entityId);
    }

    private PositionRuntimeConfigurationLoadResult Project(
        OrganizationRegistrySnapshot snapshot,
        PositionEntityId entityId)
    {
        if (!TryCreateStamp(snapshot, out var stamp, out var invalidStampReason))
        {
            return PositionRuntimeConfigurationLoadResult.InvalidStamp(invalidStampReason);
        }

        if (snapshot.OrganizationId != entityId.Organization)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot organization '{snapshot.OrganizationId.Value}' does not match requested organization '{entityId.Organization.Value}'.");
        }

        if (snapshot.Organization?.Value is not { } organization
            || organization.Id != entityId.Organization)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for organization '{entityId.Organization.Value}' is missing coherent organization metadata.");
        }

        if (snapshot.Positions is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for organization '{entityId.Organization.Value}' is missing positions.");
        }

        if (!snapshot.Positions.TryGetValue(entityId.Position, out var positionEntry))
        {
            return PositionRuntimeConfigurationLoadResult.Missing(
                $"Position '{entityId.Position.Value}' was not found in organization '{entityId.Organization.Value}'.");
        }

        if (positionEntry?.Value is not { } position
            || position.Id != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent position metadata.");
        }

        if (snapshot.Occupants is null
            || !snapshot.Occupants.TryGetValue(entityId.Position, out var occupantEntry)
            || occupantEntry?.Value is not { } occupant
            || occupant.PositionId != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent occupant metadata.");
        }

        if (snapshot.Authorities is null
            || !snapshot.Authorities.TryGetValue(entityId.Position, out var authorityEntry)
            || authorityEntry?.Value is not { } authority
            || authority.PositionId != entityId.Position)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is missing coherent authority metadata.");
        }

        if (occupant.Subscriptions is null || occupant.Tools is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' has incomplete occupant collections.");
        }

        if (authority.CanDecide is null || authority.Overrides is null)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' has incomplete authority collections.");
        }

        if (!TryProjectSchedules(snapshot, entityId, out var schedules, out var scheduleReason))
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(scheduleReason);
        }

        if (!TryProjectAiGatewayConfiguration(occupant, entityId, out var aiGateway, out var aiReason))
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(aiReason);
        }

        if (!TryProjectIdentityPrompt(
                entityId,
                organization,
                occupant,
                out var identityPrompt,
                out var identityPromptReason))
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(identityPromptReason);
        }

        try
        {
            return PositionRuntimeConfigurationLoadResult.Loaded(
                new PositionRuntimeConfiguration(
                    stamp!,
                    entityId.Organization,
                    entityId.Position,
                    new PositionRuntimeDescriptor(
                        position.Unit,
                        position.ReportsTo,
                        position.Name,
                        position.Timezone,
                        snapshot.Relations.Value.GetDirectSubordinates(entityId.Position)),
                    new OccupantRuntimeConfiguration(
                        occupant.Type,
                        occupant.IdentityPromptRef,
                        occupant.Ai,
                        occupant.WorkingHours,
                        occupant.Subscriptions,
                        occupant.Tools,
                        aiGateway,
                        identityPrompt),
                    new PositionAuthorityRuntimeConfiguration(
                        authority.CanDecide,
                        authority.Overrides.Select(item =>
                            new PositionAuthorityOverrideRuntimeConfiguration(
                                item.Key,
                                item.Gate,
                                item.Approver))),
                    schedules));
        }
        catch (ArgumentException exception)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is not a complete runtime configuration: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return PositionRuntimeConfigurationLoadResult.Incomplete(
                $"Registry snapshot for position '{entityId.Value}' is not a complete runtime configuration: {exception.Message}");
        }
    }

    private bool TryProjectIdentityPrompt(
        PositionEntityId entityId,
        RegistryOrganization organization,
        RegistryOccupant occupant,
        out IdentityPromptRuntimeConfiguration? identityPrompt,
        out string reason)
    {
        identityPrompt = null;
        reason = string.Empty;

        if (occupant.Type != OccupantType.AiAgent)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(occupant.IdentityPromptRef))
        {
            reason = $"Registry snapshot for position '{entityId.Value}' is missing an identity prompt reference for its AI occupant.";
            return false;
        }

        if (organization.Prompts is null)
        {
            reason = $"Registry snapshot for organization '{entityId.Organization.Value}' is missing the identity prompt catalog.";
            return false;
        }

        var matches = organization.Prompts
            .Where(prompt => string.Equals(
                prompt.Id,
                occupant.IdentityPromptRef,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            reason = matches.Length == 0
                ? $"Identity prompt reference '{occupant.IdentityPromptRef}' for position '{entityId.Value}' does not exist in the organization prompt catalog."
                : $"Identity prompt reference '{occupant.IdentityPromptRef}' for position '{entityId.Value}' is ambiguous in the organization prompt catalog.";
            return false;
        }

        var prompt = matches[0];
        if (!TryReadIdentityPromptFile(
                entityId.Organization,
                prompt,
                out var content,
                out reason))
        {
            reason = $"Identity prompt '{prompt.Id}' for position '{entityId.Value}' could not be loaded: {reason}";
            return false;
        }

        try
        {
            identityPrompt = new IdentityPromptRuntimeConfiguration(
                prompt.Id,
                prompt.Path,
                content);
            return true;
        }
        catch (ArgumentException exception)
        {
            reason = $"Identity prompt '{prompt.Id}' for position '{entityId.Value}' is invalid: {exception.Message}";
            return false;
        }
    }

    private bool TryReadIdentityPromptFile(
        OrganizationId organizationId,
        PromptConfiguration prompt,
        out string content,
        out string reason)
    {
        content = string.Empty;
        reason = string.Empty;

        string organizationDirectory;
        string resolvedPath;
        try
        {
            organizationDirectory = EnsureTrailingDirectorySeparator(
                Path.GetFullPath(Path.Combine(_organizationsRoot, organizationId.Value)));
            resolvedPath = Path.GetFullPath(prompt.Path, organizationDirectory);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"path '{prompt.Path}' cannot be resolved: {exception.Message}";
            return false;
        }

        if (Path.IsPathRooted(prompt.Path))
        {
            reason = $"path '{prompt.Path}' must be relative to the organization directory.";
            return false;
        }

        if (!IsInsideDirectory(resolvedPath, organizationDirectory))
        {
            reason = $"path '{prompt.Path}' resolves outside the organization directory.";
            return false;
        }

        if (!File.Exists(resolvedPath))
        {
            reason = $"path '{prompt.Path}' does not exist.";
            return false;
        }

        try
        {
            content = File.ReadAllText(resolvedPath);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException)
        {
            reason = $"path '{prompt.Path}' cannot be read: {exception.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            reason = $"path '{prompt.Path}' contains an empty identity prompt.";
            return false;
        }

        return true;
    }

    private static bool TryCreateStamp(
        OrganizationRegistrySnapshot snapshot,
        out PositionConfigurationStamp? stamp,
        out string reason)
    {
        try
        {
            stamp = new PositionConfigurationStamp(snapshot.Version, snapshot.Fingerprint);
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            stamp = null;
            reason = $"Registry snapshot for organization '{snapshot.OrganizationId.Value}' has an invalid configuration stamp: {exception.Message}";
            return false;
        }
    }

    private static bool TryProjectSchedules(
        OrganizationRegistrySnapshot snapshot,
        PositionEntityId entityId,
        out IReadOnlyList<PositionScheduleRuntimeConfiguration> schedules,
        out string reason)
    {
        schedules = Array.Empty<PositionScheduleRuntimeConfiguration>();

        if (snapshot.Schedules is null)
        {
            reason = $"Registry snapshot for organization '{entityId.Organization.Value}' is missing schedules.";
            return false;
        }

        var projected = new List<PositionScheduleRuntimeConfiguration>();
        foreach (var (key, entry) in snapshot.Schedules
            .Where(pair => pair.Key.PositionId == entityId.Position
                || pair.Value?.Value?.PositionId == entityId.Position)
            .OrderBy(pair => pair.Key.ScheduleId, StringComparer.Ordinal))
        {
            if (entry?.Value is not { } schedule)
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has an empty schedule entry.";
                return false;
            }

            if (key.PositionId != entityId.Position
                || schedule.PositionId != entityId.Position
                || !string.Equals(key.ScheduleId, schedule.ScheduleId, StringComparison.Ordinal))
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has incoherent schedule metadata.";
                return false;
            }

            try
            {
                projected.Add(new PositionScheduleRuntimeConfiguration(
                    schedule.ScheduleId,
                    schedule.Cron,
                    schedule.Instruction));
            }
            catch (ArgumentException exception)
            {
                reason = $"Registry snapshot for position '{entityId.Value}' has an invalid schedule: {exception.Message}";
                return false;
            }
        }

        schedules = projected;
        reason = string.Empty;
        return true;
    }

    private static bool TryProjectAiGatewayConfiguration(
        RegistryOccupant occupant,
        PositionEntityId entityId,
        out AiPositionRuntimeConfiguration? configuration,
        out string reason)
    {
        configuration = null;
        reason = string.Empty;

        if (occupant.Ai is null)
        {
            return true;
        }

        try
        {
            var ai = occupant.Ai;
            configuration = new AiPositionRuntimeConfiguration(
                new AiProviderMetadata(ai.Provider, ai.Model),
                new AiModelParameters(
                    ai.Temperature is null ? null : (decimal)ai.Temperature.Value,
                    ai.MaxTokens),
                ParseTimeout(ai.Timeout),
                ParseProcessing(ai.Processing),
                ai.Fallback.Select(item => new AiProviderMetadata(item.Provider, item.Model)),
                ProjectCostLimits(ai.Budget),
                ai.MaxIterations);

            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or OverflowException)
        {
            reason = $"Registry snapshot for position '{entityId.Value}' has invalid AI gateway configuration: {exception.Message}";
            return false;
        }
    }

    private static AiProcessingMode? ParseProcessing(string? processing)
    {
        if (processing is null)
        {
            return null;
        }

        if (AiProcessingModeContract.TryParseWireValue(processing, out var mode))
        {
            return mode;
        }

        throw new ArgumentException(
            $"processing mode '{processing}' is not supported.",
            nameof(processing));
    }

    private static TimeSpan? ParseTimeout(string? timeout)
    {
        if (timeout is null)
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(timeout);
        }
        catch (FormatException)
        {
            if (TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException(
                $"timeout '{timeout}' must be an ISO-8601 duration or TimeSpan text.",
                nameof(timeout));
        }
    }

    private static AiCostLimits? ProjectCostLimits(BudgetConfiguration? budget)
    {
        if (budget is null)
        {
            return null;
        }

        return new AiCostLimits(
            budget.ReactiveMaxEurPerDay,
            budget.ProactiveMaxEurPerDay,
            budget.TotalMaxEurPerDay,
            budget.MaxCallsPerHour);
    }

    private static bool IsInsideDirectory(string candidatePath, string directoryRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(candidatePath).StartsWith(directoryRoot, comparison);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
