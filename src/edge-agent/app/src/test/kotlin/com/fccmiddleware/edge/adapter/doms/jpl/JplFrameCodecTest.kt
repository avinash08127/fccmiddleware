package com.fccmiddleware.edge.adapter.doms.jpl

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Unit tests for [JplFrameCodec].
 *
 * Covers:
 *   - Encode/decode roundtrip for normal JSON payloads
 *   - Heartbeat encode/decode
 *   - Incomplete buffer handling
 *   - Multiple frames in a single buffer
 *   - Error recovery (no STX, no ETX, garbage data)
 */
class JplFrameCodecTest {

    // -----------------------------------------------------------------------
    // Encoding
    // -----------------------------------------------------------------------

    @Test
    fun `encode wraps JSON payload with STX and ETX`() {
        val json = """{"name":"test"}"""
        val frame = JplFrameCodec.encode(json)

        assertEquals(JplFrameCodec.STX, frame.first())
        assertEquals(JplFrameCodec.ETX, frame.last())

        val payload = frame.sliceArray(1 until frame.size - 1)
        assertEquals(json, String(payload, Charsets.UTF_8))
    }

    @Test
    fun `encode produces correct byte length for ASCII payload`() {
        val json = """{"a":"b"}"""
        val frame = JplFrameCodec.encode(json)

        // STX + payload bytes + ETX
        assertEquals(json.toByteArray(Charsets.UTF_8).size + 2, frame.size)
    }

    @Test
    fun `encode handles UTF-8 multi-byte characters`() {
        val json = """{"name":"caf\u00e9"}"""
        val frame = JplFrameCodec.encode(json)

        assertEquals(JplFrameCodec.STX, frame.first())
        assertEquals(JplFrameCodec.ETX, frame.last())

        val payloadBytes = json.toByteArray(Charsets.UTF_8)
        assertEquals(payloadBytes.size + 2, frame.size)
    }

    @Test
    fun `encode empty string produces frame with two-byte overhead`() {
        val frame = JplFrameCodec.encode("")
        // STX + "" + ETX = 2 bytes, but this differs from heartbeat only in how it was created;
        // the codec treats [STX][ETX] as heartbeat on decode.
        assertEquals(2, frame.size)
        assertEquals(JplFrameCodec.STX, frame[0])
        assertEquals(JplFrameCodec.ETX, frame[1])
    }

    @Test
    fun `encodeHeartbeat produces STX ETX only`() {
        val frame = JplFrameCodec.encodeHeartbeat()
        assertArrayEquals(byteArrayOf(JplFrameCodec.STX, JplFrameCodec.ETX), frame)
    }

    // -----------------------------------------------------------------------
    // Decode — roundtrip
    // -----------------------------------------------------------------------

    @Test
    fun `roundtrip encode then decode recovers original JSON`() {
        val json = """{"name":"FpMainState","subCode":6,"data":{"fpId":"1","state":"6"}}"""
        val frame = JplFrameCodec.encode(json)
        val result = JplFrameCodec.decode(frame)

        assertTrue("Expected Frame, got $result", result is DecodeResult.Frame)
        val decoded = result as DecodeResult.Frame
        assertEquals(json, decoded.payload)
        assertEquals(frame.size, decoded.bytesConsumed)
    }

    @Test
    fun `roundtrip heartbeat encode then decode`() {
        val frame = JplFrameCodec.encodeHeartbeat()
        val result = JplFrameCodec.decode(frame)

        assertTrue("Expected Heartbeat, got $result", result is DecodeResult.Heartbeat)
        assertEquals(2, (result as DecodeResult.Heartbeat).bytesConsumed)
    }

    // -----------------------------------------------------------------------
    // Decode — complete frames
    // -----------------------------------------------------------------------

    @Test
    fun `decode simple JSON frame`() {
        val payload = """{"key":"value"}"""
        val frame = byteArrayOf(0x02) + payload.toByteArray(Charsets.UTF_8) + byteArrayOf(0x03)
        val result = JplFrameCodec.decode(frame)

        assertTrue(result is DecodeResult.Frame)
        val decoded = result as DecodeResult.Frame
        assertEquals(payload, decoded.payload)
        assertEquals(frame.size, decoded.bytesConsumed)
    }

