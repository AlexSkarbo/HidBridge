using System.Buffers;

namespace HidControl.Core;

// SLIP framing helpers for UART transport.
/// <summary>
/// Core model for Slip.
/// </summary>
public static class Slip
{
    private const byte End = 0xC0;
    private const byte Esc = 0xDB;
    private const byte EscEnd = 0xDC;
    private const byte EscEsc = 0xDD;

    /// <summary>
    /// Executes Encode.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <returns>Result.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var writer = new ArrayBufferWriter<byte>(payload.Length * 2 + 2);
        writer.GetSpan(1)[0] = End;
        writer.Advance(1);

        foreach (byte b0 in payload)
        {
            if (b0 == End)
            {
                var span = writer.GetSpan(2);
                span[0] = Esc;
                span[1] = EscEnd;
                writer.Advance(2);
                continue;
            }

            if (b0 == Esc)
            {
                var span = writer.GetSpan(2);
                span[0] = Esc;
                span[1] = EscEsc;
                writer.Advance(2);
                continue;
            }

            writer.GetSpan(1)[0] = b0;
            writer.Advance(1);
        }

        writer.GetSpan(1)[0] = End;
        writer.Advance(1);

        return writer.WrittenSpan.ToArray();
    }
}
