namespace Hive.Domain.Ai;

internal sealed class AiProtocolEnumWireContract<TEnum>
    where TEnum : struct, Enum
{
    private readonly IReadOnlyDictionary<TEnum, string> _wireByValue;
    private readonly IReadOnlyDictionary<string, TEnum> _valueByWire;

    public AiProtocolEnumWireContract(params (TEnum Value, string WireValue)[] values)
    {
        _wireByValue = values.ToDictionary(entry => entry.Value, entry => entry.WireValue);
        _valueByWire = values.ToDictionary(
            entry => entry.WireValue,
            entry => entry.Value,
            StringComparer.Ordinal);
    }

    public TEnum RequireDefined(TEnum value, string parameterName)
    {
        if (!_wireByValue.ContainsKey(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{typeof(TEnum).Name} has an undefined value.");
        }

        return value;
    }

    public string ToWireValue(TEnum value)
    {
        RequireDefined(value, nameof(value));
        return _wireByValue[value];
    }

    public TEnum ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_valueByWire.TryGetValue(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{typeof(TEnum).Name} has an invalid wire value.",
            nameof(value));
    }

    public bool TryParseWireValue(string? value, out TEnum result)
    {
        if (value is not null && _valueByWire.TryGetValue(value, out result))
        {
            return true;
        }

        result = default;
        return false;
    }
}
