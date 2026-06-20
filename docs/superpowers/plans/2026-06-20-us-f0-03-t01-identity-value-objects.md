# US-F0-03-T01 Identity Value Objects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the seven immutable, strongly typed identity value objects defined by `US-F0-03-T01` and the approved contract in bible §9.1.

**Architecture:** Keep all identities in the dependency-free `Hive.Domain.Identity` namespace, with one public type per matching file as required by bible §5.11. Four structural IDs are sealed string-backed records validated through one internal helper; three runtime message IDs are sealed Guid-backed records with explicit creation and local generation. Tests exercise the public contract before production types are added.

**Tech Stack:** .NET 8, C# 12, xUnit 2.5.3, existing `Hive.Domain` and `Hive.Tests` projects.

**Approved source of truth:** `docs/bible.html` §9.1 already records representation, validation, equality, string formatting, and serialization boundaries. Do not duplicate those decisions in `docs/configuration.md`.

---

### Task 1: Structural identity value objects

**Files:**
- Create: `tests/Hive.Tests/StructuralIdentityTests.cs`
- Create: `src/Hive.Domain/Identity/IdentityValue.cs`
- Create: `src/Hive.Domain/Identity/OrganizationId.cs`
- Create: `src/Hive.Domain/Identity/UnitId.cs`
- Create: `src/Hive.Domain/Identity/PositionId.cs`
- Create: `src/Hive.Domain/Identity/OccupantId.cs`

- [x] **Step 1: Write the failing structural identity tests**

Create `tests/Hive.Tests/StructuralIdentityTests.cs`:

```csharp
using System.Reflection;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class StructuralIdentityTests
{
    [Fact]
    public void From_preserves_valid_structural_values()
    {
        Assert.Equal("acme", OrganizationId.From("acme").Value);
        Assert.Equal("engineering", UnitId.From("engineering").Value);
        Assert.Equal("bug-triage", PositionId.From("bug-triage").Value);
        Assert.Equal("agent-primary", OccupantId.From("agent-primary").Value);
    }

    [Fact]
    public void Structural_ids_compare_by_type_and_ordinal_value()
    {
        Assert.Equal(OrganizationId.From("acme"), OrganizationId.From("acme"));
        Assert.NotEqual(OrganizationId.From("acme"), OrganizationId.From("ACME"));
        Assert.NotEqual<object>(OrganizationId.From("acme"), UnitId.From("acme"));
    }

    [Fact]
    public void Structural_ids_reject_null()
    {
        foreach (var factory in StructuralFactories())
        {
            Assert.Throws<ArgumentNullException>(() => factory(null!));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" acme")]
    [InlineData("acme ")]
    public void Structural_ids_reject_non_canonical_values(string value)
    {
        foreach (var factory in StructuralFactories())
        {
            Assert.Throws<ArgumentException>(() => factory(value));
        }
    }

    [Fact]
    public void Structural_ids_render_the_canonical_value()
    {
        Assert.Equal("acme", OrganizationId.From("acme").ToString());
        Assert.Equal("engineering", UnitId.From("engineering").ToString());
        Assert.Equal("bug-triage", PositionId.From("bug-triage").ToString());
        Assert.Equal("agent-primary", OccupantId.From("agent-primary").ToString());
    }

    [Fact]
    public void Structural_ids_expose_no_implicit_conversions()
    {
        foreach (var type in StructuralTypes())
        {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    private static IEnumerable<Func<string, object>> StructuralFactories()
    {
        yield return value => OrganizationId.From(value);
        yield return value => UnitId.From(value);
        yield return value => PositionId.From(value);
        yield return value => OccupantId.From(value);
    }

    private static Type[] StructuralTypes() =>
    [
        typeof(OrganizationId),
        typeof(UnitId),
        typeof(PositionId),
        typeof(OccupantId),
    ];
}
```

- [x] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralIdentityTests"
```

Expected: build fails with `CS0234`/`CS0246` because `Hive.Domain.Identity` and the structural identity types do not exist.

- [x] **Step 3: Add the shared validation helper**

Create `src/Hive.Domain/Identity/IdentityValue.cs`:

```csharp
namespace Hive.Domain.Identity;

internal static class IdentityValue
{
    public static string RequireStructural(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identity value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Identity value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }

}
```

- [x] **Step 4: Add the four structural identity types**

Create each record below in its matching file under `src/Hive.Domain/Identity/` (`OrganizationId.cs`, `UnitId.cs`, `PositionId.cs`, and `OccupantId.cs`):

```csharp
namespace Hive.Domain.Identity;

public sealed record OrganizationId
{
    private OrganizationId(string value) => Value = value;

    public string Value { get; }