    @Test
    fun `decode heartbeat frame`() {
        val frame = byteArrayOf(0x02, 0x03)
        val result = JplFrameCodec.decode(frame)

        assertTrue(result is DecodeResult.Heartbeat)
        assertEquals(2, (result as DecodeResult.Heartbeat).bytesConsumed)
    }

    // -----------------------------------------------------------------------
    // Decode — incomplete buffer
    // -----------------------------------------------------------------------

    @Test
    fun `decode empty buffer returns Incomplete`() {
        val result = JplFrameCodec.decode(ByteArray(0))
        assertTrue("Expected Incomplete, got $result", result is DecodeResult.Incomplete)
        assertEquals(0, (result as DecodeResult.Incomplete).bytesConsumed)
    }

    @Test
    fun `decode STX only returns Incomplete`() {
        val result = JplFrameCodec.decode(byteArrayOf(0x02))
        assertTrue("Expected Incomplete, got $result", result is DecodeResult.Incomplete)
        assertEquals(0, (result as DecodeResult.Incomplete).bytesConsumed)
    }

    @Test
    fun `decode STX with partial payload but no ETX returns Incomplete`() {
        val buffer = byteArrayOf(0x02, 0x7B, 0x22) // STX + '{"' — no ETX
        val result = JplFrameCodec.decode(buffer)

        assertTrue("Expected Incomplete, got $result", result is DecodeResult.Incomplete)
        assertEquals(0, (result as DecodeResult.Incomplete).bytesConsumed)
    }

    // -----------------------------------------------------------------------
    // Decode — multiple frames in single buffer
    // -----------------------------------------------------------------------

    @Test
    fun `decode two frames in one buffer - first call returns first frame`() {
        val json1 = """{"a":"1"}"""
        val json2 = """{"b":"2"}"""
        val frame1 = JplFrameCodec.encode(json1)
        val frame2 = JplFrameCodec.encode(json2)
        val combined = frame1 + frame2

        val result1 = JplFrameCodec.decode(combined)
        assertTrue("First decode should return Frame", result1 is DecodeResult.Frame)
        val decoded1 = result1 as DecodeResult.Frame
        assertEquals(json1, decoded1.payload)
        assertEquals(frame1.size, decoded1.bytesConsumed)

        // Decode remaining bytes for second frame.
        val result2 = JplFrameCodec.decode(combined, offset = decoded1.bytesConsumed)
        assertTrue("Second decode should return Frame", result2 is DecodeResult.Frame)
        val decoded2 = result2 as DecodeResult.Frame
        assertEquals(json2, decoded2.payload)
    }

    @Test
    fun `decode heartbeat followed by data frame`() {
        val heartbeat = JplFrameCodec.encodeHeartbeat()
        val json = """{"name":"test"}"""
        val dataFrame = JplFrameCodec.encode(json)
        val combined = heartbeat + dataFrame

        val result1 = JplFrameCodec.decode(combined)
        assertTrue(result1 is DecodeResult.Heartbeat)
        val consumed1 = (result1 as DecodeResult.Heartbeat).bytesConsumed

        val result2 = JplFrameCodec.decode(combined, offset = consumed1)
        assertTrue(result2 is DecodeResult.Frame)
        assertEquals(json, (result2 as DecodeResult.Frame).payload)
    }

    @Test
    fun `decode three consecutive frames`() {
        val messages = listOf("""{"x":"1"}""", """{"x":"2"}""", """{"x":"3"}""")
        val combined = messages.map { JplFrameCodec.encode(it) }
            .fold(byteArrayOf()) { acc, b -> acc + b }

        var currentOffset = 0
        for (expected in messages) {
            val result = JplFrameCodec.decode(combined, offset = currentOffset)
            assertTrue("Expected Frame for '$expected', got $result", result is DecodeResult.Frame)
            val frame = result as DecodeResult.Frame
            assertEquals(expected, frame.payload)
            currentOffset += frame.bytesConsumed
        }
        assertEquals("All bytes should be consumed", combined.size, currentOffset)
    }

