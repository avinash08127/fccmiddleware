package com.fccmiddleware.edge.adapter.doms.jpl

/**
 * STX/ETX binary frame codec for DOMS JPL protocol.
 * Handles encoding JSON payloads into binary frames and decoding frames from TCP byte streams.
 *
 * Frame format: [STX][JSON payload bytes (UTF-8)][ETX]
 * Heartbeat frame: [STX][ETX] (empty payload)
 */
object JplFrameCodec {
    const val STX: Byte = 0x02
    const val ETX: Byte = 0x03

    /** Encode a JSON string payload into a JPL binary frame: [STX][payload][ETX]. */
    fun encode(json: String): ByteArray {
        val payload = json.toByteArray(Charsets.UTF_8)
        val frame = ByteArray(payload.size + 2)
        frame[0] = STX
        payload.copyInto(frame, destinationOffset = 1)
        frame[frame.size - 1] = ETX
        return frame
    }

    /** Encode a heartbeat (keep-alive) frame: [STX][ETX]. */
    fun encodeHeartbeat(): ByteArray = byteArrayOf(STX, ETX)

    /**
     * Decode one frame from a byte buffer starting at [offset] for [length] bytes.
     *
     * Handles:
     * - Complete frames (returns [DecodeResult.Frame] or [DecodeResult.Heartbeat])
     * - Incomplete data (returns [DecodeResult.Incomplete] -- caller should accumulate more bytes)
     * - Invalid framing (returns [DecodeResult.Error] -- caller should discard and resync)
     * - Multiple frames in buffer (returns first frame; caller re-invokes with remainder)
     */
    fun decode(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size - offset): DecodeResult {
        if (length <= 0) {
            return DecodeResult.Incomplete(bytesConsumed = 0)
        }

        val end = offset + length

        // Skip any bytes before the first STX (garbage/resync).
        var stxPos = -1
        for (i in offset until end) {
            if (buffer[i] == STX) {
                stxPos = i
                break
            }
        }

        // No STX found in the entire buffer -- all bytes are garbage.
        if (stxPos < 0) {
            return DecodeResult.Error(
                message = "No STX byte found in buffer",
                bytesConsumed = length,
            )
        }

        // If there were garbage bytes before STX, report them as an error so the caller
        // can discard them and retry from the STX position.
        if (stxPos > offset) {
            return DecodeResult.Error(
                message = "Unexpected bytes before STX",
                bytesConsumed = stxPos - offset,
            )
        }

        // Look for ETX after the STX.
        var etxPos = -1
        for (i in (stxPos + 1) until end) {
            if (buffer[i] == ETX) {
                etxPos = i
                break
            }
        }

        // No ETX yet -- frame is incomplete; wait for more data.
        if (etxPos < 0) {
            return DecodeResult.Incomplete(bytesConsumed = 0)
        }

        val totalConsumed = etxPos - offset + 1
        val payloadLength = etxPos - stxPos - 1

        return if (payloadLength == 0) {
            DecodeResult.Heartbeat(bytesConsumed = totalConsumed)
        } else {
            val payload = String(buffer, stxPos + 1, payloadLength, Charsets.UTF_8)
            DecodeResult.Frame(payload = payload, bytesConsumed = totalConsumed)
        }
    }
}

/** Result of a single [JplFrameCodec.decode] invocation. */
sealed class DecodeResult {
    /** A complete data frame was decoded. */
    data class Frame(val payload: String, val bytesConsumed: Int) : DecodeResult()

    /** A heartbeat (empty) frame was decoded. */
    data class Heartbeat(val bytesConsumed: Int) : DecodeResult()

    /** Not enough data -- caller should wait for more bytes. */
    data class Incomplete(val bytesConsumed: Int) : DecodeResult()

    /** Invalid framing detected. [bytesConsumed] indicates how many bytes to discard. */
    data class Error(val message: String, val bytesConsumed: Int) : DecodeResult()
}
