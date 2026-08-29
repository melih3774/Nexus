package dev.nexus.control.protocol

/**
 * An address this app is willing to connect to.
 *
 * **Why this type exists.** The remote transport speaks plain WebSocket, because the traffic is
 * already inside a WireGuard tunnel (see `SPEC.md` 28). That reasoning holds only while the
 * connection genuinely stays inside the tunnel — so "which address do we dial" cannot be a string
 * that arrived in a QR code. Every endpoint, whether scanned, pasted, or restored from storage,
 * has to come through [parse].
 *
 * The rules are deliberately narrow:
 *
 * - **Literal IPv4 only.** A host name is refused outright, before any lookup. Resolving one would
 *   hand the choice of destination to whoever controls DNS, and a name that resolves into the
 *   Tailscale range today can resolve elsewhere tomorrow.
 * - **Inside `100.64.0.0/10`.** The CGNAT range Tailscale assigns, checked with address arithmetic
 *   rather than a `"100."` prefix — `100.0.0.1` is a public address and starts with the same three
 *   characters. This is the check that makes `example.com`, `192.168.1.5`, and any public IP
 *   impossible regardless of what a pairing code claims.
 * - **`ws` and `/nexus`.** No `wss` (there is no certificate to validate), no other path, no
 *   credentials in the URL, no query string.
 *
 * The port is not pinned to 47812. Once the host is provably a tailnet address the port is not a
 * security boundary, and the owner can reconfigure PC Core; a privileged port is still refused.
 */
class NexusEndpoint private constructor(val host: String, val port: Int) {

    val webSocketUrl: String get() = "ws://$host:$port${NexusProtocol.PATH}"

    /** For display. Never rendered next to anything credential-shaped. */
    override fun toString(): String = "$host:$port"

    override fun equals(other: Any?): Boolean =
        other is NexusEndpoint && other.host == host && other.port == port

    override fun hashCode(): Int = host.hashCode() * 31 + port

    companion object {
        private const val CGNAT_BASE = 0x64_40_00_00L // 100.64.0.0
        private const val CGNAT_MASK = 0xFF_C0_00_00L // /10

        private const val MIN_PORT = 1024
        private const val MAX_PORT = 65535

        /**
         * The only way to obtain an endpoint. Returns a [Rejection] describing precisely what was
         * wrong, so the UI can say something true rather than "unknown error".
         */
        fun parse(host: String?, port: Int): Result<NexusEndpoint> {
            val trimmed = host?.trim().orEmpty()

            if (trimmed.isEmpty()) {
                return Result.failure(Rejection.NoHost)
            }

            val address = parseIpv4(trimmed)
                ?: return Result.failure(Rejection.NotAnAddress(trimmed))

            if (address and CGNAT_MASK != CGNAT_BASE) {
                return Result.failure(Rejection.NotOnTailnet(trimmed))
            }

            if (port !in MIN_PORT..MAX_PORT) {
                return Result.failure(Rejection.BadPort(port))
            }

            return Result.success(NexusEndpoint(trimmed, port))
        }

        /** Parses a stored or pasted `ws://host:port/nexus` URL under the same rules. */
        fun parseUrl(url: String?): Result<NexusEndpoint> {
            val value = url?.trim().orEmpty()
            val match = URL_PATTERN.matchEntire(value)
                ?: return Result.failure(Rejection.BadUrl(value))

            val port = match.groupValues[2].toIntOrNull()
                ?: return Result.failure(Rejection.BadUrl(value))

            return parse(match.groupValues[1], port)
        }

        /** Exposed for the private-network probe, which asks the same question of local interfaces. */
        fun isTailnetAddress(host: String?): Boolean {
            val address = host?.let { parseIpv4(it.trim()) } ?: return false
            return address and CGNAT_MASK == CGNAT_BASE
        }

        /**
         * Strict dotted-quad parsing. No hex, no octal, no shorthand forms, no trailing dot — the
         * lenient spellings `inet_addr` accepts are exactly how an address gets past a range check.
         */
        private fun parseIpv4(value: String): Long? {
            val parts = value.split('.')
            if (parts.size != 4) return null

            var address = 0L
            for (part in parts) {
                if (part.isEmpty() || part.length > 3 || !part.all { it in '0'..'9' }) return null
                if (part.length > 1 && part[0] == '0') return null

                val octet = part.toInt()
                if (octet > 255) return null

                address = (address shl 8) or octet.toLong()
            }
            return address
        }

        private val URL_PATTERN =
            Regex("""^ws://([0-9.]{7,15}):(\d{1,5})${Regex.escape(NexusProtocol.PATH)}$""")

        /**
         * Test-only. Production endpoints come from [parse]; this exists so the connection manager
         * can be exercised against a local scripted server, which by definition is not on a tailnet.
         * It is never called from `main`.
         */
        @JvmStatic
        fun forTestsOnly(host: String, port: Int): NexusEndpoint = NexusEndpoint(host, port)
    }

    /** Why an endpoint was refused. Each case maps to a specific sentence in the UI. */
    sealed class Rejection(message: String) : Exception(message) {
        data object NoHost : Rejection("The pairing code names no address.")

        data class NotAnAddress(val value: String) :
            Rejection("Nexus connects to a Tailscale address only, not to a host name.")

        data class NotOnTailnet(val value: String) :
            Rejection("$value is not a Tailscale address. Nexus refuses to connect over Wi-Fi or the internet.")

        data class BadPort(val value: Int) : Rejection("Port $value is not usable.")

        data class BadUrl(val value: String) : Rejection("That is not a Nexus address.")
    }
}