    // -----------------------------------------------------------------------
    // Decode — error recovery (no STX, garbage data)
    // -----------------------------------------------------------------------

    @Test
    fun `decode buffer with no STX at all returns Error`() {
        val garbage = byteArrayOf(0x41, 0x42, 0x43, 0x44) // "ABCD"
        val result = JplFrameCodec.decode(garbage)

        assertTrue("Expected Error, got $result", result is DecodeResult.Error)
        val error = result as DecodeResult.Error
        assertTrue(error.message.contains("No STX"))
        assertEquals(garbage.size, error.bytesConsumed)
    }

    @Test
    fun `decode buffer with garbage before STX returns Error with garbage byte count`() {
        val json = """{"ok":true}"""
        val frame = JplFrameCodec.encode(json)
        val garbage = byteArrayOf(0xFF.toByte(), 0xFE.toByte(), 0x41)
        val combined = garbage + frame

        val result = JplFrameCodec.decode(combined)
        assertTrue("Expected Error for garbage prefix, got $result", result is DecodeResult.Error)
        val error = result as DecodeResult.Error
        assertEquals("Should consume only the garbage bytes", garbage.size, error.bytesConsumed)

        // After discarding garbage, the next decode should succeed.
        val result2 = JplFrameCodec.decode(combined, offset = error.bytesConsumed)
        assertTrue("After skipping garbage, should decode Frame", result2 is DecodeResult.Frame)
        assertEquals(json, (result2 as DecodeResult.Frame).payload)
    }

    @Test
    fun `decode ETX without STX returns Error`() {
        val buffer = byteArrayOf(0x03) // just ETX, no STX
        val result = JplFrameCodec.decode(buffer)

        assertTrue("Expected Error, got $result", result is DecodeResult.Error)
    }

    @Test
    fun `decode only garbage followed by no valid frame returns Error`() {
        val garbage = byteArrayOf(0x10, 0x20, 0x30)
        val result = JplFrameCodec.decode(garbage)

        assertTrue(result is DecodeResult.Error)
        assertEquals(garbage.size, (result as DecodeResult.Error).bytesConsumed)
    }

    // -----------------------------------------------------------------------
    // Decode — offset and length parameters
    // -----------------------------------------------------------------------

    @Test
    fun `decode with explicit offset skips leading bytes`() {
        val padding = byteArrayOf(0x00, 0x00, 0x00)
        val json = """{"test":"offset"}"""
        val frame = JplFrameCodec.encode(json)
        val combined = padding + frame

        // Decode starting from offset past the padding.
        val result = JplFrameCodec.decode(combined, offset = padding.size)
        assertTrue(result is DecodeResult.Frame)
        assertEquals(json, (result as DecodeResult.Frame).payload)
    }

    @Test
    fun `decode with explicit length limits scan range`() {
        val json = """{"a":"1"}"""
        val frame = JplFrameCodec.encode(json)
        // Append some extra data that should not be reached.
        val combined = frame + byteArrayOf(0x02, 0x03)

        // Only scan the first frame's bytes.
        val result = JplFrameCodec.decode(combined, offset = 0, length = frame.size)
        assertTrue(result is DecodeResult.Frame)
        assertEquals(json, (result as DecodeResult.Frame).payload)
        assertEquals(frame.size, (result as DecodeResult.Frame).bytesConsumed)
    }

    @Test
    fun `decode with zero length returns Incomplete`() {
        val frame = JplFrameCodec.encode("""{"a":"b"}""")
        val result = JplFrameCodec.decode(frame, offset = 0, length = 0)

        assertTrue(result is DecodeResult.Incomplete)
        assertEquals(0, (result as DecodeResult.Incomplete).bytesConsumed)
    }

    // -----------------------------------------------------------------------
    // Decode — large payload
    // -----------------------------------------------------------------------

    @Test
    fun `roundtrip large JSON payload`() {
        val largeJson = """{"data":"${"X".repeat(10_000)}"}"""
        val frame = JplFrameCodec.encode(largeJson)
        val result = JplFrameCodec.decode(frame)

        assertTrue(result is DecodeResult.Frame)
        assertEquals(largeJson, (result as DecodeResult.Frame).payload)
    }
}
