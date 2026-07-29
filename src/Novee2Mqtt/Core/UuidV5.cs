using System.Security.Cryptography;
using System.Text;

namespace Novee2Mqtt.Core;

/// <summary>
/// RFC 4122 version 5 (SHA-1, name-based) UUIDs. The Govee app derives its
/// client id from the account email this way, and the bridge derives stable
/// entity ids for Tap-to-Run scenes the same way, so both must match the
/// original implementation exactly.
/// </summary>
public static class UuidV5
{
    public static readonly Guid NamespaceDns = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    public static Guid Create(Guid namespaceId, string name)
        => Create(namespaceId, Encoding.UTF8.GetBytes(name));

    public static Guid Create(Guid namespaceId, ReadOnlySpan<byte> name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        WriteBigEndian(namespaceId, namespaceBytes);

        var buffer = new byte[16 + name.Length];
        namespaceBytes.CopyTo(buffer);
        name.CopyTo(buffer.AsSpan(16));

        var hash = SHA1.HashData(buffer);

        Span<byte> result = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(result);

        result[6] = (byte)((result[6] & 0x0f) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3f) | 0x80); // RFC 4122 variant

        return ReadBigEndian(result);
    }

    /// <summary>Hyphen-free lowercase form.</summary>
    public static string CreateSimple(Guid namespaceId, string name) => Create(namespaceId, name).ToString("N");

    private static void WriteBigEndian(Guid guid, Span<byte> destination)
    {
        guid.TryWriteBytes(destination);
        // Guid's in-memory layout is little-endian for the first three fields.
        (destination[0], destination[3]) = (destination[3], destination[0]);
        (destination[1], destination[2]) = (destination[2], destination[1]);
        (destination[4], destination[5]) = (destination[5], destination[4]);
        (destination[6], destination[7]) = (destination[7], destination[6]);
    }

    private static Guid ReadBigEndian(Span<byte> bytes)
    {
        Span<byte> copy = stackalloc byte[16];
        bytes.CopyTo(copy);
        (copy[0], copy[3]) = (copy[3], copy[0]);
        (copy[1], copy[2]) = (copy[2], copy[1]);
        (copy[4], copy[5]) = (copy[5], copy[4]);
        (copy[6], copy[7]) = (copy[7], copy[6]);
        return new Guid(copy);
    }
}
