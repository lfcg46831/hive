using System.Security.Cryptography;
using System.Text;

namespace Hive.DemoClient;

public sealed record DemoDirectiveIds(
    Guid MessageId,
    Guid ThreadId,
    Guid DirectiveId)
{
    public static DemoDirectiveIds New() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    public static DemoDirectiveIds FromSeed(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new ArgumentException(
                "A non-empty demo seed is required.",
                nameof(seed));
        }

        return new DemoDirectiveIds(
            CreateGuid(seed, "message"),
            CreateGuid(seed, "thread"),
            CreateGuid(seed, "directive"));
    }

    private static Guid CreateGuid(string seed, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
