package dev.nexus.control

import dev.nexus.control.data.NexusCredential
import dev.nexus.control.net.AppVersion
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.FailureReason
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.net.PrivateNetworkProbe
import dev.nexus.control.protocol.Base64Url
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.NexusCrypto
import dev.nexus.control.protocol.NexusEndpoint
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.RequestEnvelope
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CopyOnWriteArrayList

/**
 * The connection manager against a scripted PC.
 *
 * A real socket, real JSON, and the real handshake — just with a server whose answers this test
 * chooses. That is what makes it possible to exercise correlation, event delivery and the failure
 * classifications without a Windows machine and a tailnet on the other end.
 *
 * The server binds to loopback, which the app would never dial in production. Getting there needs
 * `NexusEndpoint.forTestsOnly`, and `NexusEndpointTest` asserts no production source calls it.
 */
class PcConnectionManagerTest {

    private companion object {
        const val PC_INSTANCE = "test-pc-instance"
        const val DEVICE_ID = "test-device"

        /** A disposable 256-bit value that exists only inside this test. */
        const val SECRET = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"

        const val CHALLENGE = "8J-Qq0hlbGxvLU5leHVzLUNoYWxsZW5nZS0wMDAxISE"
    }

    private lateinit var server: MockWebServer
    private lateinit var scope: CoroutineScope
    private lateinit var manager: PcConnectionManager
    private lateinit var client: OkHttpClient