    public static OrganizationId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record UnitId
{
    private UnitId(string value) => Value = value;

    public string Value { get; }

    public static UnitId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record PositionId
{
    private PositionId(string value) => Value = value;

    public string Value { get; }

    public static PositionId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record OccupantId
{
    private OccupantId(string value) => Value = value;

    public string Value { get; }

    public static OccupantId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
```

- [x] **Step 5: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralIdentityTests"
```

Expected: 10 test cases pass with no failures.

---

### Task 2: Runtime message identity value objects

**Files:**
- Create: `tests/Hive.Tests/MessageIdentityTests.cs`
- Modify: `src/Hive.Domain/Identity/IdentityValue.cs`
- Create: `src/Hive.Domain/Identity/MessageId.cs`
- Create: `src/Hive.Domain/Identity/ThreadId.cs`
- Create: `src/Hive.Domain/Identity/DirectiveId.cs`

- [x] **Step 1: Write the failing runtime identity tests**

Create `tests/Hive.Tests/MessageIdentityTests.cs`:

```csharp
using System.Reflection;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class MessageIdentityTests
{
    private static readonly Guid KnownValue = Guid.Parse("67ee8e4f-a979-43d7-b30f-07e80da75b93");

    [Fact]
    public void From_preserves_non_empty_guid_values()
    {
        Assert.Equal(KnownValue, MessageId.From(KnownValue).Value);
        Assert.Equal(KnownValue, ThreadId.From(KnownValue).Value);
        Assert.Equal(KnownValue, DirectiveId.From(KnownValue).Value);
    }

    [Fact]
    public void Message_ids_compare_by_type_and_value()
    {
        Assert.Equal(MessageId.From(KnownValue), MessageId.From(KnownValue));
        Assert.NotEqual<object>(MessageId.From(KnownValue), ThreadId.From(KnownValue));
    }

    [Fact]
    public void Message_ids_reject_empty_guid_values()
    {
        Assert.Throws<ArgumentException>(() => MessageId.From(Guid.Empty));
        Assert.Throws<ArgumentException>(() => ThreadId.From(Guid.Empty));
        Assert.Throws<ArgumentException>(() => DirectiveId.From(Guid.Empty));
    }

    [Fact]
    public void New_generates_non_empty_distinct_values()
    {
        AssertGenerated(MessageId.New, id => id.Value);
        AssertGenerated(ThreadId.New, id => id.Value);
        AssertGenerated(DirectiveId.New, id => id.Value);
    }

    [Fact]
    public void Message_ids_render_guid_in_canonical_D_format()
    {
        var expected = KnownValue.ToString("D");

        Assert.Equal(expected, MessageId.From(KnownValue).ToString());
        Assert.Equal(expected, ThreadId.From(KnownValue).ToString());
        Assert.Equal(expected, DirectiveId.From(KnownValue).ToString());
    }

    [Fact]
    public void Message_ids_expose_no_implicit_conversions()
    {
        foreach (var type in MessageTypes())
        {
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    private static void AssertGenerated<T>(Func<T> create, Func<T, Guid> value)
    {
        var first = value(create());
        var second = value(create());

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(Guid.Empty, second);
        Assert.NotEqual(first, second);
    }

    private static Type[] MessageTypes() =>
    [
        typeof(MessageId),
        typeof(ThreadId),
        typeof(DirectiveId),
    ];
}
```

- [x] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageIdentityTests"
```

Expected: build fails with `CS0246` because the three runtime message identity types do not exist.

- [x] **Step 3: Extend the shared helper with runtime identity validation**

Add this method before the closing brace in `src/Hive.Domain/Identity/IdentityValue.cs`:

```csharp
    public static Guid RequireMessage(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identity value cannot be an empty Guid.", parameterName);
        }

        return value;
    }
```

- [x] **Step 4: Add the three runtime message identity types**

Create each record below in its matching file under `src/Hive.Domain/Identity/` (`MessageId.cs`, `ThreadId.cs`, and `DirectiveId.cs`):

```csharp
namespace Hive.Domain.Identity;

public sealed record MessageId
{
    private MessageId(Guid value) => Value = value;

    public Guid Value { get; }

    public static MessageId New() => From(Guid.NewGuid());

    public static MessageId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}

public sealed record ThreadId
{
    private ThreadId(Guid value) => Value = value;

    public Guid Value { get; }

    public static ThreadId New() => From(Guid.NewGuid());

    public static ThreadId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}

public sealed record DirectiveId
{
    private DirectiveId(Guid value) => Value = value;

    public Guid Value { get; }

    public static DirectiveId New() => From(Guid.NewGuid());

    public static DirectiveId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
```

- [x] **Step 5: Run the focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageIdentityTests"
```

Expected: 6 tests pass with no failures.

- [x] **Step 6: Run all identity tests together**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~IdentityTests"
```

Expected: 16 test cases pass with no failures.

---

### Task 3: Repository verification and handoff

**Files:**
- Verify: `docs/bible.html`
- Verify: all source and test changes from Tasks 1–2

- [x] **Step 1: Verify the full solution test suite**

Run:

```powershell
dotnet test Hive.sln --no-restore
```

Expected: all test projects pass with zero failures.

- [x] **Step 2: Verify a clean full solution build**

Run:

```powershell
dotnet build Hive.sln --no-restore
```

Expected: build succeeds with zero errors and zero warnings introduced by this change.

- [x] **Step 3: Verify formatting and scope**

Run:

```powershell
git diff --check
git status --short
git diff --stat
```

Expected: no whitespace errors; only the approved bible contract, this plan, identity source files, and identity tests are changed.

- [x] **Step 4: Prepare the required commit summary without committing**

Provide this short English commit message to the user, adjusted only if verification reveals a narrower outcome:

```text
feat(domain): add strongly typed identity value objects for US-F0-03-T01
```

Do not create a Git commit unless the user explicitly asks for one.
