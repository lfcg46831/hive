using System.Globalization;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hive.Infrastructure.Organization.Configuration;

/// <summary>
/// Parses an organization YAML document (§4.8 + the §6.2 occupant interior) into the typed
/// <see cref="OrganizationConfiguration"/> model of US-F0-05-T03, producing readable
/// <see cref="OrganizationConfigurationParseError"/>s — with the file path and, where the node can
/// be located, the line/column of the offending field — instead of throwing on malformed input
/// (US-F0-05-T04).
/// </summary>
/// <remarks>
/// <para>
/// The parser walks the YAML representation graph (rather than deserializing onto POCOs) so it can
/// attach a precise field path and source position to each problem. It surfaces every parse-level
/// problem it can in a single pass and returns them aggregated and deterministically ordered.
/// </para>
/// <para>
/// Its responsibility ends at translating a well-formed document into the typed shape: it requires
/// only what the model cannot exist without — the <c>organization</c> header with its <c>id</c>,
/// <c>root_unit</c> and <c>owner</c> — and never applies silent defaults to the data. Semantic rules
/// (uniqueness, cross-references and structure) are deliberately left to US-F0-05-T05–T07 over the
/// resulting model, so a well-typed but semantically broken document still parses successfully.
/// </para>
/// </remarks>
public sealed class OrganizationConfigurationParser
{
    /// <summary>The field path used for problems anchored at the document root.</summary>
    private const string RootPath = "$";

    private static readonly string[] NullScalars = ["null", "Null", "NULL", "~"];

