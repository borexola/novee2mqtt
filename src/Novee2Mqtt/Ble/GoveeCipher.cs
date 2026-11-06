using System.Security.Cryptography;
using System.Text;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Ble;

/// <summary>
/// Govee's BLE frame cipher, used by firmware that reports encrypt version 1:
/// AES-ECB on the first 16 bytes of a frame and RC4 on the last 4, with the RC4
/// stream restarted for every frame. The key is a public constant during the
/// handshake and a device-chosen session key afterwards, so this is obfuscation
/// rather than security. Ported from the MIT-licensed
/// <see href="https://github.com/Brady-Woods/govee-ble-local">govee-ble-local</see>.
/// </summary>
public sealed class GoveeCipher
{
    /// <summary>The pre-shared key the handshake frames are encrypted with.</summary>
    public static readonly byte[] HandshakeKey = Encoding.ASCII.GetBytes("MakingLifeSmarte");

    public const byte HandshakeOp = 0xe7;
    public const byte RequestSessionKey = 0x01;
    public const byte ConfirmSession = 0x02;

    private const int FrameLength = 20;
    private const int BlockLength = 16;

    private readonly byte[] _key;

    public GoveeCipher(byte[] key)
    {
        if (key.Length != BlockLength)
        {
            throw new GoveeException($"a Govee cipher key is {BlockLength} bytes, got {key.Length}");
        }
        _key = key;
    }

    public byte[] Encrypt(byte[] frame) => Transform(frame, encrypt: true);

    public byte[] Decrypt(byte[] frame) => Transform(frame, encrypt: false);

    private byte[] Transform(byte[] frame, bool encrypt)
    {
        if (frame.Length != FrameLength)
        {
            throw new GoveeException($"a Govee frame is {FrameLength} bytes, got {frame.Length}");
        }

        using var aes = Aes.Create();
        aes.Key = _key;

        var block = frame.AsSpan(0, BlockLength);
        var head = encrypt
            ? aes.EncryptEcb(block, PaddingMode.None)
            : aes.DecryptEcb(block, PaddingMode.None);

        var output = new byte[FrameLength];
        head.CopyTo(output, 0);
        Rc4(_key, frame.AsSpan(BlockLength), output.AsSpan(BlockLength));
        return output;
    }

    private static void Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> state = stackalloc byte[256];
        for (var n = 0; n < 256; n++)
        {
            state[n] = (byte)n;
        }

        var j = 0;
        for (var n = 0; n < 256; n++)
        {
            j = (j + state[n] + key[n % key.Length]) & 0xff;
            (state[n], state[j]) = (state[j], state[n]);
        }

        int a = 0, b = 0;
        for (var n = 0; n < input.Length; n++)
        {
            a = (a + 1) & 0xff;
            b = (b + state[a]) & 0xff;
            (state[a], state[b]) = (state[b], state[a]);
            output[n] = (byte)(input[n] ^ state[(state[a] + state[b]) & 0xff]);
        }
    }

    /// <summary>
    /// One PSK-encrypted handshake request: <c>e7 step</c>, random filler, and
    /// the XOR checksum. The device ignores the filler.
    /// </summary>
    public static byte[] BuildHandshakeFrame(byte step)
    {
        Span<byte> body = stackalloc byte[19];
        body[0] = HandshakeOp;
        body[1] = step;
        RandomNumberGenerator.Fill(body[2..]);
        return new GoveeCipher(HandshakeKey).Encrypt(PacketManager.Frame(body));
    }

    /// <summary>The session key from an encrypted <c>e7 01</c> reply, or null for any other frame.</summary>
    public static byte[]? ParseSessionKey(byte[] reply)
    {
        if (reply.Length != FrameLength)
        {
            return null;
        }
        var plain = new GoveeCipher(HandshakeKey).Decrypt(reply);
        return plain[0] == HandshakeOp && plain[1] == RequestSessionKey ? plain[2..18] : null;
    }

    public static bool IsConfirmReply(byte[] reply)
    {
        if (reply.Length != FrameLength)
        {
            return false;
        }
        var plain = new GoveeCipher(HandshakeKey).Decrypt(reply);
        return plain[0] == HandshakeOp && plain[1] == ConfirmSession;
    }
}
