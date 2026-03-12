using System.Text;

namespace FccDesktopAgent.Core.Adapter.Doms.Jpl;

/// <summary>
/// STX/ETX binary frame codec for DOMS JPL protocol.
/// Uses ReadOnlySpan&lt;byte&gt; for zero-allocation decoding where possible.
/// Frame layout: [STX][JSON payload bytes][ETX]
/// Heartbeat frame: [STX][ETX] (empty payload)
/// </summary>
public static class JplFrameCodec
{
    public const byte STX = 0x02;
    public const byte ETX = 0x03;

    /// <summary>Encode a JSON payload into a JPL binary frame: [STX][payload][ETX].</summary>
    public static byte[] Encode(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        var payloadBytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payloadBytes.Length + 2];
        frame[0] = STX;
        payloadBytes.CopyTo(frame, 1);
        frame[^1] = ETX;
        return frame;
    }

    /// <summary>Encode a heartbeat (keep-alive) frame: [STX][ETX].</summary>
    public static byte[] EncodeHeartbeat() => [STX, ETX];

    /// <summary>
    /// Decode one frame from a byte buffer.
    /// Returns the decode result and how many bytes were consumed.
    /// Scans forward to find STX, then reads until ETX is found.
    /// </summary>
    public static DecodeResult Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            return new DecodeResult.Incomplete(BytesConsumed: 0);

        // Scan forward to find STX, skipping any garbage bytes before it.
        var stxIndex = buffer.IndexOf(STX);
        if (stxIndex < 0)
            return new DecodeResult.Incomplete(BytesConsumed: buffer.Length);

        // Look for ETX after the STX byte.
        var afterStx = buffer[(stxIndex + 1)..];
        var etxOffset = afterStx.IndexOf(ETX);

        if (etxOffset < 0)
        {
            // ETX not yet received — need more data.
            // Consume up to (but not including) STX so caller retains the partial frame.
            return new DecodeResult.Incomplete(BytesConsumed: stxIndex);
        }

        var totalConsumed = stxIndex + 1 + etxOffset + 1; // STX + payload + ETX

        if (etxOffset == 0)
        {
            // [STX][ETX] with no payload — heartbeat frame.
            return new DecodeResult.Heartbeat(BytesConsumed: totalConsumed);
        }

        // Extract payload bytes between STX and ETX.
        var payloadSpan = afterStx[..etxOffset];

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(payloadSpan);
        }
        catch (DecoderFallbackException ex)
        {
            return new DecodeResult.Error(
                Message: $"Invalid UTF-8 in JPL frame: {ex.Message}",
                BytesConsumed: totalConsumed);
        }

        return new DecodeResult.Frame(Payload: payload, BytesConsumed: totalConsumed);
    }
}

/// <summary>
/// Discriminated result of decoding a JPL frame from a byte buffer.
/// Each variant carries <see cref="BytesConsumed"/> so the caller can advance the buffer.
/// </summary>
public abstract record DecodeResult(int BytesConsumed)
{
    /// <summary>A complete frame with a JSON payload was decoded.</summary>
    public sealed record Frame(string Payload, int BytesConsumed) : DecodeResult(BytesConsumed);

    /// <summary>A heartbeat (empty payload) frame was decoded.</summary>
    public sealed record Heartbeat(int BytesConsumed) : DecodeResult(BytesConsumed);

    /// <summary>Not enough data to form a complete frame — caller should wait for more bytes.</summary>
    public sealed record Incomplete(int BytesConsumed) : DecodeResult(BytesConsumed);

    /// <summary>A frame was found but its content is malformed.</summary>
    public sealed record Error(string Message, int BytesConsumed) : DecodeResult(BytesConsumed);
}
