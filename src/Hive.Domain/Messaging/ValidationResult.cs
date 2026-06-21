using System.Collections.Immutable;

namespace Hive.Domain.Messaging;

public sealed record ValidationResult
{
    private ValidationResult(ImmutableArray<ValidationError> errors)
    {
        Errors = errors;
    }

    public static ValidationResult Valid { get; } = new([]);

    public IReadOnlyList<ValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Create(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var snapshot = errors.ToImmutableArray();

        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "Validation errors cannot contain null entries.",
                nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            return Valid;
        }

        return new ValidationResult(
            snapshot
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Reason)
                .ToImmutableArray());
    }
}
