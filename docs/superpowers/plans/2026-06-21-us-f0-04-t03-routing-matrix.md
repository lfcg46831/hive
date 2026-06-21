# US-F0-04-T03 Routing Matrix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Define the immutable semantic routing matrix for vertical and governance messages without implementing contextual route validation.

**Architecture:** Add a dedicated `MessageRoutingRules` catalog under `Hive.Domain.Messaging`. Each message rule declares its channel and immutable origin/destination path rows, while each path names the organizational or authority relation that later validators must prove; structural envelope rules remain in `MessageContractRules`.

**Tech Stack:** .NET 8, C# records and immutable collections, xUnit

---

### Task 1: Specify the closed routing matrix

**Files:**
- Create: `tests/Hive.Tests/MessageRoutingRulesTests.cs`

- [x] **Step 1: Write the failing routing matrix tests**

Create `MessageRoutingRulesTests.cs`:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageRoutingRulesTests
{
    [Fact]
    public void Routing_relations_are_a_closed_set()
    {
        Assert.Equal(1, (int)RoutingRelation.DirectSuperiorToDirectSubordinate);
        Assert.Equal(2, (int)RoutingRelation.DirectSubordinateToDirectSuperior);
        Assert.Equal(3, (int)RoutingRelation.RootLeadershipToOrganizationOwner);
        Assert.Equal(4, (int)RoutingRelation.RequesterToAuthorizedApprover);
        Assert.Equal(5, (int)RoutingRelation.AuthorizedApproverToOriginalRequester);
        Assert.Equal(
            [
                RoutingRelation.DirectSuperiorToDirectSubordinate,
                RoutingRelation.DirectSubordinateToDirectSuperior,
                RoutingRelation.RootLeadershipToOrganizationOwner,
                RoutingRelation.RequesterToAuthorizedApprover,
                RoutingRelation.AuthorizedApproverToOriginalRequester,
            ],
            Enum.GetValues<RoutingRelation>());
    }

    [Fact]
    public void Catalog_contains_only_vertical_and_governance_message_types()
    {
        Assert.Equal(
            [
                typeof(ApprovalDecision),
                typeof(ApprovalRequest),
                typeof(Directive),
                typeof(Escalation),
                typeof(Report),
            ],
            MessageRoutingRules.All.Keys.OrderBy(type => type.Name));
    }

    [Fact]
    public void Vertical_matrix_defines_downward_upward_and_root_escalation_paths()
    {
        AssertRule<Directive>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSuperiorToDirectSubordinate));
        AssertRule<Report>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSubordinateToDirectSuperior));
        AssertRule<Escalation>(
            MessageChannel.Vertical,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.DirectSubordinateToDirectSuperior),
            Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                RoutingRelation.RootLeadershipToOrganizationOwner));
    }

    [Fact]
    public void Governance_matrix_defines_authorized_request_and_decision_paths()
    {
        AssertRule<ApprovalRequest>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.RequesterToAuthorizedApprover),
            Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                RoutingRelation.RequesterToAuthorizedApprover));
        AssertRule<ApprovalDecision>(
            MessageChannel.Governance,
            Path<PositionEndpointRef, PositionEndpointRef>(
                RoutingRelation.AuthorizedApproverToOriginalRequester),
            Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                RoutingRelation.AuthorizedApproverToOriginalRequester));
    }

    [Fact]
    public void Unknown_or_null_message_types_have_no_routing_rule()
    {
        Assert.Throws<ArgumentNullException>(() => MessageRoutingRules.For(null!));
        Assert.Throws<ArgumentException>(() => MessageRoutingRules.For(typeof(Memo)));
    }

    private static void AssertRule<TMessage>(
        MessageChannel channel,
        params ExpectedPath[] expectedPaths)
        where TMessage : OrgMessage
    {
        var rule = MessageRoutingRules.For<TMessage>();

        Assert.Equal(typeof(TMessage), rule.MessageType);
        Assert.Equal(channel, rule.Channel);
        Assert.Equal(
            expectedPaths,
            rule.Paths.Select(path => new ExpectedPath(
                path.FromEndpointType,
                path.ToEndpointType,
                path.Relation)));
    }

    private static ExpectedPath Path<TFrom, TTo>(RoutingRelation relation)
        where TFrom : EndpointRef
        where TTo : EndpointRef =>
        new(typeof(TFrom), typeof(TTo), relation);

    private sealed record ExpectedPath(
        Type FromEndpointType,
        Type ToEndpointType,
        RoutingRelation Relation);
}
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageRoutingRulesTests -v minimal`

Expected: compilation fails because `RoutingRelation` and `MessageRoutingRules` do not exist.

### Task 2: Implement the immutable routing catalog

**Files:**
- Create: `src/Hive.Domain/Messaging/RoutingRelation.cs`
- Create: `src/Hive.Domain/Messaging/MessageRoutingRules.cs`
- Test: `tests/Hive.Tests/MessageRoutingRulesTests.cs`

- [x] **Step 1: Define the closed semantic relations**

Create `RoutingRelation.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum RoutingRelation
{
    DirectSuperiorToDirectSubordinate = 1,
    DirectSubordinateToDirectSuperior = 2,
    RootLeadershipToOrganizationOwner = 3,
    RequesterToAuthorizedApprover = 4,
    AuthorizedApproverToOriginalRequester = 5,
}
```

- [x] **Step 2: Define the immutable matrix and lookup API**

Create `MessageRoutingRules.cs`:

```csharp
using System.Collections.Immutable;

