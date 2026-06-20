# US-F0-03-T02 Common Message Envelope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the canonical priority prerequisite, typed endpoint references, and immutable common organizational-message base defined by `US-F0-03-T02`.

**Architecture:** Add focused protocol contracts under `Hive.Domain.Messaging`. A closed `Priority` enum and `PriorityContract` implement `T03a`; a discriminated `EndpointRef` record hierarchy represents logical producers and consumers; an abstract `OrgMessage` record centralizes common metadata and local invariants while leaving routing, cross-field validation, and serialization to later tasks.

**Tech Stack:** .NET 8, C# records/enums, xUnit 2.5.3, existing `Hive.Domain.Identity` value objects

---

## File map

- Create `src/Hive.Domain/Messaging/Priority.cs`: canonical priority values, validation, comparison, and wire-name conversion.
- Create `src/Hive.Domain/Messaging/SystemEndpointKind.cs`: closed F0 system endpoint kinds.
- Create `src/Hive.Domain/Messaging/EndpointRef.cs`: endpoint union and its initial variants.
- Create `src/Hive.Domain/Messaging/OrgMessage.cs`: immutable abstract common message base.
- Create `tests/Hive.Tests/MessagePriorityTests.cs`: `Priority` and `PriorityContract` behavior.
- Create `tests/Hive.Tests/MessageEndpointTests.cs`: endpoint union behavior and invariants.
- Create `tests/Hive.Tests/OrgMessageTests.cs`: common envelope behavior and invariants.
- Preserve `docs/bible.html`: it is already the source of truth and needs no implementation narrative.
- Preserve `docs/configuration.md`: this task introduces no operational configuration.

---

### Task 1: Implement the `US-F0-03-T03a` priority prerequisite

**Files:**
- Create: `tests/Hive.Tests/MessagePriorityTests.cs`
- Create: `src/Hive.Domain/Messaging/Priority.cs`

- [x] **Step 1: Write the failing priority contract tests**

Create `tests/Hive.Tests/MessagePriorityTests.cs`:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessagePriorityTests
{
    [Fact]
    public void Levels_have_stable_semantic_ranks()
    {
        Assert.Equal(1, (int)Priority.Low);
        Assert.Equal(2, (int)Priority.Normal);
        Assert.Equal(3, (int)Priority.High);
        Assert.Equal(4, (int)Priority.Critical);
        Assert.Equal(
            [Priority.Low, Priority.Normal, Priority.High, Priority.Critical],
            Enum.GetValues<Priority>());
    }

    [Fact]
    public void Compare_uses_semantic_rank()
    {
        Assert.True(PriorityContract.Compare(Priority.Critical, Priority.High) > 0);
        Assert.True(PriorityContract.Compare(Priority.High, Priority.Normal) > 0);
        Assert.True(PriorityContract.Compare(Priority.Normal, Priority.Low) > 0);
        Assert.Equal(0, PriorityContract.Compare(Priority.Normal, Priority.Normal));
    }

    [Theory]
    [InlineData(Priority.Low, "low")]
    [InlineData(Priority.Normal, "normal")]
    [InlineData(Priority.High, "high")]
    [InlineData(Priority.Critical, "critical")]
    public void Wire_values_round_trip_canonically(Priority priority, string wireValue)
    {
        Assert.Equal(wireValue, PriorityContract.ToWireValue(priority));
        Assert.Equal(priority, PriorityContract.ParseWireValue(wireValue));
        Assert.True(PriorityContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(priority, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("normal ")]
    [InlineData(" Normal")]
    [InlineData("Normal")]
    [InlineData("2")]
    [InlineData("urgent")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => PriorityContract.ParseWireValue(value!));
        Assert.False(PriorityContract.TryParseWireValue(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PriorityContract.ParseWireValue(null!));
        Assert.False(PriorityContract.TryParseWireValue(null, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var priority = (Priority)rawValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.RequireDefined(priority, "priority"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.Compare(priority, Priority.Normal));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.Compare(Priority.Normal, priority));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PriorityContract.ToWireValue(priority));
    }
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessagePriorityTests"
```

Expected: build fails with `CS0234`/`CS0246` because `Hive.Domain.Messaging`, `Priority`, and `PriorityContract` do not exist.

- [x] **Step 3: Add the canonical priority contract**

Create `src/Hive.Domain/Messaging/Priority.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum Priority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4,
}

public static class PriorityContract
{
    public static Priority RequireDefined(Priority value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Priority must be Low, Normal, High, or Critical.");
        }

        return value;
    }

    public static int Compare(Priority left, Priority right)
    {
        RequireDefined(left, nameof(left));
        RequireDefined(right, nameof(right));

        return ((int)left).CompareTo((int)right);
    }

    public static string ToWireValue(Priority value) =>
        RequireDefined(value, nameof(value)) switch
        {
            Priority.Low => "low",
            Priority.Normal => "normal",
            Priority.High => "high",
            Priority.Critical => "critical",
            _ => throw new InvalidOperationException("Validated priority is not mapped."),
        };

    public static Priority ParseWireValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "low" => Priority.Low,
            "normal" => Priority.Normal,
            "high" => Priority.High,
            "critical" => Priority.Critical,
            _ => throw new ArgumentException(
                "Priority must be low, normal, high, or critical.",
                nameof(value)),
        };
    }

    public static bool TryParseWireValue(string? value, out Priority priority)
    {
        switch (value)
        {
            case "low":
                priority = Priority.Low;
                return true;
            case "normal":
                priority = Priority.Normal;
                return true;
            case "high":
                priority = Priority.High;
                return true;
            case "critical":
                priority = Priority.Critical;
                return true;
            default:
                priority = default;
                return false;
        }
    }
}
```

- [x] **Step 4: Run the focused test and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessagePriorityTests"
```

