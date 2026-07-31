using System.Globalization;
using System.Xml;

namespace Hive.Evaluation.Tooling.Evaluation;

internal static class EvaluationOrganizationSnapshotReader
{
    private const int MaxConfigurationBytes = 512 * 1024;
    private const int MaxLineLength = 16 * 1024;

    public static EvaluationOrganizationSnapshot Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Experiment organization snapshot must contain between 1 and {MaxConfigurationBytes} bytes.");
        }

        string? section = null;
        string? organizationId = null;
        EvaluationPositionSnapshotBuilder? current = null;
        var inOccupant = false;
        var inAi = false;
        var positions = new Dictionary<string, EvaluationPositionSnapshot>(
            StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            if (rawLine.Length > MaxLineLength || rawLine.Contains('\t'))
            {
                throw new InvalidDataException(
                    "Experiment organization snapshot contains an unsupported line.");
            }

            var line = StripComment(rawLine).TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indentation = line.TakeWhile(character => character == ' ').Count();
            if ((indentation & 1) != 0)
            {
                throw new InvalidDataException(
                    "Experiment organization snapshot indentation must use pairs of spaces.");
            }

            var content = line[indentation..];
            if (indentation == 0)
            {
                Commit(current, positions);
                current = null;
                inOccupant = false;
                inAi = false;
                section = MappingName(content);
                continue;
            }

            if (section == "organization"
                && indentation == 2
                && TryField(content, "id", out var declaredOrganizationId))
            {
                SetOnce(
                    ref organizationId,
                    declaredOrganizationId,
                    "organization.id");
                continue;
            }

            if (section != "positions")
            {
                continue;
            }

            if (indentation == 2 && content.StartsWith("- ", StringComparison.Ordinal))
            {
                Commit(current, positions);
                if (!TryField(content[2..], "id", out var positionId))
                {
                    throw new InvalidDataException(
                        "Experiment organization positions must declare id on the sequence item.");
                }

                current = new EvaluationPositionSnapshotBuilder(positionId);
                inOccupant = false;
                inAi = false;
                continue;
            }

            if (current is null)
            {
                throw new InvalidDataException(
                    "Experiment organization position fields require a position id.");
            }

            if (indentation == 4)
            {
                inOccupant = string.Equals(
                    content.Trim(),
                    "occupant:",
                    StringComparison.Ordinal);
                inAi = false;
                if (inOccupant)
                {
                    continue;
                }
            }

            if (indentation == 4
                && TryField(content, "reports_to", out var reportsTo))
            {
                current.SetReportsTo(reportsTo);
            }
            else if (indentation == 6 && inOccupant
                && TryField(content, "type", out var occupantType))
            {
                inAi = false;
                current.SetOccupantType(occupantType);
            }
            else if (indentation == 6 && inOccupant)
            {
                inAi = string.Equals(
                    content.Trim(),
                    "ai:",
                    StringComparison.Ordinal);
            }
            else if (indentation == 8 && inAi
                && TryField(content, "provider", out var provider))
            {
                current.SetProvider(provider);
            }
            else if (indentation == 8 && inAi
                && TryField(content, "model", out var model))
            {
                current.SetModel(model);
            }
            else if (indentation == 8 && inAi
                && TryField(content, "max_tokens", out var maxTokens))
            {
                current.SetMaxOutputTokens(PositiveInt(maxTokens, "max_tokens"));
            }
            else if (indentation == 8 && inAi
                && TryField(content, "max_iterations", out var maxIterations))
            {
                current.SetMaxIterations(PositiveInt(maxIterations, "max_iterations"));
            }
            else if (indentation == 8 && inAi
                && TryField(content, "limits_version", out var limitsVersion))
            {
                current.SetLimitsVersion(PositiveInt(limitsVersion, "limits_version"));
            }
            else if (indentation == 8 && inAi
                && TryField(content, "timeout", out var timeout))
            {
                var duration = XmlConvert.ToTimeSpan(timeout);
                var milliseconds = checked((int)duration.TotalMilliseconds);
                if (milliseconds <= 0)
                {
                    throw new InvalidDataException(
                        "Experiment organization timeout must be positive.");
                }

                current.SetProviderTimeout(milliseconds);
            }
            else if (indentation == 8 && inAi
                && TryField(content, "execution_timeout", out var executionTimeout))
            {
                var duration = XmlConvert.ToTimeSpan(executionTimeout);
                var milliseconds = checked((int)duration.TotalMilliseconds);
                if (milliseconds <= 0)
                {
                    throw new InvalidDataException(
                        "Experiment organization execution timeout must be positive.");
                }

                current.SetExecutionTimeout(milliseconds);
            }
        }

        Commit(current, positions);
        if (string.IsNullOrWhiteSpace(organizationId) || positions.Count == 0)
        {
            throw new InvalidDataException(
                "Experiment organization snapshot is missing its organization or positions.");
        }

        return new EvaluationOrganizationSnapshot(organizationId, positions);
    }

    private static void Commit(
        EvaluationPositionSnapshotBuilder? builder,
        IDictionary<string, EvaluationPositionSnapshot> positions)
    {
        if (builder is null)
        {
            return;
        }

        var snapshot = builder.Build();
        if (!positions.TryAdd(snapshot.PositionId, snapshot))
        {
            throw new InvalidDataException(
                $"Experiment organization position '{snapshot.PositionId}' is duplicated.");
        }
    }

    private static string? MappingName(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.EndsWith(':') || trimmed.Count(character => character == ':') != 1)
        {
            throw new InvalidDataException(
                "Experiment organization root entries must be YAML mappings.");
        }

        return trimmed[..^1];
    }

    private static bool TryField(
        string content,
        string expectedName,
        out string value)
    {
        var separator = content.IndexOf(':');
        if (separator <= 0
            || !string.Equals(
                content[..separator].Trim(),
                expectedName,
                StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = Scalar(content[(separator + 1)..], expectedName);
        return true;
    }

    private static string Scalar(string raw, string field)
    {
        var value = raw.Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Experiment organization field '{field}' must be a bounded scalar.");
        }

        return value;
    }

    private static int PositiveInt(string value, string field) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
        && parsed > 0
            ? parsed
            : throw new InvalidDataException(
                $"Experiment organization field '{field}' must be a positive integer.");

    private static string StripComment(string line)
    {
        var singleQuoted = false;
        var doubleQuoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            switch (line[index])
            {
                case '\'' when !doubleQuoted:
                    singleQuoted = !singleQuoted;
                    break;
                case '"' when !singleQuoted:
                    doubleQuoted = !doubleQuoted;
                    break;
                case '#' when !singleQuoted && !doubleQuoted:
                    return line[..index];
            }
        }

        if (singleQuoted || doubleQuoted)
        {
            throw new InvalidDataException(
                "Experiment organization snapshot contains an unterminated quoted scalar.");
        }

        return line;
    }

    private static void SetOnce(
        ref string? target,
        string value,
        string field)
    {
        if (target is not null)
        {
            throw new InvalidDataException(
                $"Experiment organization field '{field}' is duplicated.");
        }

        target = value;
    }

    private sealed class EvaluationPositionSnapshotBuilder(string positionId)
    {
        private string? _reportsTo;
        private string? _occupantType;
        private string? _provider;
        private string? _model;
        private int? _maxOutputTokens;
        private int? _maxIterations;
        private int? _limitsVersion;
        private int? _providerTimeoutMilliseconds;
        private int? _executionTimeoutMilliseconds;

        public void SetReportsTo(string value) =>
            EvaluationOrganizationSnapshotReader.SetOnce(
                ref _reportsTo,
                value,
                "reports_to");

        public void SetOccupantType(string value) =>
            EvaluationOrganizationSnapshotReader.SetOnce(
                ref _occupantType,
                value,
                "occupant.type");

        public void SetProvider(string value) =>
            EvaluationOrganizationSnapshotReader.SetOnce(
                ref _provider,
                value,
                "occupant.ai.provider");

        public void SetModel(string value) =>
            EvaluationOrganizationSnapshotReader.SetOnce(
                ref _model,
                value,
                "occupant.ai.model");

        public void SetMaxOutputTokens(int value) =>
            SetOnce(ref _maxOutputTokens, value, "occupant.ai.max_tokens");

        public void SetMaxIterations(int value) =>
            SetOnce(ref _maxIterations, value, "occupant.ai.max_iterations");

        public void SetLimitsVersion(int value) =>
            SetOnce(ref _limitsVersion, value, "occupant.ai.limits_version");

        public void SetProviderTimeout(int value) =>
            SetOnce(
                ref _providerTimeoutMilliseconds,
                value,
                "occupant.ai.timeout");

        public void SetExecutionTimeout(int value) =>
            SetOnce(
                ref _executionTimeoutMilliseconds,
                value,
                "occupant.ai.execution_timeout");

        public EvaluationPositionSnapshot Build()
        {
            if (_limitsVersion is not null and not 1
                || (_executionTimeoutMilliseconds.HasValue && !_limitsVersion.HasValue)
                || (_limitsVersion == 1
                    && (!_providerTimeoutMilliseconds.HasValue
                        || !_executionTimeoutMilliseconds.HasValue)))
            {
                throw new InvalidDataException(
                    "Experiment organization execution limits are invalid.");
            }

            var effectiveLimitsVersion = _providerTimeoutMilliseconds.HasValue
                ? _limitsVersion ?? 0
                : _limitsVersion;
            var effectiveExecutionTimeout = _limitsVersion.HasValue
                ? _executionTimeoutMilliseconds
                : _providerTimeoutMilliseconds;

            return new(
                positionId,
                _reportsTo,
                _occupantType,
                _provider,
                _model,
                _maxOutputTokens,
                _maxIterations,
                effectiveLimitsVersion,
                _providerTimeoutMilliseconds,
                effectiveExecutionTimeout);
        }

        private static void SetOnce<T>(
            ref T? target,
            T value,
            string field)
            where T : struct
        {
            if (target.HasValue)
            {
                throw new InvalidDataException(
                    $"Experiment organization field '{field}' is duplicated.");
            }

            target = value;
        }
    }
}

internal sealed record EvaluationOrganizationSnapshot(
    string OrganizationId,
    IReadOnlyDictionary<string, EvaluationPositionSnapshot> Positions)
{
    public EvaluationPositionSnapshot Position(string positionId) =>
        Positions.TryGetValue(positionId, out var position)
            ? position
            : throw new InvalidDataException(
                $"Experiment position '{positionId}' does not exist in the organization snapshot.");
}

internal sealed record EvaluationPositionSnapshot(
    string PositionId,
    string? ReportsTo,
    string? OccupantType,
    string? ProviderId,
    string? ModelId,
    int? MaxOutputTokens,
    int? MaxIterations,
    int? LimitsVersion,
    int? ProviderTimeoutMilliseconds,
    int? ExecutionTimeoutMilliseconds);
