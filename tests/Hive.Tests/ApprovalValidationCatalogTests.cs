using System.Reflection;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ApprovalValidationCatalogTests
{
    public static TheoryData<ValidationError, string, string, RejectionReason> Entries => new()
    {
        { ApprovalValidationCatalog.EndpointNotAllowed("from"), "endpoint-not-allowed", "from", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.OrganizationNotFound(), "organization-not-found", "organizationId", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ApprovalPolicyNotFound(), "approval-policy-not-found", "policy", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ActionNotAuthorized(), "action-not-authorized", "action", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.AuthorizedApproverRequired(), "authorized-approver-required", "to", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ApprovalRequestExpired(), "approval-request-expired", "deadline", RejectionReason.Expired },
        { ApprovalValidationCatalog.ApprovalRequestNotFound(), "approval-request-not-found", "requestId", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ApprovalThreadMismatch(), "approval-thread-mismatch", "thread", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.UnauthorizedApprover(), "unauthorized-approver", "from", RejectionReason.Unauthorized },
        { ApprovalValidationCatalog.OriginalRequesterRequired(), "original-requester-required", "to", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ApprovalRequestNotOpen(), "approval-request-not-open", "requestId", RejectionReason.InvalidRoute },
        { ApprovalValidationCatalog.ApprovalDecisionExpired(), "approval-decision-expired", "requestId", RejectionReason.Expired },
        { ApprovalValidationCatalog.ApprovalDecisionDuplicate(), "approval-decision-duplicate", "requestId", RejectionReason.Duplicate },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void Catalog_entry_has_canonical_code_path_and_reason(
        ValidationError error,
        string code,
        string path,
        RejectionReason reason)
    {
        Assert.Equal(new ValidationError(code, path, reason), error);
    }

    [Fact]
    public void Duplicate_decision_is_classified_as_duplicate()
    {
        var error = ApprovalValidationCatalog.ApprovalDecisionDuplicate();

        Assert.Equal(RejectionReason.Duplicate, error.Reason);
        Assert.Equal(ApprovalValidationCatalog.Codes.ApprovalDecisionDuplicate, error.Code);
    }

    [Fact]
    public void Catalog_codes_are_unique()
    {
        var codes = typeof(ApprovalValidationCatalog.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }
}