    private val probe = object : PrivateNetworkProbe {
        var network = true
        var tailnet = true
        override fun hasNetwork() = network
        override fun hasTailnetAddress() = tailnet
    }

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        client = PcConnectionManager.defaultClient()
        manager = PcConnectionManager(scope, probe, client, appVersion = AppVersion(16, "1.3.1"))
    }

    @After
    fun tearDown() {
        // Order matters: MockWebServer refuses to shut down while a WebSocket is still open, so the
        // client has to let go of its connections before the server is closed.
        manager.disconnect()
        scope.cancel()
        client.dispatcher.cancelAll()
        client.connectionPool.evictAll()
        client.dispatcher.executorService.shutdown()
        server.close()
    }

    // --- The scripted PC --------------------------------------------------------------------------

    /**
     * A PC Core stand-in. It answers the handshake the way the real one does — challenge first,
     * then verify the HMAC — so a phone-side mistake in the signed bytes fails here rather than
     * being papered over by a server that accepts anything.
     */
    private open inner class FakePc(
        private val acceptAuth: Boolean = true,
        private val onRequest: (WebSocket, RequestEnvelope) -> Unit = { _, _ -> },
        private val updateRequired: Boolean = false,
    ) : WebSocketListener() {

        val received = CopyOnWriteArrayList<RequestEnvelope>()

        @Volatile
        var socket: WebSocket? = null

        override fun onOpen(webSocket: WebSocket, response: Response) {
            socket = webSocket

            webSocket.send(
                """
                {"protocol_version":1,"id":"c1","type":"event","event":"auth.challenge",
                 "payload":{"challenge_id":"chal-1","challenge":"$CHALLENGE",
                            "expires_at_utc":"2026-08-29T12:34:56.789+00:00"},
                 "timestamp_utc":"2026-08-29T12:34:56.789+00:00"}
                """.trimIndent().replace("\n", ""),
            )
        }

        /** PC Core answers a close frame with its own; without this the socket lingers half-open. */
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            val request = NexusJson.decodeFromString(RequestEnvelope.serializer(), text)
            received += request

            if (request.action == NexusProtocol.Actions.AUTHENTICATE) {
                val params = request.params!!.jsonObject
                val proof = params["proof"]!!.jsonPrimitive.content

                val expected = Base64Url.encode(
                    NexusCrypto.hmacSha256(
                        key = Base64Url.decode(SECRET),
                        message = NexusCrypto.buildAuthMessage(
                            PC_INSTANCE,
                            params["device_id"]!!.jsonPrimitive.content,
                            Base64Url.decode(CHALLENGE),
                        ),
                    ),
                )

                if (acceptAuth && proof == expected) {
                    webSocket.send(
                        """
                        {"protocol_version":1,"id":"${request.id}","type":"response",
                         "action":"auth.authenticate","success":true,
                         "result":{"authenticated":true,"actor_type":"DEVICE","device_id":"$DEVICE_ID",
                                   "session_id":"sess-1","pc_instance_id":"$PC_INSTANCE",
                                   "allowed_roots":["C:\\Users\\Example\\Desktop"],
                                   "update_required":$updateRequired,"minimum_client_version_code":20},
                         "error":null,"duration_ms":1}
                        """.trimIndent().replace("\n", ""),
                    )
                } else {
                    webSocket.send(
                        """
                        {"protocol_version":1,"id":"${request.id}","type":"response",
                         "action":"auth.authenticate","success":false,"result":null,
                         "error":{"code":"authentication_failed","message":"Authentication failed.",
                                  "details":null},"duration_ms":1}
                        """.trimIndent().replace("\n", ""),
                    )
                }
                return
            }

            onRequest(webSocket, request)
        }
    }

    private fun start(listener: WebSocketListener) {
        server.enqueue(MockResponse.Builder().webSocketUpgrade(listener).build())
    }

    private fun connect() = manager.connectTo(
        Result.success(NexusEndpoint.forTestsOnly("127.0.0.1", server.port)),
        credential(),
    )

    private fun credential() = NexusCredential(
        pcInstanceId = PC_INSTANCE,
        deviceId = DEVICE_ID,
        sharedSecret = SECRET,
        host = "100.64.0.1",
        port = 47812,
    )

    private fun awaitConnected(): ConnectionState.Connected = runBlocking {
        val state = await { manager.state.value is ConnectionState.Connected }
        assertTrue("never connected; last state was ${manager.state.value}", state)
        manager.state.value as ConnectionState.Connected
    }

    private suspend fun await(timeoutMs: Long = 10_000, condition: () -> Boolean): Boolean =
        withTimeoutOrNull(timeoutMs) {
            while (!condition()) delay(20)
            true
        } ?: false

    private fun ok(id: String, action: String, result: String) =
        """{"protocol_version":1,"id":"$id","type":"response","action":"$action","success":true,""" +
            """"result":$result,"error":null,"duration_ms":1}"""

    // --- Tests ------------------------------------------------------------------------------------

    @Test
    fun `the handshake produces a DEVICE session with the roots the PC named`() {
        val pc = FakePc()
        start(pc)
        connect()

        val connected = awaitConnected()

        assertEquals(DEVICE_ID, connected.deviceId)
        assertEquals("sess-1", connected.sessionId)
        assertEquals(PC_INSTANCE, connected.pcInstanceId)
        assertEquals(listOf("""C:\Users\Example\Desktop"""), connected.allowedRoots)

        // The proof the PC accepted was the one this app computed from the challenge.
        val auth = pc.received.single { it.action == NexusProtocol.Actions.AUTHENTICATE }
        assertEquals("chal-1", auth.params!!.jsonObject["challenge_id"]!!.jsonPrimitive.content)
        assertEquals(DEVICE_ID, auth.params!!.jsonObject["device_id"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a wrong credential fails authentication and stops retrying`() {
        // Not transient: a rejected credential will not become valid by trying again, and a phone
        // that reconnects forever against a PC that unpaired it is just noise.
        start(FakePc(acceptAuth = false))
        connect()

        val failed = runBlocking {
            await { manager.state.value.let { it is ConnectionState.Failed } }
        }

        assertTrue(failed)
        val state = manager.state.value as ConnectionState.Failed
        assertEquals(FailureReason.AuthenticationFailed, state.reason)
        assertEquals(false, state.willRetry)
    }

    /**
     * The reconnect loop the owner actually saw: PC Core rejects the credential, and the app tries
     * again on the next foreground, and the next, writing the same warning to the PC's log forever.
     */
    @Test
    fun `a refused credential is not retried by later connect calls`() {
        start(FakePc(acceptAuth = false))
        connect()

        assertTrue(runBlocking { await { manager.state.value is ConnectionState.Failed } })

        // No second socket is enqueued: if the manager opened one, MockWebServer would have no
        // response for it and this would fail on the attempt rather than on the assertion.
        repeat(3) { connect() }

        val state = manager.state.value as ConnectionState.Failed
        assertEquals(FailureReason.AuthenticationFailed, state.reason)
        assertEquals(false, state.willRetry)
        assertEquals(0, server.requestCount - 1)
    }

    /** The Retry button, which is the owner saying they know something the app does not. */
    @Test
    fun `an explicit retry is allowed to try the refused credential again`() {
        start(FakePc(acceptAuth = false))
        connect()

        assertTrue(runBlocking { await { manager.state.value is ConnectionState.Failed } })

        // The PC has been re-paired in the meantime, so this time it accepts.
        start(FakePc(acceptAuth = true))
        manager.allowAuthenticationRetry()
        connect()

        val connected = awaitConnected()
        assertEquals(DEVICE_ID, connected.deviceId)
    }

    /**
     * PC Core answers a bad credential and closes at once. When the close is processed first, the
     * handshake never sees the response — and used to call that an ordinary drop and retry forever.
     */
    @Test
    fun `a policy-violation close during the handshake is a refusal, not a dropped socket`() {
        start(object : FakePc(acceptAuth = false) {
            override fun onMessage(webSocket: WebSocket, text: String) {
                val request = NexusJson.decodeFromString(RequestEnvelope.serializer(), text)

                if (request.action == NexusProtocol.Actions.AUTHENTICATE) {
                    // No response at all: just the close PC Core sends immediately afterwards.
                    webSocket.close(1008, "authentication failed")
                    return
                }

                super.onMessage(webSocket, text)
            }
        })

        connect()

        assertTrue(runBlocking { await { manager.state.value is ConnectionState.Failed } })

        val state = manager.state.value as ConnectionState.Failed
        assertEquals(FailureReason.AuthenticationFailed, state.reason)
        assertEquals(false, state.willRetry)
    }

    @Test
    fun `responses are matched to their own requests, whatever order they arrive in`() {
        // The failure this prevents is the quiet one: two screens each get an answer, and they are
        // each other's. Answering in reverse order is the cheapest way to provoke it.
        val held = CopyOnWriteArrayList<RequestEnvelope>()

        val pc = FakePc(onRequest = { socket, request ->
            held += request

            if (held.size == 2) {
                socket.send(ok(held[1].id, held[1].action, """{"count":2,"processes":[]}"""))
                socket.send(ok(held[0].id, held[0].action, """{"machine_name":"FIRST"}"""))
            }
        })

        start(pc)
        connect()
        awaitConnected()

        runBlocking {
            val first = scope.async { manager.request(NexusProtocol.Actions.SYSTEM_INFO) }
            val second = scope.async {
                delay(150)
                manager.request(NexusProtocol.Actions.LIST_PROCESSES)
            }

            val info = withTimeoutOrNull(10_000) { first.await() }
            val processes = withTimeoutOrNull(10_000) { second.await() }

            assertNotNull("system.get_info never received its answer", info)
            assertNotNull("system.list_processes never received its answer", processes)

            assertEquals("FIRST", info!!.jsonObject["machine_name"]!!.jsonPrimitive.content)
            assertEquals("2", processes!!.jsonObject["count"]!!.jsonPrimitive.content)
        }
    }

    @Test
    fun `a refused request surfaces the structured code and leaves the session alive`() {
        val pc = FakePc(onRequest = { socket, request ->
            socket.send(
                """{"protocol_version":1,"id":"${request.id}","type":"response",""" +
                    """"action":"${request.action}","success":false,"result":null,""" +
                    """"error":{"code":"not_authorized","message":"Action not permitted.","details":null},""" +
                    """"duration_ms":0}""",
            )
        })

        start(pc)
        connect()
        awaitConnected()

        runBlocking {
            val failure = runCatching { manager.request("device.unpair_primary") }.exceptionOrNull()

            assertTrue(failure is NexusRequestException)
            assertEquals(
                NexusProtocol.Errors.NOT_AUTHORIZED,
                (failure as NexusRequestException).error.code,
            )
        }

        // Still connected: one denial is not a disconnection.
        assertTrue(manager.state.value is ConnectionState.Connected)
    }

    @Test
    fun `events reach subscribers and are not mistaken for responses`() {
        val pc = FakePc(onRequest = { socket, request ->
            // An event arrives between the request and its response — the case that breaks a
            // client which assumes the next message is the answer.
            socket.send(
                """{"protocol_version":1,"id":"e1","type":"event","event":"security.state_changed",""" +
                    """"payload":{"previous_state":"OWNER_PRESENT","current_state":"ARMED"},""" +
                    """"timestamp_utc":"2026-08-29T12:34:56.789+00:00"}""",
            )
            socket.send(ok(request.id, request.action, """{"changed":true,"state":"ARMED"}"""))
        })

        start(pc)
        connect()
        awaitConnected()

        val events = CopyOnWriteArrayList<EventEnvelope>()

        runBlocking {
            val subscription = scope.launch { manager.events.collect { events += it } }
            delay(200)

            val result = manager.request(NexusProtocol.Actions.ARM)
            assertEquals("ARMED", result!!.jsonObject["state"]!!.jsonPrimitive.content)

            await { events.isNotEmpty() }
            subscription.cancel()
        }

        assertEquals(1, events.size)
        assertEquals(NexusProtocol.Events.SECURITY_STATE_CHANGED, events.first().eventName)
        assertEquals(
            "ARMED",
            events.first().payload!!.jsonObject["current_state"]!!.jsonPrimitive.content,
        )
    }

    @Test
    fun `a request without a session fails immediately rather than hanging`() {
        runBlocking {
            val failure = runCatching { manager.request(NexusProtocol.Actions.SYSTEM_INFO) }.exceptionOrNull()
            assertTrue(failure is NotConnectedException)
        }
    }

    @Test
    fun `disconnecting ends the session and reports Idle`() {
        start(FakePc())
        connect()
        awaitConnected()

        manager.disconnect()

        assertEquals(ConnectionState.Idle, manager.state.value)

        runBlocking {
            val failure = runCatching { manager.request(NexusProtocol.Actions.SYSTEM_INFO) }.exceptionOrNull()
            assertTrue(failure is NotConnectedException)
        }
    }

    @Test
    fun `an unreachable PC is classified by what the phone can see`() {
        // The distinction the UI depends on: the same socket failure means different things, and
        // only the phone's own network state tells them apart.
        // Pointed at a port nothing is listening on, so every attempt fails at the socket and the
        // classification is the only thing that varies.
        val dead = closedPort()

        fun attempt() = manager.connectTo(
            Result.success(NexusEndpoint.forTestsOnly("127.0.0.1", dead)),
            credential(),
        )

        probe.network = false
        attempt()
        assertTrue("expected NoNetwork, got ${reason()}", runBlocking { await { reason() == FailureReason.NoNetwork } })

        manager.disconnect()
        probe.network = true
        probe.tailnet = false
        attempt()
        assertTrue(
            "expected PrivateNetworkUnavailable, got ${reason()}",
            runBlocking { await { reason() == FailureReason.PrivateNetworkUnavailable } },
        )

        manager.disconnect()
        probe.tailnet = true
        attempt()
        assertTrue(
            "expected PcUnreachable, got ${reason()}",
            runBlocking { await { reason() == FailureReason.PcUnreachable } },
        )
    }

    @Test
    fun `an endpoint that no longer validates is refused without retrying`() {
        manager.connectTo(NexusEndpoint.parse("192.168.1.5", 47812), credential())

        assertTrue(runBlocking { await { manager.state.value is ConnectionState.Failed } })

        val state = manager.state.value as ConnectionState.Failed
        assertEquals(FailureReason.ProtocolError, state.reason)
        assertEquals(false, state.willRetry)
    }

    @Test
    fun `the request envelope sent to the PC carries no identity claim`() {
        // A request must never be able to say who it is. The identity lives on the session, and
        // this pins that nothing leaks back into the message body.
        val pc = FakePc(onRequest = { socket, request ->
            socket.send(ok(request.id, request.action, """{"machine_name":"X"}"""))
        })

        start(pc)
        connect()
        awaitConnected()

        runBlocking {
            manager.request(
                NexusProtocol.Actions.LIST_FILES,
                buildJsonObject { },
            )
        }

        val sent = pc.received.last { it.action == NexusProtocol.Actions.LIST_FILES }
        val encoded = NexusJson.encodeToString(RequestEnvelope.serializer(), sent)

        assertTrue(!encoded.contains("actor_type"))
        assertTrue(!encoded.contains("device_id"))
        assertTrue(!encoded.contains(SECRET))
    }

    @Test
    fun `the app says which build it is, and hears when the PC needs a newer one`() {
        val pc = FakePc(updateRequired = true)
        start(pc)
        connect()

        val connected = awaitConnected()

        // Told outright, so the app can show the update instead of a string of refusals.
        assertTrue(connected.updateRequired)

        val auth = pc.received.single { it.action == NexusProtocol.Actions.AUTHENTICATE }.params!!.jsonObject
        assertEquals("16", auth["client_version_code"]!!.jsonPrimitive.content)
        assertEquals("1.3.1", auth["client_version_name"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a current app is not told to update`() {
        start(FakePc())
        connect()

        assertEquals(false, awaitConnected().updateRequired)
    }
    private fun reason() = (manager.state.value as? ConnectionState.Failed)?.reason

    /** A port that was free a moment ago and has nothing listening on it now. */
    private fun closedPort(): Int = java.net.ServerSocket(0).use { it.localPort }
}
