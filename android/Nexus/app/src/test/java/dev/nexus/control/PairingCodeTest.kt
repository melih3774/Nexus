package dev.nexus.control

import dev.nexus.control.protocol.Base64Url
import dev.nexus.control.protocol.PairingCode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The pairing code is the app's only untrusted input, and it arrives from a camera.
 *
 * Everything here is about refusing gracefully: parsing never throws, never half-succeeds, and
 * always says something the owner could act on. A scan that produced an exception would crash the
 * scanner screen thirty times a second.
 */
class PairingCodeTest {

    private fun encode(json: String) = PairingCode.PREFIX_V2 + Base64Url.encode(json.toByteArray())

    @Test
    fun `a well-formed code parses`() {
        val code = PairingCode.parse(
            encode("""{"v":2,"pc":"pc-1","s":"sess-1","k":"secret-1","h":"100.64.0.5","p":47812,"pv":1}"""),
        ).getOrThrow()

        assertEquals("pc-1", code.pcInstanceId)
        assertEquals("sess-1", code.sessionId)
        assertEquals("secret-1", code.pairingSecret)
        assertEquals("100.64.0.5", code.host)
        assertEquals(47812, code.port)
    }

    @Test
    fun `surrounding whitespace from a paste is tolerated`() {
        val raw = encode("""{"v":2,"pc":"pc-1","s":"s","k":"k","h":"100.64.0.5","p":47812,"pv":1}""")
        assertTrue(PairingCode.parse("  \n$raw\n ").isSuccess)
    }

    @Test
    fun `a v1 code is refused with an explanation, not a parse error`() {
        // v1 is a real code from an older PC Core, and it carries no address. Telling the owner
        // that is more useful than "invalid".
        val v1 = PairingCode.PREFIX_V1 + Base64Url.encode(
            """{"v":1,"pc":"pc-1","s":"s","k":"k"}""".toByteArray(),
        )

        val failure = PairingCode.parse(v1).exceptionOrNull()
        assertTrue(failure is PairingCode.Rejection.OldFormat)
        assertTrue(failure!!.message!!.contains("new one"))
    }

    @Test
    fun `arbitrary text is refused`() {
        for (raw in listOf(
            null,
            "",
            "   ",
            "hello",
            "https://example.com",
            "NEXUS",
            "NEXUS2",
            "NEXUS2.",
            "NEXUS2.!!!not-base64!!!",
            "nexus2.abc",
        )) {
            assertTrue("should have refused: $raw", PairingCode.parse(raw).isFailure)
        }
    }

    @Test
    fun `a code missing any required field is refused`() {
        assertTrue(PairingCode.parse(encode("""{"v":2,"s":"s","k":"k","pv":1}""")).isFailure)
        assertTrue(PairingCode.parse(encode("""{"v":2,"pc":"p","k":"k","pv":1}""")).isFailure)
        assertTrue(PairingCode.parse(encode("""{"v":2,"pc":"p","s":"s","pv":1}""")).isFailure)
        assertTrue(PairingCode.parse(encode("""{"v":2,"pc":"","s":"s","k":"k","pv":1}""")).isFailure)
    }

    @Test
    fun `a future payload version is refused by version, not by shape`() {
        val failure = PairingCode.parse(encode("""{"v":3,"pc":"p","s":"s","k":"k","pv":1}"""))
            .exceptionOrNull()

        assertTrue(failure is PairingCode.Rejection.UnsupportedVersion)
        assertEquals(3, (failure as PairingCode.Rejection.UnsupportedVersion).version)
    }

    @Test
    fun `a protocol version this app does not speak is refused by name`() {
        val failure = PairingCode.parse(encode("""{"v":2,"pc":"p","s":"s","k":"k","p":47812,"pv":9}"""))
            .exceptionOrNull()

        assertTrue(failure is PairingCode.Rejection.ProtocolMismatch)
        assertTrue(failure!!.message!!.contains("9"))
    }

    @Test
    fun `a code with no endpoint parses but refuses to yield one`() {
        val code = PairingCode.parse(encode("""{"v":2,"pc":"p","s":"s","k":"k","p":0,"pv":1}""")).getOrThrow()

        assertNull(code.host)
        assertTrue(code.endpoint().isFailure)
    }

    @Test
    fun `a code claiming a LAN or internet address is refused at the endpoint`() {
        // The decisive case: the payload is well-formed and internally consistent, and the app
        // still will not dial it. Nothing in a scanned code can talk this app onto another network.
        for (host in listOf("192.168.1.5", "example.com", "8.8.8.8", "127.0.0.1")) {
            val code = PairingCode.parse(
                encode("""{"v":2,"pc":"p","s":"s","k":"k","h":"$host","p":47812,"pv":1}"""),
            ).getOrThrow()

            assertFalse("should have refused $host", code.endpoint().isSuccess)
        }
    }

    @Test
    fun `a truncated scan is refused rather than yielding partial fields`() {
        val full = encode("""{"v":2,"pc":"pc-1","s":"sess-1","k":"secret-1","h":"100.64.0.5","p":47812,"pv":1}""")

        for (length in listOf(full.length / 4, full.length / 2, full.length - 3)) {
            assertTrue(
                "a partial scan must not parse",
                PairingCode.parse(full.take(length)).isFailure,
            )
        }
    }
}