Expected: 15 test cases pass with zero failures.

- [x] **Step 5: Confirm the task boundary**

Run:

```powershell
git diff -- src/Hive.Domain/Messaging/Priority.cs tests/Hive.Tests/MessagePriorityTests.cs
```

Expected: the diff contains only the closed priority contract and its tests; it does not add `ReportKind`, channel, message status, rejection reasons, serializer configuration, or Akka mailbox configuration. `US-F0-03-T03` remains incomplete because `T03b` is not part of this task.

---

### Task 2: Implement typed endpoint references

**Files:**
- Create: `tests/Hive.Tests/MessageEndpointTests.cs`
- Create: `src/Hive.Domain/Messaging/SystemEndpointKind.cs`
- Create: `src/Hive.Domain/Messaging/EndpointRef.cs`

- [x] **Step 1: Write the failing endpoint contract tests**

Create `tests/Hive.Tests/MessageEndpointTests.cs`:

```csharp
using System.Reflection;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageEndpointTests
{
    [Fact]
    public void Position_endpoint_preserves_typed_identity_and_value_equality()
    {
        var positionId = PositionId.From("bug-triage");

        var first = new PositionEndpointRef(positionId);
        var second = new PositionEndpointRef(PositionId.From("bug-triage"));

        Assert.Equal(positionId, first.PositionId);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Organization_owner_endpoint_has_value_equality_without_an_extra_identifier()
    {
        Assert.Equal(
            new OrganizationOwnerEndpointRef(),
            new OrganizationOwnerEndpointRef());
    }

    [Theory]
    [InlineData(SystemEndpointKind.Scheduler)]
    [InlineData(SystemEndpointKind.DomainEvents)]
    public void System_endpoint_preserves_defined_kind(SystemEndpointKind kind)
    {
        var endpoint = new SystemEndpointRef(kind);

        Assert.Equal(kind, endpoint.Kind);
        Assert.Equal(endpoint, new SystemEndpointRef(kind));
    }

    [Fact]
    public void Endpoint_union_is_abstract_and_variants_are_sealed()
    {
        Assert.True(typeof(EndpointRef).IsAbstract);
        Assert.True(typeof(PositionEndpointRef).IsSealed);
        Assert.True(typeof(OrganizationOwnerEndpointRef).IsSealed);
        Assert.True(typeof(SystemEndpointRef).IsSealed);
    }

    [Fact]
    public void Endpoint_variants_expose_no_implicit_conversions()
    {
        var endpointTypes = new[]
        {
            typeof(PositionEndpointRef),
            typeof(OrganizationOwnerEndpointRef),
            typeof(SystemEndpointRef),
        };

        foreach (var type in endpointTypes)
        {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    [Fact]
    public void System_endpoint_kinds_are_limited_to_the_F0_producers()
    {
        Assert.Equal(
            [SystemEndpointKind.Scheduler, SystemEndpointKind.DomainEvents],
            Enum.GetValues<SystemEndpointKind>());
    }

    [Fact]
    public void Position_endpoint_rejects_null_identity()
    {
        Assert.Throws<ArgumentNullException>(() => new PositionEndpointRef(null!));
    }

    [Fact]
    public void System_endpoint_rejects_undefined_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SystemEndpointRef((SystemEndpointKind)2));
    }
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageEndpointTests"
```

