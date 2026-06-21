# US-F0-03-T03 Shared Protocol Types Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the four closed shared protocol types and the canonical message-state transition contract defined by `docs/bible.html` §9.5.

**Architecture:** Each public enum and its public validation/wire contract live in a focused file under `Hive.Domain.Messaging`. A small internal generic helper centralizes strict enum validation and exact ordinal wire parsing; `MessageStateContract` additionally owns the closed transition matrix. The immutable `OrgMessage` envelope remains unchanged because channel and processing state are derived or persisted separately.

**Tech Stack:** .NET 8, C# records/enums, xUnit

---

### Task 1: Message channel contract and shared wire helper

**Files:**
- Create: `tests/Hive.Tests/MessageChannelTests.cs`
- Create: `src/Hive.Domain/Messaging/ProtocolEnumWireContract.cs`
- Create: `src/Hive.Domain/Messaging/MessageChannel.cs`

- [x] **Step 1: Write the failing channel tests**

Create `MessageChannelTests.cs` with enum-rank, canonical wire round-trip, invalid wire input, null input, and undefined in-memory value tests following the existing `MessagePriorityTests` structure:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageChannelTests
{
    [Fact]
    public void Channels_have_stable_values()
    {
        Assert.Equal(1, (int)MessageChannel.Vertical);
        Assert.Equal(2, (int)MessageChannel.Horizontal);
        Assert.Equal(3, (int)MessageChannel.Governance);
        Assert.Equal(4, (int)MessageChannel.System);
        Assert.Equal(
            [MessageChannel.Vertical, MessageChannel.Horizontal,
             MessageChannel.Governance, MessageChannel.System],
            Enum.GetValues<MessageChannel>());
    }

    [Theory]
    [InlineData(MessageChannel.Vertical, "vertical")]
    [InlineData(MessageChannel.Horizontal, "horizontal")]
    [InlineData(MessageChannel.Governance, "governance")]
    [InlineData(MessageChannel.System, "system")]
    public void Wire_values_round_trip_canonically(MessageChannel value, string wireValue)
    {
        Assert.Equal(wireValue, MessageChannelContract.ToWireValue(value));
        Assert.Equal(value, MessageChannelContract.ParseWireValue(wireValue));
        Assert.True(MessageChannelContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vertical ")]
    [InlineData("Vertical")]
    [InlineData("1")]
    [InlineData("external")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => MessageChannelContract.ParseWireValue(value));
        Assert.False(MessageChannelContract.TryParseWireValue(value, out _));
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => MessageChannelContract.ParseWireValue(null!));
        Assert.False(MessageChannelContract.TryParseWireValue(null, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (MessageChannel)rawValue;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageChannelContract.RequireDefined(value, "channel"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageChannelContract.ToWireValue(value));
    }
}
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageChannelTests -v minimal`

Expected: compilation fails because `MessageChannel` and `MessageChannelContract` do not exist.

- [x] **Step 3: Implement the minimal channel contract**

Create `ProtocolEnumWireContract.cs`:

```csharp
namespace Hive.Domain.Messaging;

internal sealed class ProtocolEnumWireContract<TEnum>
    where TEnum : struct, Enum
{
    private readonly IReadOnlyDictionary<TEnum, string> _wireByValue;
    private readonly IReadOnlyDictionary<string, TEnum> _valueByWire;

    public ProtocolEnumWireContract(params (TEnum Value, string WireValue)[] values)
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
```

Create `MessageChannel.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum MessageChannel
{
    Vertical = 1,
    Horizontal = 2,
    Governance = 3,
    System = 4,
}

public static class MessageChannelContract
{
    private static readonly ProtocolEnumWireContract<MessageChannel> Contract = new(
        (MessageChannel.Vertical, "vertical"),
        (MessageChannel.Horizontal, "horizontal"),
        (MessageChannel.Governance, "governance"),
        (MessageChannel.System, "system"));

    public static MessageChannel RequireDefined(MessageChannel value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(MessageChannel value) => Contract.ToWireValue(value);

    public static MessageChannel ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out MessageChannel channel) =>
        Contract.TryParseWireValue(value, out channel);
}
```

- [x] **Step 4: Run the focused tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageChannelTests -v minimal`

Expected: all `MessageChannelTests` pass.

### Task 2: Message lifecycle contract

**Files:**
- Create: `tests/Hive.Tests/MessageStateTests.cs`
- Create: `src/Hive.Domain/Messaging/MessageState.cs`

- [x] **Step 1: Write the failing lifecycle tests**

Create `MessageStateTests.cs`:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MessageStateTests
{
    [Fact]
    public void States_have_stable_values()
    {
        Assert.Equal(1, (int)MessageState.Received);
        Assert.Equal(2, (int)MessageState.Accepted);
        Assert.Equal(3, (int)MessageState.Processing);
        Assert.Equal(4, (int)MessageState.Completed);
        Assert.Equal(5, (int)MessageState.Rejected);
        Assert.Equal(6, (int)MessageState.Failed);
        Assert.Equal(
            [MessageState.Received, MessageState.Accepted, MessageState.Processing,
             MessageState.Completed, MessageState.Rejected, MessageState.Failed],
            Enum.GetValues<MessageState>());
    }

    [Theory]
    [InlineData(MessageState.Received, "received")]
    [InlineData(MessageState.Accepted, "accepted")]
    [InlineData(MessageState.Processing, "processing")]
    [InlineData(MessageState.Completed, "completed")]
    [InlineData(MessageState.Rejected, "rejected")]
    [InlineData(MessageState.Failed, "failed")]
    public void Wire_values_round_trip_canonically(MessageState value, string wireValue)
    {
        Assert.Equal(wireValue, MessageStateContract.ToWireValue(value));
        Assert.Equal(value, MessageStateContract.ParseWireValue(wireValue));
        Assert.True(MessageStateContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("received ")]
    [InlineData("Received")]
    [InlineData("1")]
    [InlineData("pending")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => MessageStateContract.ParseWireValue(value));
        Assert.False(MessageStateContract.TryParseWireValue(value, out _));
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => MessageStateContract.ParseWireValue(null!));
        Assert.False(MessageStateContract.TryParseWireValue(null, out _));
    }

    [Fact]
    public void Transition_matrix_contains_only_canonical_transitions()
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
                Assert.Equal(
                    allowed.Contains((from, to)),
                    MessageStateContract.CanTransition(from, to));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (MessageState)rawValue;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.RequireDefined(value, "state"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.ToWireValue(value));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.CanTransition(value, MessageState.Accepted));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageStateContract.CanTransition(MessageState.Received, value));
    }
}
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageStateTests -v minimal`

Expected: compilation fails because `MessageState` and `MessageStateContract` do not exist.

- [x] **Step 3: Implement the minimal lifecycle contract**

Create `MessageState.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum MessageState
{
    Received = 1,
    Accepted = 2,
    Processing = 3,
    Completed = 4,
    Rejected = 5,
    Failed = 6,
}

