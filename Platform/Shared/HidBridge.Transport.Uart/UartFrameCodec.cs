using System.Security.Cryptography;

namespace HidBridge.Transport.Uart;

/// <summary>
/// Builds and parses UART transport frames used by bridge firmware protocol v2.
/// </summary>
internal static class UartFrameCodec
{
    internal const byte Magic = 0xF1;
    internal const byte Version = 0x01;
    internal const int HeaderLen = 6;
    internal const int CrcLen = 2;
    internal const int HmacLen = 16;

    /// <summary>
    /// Builds one UART frame with CRC16 and truncated HMAC.
    /// </summary>
    /// <param name="seq">Protocol sequence number.</param>
    /// <param name="cmd">Command identifier.</param>
    /// <param name="flags">Protocol flags byte.</param>
    /// <param name="payload">Raw payload bytes.</param>
    /// <param name="hmacKey">HMAC key used to sign frame bytes.</param>
    /// <returns>Encoded UART frame bytes without SLIP wrapping.</returns>
    internal static byte[] BuildFrame(byte seq, byte cmd, byte flags, ReadOnlySpan<byte> payload, byte[] hmacKey)
    {
        var payloadLen = payload.Length;
        var frame = new byte[HeaderLen + payloadLen + CrcLen + HmacLen];
        frame[0] = Magic;
        frame[1] = Version;
        frame[2] = flags;
        frame[3] = seq;
        frame[4] = cmd;
        frame[5] = (byte)payloadLen;
        if (payloadLen > 0)
        {
            payload.CopyTo(frame.AsSpan(HeaderLen));
        }

        var crc = ComputeCrc16Ccitt(frame.AsSpan(0, HeaderLen + payloadLen));
        frame[HeaderLen + payloadLen + 0] = (byte)(crc & 0xFF);
        frame[HeaderLen + payloadLen + 1] = (byte)(crc >> 8);

        using var hmac = new HMACSHA256(hmacKey);
        var hash = hmac.ComputeHash(frame.AsSpan(0, HeaderLen + payloadLen + CrcLen).ToArray());
        hash.AsSpan(0, HmacLen).CopyTo(frame.AsSpan(HeaderLen + payloadLen + CrcLen));
        return frame;
    }

    /// <summary>
    /// Parses one UART frame and validates CRC/HMAC with primary and optional alternate key.
    /// </summary>
    /// <param name="frame">Raw frame bytes without SLIP wrapping.</param>
    /// <param name="expectedHmacKey">Primary HMAC key expected for validation.</param>
    /// <param name="alternateHmacKey">Optional alternate key accepted for compatibility fallback.</param>
    /// <param name="parsedFrame">Parsed frame result when validation succeeds.</param>
    /// <returns><c>true</c> when frame is valid and parsed; otherwise <c>false</c>.</returns>
    internal static bool TryParseFrame(
        ReadOnlySpan<byte> frame,
        byte[] expectedHmacKey,
        byte[]? alternateHmacKey,
        out UartParsedFrame parsedFrame)
    {
        parsedFrame = default;
        if (frame.Length < HeaderLen + CrcLen + HmacLen)
        {
            return false;
        }

        if (frame[0] != Magic || frame[1] != Version)
        {
            return false;
        }

        var payloadLen = frame[5];
        if (frame.Length != HeaderLen + payloadLen + CrcLen + HmacLen)
        {
            return false;
        }

        var crc = ComputeCrc16Ccitt(frame[..(HeaderLen + payloadLen)]);
        var actualCrc = (ushort)(frame[HeaderLen + payloadLen] | (frame[HeaderLen + payloadLen + 1] << 8));
        if (crc != actualCrc)
        {
            return false;
        }

        var usedAlternateKey = false;
        if (!TryValidateHmac(frame, payloadLen, expectedHmacKey))
        {
            if (alternateHmacKey is null || !TryValidateHmac(frame, payloadLen, alternateHmacKey))
            {
                return false;
            }

            usedAlternateKey = true;
        }

        parsedFrame = new UartParsedFrame(
            frame[3],
            frame[4],
            frame[2],
            frame.Slice(HeaderLen, payloadLen).ToArray(),
            usedAlternateKey);
        return true;
    }

    /// <summary>
    /// Computes protocol CRC16-CCITT checksum over provided bytes.
    /// </summary>
    internal static ushort ComputeCrc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : (crc << 1));
            }
        }

        return crc;
    }

    /// <summary>
    /// Validates truncated HMAC segment for one frame.
    /// </summary>
    private static bool TryValidateHmac(ReadOnlySpan<byte> frame, int payloadLen, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(frame[..(HeaderLen + payloadLen + CrcLen)].ToArray());
        return hash.AsSpan(0, HmacLen).SequenceEqual(frame.Slice(HeaderLen + payloadLen + CrcLen, HmacLen));
    }
}

/// <summary>
/// Represents one parsed and validated UART frame.
/// </summary>
/// <param name="Seq">Protocol sequence number.</param>
/// <param name="Cmd">Echoed command identifier.</param>
/// <param name="Flags">Raw protocol flags byte.</param>
/// <param name="Payload">Decoded payload bytes.</param>
/// <param name="UsedAlternateHmacKey">Indicates fallback HMAC key validation path.</param>
internal readonly record struct UartParsedFrame(
    byte Seq,
    byte Cmd,
    byte Flags,
    byte[] Payload,
    bool UsedAlternateHmacKey);