Expected: build fails with `CS0246` because the endpoint reference types and `SystemEndpointKind` do not exist.

- [x] **Step 3: Add the closed F0 system endpoint kinds**

Create `src/Hive.Domain/Messaging/SystemEndpointKind.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum SystemEndpointKind
{
    Scheduler,
    DomainEvents,
}
```

- [x] **Step 4: Add the endpoint discriminated union**

Create `src/Hive.Domain/Messaging/EndpointRef.cs`:

```csharp
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public abstract record EndpointRef;

public sealed record PositionEndpointRef : EndpointRef
{
    public PositionEndpointRef(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        PositionId = positionId;
    }

    public PositionId PositionId { get; }
}

public sealed record OrganizationOwnerEndpointRef : EndpointRef;

public sealed record SystemEndpointRef : EndpointRef
{
    public SystemEndpointRef(SystemEndpointKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "System endpoint must be Scheduler or DomainEvents.");
        }

        Kind = kind;
    }

    public SystemEndpointKind Kind { get; }
}
```

- [x] **Step 5: Run the focused test and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageEndpointTests"
```

Expected: 9 test cases pass with zero failures.

- [x] **Step 6: Run all new protocol prerequisite tests together**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessagePriorityTests|FullyQualifiedName~MessageEndpointTests"
```

Expected: 24 test cases pass with zero failures.

---

### Task 3: Implement the immutable common message base

**Files:**
- Create: `tests/Hive.Tests/OrgMessageTests.cs`
- Create: `src/Hive.Domain/Messaging/OrgMessage.cs`

- [x] **Step 1: Write the failing common-envelope tests**

Create `tests/Hive.Tests/OrgMessageTests.cs`:

```csharp
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class OrgMessageTests
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 21, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Deadline = SentAt.AddHours(4);

    [Fact]
    public void Derived_message_preserves_every_common_envelope_value()
    {
        var id = MessageId.From(Guid.Parse("61d9c90c-2f73-4b98-9394-8107a326a849"));
        var organizationId = OrganizationId.From("acme");
        var from = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var to = new PositionEndpointRef(PositionId.From("bug-triage"));
        var thread = ThreadId.From(Guid.Parse("e67184b3-8248-4d11-ab1c-00d3495ff51c"));

        var message = new TestOrgMessage(
            id,
            organizationId,
            from,
            to,
            thread,
            Priority.High,
            1,
            SentAt,
            Deadline);

        Assert.Equal(id, message.Id);
        Assert.Equal(organizationId, message.OrganizationId);
        Assert.Equal(from, message.From);
        Assert.Equal(to, message.To);
        Assert.Equal(thread, message.Thread);
        Assert.Equal(Priority.High, message.Priority);
        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal(SentAt, message.SentAt);
        Assert.Equal(Deadline, message.Deadline);
    }

    [Fact]
    public void Deadline_is_optional()
    {
        var message = CreateMessage(deadline: null);

        Assert.Null(message.Deadline);
    }

    [Fact]
    public void Common_properties_are_get_only()
    {
        var propertyNames = new[]
        {
            nameof(OrgMessage.Id),
            nameof(OrgMessage.OrganizationId),
            nameof(OrgMessage.From),
            nameof(OrgMessage.To),
            nameof(OrgMessage.Thread),
            nameof(OrgMessage.Priority),
            nameof(OrgMessage.SchemaVersion),
            nameof(OrgMessage.SentAt),
            nameof(OrgMessage.Deadline),
        };

        foreach (var propertyName in propertyNames)
        {
            var property = typeof(OrgMessage).GetProperty(propertyName);

            Assert.NotNull(property);
            Assert.Null(property.SetMethod);
        }
    }

    [Fact]
    public void Constructor_rejects_null_required_references()
    {
        var id = MessageId.New();
        var organizationId = OrganizationId.From("acme");
        var from = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var to = new PositionEndpointRef(PositionId.From("bug-triage"));
        var thread = ThreadId.New();

        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                null!, organizationId, from, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, null!, from, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, null!, to, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, from, null!, thread,
                Priority.Normal, 1, SentAt, null));
        Assert.Throws<ArgumentNullException>(
            () => new TestOrgMessage(
                id, organizationId, from, to, null!,
                Priority.Normal, 1, SentAt, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Constructor_rejects_undefined_priority(int rawValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateMessage(priority: (Priority)rawValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_schema_version(int schemaVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateMessage(schemaVersion: schemaVersion));
    }

    private static TestOrgMessage CreateMessage(
        MessageId? id = null,
        OrganizationId? organizationId = null,
        EndpointRef? from = null,
        EndpointRef? to = null,
        ThreadId? thread = null,
        Priority priority = Priority.Normal,
        int schemaVersion = 1,
        DateTimeOffset? deadline = null) =>
        new(
            id ?? MessageId.New(),
            organizationId ?? OrganizationId.From("acme"),
            from ?? new PositionEndpointRef(PositionId.From("delivery-lead")),
            to ?? new PositionEndpointRef(PositionId.From("bug-triage")),
            thread ?? ThreadId.New(),
            priority,
            schemaVersion,
            SentAt,
            deadline);

    private sealed record TestOrgMessage : OrgMessage
    {
        public TestOrgMessage(
            MessageId id,
            OrganizationId organizationId,
            EndpointRef from,
            EndpointRef to,
            ThreadId thread,
            Priority priority,
            int schemaVersion,
            DateTimeOffset sentAt,
            DateTimeOffset? deadline)
            : base(
                id,
                organizationId,
                from,
                to,
                thread,
                priority,
                schemaVersion,
                sentAt,
                deadline)
        {
        }
    }
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~OrgMessageTests"
```

