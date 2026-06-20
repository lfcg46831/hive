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
