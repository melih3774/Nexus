package dev.nexus.control

import dev.nexus.control.protocol.NexusEndpoint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The rule that keeps plain WebSocket honest.
 *
 * PC Core speaks cleartext because the tunnel underneath it is encrypted. That argument survives
 * only while the app refuses to dial anything outside the tunnel, so these are the tests that hold
 * up the design decision rather than merely the code.
 */
class NexusEndpointTest {

    @Test
    fun `a tailnet address is accepted`() {
        val endpoint = NexusEndpoint.parse("100.101.102.103", 47812).getOrThrow()

        assertEquals("100.101.102.103", endpoint.host)
        assertEquals(47812, endpoint.port)
        assertEquals("ws://100.101.102.103:47812/nexus", endpoint.webSocketUrl)
    }

    @Test
    fun `the CGNAT range is checked by arithmetic, not by prefix`() {
        // 100.64.0.0/10 is 100.64.x.x through 100.127.x.x. The addresses either side of those
        // boundaries are ordinary public addresses that happen to start with "100.".
        assertTrue(accepted("100.64.0.0"))
        assertTrue(accepted("100.127.255.255"))
        assertTrue(accepted("100.100.100.100"))

        assertFalse(accepted("100.63.255.255"))
        assertFalse(accepted("100.128.0.0"))
        assertFalse(accepted("100.0.0.1"))
        assertFalse(accepted("100.255.255.255"))
        assertFalse(accepted("101.64.0.1"))
    }

    @Test
    fun `LAN, loopback and public addresses are refused`() {
        for (host in listOf(
            "192.168.1.5",
            "10.0.0.4",
            "172.16.3.9",
            "127.0.0.1",
            "0.0.0.0",
            "8.8.8.8",
            "203.0.113.7",
            "169.254.1.1",
            "255.255.255.255",
        )) {
            assertFalse("should have refused $host", accepted(host))
        }
    }

    @Test
    fun `host names are refused without being resolved`() {
        // A name is refused on sight. Resolving one would hand the choice of destination to
        // whoever answers the lookup, and a name that resolves into the range today can resolve
        // somewhere else tomorrow.
        for (host in listOf("example.com", "pc.local", "my-pc", "localhost", "100.64.0.1.example.com")) {
            val failure = NexusEndpoint.parse(host, 47812).exceptionOrNull()
            assertTrue("should have refused $host", failure is NexusEndpoint.Rejection)
        }
    }

    @Test
    fun `lenient IPv4 spellings that would slip past a range check are refused`() {
        // inet_addr-style forms: octal, hex, shorthand and padded octets can all denote an address
        // outside the range while looking like one inside it.
        for (host in listOf(
            "0100.64.0.1",
            "100.064.0.1",
            "0x64.0x40.0.1",
            "100.64.1",
            "100.64.0.1.",
            "100.64.0.256",
            "100.64.0.-1",
        )) {
            assertFalse("should have refused $host", accepted(host))
        }
    }

    @Test
    fun `pasted whitespace is trimmed, and trimming never changes which address is accepted`() {
        // Kotlin's trim() strips Unicode space separators, so a code pasted from a document with a
        // non-breaking space still works. That is a convenience and not a loophole: whatever
        // survives the trim goes through the same range check as anything else.
        val nbsp = Char(0xA0)

        assertTrue(accepted("$nbsp 100.64.0.1 "))
        assertFalse(accepted("$nbsp 192.168.1.5 "))
        assertFalse(accepted("$nbsp example.com "))
    }

    @Test
    fun `empty and null hosts are refused`() {
        assertTrue(NexusEndpoint.parse(null, 47812).exceptionOrNull() is NexusEndpoint.Rejection.NoHost)
        assertTrue(NexusEndpoint.parse("", 47812).exceptionOrNull() is NexusEndpoint.Rejection.NoHost)
        assertTrue(NexusEndpoint.parse("   ", 47812).exceptionOrNull() is NexusEndpoint.Rejection.NoHost)
    }

    @Test
    fun `privileged and impossible ports are refused`() {
        assertFalse(NexusEndpoint.parse("100.64.0.1", 0).isSuccess)
        assertFalse(NexusEndpoint.parse("100.64.0.1", 80).isSuccess)
        assertFalse(NexusEndpoint.parse("100.64.0.1", 1023).isSuccess)
        assertFalse(NexusEndpoint.parse("100.64.0.1", 70000).isSuccess)
        assertFalse(NexusEndpoint.parse("100.64.0.1", -1).isSuccess)

        // A reconfigured PC Core port is fine once the host is provably on the tailnet.
        assertTrue(NexusEndpoint.parse("100.64.0.1", 51000).isSuccess)
    }

    @Test
    fun `only the Nexus URL form is accepted`() {
        assertTrue(NexusEndpoint.parseUrl("ws://100.64.0.1:47812/nexus").isSuccess)

        for (url in listOf(
            "wss://100.64.0.1:47812/nexus",
            "http://100.64.0.1:47812/nexus",
            "ws://100.64.0.1:47812/",
            "ws://100.64.0.1:47812/nexus/../admin",
            "ws://100.64.0.1:47812/nexus?token=x",
            "ws://user:pass@100.64.0.1:47812/nexus",
            "ws://example.com:47812/nexus",
            "ws://192.168.1.5:47812/nexus",
            "100.64.0.1:47812",
            "",
        )) {
            assertFalse("should have refused $url", NexusEndpoint.parseUrl(url).isSuccess)
        }
    }

    @Test
    fun `each refusal explains itself`() {
        // The UI shows these. "Unknown error" is what this test exists to prevent.
        val notOnTailnet = NexusEndpoint.parse("192.168.1.5", 47812).exceptionOrNull()!!
        assertTrue(notOnTailnet is NexusEndpoint.Rejection.NotOnTailnet)
        assertTrue(notOnTailnet.message!!.contains("192.168.1.5"))
        assertTrue(notOnTailnet.message!!.contains("Tailscale"))

        val name = NexusEndpoint.parse("example.com", 47812).exceptionOrNull()!!
        assertTrue(name is NexusEndpoint.Rejection.NotAnAddress)
    }

    @Test
    fun `the test-only constructor is never used by the app itself`() {
        // NexusEndpoint's constructor is private, so the only ways to obtain one are parse — which
        // enforces everything above — and forTestsOnly. This asserts the app never takes the second
        // route, which is what makes the seam in PcConnectionManager.connectTo safe.
        val main = File("src/main/java")
        assertTrue("expected to find the main sources at ${main.absolutePath}", main.isDirectory)

        val offenders = main.walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .filter { it.name != "NexusEndpoint.kt" }
            // Calls, not mentions: the seam in PcConnectionManager documents this rule in prose.
            .filter { it.readText().contains("forTestsOnly(") }
            .map { it.name }
            .toList()

        assertEquals("production code must not build unvalidated endpoints", emptyList<String>(), offenders)
    }

    private fun accepted(host: String) = NexusEndpoint.parse(host, 47812).isSuccess
}
