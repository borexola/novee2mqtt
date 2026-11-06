using System.Security.Cryptography;
using Novee2Mqtt.Core;

namespace Novee2Mqtt.Ble;

/// <summary>
/// Govee's newer BLE session cipher, used by firmware that reports encrypt
/// version 2. The handshake exchanges an 8-byte IV key in each direction and
/// the device's identity, from which the session key is derived. Every frame
/// then travels as a 4-byte counter, the AES-GCM ciphertext and a 16-byte tag,
/// so a 20-byte frame becomes a 40-byte packet.
///
/// The two keys are constants compiled into the vendor app and shipped in every
/// device, so they are not secrets. Ported from the MIT-licensed
/// <see href="https://github.com/lasswellt/govee-homeassistant">govee-homeassistant</see>,
/// which confirmed the scheme on an H1270.
/// </summary>
public sealed class GoveeGcmCipher : IBleCipher
{
    private const int IvLength = 12;
    private const int IvKeyLength = 8;
    private const int CounterLength = 4;
    private const int TagLength = 16;
    private const int DeviceInfoLength = 11;
    private const int SkuLength = 5;

    private static readonly byte[] HandshakeKey = Convert.FromHexString("fc03783c7c42cb83e202a1643648aff6");
    private static readonly byte[] DeviceKey = Convert.FromHexString("ae028b630bae6ecc4bff1b249e22f955");

    private static readonly byte[] RequestHeader = [GoveeCipher.HandshakeOp, 0x11, 0x01];
    private static readonly byte[] ResponseHeader = [GoveeCipher.HandshakeOp, 0x11, 0x00];

    private readonly byte[] _sessionKey;
    private readonly byte[] _txIvKey;
    private readonly byte[] _rxIvKey;
    private uint _txCounter;

    public GoveeGcmCipher(byte[] sessionKey, byte[] txIvKey, byte[] rxIvKey)
    {
        if (txIvKey.Length != IvKeyLength || rxIvKey.Length != IvKeyLength)
        {
            throw new GoveeException($"an IV key is {IvKeyLength} bytes");
        }
        _sessionKey = sessionKey;
        _txIvKey = txIvKey;
        _rxIvKey = rxIvKey;
    }

    public static byte[] NewIv() => RandomNumberGenerator.GetBytes(IvLength);

    public static byte[] NewIvKey() => RandomNumberGenerator.GetBytes(IvKeyLength);

    /// <summary>Counters start at 1 on each connection, so a cipher must not outlive its link.</summary>
    public byte[] Wrap(byte[] frame)
    {
        _txCounter++;
        var counter = new byte[CounterLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(counter, _txCounter);

        var packet = new byte[CounterLength + frame.Length + TagLength];
        counter.CopyTo(packet, 0);

        using var aes = new AesGcm(_sessionKey, TagLength);
        aes.Encrypt(
            Nonce(_txIvKey, counter),
            frame,
            packet.AsSpan(CounterLength, frame.Length),
            packet.AsSpan(CounterLength + frame.Length, TagLength),
            counter);
        return packet;
    }

    /// <summary>The device carries its counter in each packet, so no receive state is needed.</summary>
    public byte[]? Unwrap(byte[] packet)
    {
        if (packet.Length <= CounterLength + TagLength)
        {
            return null;
        }

        var counter = packet.AsSpan(0, CounterLength);
        var length = packet.Length - CounterLength - TagLength;
        var frame = new byte[length];

        try
        {
            using var aes = new AesGcm(_sessionKey, TagLength);
            aes.Decrypt(
                Nonce(_rxIvKey, counter),
                packet.AsSpan(CounterLength, length),
                packet.AsSpan(CounterLength + length, TagLength),
                frame,
                counter);
            return frame;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null; // the device also sends packets outside this session
        }
    }

    private static byte[] Nonce(ReadOnlySpan<byte> ivKey, ReadOnlySpan<byte> counter)
    {
        var nonce = new byte[IvLength];
        ivKey.CopyTo(nonce);
        counter.CopyTo(nonce.AsSpan(IvKeyLength));
        return nonce;
    }

    /// <summary><c>e7 11 01 iv 10</c>, then our IV key sealed under the handshake key.</summary>
    public static byte[] BuildHandshakeRequest(byte[] iv, byte[] txIvKey)
    {
        var header = new byte[RequestHeader.Length + IvLength + 1];
        RequestHeader.CopyTo(header, 0);
        iv.CopyTo(header, RequestHeader.Length);
        header[^1] = 0x10;

        var request = new byte[header.Length + txIvKey.Length + TagLength];
        header.CopyTo(request, 0);

        using var aes = new AesGcm(HandshakeKey, TagLength);
        aes.Encrypt(
            iv,
            txIvKey,
            request.AsSpan(header.Length, txIvKey.Length),
            request.AsSpan(header.Length + txIvKey.Length, TagLength),
            header);
        return request;
    }

    /// <summary>
    /// The device's IV key and identity from an <c>e7 11 00</c> reply, or null
    /// for any other packet or one that fails authentication.
    /// </summary>
    public static (byte[] RxIvKey, byte[] DeviceInfo)? ParseHandshakeResponse(byte[] reply)
    {
        var headerLength = ResponseHeader.Length + IvLength;
        var plainLength = reply.Length - headerLength - TagLength;
        if (plainLength <= IvKeyLength || !reply.AsSpan().StartsWith(ResponseHeader))
        {
            return null;
        }

        var plain = new byte[plainLength];
        try
        {
            using var aes = new AesGcm(HandshakeKey, TagLength);
            aes.Decrypt(
                reply.AsSpan(ResponseHeader.Length, IvLength),
                reply.AsSpan(headerLength, plainLength),
                reply.AsSpan(headerLength + plainLength, TagLength),
                plain,
                reply.AsSpan(0, headerLength));
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        return (plain[..IvKeyLength], plain[IvKeyLength..]);
    }

    /// <summary>AES-ECB of the device's identity, padded to one block, under the device key.</summary>
    public static byte[] DeriveSessionKey(byte[] deviceInfo)
    {
        using var aes = Aes.Create();
        aes.Key = DeviceKey;
        var block = new byte[16];
        deviceInfo.AsSpan(0, Math.Min(deviceInfo.Length, block.Length)).CopyTo(block);
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    /// <summary>
    /// The reply holds the SKU then the Bluetooth address reversed, so a match
    /// proves the session is with the device that was dialled.
    /// </summary>
    public static bool IdentityMatches(byte[] deviceInfo, string address)
    {
        if (deviceInfo.Length < DeviceInfoLength)
        {
            return false;
        }
        var mac = Convert.FromHexString(address.Replace(":", ""));
        Array.Reverse(mac);
        return deviceInfo.AsSpan(SkuLength, mac.Length).SequenceEqual(mac);
    }
}