    /// <summary>Reads and parses the document at <paramref name="filePath"/>.</summary>
    /// <remarks>An I/O failure reading the file is a technical failure and is allowed to propagate.</remarks>
    public OrganizationConfigurationParseResult ParseFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var yaml = File.ReadAllText(filePath);
        return Parse(yaml, filePath);
    }

    /// <summary>Parses <paramref name="yaml"/>, attributing problems to <paramref name="filePath"/>.</summary>
    public OrganizationConfigurationParseResult Parse(string yaml, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(filePath);

        var context = new ParseContext(filePath);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            context.Add(
                RootPath,
                $"invalid YAML: {CleanMessage(exception.Message)}",
                (int)exception.Start.Line,
                (int)exception.Start.Column);
            return OrganizationConfigurationParseResult.Failure(context.Errors);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is null)
        {
            context.Add(RootPath, "the document is empty; an 'organization' block is required.", 1, 1);
            return OrganizationConfigurationParseResult.Failure(context.Errors);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            context.AddAt(
                stream.Documents[0].RootNode,
                RootPath,
                "the document root must be a mapping with 'organization', 'units' and 'positions' blocks.");
            return OrganizationConfigurationParseResult.Failure(context.Errors);
        }

        var configuration = ReadConfiguration(root, context);

        if (configuration is null || context.Errors.Count > 0)
        {
            return OrganizationConfigurationParseResult.Failure(context.Errors);
        }

        return OrganizationConfigurationParseResult.Success(configuration);
    }

    private static OrganizationConfiguration? ReadConfiguration(YamlMappingNode root, ParseContext context)
    {
        var header = ReadHeader(root, context);
        var prompts = ReadPrompts(root, context);
        var units = ReadUnits(root, context);
        var positions = ReadPositions(root, context);

        if (header is null)
        {
            return null;
        }

        return new OrganizationConfiguration(header, units, positions, prompts);
    }

    private static OrganizationHeader? ReadHeader(YamlMappingNode root, ParseContext context)
    {
        var node = Child(root, "organization");
        if (node is null)
        {
            context.AddAt(root, "organization", "required block 'organization' is missing.");
            return null;
        }

        if (node is not YamlMappingNode organization)
        {
            context.AddAt(node, "organization", "block 'organization' must be a mapping.");
            return null;
        }

        var idNode = Child(organization, "id");
        var rootUnitNode = Child(organization, "root_unit");
        var idValue = RequireScalar(organization, "id", "organization", context);
        var rootUnitValue = RequireScalar(organization, "root_unit", "organization", context);
        var name = OptionalScalar(organization, "name", "organization", context);
        var owner = ReadOwner(organization, "organization", context);

        var id = idValue is null ? null : Identity(OrganizationId.From, idValue, idNode!, "organization.id", context);
        var rootUnit = rootUnitValue is null
            ? null
            : Identity(UnitId.From, rootUnitValue, rootUnitNode!, "organization.root_unit", context);

        if (id is null || rootUnit is null || owner is null)
        {
            return null;
        }

        return new OrganizationHeader(id, rootUnit, owner, name);
    }

    private static OwnerConfiguration? ReadOwner(YamlMappingNode organization, string path, ParseContext context)
    {
        var ownerPath = $"{path}.owner";
        var node = Child(organization, "owner");
        if (node is null)
        {
            context.AddAt(organization, ownerPath, "required field 'owner' is missing.");
            return null;
        }

        if (node is not YamlMappingNode owner)
        {
            context.AddAt(node, ownerPath, "field 'owner' must be a mapping with 'type' and 'ref'.");
            return null;
        }

        var typeNode = Child(owner, "type");
        var typeValue = RequireScalar(owner, "type", ownerPath, context);
        var reference = RequireScalar(owner, "ref", ownerPath, context);

        OwnerType? type = typeValue is null ? null : ReadOwnerType(typeValue, typeNode!, $"{ownerPath}.type", context);

        if (type is null || reference is null)
        {
            return null;
        }

        return new OwnerConfiguration(type.Value, reference);
    }

    private static IReadOnlyList<PromptConfiguration> ReadPrompts(YamlMappingNode root, ParseContext context)
    {
        var sequence = OptionalSequence(root, "prompts", "prompts", context);
        if (sequence is null)
        {
            return Array.Empty<PromptConfiguration>();
        }

        var prompts = new List<PromptConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var path = $"prompts[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], path, "each prompt must be a mapping with 'id' and 'path'.");
                continue;
            }

            var id = RequireScalar(entry, "id", path, context);
            var promptPath = RequireScalar(entry, "path", path, context);

            if (id is null || promptPath is null)
            {
                continue;
            }

            prompts.Add(new PromptConfiguration(id, promptPath));
        }

        return prompts;
    }

    private static IReadOnlyList<UnitConfiguration> ReadUnits(YamlMappingNode root, ParseContext context)
    {
        var sequence = OptionalSequence(root, "units", "units", context);
        if (sequence is null)
        {
            return Array.Empty<UnitConfiguration>();
        }

        var units = new List<UnitConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var path = $"units[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], path, "each unit must be a mapping.");
                continue;
            }

            var idNode = Child(entry, "id");
            var leadershipNode = Child(entry, "leadership");
            var idValue = RequireScalar(entry, "id", path, context);
            var leadershipValue = RequireScalar(entry, "leadership", path, context);
            var name = OptionalScalar(entry, "name", path, context);
            var (parentOk, parent) = ReadRequiredNullableId(entry, "parent", UnitId.From, path, context);

            var id = idValue is null ? null : Identity(UnitId.From, idValue, idNode!, $"{path}.id", context);
            var leadership = leadershipValue is null
                ? null
                : Identity(PositionId.From, leadershipValue, leadershipNode!, $"{path}.leadership", context);

            if (id is null || leadership is null || !parentOk)
            {
                continue;
            }

            units.Add(new UnitConfiguration(id, leadership, parent, name));
        }

        return units;
    }

    private static IReadOnlyList<PositionConfiguration> ReadPositions(YamlMappingNode root, ParseContext context)
    {
        var sequence = OptionalSequence(root, "positions", "positions", context);
        if (sequence is null)
        {
            return Array.Empty<PositionConfiguration>();
        }

        var positions = new List<PositionConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var path = $"positions[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], path, "each position must be a mapping.");
                continue;
            }

            var idNode = Child(entry, "id");
            var unitNode = Child(entry, "unit");
            var idValue = RequireScalar(entry, "id", path, context);
            var unitValue = RequireScalar(entry, "unit", path, context);
            var name = OptionalScalar(entry, "name", path, context);
            var timezone = OptionalScalar(entry, "timezone", path, context);
            var (reportsToOk, reportsTo) = ReadRequiredNullableId(entry, "reports_to", PositionId.From, path, context);
            var occupant = ReadOccupant(entry, path, context);

            var id = idValue is null ? null : Identity(PositionId.From, idValue, idNode!, $"{path}.id", context);
            var unit = unitValue is null ? null : Identity(UnitId.From, unitValue, unitNode!, $"{path}.unit", context);

            if (id is null || unit is null || !reportsToOk || occupant is null)
            {
                continue;
            }

            positions.Add(new PositionConfiguration(id, unit, occupant, reportsTo, name, timezone));
        }

        return positions;
    }

    private static OccupantConfiguration? ReadOccupant(YamlMappingNode position, string path, ParseContext context)
    {
        var occupantPath = $"{path}.occupant";
        var node = Child(position, "occupant");
        if (node is null)
        {
            context.AddAt(position, occupantPath, "required field 'occupant' is missing.");
            return null;
        }

        if (node is not YamlMappingNode occupant)
        {
            context.AddAt(node, occupantPath, "field 'occupant' must be a mapping.");
            return null;
        }

        var typeNode = Child(occupant, "type");
        var typeValue = RequireScalar(occupant, "type", occupantPath, context);
        OccupantType? type = typeValue is null
            ? null
            : ReadOccupantType(typeValue, typeNode!, $"{occupantPath}.type", context);

        var identityPromptRef = OptionalScalar(occupant, "identity_prompt_ref", occupantPath, context);
        var ai = ReadAi(occupant, occupantPath, context);
        var workingHours = ReadWorkingHours(occupant, occupantPath, context);
        var authority = ReadAuthority(occupant, occupantPath, context);
        var schedule = ReadSchedule(occupant, occupantPath, context);
        var subscriptions = ReadSubscriptions(occupant, occupantPath, context);
        var tools = ReadTools(occupant, occupantPath, context);

        if (type is null)
        {
            return null;
        }

        return new OccupantConfiguration(
            type.Value,
            identityPromptRef,
            ai,
            workingHours,
            authority,
            schedule,
            subscriptions,
            tools);
    }

    private static AiConfiguration? ReadAi(YamlMappingNode occupant, string path, ParseContext context)
    {
        var aiPath = $"{path}.ai";
        var node = Child(occupant, "ai");
        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode ai)
        {
            context.AddAt(node, aiPath, "field 'ai' must be a mapping.");
            return null;
        }

        var provider = RequireScalar(ai, "provider", aiPath, context);
        var model = RequireScalar(ai, "model", aiPath, context);
        var temperature = OptionalDouble(ai, "temperature", aiPath, context);
        var maxTokens = OptionalInt(ai, "max_tokens", aiPath, context);
        var processing = OptionalScalar(ai, "processing", aiPath, context);
        var batchWindow = OptionalScalar(ai, "batch_window", aiPath, context);
        var timeout = OptionalScalar(ai, "timeout", aiPath, context);
        var fallback = ReadFallback(ai, aiPath, context);
        var budget = ReadBudget(ai, aiPath, context);

        if (provider is null || model is null)
        {
            return null;
        }

        return new AiConfiguration(
            provider,
            model,
            temperature,
            maxTokens,
            processing,
            batchWindow,
            fallback,
            budget,
            timeout);
    }

    private static IReadOnlyList<AiFallbackConfiguration> ReadFallback(YamlMappingNode ai, string path, ParseContext context)
    {
        var sequence = OptionalSequence(ai, "fallback", $"{path}.fallback", context);
        if (sequence is null)
        {
            return Array.Empty<AiFallbackConfiguration>();
        }

        var fallback = new List<AiFallbackConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var entryPath = $"{path}.fallback[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], entryPath, "each fallback must be a mapping with 'provider' and 'model'.");
                continue;
            }

            var provider = RequireScalar(entry, "provider", entryPath, context);
            var model = RequireScalar(entry, "model", entryPath, context);

            if (provider is null || model is null)
            {
                continue;
            }

            fallback.Add(new AiFallbackConfiguration(provider, model));
        }

        return fallback;
    }

    private static BudgetConfiguration? ReadBudget(YamlMappingNode ai, string path, ParseContext context)
    {
        var budgetPath = $"{path}.budget";
        var node = Child(ai, "budget");
        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode budget)
        {
            context.AddAt(node, budgetPath, "field 'budget' must be a mapping.");
            return null;
        }

        var reactive = OptionalDecimal(budget, "reactive_max_eur_per_day", budgetPath, context);
        var proactive = OptionalDecimal(budget, "proactive_max_eur_per_day", budgetPath, context);
        var total = OptionalDecimal(budget, "total_max_eur_per_day", budgetPath, context);
        var maxCalls = OptionalInt(budget, "max_calls_per_hour", budgetPath, context);

        return new BudgetConfiguration(reactive, proactive, total, maxCalls);
    }

    private static WorkingHoursConfiguration? ReadWorkingHours(YamlMappingNode occupant, string path, ParseContext context)
    {
        var workingHoursPath = $"{path}.working_hours";
        var node = Child(occupant, "working_hours");
        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode workingHours)
        {
            context.AddAt(node, workingHoursPath, "field 'working_hours' must be a mapping with 'start' and 'end'.");
            return null;
        }

        var start = RequireScalar(workingHours, "start", workingHoursPath, context);
        var end = RequireScalar(workingHours, "end", workingHoursPath, context);

        if (start is null || end is null)
        {
            return null;
        }

        return new WorkingHoursConfiguration(start, end);
    }

    private static AuthorityConfiguration? ReadAuthority(YamlMappingNode occupant, string path, ParseContext context)
    {
        var authorityPath = $"{path}.authority";
        var node = Child(occupant, "authority");
        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode authority)
        {
            context.AddAt(node, authorityPath, "field 'authority' must be a mapping.");
            return null;
        }

        RejectDeprecatedAuthorityList(authority, "must_escalate", authorityPath, context);
        RejectDeprecatedAuthorityList(authority, "requires_human_approval", authorityPath, context);

        var canDecide = OptionalStringList(authority, "can_decide", authorityPath, context);
        var overrides = ReadAuthorityOverrides(authority, authorityPath, context);

        try
        {
            return new AuthorityConfiguration(canDecide, overrides);
        }
        catch (ArgumentException exception)
        {
            context.AddAt(authority, authorityPath, $"invalid authority configuration: {exception.Message}");
            return null;
        }
    }

    private static void RejectDeprecatedAuthorityList(
        YamlMappingNode authority,
        string key,
        string path,
        ParseContext context)
    {
        var node = Child(authority, key);
        if (node is not null)
        {
            context.AddAt(
                node,
                $"{path}.{key}",
                $"field '{key}' is no longer supported; use catalog predicates and authority.overrides instead.");
        }
    }

    private static IReadOnlyList<AuthorityOverrideConfiguration> ReadAuthorityOverrides(
        YamlMappingNode authority,
        string path,
        ParseContext context)
    {
        var sequence = OptionalSequence(authority, "overrides", $"{path}.overrides", context);
        if (sequence is null)
        {
            return Array.Empty<AuthorityOverrideConfiguration>();
        }

        var overrides = new List<AuthorityOverrideConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var overridePath = $"{path}.overrides[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], overridePath, "each authority override must be a mapping with 'key' and 'gate'.");
                continue;
            }

            var keyNode = Child(entry, "key");
            var gateNode = Child(entry, "gate");
            var key = RequireScalar(entry, "key", overridePath, context);
            var gateValue = RequireScalar(entry, "gate", overridePath, context);
            var approver = OptionalScalar(entry, "approver", overridePath, context);
            var gate = gateValue is null
                ? null
                : ReadActionDomainGate(gateValue, gateNode!, $"{overridePath}.gate", context);

            if (key is null || gate is null)
            {
                continue;
            }

            try
            {
                overrides.Add(new AuthorityOverrideConfiguration(key, gate.Value, approver));
            }
            catch (ArgumentException exception)
            {
                context.AddAt(keyNode ?? entry, overridePath, $"invalid authority override: {exception.Message}");
            }
        }

        return overrides;
    }

    private static IReadOnlyList<ScheduleEntryConfiguration> ReadSchedule(YamlMappingNode occupant, string path, ParseContext context)
    {
        var sequence = OptionalSequence(occupant, "schedule", $"{path}.schedule", context);
        if (sequence is null)
        {
            return Array.Empty<ScheduleEntryConfiguration>();
        }

        var schedule = new List<ScheduleEntryConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var entryPath = $"{path}.schedule[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], entryPath, "each schedule entry must be a mapping with 'id', 'cron' and 'instruction'.");
                continue;
            }

            var id = RequireScalar(entry, "id", entryPath, context);
            var cron = RequireScalar(entry, "cron", entryPath, context);
            var instruction = RequireScalar(entry, "instruction", entryPath, context);

            if (id is null || cron is null || instruction is null)
            {
                continue;
            }

            schedule.Add(new ScheduleEntryConfiguration(id, cron, instruction));
        }

        return schedule;
    }

    private static IReadOnlyList<SubscriptionConfiguration> ReadSubscriptions(YamlMappingNode occupant, string path, ParseContext context)
    {
        var sequence = OptionalSequence(occupant, "subscriptions", $"{path}.subscriptions", context);
        if (sequence is null)
        {
            return Array.Empty<SubscriptionConfiguration>();
        }

        var subscriptions = new List<SubscriptionConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var entryPath = $"{path}.subscriptions[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], entryPath, "each subscription must be a mapping with 'event' and 'within'.");
                continue;
            }

            var @event = RequireScalar(entry, "event", entryPath, context);
            var within = RequireScalar(entry, "within", entryPath, context);

            if (@event is null || within is null)
            {
                continue;
            }

            subscriptions.Add(new SubscriptionConfiguration(@event, within));
        }

        return subscriptions;
    }

    private static IReadOnlyList<ToolConfiguration> ReadTools(YamlMappingNode occupant, string path, ParseContext context)
    {
        var sequence = OptionalSequence(occupant, "tools", $"{path}.tools", context);
        if (sequence is null)
        {
            return Array.Empty<ToolConfiguration>();
        }

        var tools = new List<ToolConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var entryPath = $"{path}.tools[{index}]";
            if (sequence.Children[index] is not YamlMappingNode entry)
            {
                context.AddAt(sequence.Children[index], entryPath, "each tool must be a mapping with a 'connector'.");
                continue;
            }

            var connector = RequireScalar(entry, "connector", entryPath, context);
            var scope = OptionalStringList(entry, "scope", entryPath, context);

            if (connector is null)
            {
                continue;
            }

            tools.Add(new ToolConfiguration(connector, scope));
        }

        return tools;
    }

    private static OwnerType? ReadOwnerType(string value, YamlNode node, string path, ParseContext context)
    {
        switch (value)
        {
            case "human":
                return OwnerType.Human;
            case "group":
                return OwnerType.Group;
            default:
                context.AddAt(node, path, $"unknown owner type '{value}'; expected 'human' or 'group'.");
                return null;
        }
    }

    private static OccupantType? ReadOccupantType(string value, YamlNode node, string path, ParseContext context)
    {
        switch (value)
        {
            case "ai-agent":
                return OccupantType.AiAgent;
            case "human":
                return OccupantType.Human;
            default:
                context.AddAt(node, path, $"unknown occupant type '{value}'; expected 'ai-agent' or 'human'.");
                return null;
        }
    }

    private static ActionDomainGate? ReadActionDomainGate(
        string value,
        YamlNode node,
        string path,
        ParseContext context)
    {
        switch (value)
        {
            case "decide":
                return ActionDomainGate.Decide;
            case "escalate":
                return ActionDomainGate.Escalate;
            case "human-approval":
                return ActionDomainGate.HumanApproval;
            default:
                context.AddAt(node, path, $"unknown gate '{value}'; expected 'decide', 'escalate' or 'human-approval'.");
                return null;
        }
    }

    private static T? Identity<T>(Func<string, T> factory, string value, YamlNode node, string path, ParseContext context)
        where T : class
    {
        try
        {
            return factory(value);
        }
        catch (ArgumentException exception)
        {
            context.AddAt(node, path, $"invalid identifier: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads a field whose key is required but whose value may be the explicit YAML <c>null</c>
    /// (the nulled edges of the root unit's <c>parent</c> and the root leadership's
    /// <c>reports_to</c>). Returns whether the field was well-formed and the parsed identifier
    /// (<see langword="null"/> for an explicit null).
    /// </summary>
    private static (bool Ok, T? Value) ReadRequiredNullableId<T>(
        YamlMappingNode map,
        string key,
        Func<string, T> factory,
        string path,
        ParseContext context)
        where T : class
    {
        var fieldPath = $"{path}.{key}";
        var node = Child(map, key);
        if (node is null)
        {
            context.AddAt(map, fieldPath, $"required field '{key}' is missing (use 'null' only on the root edge).");
            return (false, null);
        }

        if (IsNull(node))
        {
            return (true, null);
        }

        if (node is not YamlScalarNode scalar)
        {
            context.AddAt(node, fieldPath, $"field '{key}' must be a scalar identifier or null.");
            return (false, null);
        }

        var value = Identity(factory, scalar.Value ?? string.Empty, node, fieldPath, context);
        return (value is not null, value);
    }

    private static string? RequireScalar(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var fieldPath = $"{path}.{key}";
        var node = Child(map, key);
        if (node is null)
        {
            context.AddAt(map, fieldPath, $"required field '{key}' is missing.");
            return null;
        }

        if (IsNull(node))
        {
            context.AddAt(node, fieldPath, $"required field '{key}' must not be null.");
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            context.AddAt(node, fieldPath, $"field '{key}' must be a scalar value.");
            return null;
        }

        return scalar.Value;
    }

    private static string? OptionalScalar(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var node = Child(map, key);
        if (node is null || IsNull(node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            context.AddAt(node, $"{path}.{key}", $"field '{key}' must be a scalar value.");
            return null;
        }

        return scalar.Value;
    }

    private static double? OptionalDouble(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var raw = OptionalScalar(map, key, path, context);
        if (raw is null)
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        context.AddAt(Child(map, key)!, $"{path}.{key}", $"field '{key}' must be a number; got '{raw}'.");
        return null;
    }

    private static int? OptionalInt(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var raw = OptionalScalar(map, key, path, context);
        if (raw is null)
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        context.AddAt(Child(map, key)!, $"{path}.{key}", $"field '{key}' must be an integer; got '{raw}'.");
        return null;
    }

    private static decimal? OptionalDecimal(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var raw = OptionalScalar(map, key, path, context);
        if (raw is null)
        {
            return null;
        }

        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        context.AddAt(Child(map, key)!, $"{path}.{key}", $"field '{key}' must be a decimal number; got '{raw}'.");
        return null;
    }

    private static IReadOnlyList<string>? OptionalStringList(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var fieldPath = $"{path}.{key}";
        var node = Child(map, key);
        if (node is null || IsNull(node))
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            context.AddAt(node, fieldPath, $"field '{key}' must be a sequence of strings.");
            return null;
        }

        var values = new List<string>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlScalarNode scalar || IsNull(sequence.Children[index]))
            {
                context.AddAt(sequence.Children[index], $"{fieldPath}[{index}]", "each entry must be a scalar string.");
                continue;
            }

            values.Add(scalar.Value ?? string.Empty);
        }

        return values;
    }

    private static YamlSequenceNode? OptionalSequence(YamlMappingNode map, string key, string path, ParseContext context)
    {
        var node = Child(map, key);
        if (node is null || IsNull(node))
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            context.AddAt(node, path, $"field '{key}' must be a sequence.");
            return null;
        }

        return sequence;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool IsNull(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain)
        {
            return false;
        }

        var value = scalar.Value;
        return string.IsNullOrEmpty(value) || Array.IndexOf(NullScalars, value) >= 0;
    }

    private static string CleanMessage(string message)
    {
        // YamlDotNet prefixes its messages with the source position, which we already surface
        // separately as Line/Column; keep only the human-readable tail.
        var separator = message.LastIndexOf("): ", StringComparison.Ordinal);
        var tail = separator >= 0 ? message[(separator + 3)..] : message;
        return tail.Trim();
    }

    private sealed class ParseContext
    {
        private readonly List<OrganizationConfigurationParseError> _errors = new();

        public ParseContext(string filePath) => FilePath = filePath;

        public string FilePath { get; }

        public IReadOnlyList<OrganizationConfigurationParseError> Errors => _errors;

        public void Add(string fieldPath, string message, int? line = null, int? column = null) =>
            _errors.Add(new OrganizationConfigurationParseError(FilePath, fieldPath, message, line, column));

        public void AddAt(YamlNode node, string fieldPath, string message)
        {
            int? line = node is null ? null : (int)node.Start.Line;
            int? column = node is null ? null : (int)node.Start.Column;
            _errors.Add(new OrganizationConfigurationParseError(FilePath, fieldPath, message, line, column));
        }
    }
}
