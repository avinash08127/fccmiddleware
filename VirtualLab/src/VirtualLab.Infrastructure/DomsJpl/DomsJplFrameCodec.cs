using System.Text;

namespace VirtualLab.Infrastructure.DomsJpl;

/// <summary>
/// Binary STX/ETX frame codec matching the real DOMS JPL wire format.
/// Frame layout: [STX][JSON payload][ETX]
/// Heartbeat:    [STX][ETX]  (empty payload)
/// </summary>
public static class DomsJplFrameCodec
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;

    /// <summary>
    /// Wraps a JSON payload string in STX/ETX framing.
    /// </summary>
    public static byte[] Encode(string jsonPayload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
        byte[] frame = new byte[payloadBytes.Length + 2];
        frame[0] = Stx;
        Buffer.BlockCopy(payloadBytes, 0, frame, 1, payloadBytes.Length);
        frame[^1] = Etx;
        return frame;
    }

    /// <summary>
    /// Encodes a heartbeat frame (STX immediately followed by ETX, no payload).
    /// </summary>
    public static byte[] EncodeHeartbeat() => [Stx, Etx];

    /// <summary>
    /// Attempts to extract the next complete frame from a byte buffer.
    /// Returns <c>true</c> if a frame was found, with the JSON payload and
    /// an <paramref name="isHeartbeat"/> flag. Advances <paramref name="consumed"/>
    /// past the frame bytes so the caller can trim the buffer.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> buffer,
        out string payload,
        out bool isHeartbeat,
        out int consumed)
    {
        payload = string.Empty;
        isHeartbeat = false;
        consumed = 0;

        int stxIndex = buffer.IndexOf(Stx);
        if (stxIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> searchArea = buffer[(stxIndex + 1)..];
        int etxOffset = searchArea.IndexOf(Etx);
        if (etxOffset < 0)
        {
            return false;
        }

        // Frame found: stxIndex -> stxIndex + 1 + etxOffset (ETX position)
        consumed = stxIndex + 1 + etxOffset + 1; // bytes up to and including ETX

        if (etxOffset == 0)
        {
            // STX immediately followed by ETX -> heartbeat
            isHeartbeat = true;
            return true;
        }

        ReadOnlySpan<byte> payloadBytes = buffer.Slice(stxIndex + 1, etxOffset);
        payload = Encoding.UTF8.GetString(payloadBytes);
        isHeartbeat = false;
        return true;
    }

    /// <summary>
    /// Extracts all complete frames from a mutable buffer, returning each
    /// payload (or heartbeat marker). Removes consumed bytes from the list.
    /// </summary>
    public static IReadOnlyList<DecodedFrame> DecodeAll(ref byte[] buffer, ref int length)
    {
        List<DecodedFrame> frames = [];
        int offset = 0;

        while (offset < length)
        {
            ReadOnlySpan<byte> remaining = buffer.AsSpan(offset, length - offset);
            if (!TryDecode(remaining, out string payload, out bool isHeartbeat, out int consumed))
            {
                break;
            }

            frames.Add(new DecodedFrame(payload, isHeartbeat));
            offset += consumed;
        }

        if (offset > 0)
        {
            int remaining = length - offset;
            if (remaining > 0)
            {
                Buffer.BlockCopy(buffer, offset, buffer, 0, remaining);
            }

            length = remaining;
        }

        return frames;
    }
}

public readonly record struct DecodedFrame(string Payload, bool IsHeartbeat);
