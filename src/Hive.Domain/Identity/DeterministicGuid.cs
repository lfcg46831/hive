using System.Security.Cryptography;
using System.Text;

namespace Hive.Domain.Identity;

public static class DeterministicGuid
{
    public static Guid FromName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Deterministic GUID name cannot be empty or whitespace.", nameof(value));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
