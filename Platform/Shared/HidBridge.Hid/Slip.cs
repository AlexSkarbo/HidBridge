using System.Buffers;

namespace HidBridge.Hid;

/// <summary>
/// Provides SLIP framing used by the UART bridge transport.
/// </summary>
public static class Slip
{
    private const byte End = 0xC0;
    private const byte Esc = 0xDB;
    private const byte EscEnd = 0xDC;
    private const byte EscEsc = 0xDD;

    /// <summary>
    /// Encodes a payload into SLIP format.
    /// </summary>
    /// <param name="payload">The raw frame bytes to encode.</param>
    /// <returns>The encoded SLIP frame.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var writer = new ArrayBufferWriter<byte>(payload.Length * 2 + 2);
        writer.GetSpan(1)[0] = End;
        writer.Advance(1);

        foreach (var value in payload)
        {
            if (value == End)
            {
                var span = writer.GetSpan(2);
                span[0] = Esc;
                span[1] = EscEnd;
                writer.Advance(2);
                continue;
            }

            if (value == Esc)
            {
                var span = writer.GetSpan(2);
                span[0] = Esc;
                span[1] = EscEsc;
                writer.Advance(2);
                continue;
            }

            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }

        writer.GetSpan(1)[0] = End;
        writer.Advance(1);
        return writer.WrittenSpan.ToArray();
    }
}
