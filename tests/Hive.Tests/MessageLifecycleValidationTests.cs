using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageLifecycleValidationTests
{
    private readonly MessageContractValidator _validator = new();

    [Fact]
    public void Transition_validation_matches_the_canonical_matrix()
    {
        var allowed = new HashSet<(MessageState From, MessageState To)>
        {
            (MessageState.Received, MessageState.Rejected),
            (MessageState.Received, MessageState.Accepted),
            (MessageState.Accepted, MessageState.Processing),
            (MessageState.Processing, MessageState.Completed),
            (MessageState.Processing, MessageState.Failed),
        };

        foreach (var from in Enum.GetValues<MessageState>())
        {
            foreach (var to in Enum.GetValues<MessageState>())
            {
                var reason = to == MessageState.Rejected
                    ? RejectionReason.InvalidContract
                    : (RejectionReason?)null;

                var result = _validator.ValidateTransition(from, to, reason);

                Assert.Equal(
                    allowed.Contains((from, to)),
                    result.IsValid);
            }
        }
    }

    [Fact]
    public void Undefined_states_are_aggregated_without_throwing()
    {
        var result = _validator.ValidateTransition(
            (MessageState)0,
            (MessageState)7,
            rejectionReason: null);

        Assert.Equal(
            [
                new ValidationError(
                    "invalid-state", "state.from", RejectionReason.InvalidContract),
                new ValidationError(
                    "invalid-state", "state.to", RejectionReason.InvalidContract),
            ],
            result.Errors);
    }

    [Fact]
    public void Invalid_defined_transition_is_rejected()
    {
        var result = _validator.ValidateTransition(
            MessageState.Accepted,
            MessageState.Completed,
            rejectionReason: null);

        Assert.Equal(
            [new ValidationError(
                "invalid-state-transition", "state", RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public void Rejected_state_requires_a_rejection_reason()
    {
        var result = _validator.ValidateTransition(
            MessageState.Received,
            MessageState.Rejected,
            rejectionReason: null);

        Assert.Equal(
            [new ValidationError(
                "rejection-reason-required",
                "rejectionReason",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Theory]
    [InlineData(MessageState.Accepted)]
    [InlineData(MessageState.Processing)]
    [InlineData(MessageState.Completed)]
    [InlineData(MessageState.Failed)]
    public void Non_rejected_state_does_not_allow_a_rejection_reason(MessageState to)
    {
        var from = to == MessageState.Accepted
            ? MessageState.Received
            : to == MessageState.Processing
                ? MessageState.Accepted
                : MessageState.Processing;

        var result = _validator.ValidateTransition(
            from,
            to,
            RejectionReason.InvalidContract);

        Assert.Equal(
            [new ValidationError(
                "rejection-reason-not-allowed",
                "rejectionReason",
                RejectionReason.InvalidContract)],
            result.Errors);
    }

    [Fact]
    public void Undefined_rejection_reason_is_reported_without_throwing()
    {
        var result = _validator.ValidateTransition(
            MessageState.Received,
            MessageState.Rejected,
            (RejectionReason)0);

        Assert.Equal(
            [new ValidationError(
                "invalid-rejection-reason",
                "rejectionReason",
                RejectionReason.InvalidContract)],
            result.Errors);
    }
}