namespace Hive.Domain.Messaging;

public sealed record RoutingPathRule
{
    internal RoutingPathRule(
        Type fromEndpointType,
        Type toEndpointType,
        RoutingRelation relation)
    {
        FromEndpointType = fromEndpointType;
        ToEndpointType = toEndpointType;
        Relation = relation;
    }

    public Type FromEndpointType { get; }

    public Type ToEndpointType { get; }

    public RoutingRelation Relation { get; }
}

public sealed record MessageRoutingRule
{
    internal MessageRoutingRule(
        Type messageType,
        MessageChannel channel,
        ImmutableArray<RoutingPathRule> paths)
    {
        MessageType = messageType;
        Channel = channel;
        Paths = paths;
    }

    public Type MessageType { get; }

    public MessageChannel Channel { get; }

    public ImmutableArray<RoutingPathRule> Paths { get; }
}

public static class MessageRoutingRules
{
    public static ImmutableDictionary<Type, MessageRoutingRule> All { get; } =
        new[]
        {
            Rule<Directive>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSuperiorToDirectSubordinate)),
            Rule<Report>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSubordinateToDirectSuperior)),
            Rule<Escalation>(
                MessageChannel.Vertical,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.DirectSubordinateToDirectSuperior),
                Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                    RoutingRelation.RootLeadershipToOrganizationOwner)),
            Rule<ApprovalRequest>(
                MessageChannel.Governance,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.RequesterToAuthorizedApprover),
                Path<PositionEndpointRef, OrganizationOwnerEndpointRef>(
                    RoutingRelation.RequesterToAuthorizedApprover)),
            Rule<ApprovalDecision>(
                MessageChannel.Governance,
                Path<PositionEndpointRef, PositionEndpointRef>(
                    RoutingRelation.AuthorizedApproverToOriginalRequester),
                Path<OrganizationOwnerEndpointRef, PositionEndpointRef>(
                    RoutingRelation.AuthorizedApproverToOriginalRequester)),
        }.ToImmutableDictionary(rule => rule.MessageType);

    public static MessageRoutingRule For<TMessage>()
        where TMessage : OrgMessage =>
        For(typeof(TMessage));

    public static MessageRoutingRule For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (All.TryGetValue(messageType, out var rule))
        {
            return rule;
        }

        throw new ArgumentException(
            $"{messageType.Name} has no vertical or governance routing rule.",
            nameof(messageType));
    }

    private static MessageRoutingRule Rule<TMessage>(
        MessageChannel channel,
        params RoutingPathRule[] paths)
        where TMessage : OrgMessage =>
        new(typeof(TMessage), channel, [.. paths]);

    private static RoutingPathRule Path<TFrom, TTo>(RoutingRelation relation)
        where TFrom : EndpointRef
        where TTo : EndpointRef =>
        new(typeof(TFrom), typeof(TTo), relation);
}
```

- [x] **Step 3: Run the focused tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageRoutingRulesTests -v minimal`

Expected: all `MessageRoutingRulesTests` pass.

- [x] **Step 4: Review the implementation while tests remain green**

Confirm that the catalog contains only the five T03 message types, exposes immutable collections, and performs no registry lookup, policy resolution, correlation, or acceptance decision.

### Task 3: Verify the task and repository

**Files:**
- Modify: `docs/bible.html`
- Verify: `src/Hive.Domain/Messaging/RoutingRelation.cs`
- Verify: `src/Hive.Domain/Messaging/MessageRoutingRules.cs`
- Verify: `tests/Hive.Tests/MessageRoutingRulesTests.cs`

- [x] **Step 1: Run the complete test suite**

Run: `dotnet test Hive.sln --no-restore -v minimal`

Expected: all tests pass with zero failures.

- [x] **Step 2: Build the solution**

Run: `dotnet build Hive.sln --no-restore -v minimal`

Expected: build succeeds with zero errors.

- [x] **Step 3: Check formatting and diff hygiene**

Run: `dotnet format Hive.sln --no-restore --verify-no-changes --include src/Hive.Domain/Messaging/MessageRoutingRules.cs src/Hive.Domain/Messaging/RoutingRelation.cs tests/Hive.Tests/MessageRoutingRulesTests.cs`

Expected: exit code 0.

Run: `git diff --check`

Expected: no whitespace errors.

- [x] **Step 4: Prepare the requested commit message without committing**

Use: `feat(domain): define vertical and governance routing matrix`