public static class MessageStateContract
{
    private static readonly ProtocolEnumWireContract<MessageState> Contract = new(
        (MessageState.Received, "received"),
        (MessageState.Accepted, "accepted"),
        (MessageState.Processing, "processing"),
        (MessageState.Completed, "completed"),
        (MessageState.Rejected, "rejected"),
        (MessageState.Failed, "failed"));

    private static readonly HashSet<(MessageState From, MessageState To)> AllowedTransitions =
    [
        (MessageState.Received, MessageState.Rejected),
        (MessageState.Received, MessageState.Accepted),
        (MessageState.Accepted, MessageState.Processing),
        (MessageState.Processing, MessageState.Completed),
        (MessageState.Processing, MessageState.Failed),
    ];

    public static MessageState RequireDefined(MessageState value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(MessageState value) => Contract.ToWireValue(value);

    public static MessageState ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out MessageState state) =>
        Contract.TryParseWireValue(value, out state);

public static bool CanTransition(MessageState from, MessageState to)
{
    Contract.RequireDefined(from, nameof(from));
    Contract.RequireDefined(to, nameof(to));
    return AllowedTransitions.Contains((from, to));
}
}
```

- [x] **Step 4: Run the focused tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~MessageStateTests -v minimal`

Expected: all `MessageStateTests` pass.