Expected: build fails with `CS0246` because `OrgMessage` does not exist.

- [x] **Step 3: Add the abstract common message record**

Create `src/Hive.Domain/Messaging/OrgMessage.cs`:

```csharp
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public abstract record OrgMessage
{
    protected OrgMessage(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(thread);

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be greater than zero.");
        }

        Id = id;
        OrganizationId = organizationId;
        From = from;
        To = to;
        Thread = thread;
        Priority = PriorityContract.RequireDefined(priority, nameof(priority));
        SchemaVersion = schemaVersion;
        SentAt = sentAt;
        Deadline = deadline;
    }

    public MessageId Id { get; }

    public OrganizationId OrganizationId { get; }

    public EndpointRef From { get; }

    public EndpointRef To { get; }

    public ThreadId Thread { get; }

    public Priority Priority { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset SentAt { get; }

    public DateTimeOffset? Deadline { get; }
}
```

- [x] **Step 4: Run the focused test and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~OrgMessageTests"
```

Expected: 8 test cases pass with zero failures.

- [x] **Step 5: Run all message-contract tests together**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessagePriorityTests|FullyQualifiedName~MessageEndpointTests|FullyQualifiedName~OrgMessageTests"
```

Expected: 32 test cases pass with zero failures.

---

### Task 4: Verify the repository and prepare the handoff

**Files:**
- Verify: `docs/bible.html`
- Verify: `docs/superpowers/specs/2026-06-21-us-f0-03-t02-message-envelope-design.md`
- Verify: `docs/superpowers/plans/2026-06-21-us-f0-03-t02-message-envelope.md`
- Verify: all source and test files created in Tasks 1–3

- [x] **Step 1: Run the full solution test suite**

Run:

```powershell
dotnet test Hive.sln --no-restore
```

Expected: every test project passes with zero failures.

- [x] **Step 2: Run a clean full solution build**

Run:

```powershell
dotnet build Hive.sln --no-restore
```

Expected: build succeeds with zero errors and no warnings introduced by this change.

- [x] **Step 3: Verify formatting, boundaries, and scope**

Run:

```powershell
git diff --check
git status --short
git diff --stat
```

Expected: no whitespace errors; only the approved design, this plan, messaging source files, and messaging tests are new or changed. `docs/bible.html` and `docs/configuration.md` remain unchanged by the implementation, and `Hive.Domain` has no new project or package references.

- [x] **Step 4: Re-read requirements against the final diff**

Check the final diff against these requirements:

```text
T03a: Low=1, Normal=2, High=3, Critical=4; canonical lowercase wire values; explicit ordering and controlled rejection.
T02: OrganizationId, MessageId, typed From/To endpoints, ThreadId, Priority, SchemaVersion, SentAt, and optional Deadline.
Endpoints: Position, OrganizationOwner, Scheduler, and DomainEvents only.
Deferred: T03b types, concrete messages, routing matrix, cross-field validation, serialization, Akka mailbox configuration.
```

Expected: every included requirement maps to a source file and test; every deferred item is absent.

- [x] **Step 5: Prepare the required commit summary without committing**

Provide this short English commit message to the user, adjusted only if verification reveals a narrower result:

```text
feat(domain): add the common organizational message envelope
```

Do not create a Git commit unless the user explicitly asks for one.
