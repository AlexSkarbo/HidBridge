using System.Text;
using HidBridge.Transport.Uart;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies deterministic UART frame codec behavior (frame shape, CRC, HMAC, alternate key path).
/// </summary>
public sealed class UartFrameCodecTests
{
    /// <summary>
    /// Ensures one frame built by codec can be parsed back with the same key.
    /// </summary>
    [Fact]
    public void BuildFrame_ThenParse_RoundTrips()
    {
        var key = Encoding.UTF8.GetBytes("your-master-secret");
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var frame = UartFrameCodec.BuildFrame(seq: 0x5A, cmd: 0x01, flags: 0x00, payload, key);

        var ok = UartFrameCodec.TryParseFrame(frame, key, alternateHmacKey: null, out var parsed);

        Assert.True(ok);
        Assert.Equal(0x5A, parsed.Seq);
        Assert.Equal(0x01, parsed.Cmd);
        Assert.Equal(0x00, parsed.Flags);
        Assert.Equal(payload, parsed.Payload);
        Assert.False(parsed.UsedAlternateHmacKey);
    }

    /// <summary>
    /// Ensures parser rejects frames with invalid CRC checksum bytes.
    /// </summary>
    [Fact]
    public void TryParseFrame_InvalidCrc_ReturnsFalse()
    {
        var key = Encoding.UTF8.GetBytes("your-master-secret");
        var frame = UartFrameCodec.BuildFrame(seq: 0x01, cmd: 0x02, flags: 0x00, new byte[] { 0xAA, 0xBB }, key);
        frame[UartFrameCodec.HeaderLen] ^= 0x01;

        var ok = UartFrameCodec.TryParseFrame(frame, key, alternateHmacKey: null, out _);

        Assert.False(ok);
    }

    /// <summary>
    /// Ensures parser rejects frames with invalid HMAC segment.
    /// </summary>
    [Fact]
    public void TryParseFrame_InvalidHmac_ReturnsFalse()
    {
        var key = Encoding.UTF8.GetBytes("your-master-secret");
        var frame = UartFrameCodec.BuildFrame(seq: 0x01, cmd: 0x02, flags: 0x00, new byte[] { 0xAA, 0xBB }, key);
        frame[^1] ^= 0x01;

        var ok = UartFrameCodec.TryParseFrame(frame, key, alternateHmacKey: null, out _);

        Assert.False(ok);
    }

    /// <summary>
    /// Ensures parser accepts frame signed by alternate key when fallback key is provided.
    /// </summary>
    [Fact]
    public void TryParseFrame_AlternateHmacKey_SucceedsAndMarksFallback()
    {
        var primaryKey = Encoding.UTF8.GetBytes("primary-key");
        var alternateKey = Encoding.UTF8.GetBytes("alternate-key");
        var frame = UartFrameCodec.BuildFrame(seq: 0x03, cmd: 0x09, flags: 0x80, new byte[] { 0x01 }, alternateKey);

        var ok = UartFrameCodec.TryParseFrame(frame, primaryKey, alternateKey, out var parsed);

        Assert.True(ok);
        Assert.True(parsed.UsedAlternateHmacKey);
        Assert.Equal(0x80, parsed.Flags);
    }
}
