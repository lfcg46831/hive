using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ValidationResultTests
{
    [Fact]
    public void Empty_error_sequence_is_valid()
    {
        var result = ValidationResult.Create([]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Same(ValidationResult.Valid, ValidationResult.Valid);
    }

    [Fact]
    public void Errors_are_snapshotted_deduplicated_and_sorted_deterministically()
    {
        var organizationRequired = new ValidationError(
            "required-field",
            "organizationId",
            RejectionReason.InvalidContract);
        var source = new List<ValidationError>
        {
            organizationRequired,
            new("endpoint-not-allowed", "from", RejectionReason.InvalidRoute),
            new("unauthorized", "$", RejectionReason.Unauthorized),
            organizationRequired,
            new("invalid-value", "organizationId", RejectionReason.InvalidContract),
        };

        var result = ValidationResult.Create(source);
        source.Clear();

        Assert.False(result.IsValid);
        Assert.Equal(
            [
                new("unauthorized", "$", RejectionReason.Unauthorized),
                new("endpoint-not-allowed", "from", RejectionReason.InvalidRoute),
                new("invalid-value", "organizationId", RejectionReason.InvalidContract),
                organizationRequired,
            ],
            result.Errors);
    }

    [Fact]
    public void Null_error_sequence_is_internal_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(() => ValidationResult.Create(null!));
    }

    [Fact]
    public void Null_error_entry_is_internal_api_misuse()
    {
        Assert.Throws<ArgumentException>(
            () => ValidationResult.Create([null!]));
    }
}