### Task 3: Rejection reason contract

**Files:**
- Create: `tests/Hive.Tests/RejectionReasonTests.cs`
- Create: `src/Hive.Domain/Messaging/RejectionReason.cs`

- [x] **Step 1: Write the failing rejection-reason tests**

Create `RejectionReasonTests.cs`:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RejectionReasonTests
{
    [Fact]
    public void Reasons_have_stable_values()
    {
        Assert.Equal(1, (int)RejectionReason.InvalidContract);
        Assert.Equal(2, (int)RejectionReason.UnsupportedSchemaVersion);
        Assert.Equal(3, (int)RejectionReason.InvalidRoute);
        Assert.Equal(4, (int)RejectionReason.Unauthorized);
        Assert.Equal(5, (int)RejectionReason.Duplicate);
        Assert.Equal(6, (int)RejectionReason.Expired);
        Assert.Equal(
            [RejectionReason.InvalidContract, RejectionReason.UnsupportedSchemaVersion,
             RejectionReason.InvalidRoute, RejectionReason.Unauthorized,
             RejectionReason.Duplicate, RejectionReason.Expired],
            Enum.GetValues<RejectionReason>());
    }

    [Theory]
    [InlineData(RejectionReason.InvalidContract, "invalid-contract")]
    [InlineData(RejectionReason.UnsupportedSchemaVersion, "unsupported-schema-version")]
    [InlineData(RejectionReason.InvalidRoute, "invalid-route")]
    [InlineData(RejectionReason.Unauthorized, "unauthorized")]
    [InlineData(RejectionReason.Duplicate, "duplicate")]
    [InlineData(RejectionReason.Expired, "expired")]
    public void Wire_values_round_trip_canonically(RejectionReason value, string wireValue)
    {
        Assert.Equal(wireValue, RejectionReasonContract.ToWireValue(value));
        Assert.Equal(value, RejectionReasonContract.ParseWireValue(wireValue));
        Assert.True(RejectionReasonContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-contract ")]
    [InlineData("InvalidContract")]
    [InlineData("invalidContract")]
    [InlineData("invalid_contract")]
    [InlineData("1")]
    [InlineData("unknown")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => RejectionReasonContract.ParseWireValue(value));
        Assert.False(RejectionReasonContract.TryParseWireValue(value, out _));
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => RejectionReasonContract.ParseWireValue(null!));
        Assert.False(RejectionReasonContract.TryParseWireValue(null, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (RejectionReason)rawValue;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RejectionReasonContract.RequireDefined(value, "reason"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RejectionReasonContract.ToWireValue(value));
    }
}
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~RejectionReasonTests -v minimal`

Expected: compilation fails because `RejectionReason` and `RejectionReasonContract` do not exist.

- [x] **Step 3: Implement the minimal rejection-reason contract**

Create `RejectionReason.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum RejectionReason
{
    InvalidContract = 1,
    UnsupportedSchemaVersion = 2,
    InvalidRoute = 3,
    Unauthorized = 4,
    Duplicate = 5,
    Expired = 6,
}

public static class RejectionReasonContract
{
private static readonly ProtocolEnumWireContract<RejectionReason> Contract = new(
    (RejectionReason.InvalidContract, "invalid-contract"),
    (RejectionReason.UnsupportedSchemaVersion, "unsupported-schema-version"),
    (RejectionReason.InvalidRoute, "invalid-route"),
    (RejectionReason.Unauthorized, "unauthorized"),
    (RejectionReason.Duplicate, "duplicate"),
    (RejectionReason.Expired, "expired"));

    public static RejectionReason RequireDefined(RejectionReason value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(RejectionReason value) => Contract.ToWireValue(value);

    public static RejectionReason ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out RejectionReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}
```

- [x] **Step 4: Run the focused tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~RejectionReasonTests -v minimal`

Expected: all `RejectionReasonTests` pass.

### Task 4: Report kind contract

**Files:**
- Create: `tests/Hive.Tests/ReportKindTests.cs`
- Create: `src/Hive.Domain/Messaging/ReportKind.cs`

- [x] **Step 1: Write the failing report-kind tests**

Create `ReportKindTests.cs`:

```csharp
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ReportKindTests
{
    [Fact]
    public void Kinds_have_stable_values()
    {
        Assert.Equal(1, (int)ReportKind.Progress);
        Assert.Equal(2, (int)ReportKind.Done);
        Assert.Equal([ReportKind.Progress, ReportKind.Done], Enum.GetValues<ReportKind>());
    }

    [Theory]
    [InlineData(ReportKind.Progress, "progress")]
    [InlineData(ReportKind.Done, "done")]
    public void Wire_values_round_trip_canonically(ReportKind value, string wireValue)
    {
        Assert.Equal(wireValue, ReportKindContract.ToWireValue(value));
        Assert.Equal(value, ReportKindContract.ParseWireValue(wireValue));
        Assert.True(ReportKindContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("progress ")]
    [InlineData("Progress")]
    [InlineData("1")]
    [InlineData("blocked")]
    public void Wire_parsing_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ReportKindContract.ParseWireValue(value));
        Assert.False(ReportKindContract.TryParseWireValue(value, out _));
    }

    [Fact]
    public void Wire_parsing_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ReportKindContract.ParseWireValue(null!));
        Assert.False(ReportKindContract.TryParseWireValue(null, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Operations_reject_undefined_in_memory_values(int rawValue)
    {
        var value = (ReportKind)rawValue;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportKindContract.RequireDefined(value, "kind"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportKindContract.ToWireValue(value));
    }
}
```

- [x] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ReportKindTests -v minimal`

Expected: compilation fails because `ReportKind` and `ReportKindContract` do not exist.

- [x] **Step 3: Implement the minimal report-kind contract**

Create `ReportKind.cs`:

```csharp
namespace Hive.Domain.Messaging;

public enum ReportKind
{
    Progress = 1,
    Done = 2,
}

public static class ReportKindContract
{
    private static readonly ProtocolEnumWireContract<ReportKind> Contract = new(
        (ReportKind.Progress, "progress"),
        (ReportKind.Done, "done"));

    public static ReportKind RequireDefined(ReportKind value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(ReportKind value) => Contract.ToWireValue(value);

    public static ReportKind ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out ReportKind kind) =>
        Contract.TryParseWireValue(value, out kind);
}
```

- [x] **Step 4: Run the focused tests and verify GREEN**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ReportKindTests -v minimal`

Expected: all `ReportKindTests` pass.

### Task 5: Regression and repository verification

**Files:**
- Verify all files created by Tasks 1–4.

- [x] **Step 1: Run all shared protocol tests together**

Run: `dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageChannelTests|FullyQualifiedName~MessageStateTests|FullyQualifiedName~RejectionReasonTests|FullyQualifiedName~ReportKindTests" -v minimal`

Expected: all focused protocol tests pass.

- [x] **Step 2: Run the complete test suite**

Run: `dotnet test Hive.sln --no-restore -v minimal`

Expected: all tests pass with zero failures.

- [x] **Step 3: Build the solution**

Run: `dotnet build Hive.sln --no-restore -v minimal`

Expected: build succeeds with zero warnings and zero errors.

- [x] **Step 4: Check formatting and diff hygiene**

Run: `dotnet format Hive.sln --no-restore --verify-no-changes`

Expected: exit code 0.

Run: `git diff --check`

Expected: no output and exit code 0.

- [x] **Step 5: Prepare the requested commit message without committing**

Use: `feat(domain): add shared message protocol types`
