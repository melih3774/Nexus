package dev.nexus.control.net

import android.util.Log
import dev.nexus.control.data.NexusCredential
import dev.nexus.control.protocol.AuthChallenge
import dev.nexus.control.protocol.AuthenticatedSession
import dev.nexus.control.protocol.EnrollmentResult
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.NexusCrypto
import dev.nexus.control.protocol.NexusEndpoint
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.ProtocolError
import dev.nexus.control.protocol.RequestEnvelope
import dev.nexus.control.protocol.ResponseEnvelope
import dev.nexus.control.protocol.StreamFrame
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonElement
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/** A failed request, carrying the structured error PC Core returned. */
class NexusRequestException(val error: ProtocolError) : Exception("${error.code}: ${error.message}")

/** Raised when there is no live session to send on. */
class NotConnectedException : Exception("Not connected to the PC.")

/**
 * What a caller needs from the connection: ask, or tell.
 *
 * Narrow on purpose. [RemoteControlSession] does a lot of ordering that is worth testing — modifiers
 * down before a key and up after it even when the key fails, held state forgotten on the way out —
 * and none of it needs a socket to be right. This is the seam that lets those tests exist.
 */
interface NexusCommandChannel {

    /** Sends and waits. Throws [NexusRequestException] when the PC refuses. */
    suspend fun request(action: String, params: JsonElement? = null, timeoutMs: Long = 15_000): JsonElement?

    /** Sends without waiting. False when there is no live socket. */
    fun notify(action: String, params: JsonElement? = null): Boolean
}

/**
 * The one owner of the connection to PC Core.
 *
 * Every screen goes through this. Nothing else opens a socket, which is what makes "am I
 * connected", "what is my session", and "who receives events" answerable in one place instead of
 * being a property of whichever screen happens to be visible.
 *
 * **Lifetime.** A connection exists while the app is being used and is dropped when it is not.
 * There is no background service and no persistent socket: v0.5 has nothing that needs to arrive
 * while the app is closed, and a phone that holds a WebSocket open all day for no reason is a
 * battery cost with no feature behind it. Security notifications, when they arrive, will need a
 * push channel designed for the purpose rather than this connection left running.
 *
 * **Reconnection** is bounded and unhurried: a handful of seconds, doubling to half a minute. A
 * phone that cannot reach its PC is usually a phone whose owner walked out of range, and hammering
 * a dead route drains the battery without ever being the thing that fixes it. Authentication
 * failure stops retrying altogether — the credential will not become valid by trying again.
 */
class PcConnectionManager(
    private val scope: CoroutineScope,
    private val probe: PrivateNetworkProbe,
    private val http: OkHttpClient = defaultClient(),
) : NexusCommandChannel {
    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    /**
     * Server-sent events. `replay = 0`: an event is a notification of something that happened, and
     * a late subscriber wants current state from `security.get_state`, not a stale announcement.
     */
    private val _events = MutableSharedFlow<EventEnvelope>(extraBufferCapacity = 64)
    val events: SharedFlow<EventEnvelope> = _events.asSharedFlow()

    /**
     * Live desktop frames, newest only.
     *
     * `extraBufferCapacity = 1` with `DROP_OLDEST`: the same latest-frame-wins rule the PC applies
     * before sending. A frame that has been superseded on the wire has nothing to offer a viewer,
     * and buffering it would only delay the one that has.
     */
    private val _frames = MutableSharedFlow<StreamFrame>(
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val frames: SharedFlow<StreamFrame> = _frames.asSharedFlow()

    private val pending = ConcurrentHashMap<String, CompletableDeferred<ResponseEnvelope>>()
    private val requestCounter = AtomicLong(0)

    @Volatile
    private var socket: WebSocket? = null

    private var sessionJob: Job? = null

    /** Starts, or restarts, the connection. Safe to call when already connected. */
    fun connect(credential: NexusCredential) {
        connectTo(credential.endpoint(), credential)
    }

    /**
     * The shared body of [connect].
     *
     * Taking an already-validated [NexusEndpoint] is what lets tests point the manager at a local
     * scripted server, which by definition is not on a tailnet. That is safe for the same reason
     * the equivalent seam is safe on the PC side: a [NexusEndpoint] cannot be conjured from a
     * string. It comes from `NexusEndpoint.parse`, or from `forTestsOnly`, and
     * `NexusEndpointTest` asserts that nothing under `src/main` ever calls the latter.
     */
    internal fun connectTo(endpoint: Result<NexusEndpoint>, credential: NexusCredential) {
        if (sessionJob?.isActive == true && _state.value.isConnected) return

        sessionJob?.cancel()
        sessionJob = scope.launch { maintainConnection(endpoint, credential) }
    }

    /** Ends the connection and stops reconnecting. */
    fun disconnect() {
        sessionJob?.cancel()
        sessionJob = null
        closeSocket("client disconnected")
        _state.value = ConnectionState.Idle
    }

    /**
     * Sends a request on the live session and waits for the response with the matching id.
     *
     * @throws NotConnectedException when there is no session.
     * @throws NexusRequestException when PC Core refuses the request.
     */
    override suspend fun request(
        action: String,
        params: JsonElement?,
        timeoutMs: Long,
    ): JsonElement? {
        val response = send(action, params, timeoutMs)

        if (!response.success) {
            throw NexusRequestException(
                response.error ?: ProtocolError(NexusProtocol.Errors.INTERNAL_ERROR, "The PC refused the request."),
            )
        }

        return response.result
    }

    /**
     * Sends a request without waiting for its answer.
     *
     * For pointer movement during a drag, which arrives sixty times a second and has nothing useful
     * to say back. Awaiting each one would put a network round trip between the finger and the
     * cursor. The PC still replies — the protocol has no one-way messages — and the reply is simply
     * dropped, which costs a few small frames and keeps the gesture smooth.
     *
     * Everything that can fail in a way the owner needs to see still uses [request].
     */
    override fun notify(action: String, params: JsonElement?): Boolean {
        val live = socket ?: return false
        val id = "n-${requestCounter.incrementAndGet()}"
        val envelope = RequestEnvelope(id = id, action = action, params = params)

        return live.send(NexusJson.encodeToString(RequestEnvelope.serializer(), envelope))
    }

    private suspend fun send(action: String, params: JsonElement?, timeoutMs: Long): ResponseEnvelope {
        val live = socket ?: throw NotConnectedException()
        val id = "a-${requestCounter.incrementAndGet()}"
        val waiter = CompletableDeferred<ResponseEnvelope>()

        pending[id] = waiter
        try {
            val envelope = RequestEnvelope(id = id, action = action, params = params)

            if (!live.send(NexusJson.encodeToString(RequestEnvelope.serializer(), envelope))) {
                throw NotConnectedException()
            }

            return withTimeoutOrNull(timeoutMs) { waiter.await() }
                ?: throw NotConnectedException()
        } finally {
            pending.remove(id)
        }
    }

    // --- Connection lifecycle -----------------------------------------------------------------

    private suspend fun maintainConnection(resolved: Result<NexusEndpoint>, credential: NexusCredential) {
        var attempt = 0

        val endpoint = resolved.getOrElse {
            // A stored endpoint that no longer passes validation is a refusal, not a retry: no
            // amount of waiting turns a LAN address into a tailnet one.
            _state.value = ConnectionState.Failed(FailureReason.ProtocolError, willRetry = false, attempt)
            return
        }

        while (true) {
            _state.value = ConnectionState.Connecting

            val outcome = runSession(endpoint, credential)

            if (outcome == FailureReason.AuthenticationFailed) {
                _state.value = ConnectionState.Failed(outcome, willRetry = false, attempt)
                return
            }

            attempt++
            val reason = outcome ?: classifyUnreachable()
            _state.value = ConnectionState.Failed(reason, willRetry = true, attempt)

            delay(backoffMs(attempt))
        }
    }

    /**
     * One connection attempt: connect, authenticate, then pump until the socket ends.
     *
     * @return the reason it ended, or null when the cause was an ordinary drop.
     */
    private suspend fun runSession(endpoint: NexusEndpoint, credential: NexusCredential): FailureReason? {
        val incoming = Channel<String>(Channel.BUFFERED)
        val closed = CompletableDeferred<Unit>()

        val request = Request.Builder()
            .url(endpoint.webSocketUrl)
            // PC Core echoes this when it recognises it; it treats an absent subprotocol as fine.
            .addHeader("Sec-WebSocket-Protocol", NexusProtocol.SUBPROTOCOL)
            .build()

        val live = http.newWebSocket(
            request,
            object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    incoming.trySend(text)
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    // Binary means one thing on this connection: a desktop frame. It goes straight
                    // to the frame flow rather than through the request pump, so a stream running at
                    // fifteen frames a second never queues behind a command or delays one.
                    val frame = StreamFrame.parse(bytes.toByteArray())

                    if (frame == null) {
                        Log.d(TAG, "Discarded a malformed binary message")
                        return
                    }

                    _frames.tryEmit(frame)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    // Never logs the throwable's message at a level that could carry request data.
                    Log.d(TAG, "WebSocket failed: ${t.javaClass.simpleName}")
                    incoming.close()
                    closed.complete(Unit)
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(NORMAL_CLOSURE, null)
                    incoming.close()
                    closed.complete(Unit)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    incoming.close()
                    closed.complete(Unit)
                }
            },
        )

        socket = live

        try {
            val challenge = withTimeoutOrNull(HANDSHAKE_TIMEOUT_MS) { readChallenge(incoming) }
                ?: return null

            _state.value = ConnectionState.Authenticating

            val proof = NexusCrypto.signAuth(
                pcInstanceId = credential.pcInstanceId,
                deviceId = credential.deviceId,
                sharedSecretB64Url = credential.sharedSecret,
                challengeB64Url = challenge.challenge,
            )

            val authRequest = RequestEnvelope(
                id = "auth-${requestCounter.incrementAndGet()}",
                action = NexusProtocol.Actions.AUTHENTICATE,
                params = NexusJson.encodeToJsonElement(
                    AuthenticateParams.serializer(),
                    AuthenticateParams(challenge.challengeId, credential.deviceId, proof),
                ),
            )

            if (!live.send(NexusJson.encodeToString(RequestEnvelope.serializer(), authRequest))) {
                return null
            }

            val reply = withTimeoutOrNull(HANDSHAKE_TIMEOUT_MS) {
                readResponse(incoming, authRequest.id)
            } ?: return null

            if (!reply.success) {
                return if (reply.error?.code == NexusProtocol.Errors.AUTHENTICATION_FAILED) {
                    FailureReason.AuthenticationFailed
                } else {
                    FailureReason.ProtocolError
                }
            }

            val session = reply.result
                ?.let { NexusJson.decodeFromJsonElement(AuthenticatedSession.serializer(), it) }
                ?: return FailureReason.ProtocolError

            _state.value = ConnectionState.Connected(
                deviceId = session.deviceId.orEmpty(),
                sessionId = session.sessionId.orEmpty(),
                pcInstanceId = session.pcInstanceId.orEmpty(),
                allowedRoots = session.allowedRoots,
            )

            pump(incoming)
            return null
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.d(TAG, "Session ended: ${e.javaClass.simpleName}")
            return null
        } finally {
            failPending()
            socket = null
            live.cancel()
        }
    }

    /** Reads until the socket ends, dispatching responses to waiters and events to subscribers. */
    private suspend fun pump(incoming: Channel<String>) {
        for (message in incoming) {
            dispatch(message)
        }
    }

    private suspend fun readChallenge(incoming: Channel<String>): AuthChallenge? {
        for (message in incoming) {
            val event = decodeEvent(message) ?: continue
            if (event.eventName != NexusProtocol.Events.CHALLENGE) continue

            return event.payload?.let { NexusJson.decodeFromJsonElement(AuthChallenge.serializer(), it) }
        }
        return null
    }

    private suspend fun readResponse(incoming: Channel<String>, id: String): ResponseEnvelope? {
        for (message in incoming) {
            val type = peekType(message)

            if (type == NexusProtocol.MessageTypes.EVENT) {
                decodeEvent(message)?.let { _events.tryEmit(it) }
                continue
            }

            val response = decodeResponse(message) ?: continue
            if (response.id == id) return response
        }
        return null
    }

    private fun dispatch(message: String) {
        when (peekType(message)) {
            NexusProtocol.MessageTypes.EVENT -> decodeEvent(message)?.let { _events.tryEmit(it) }

            NexusProtocol.MessageTypes.RESPONSE -> decodeResponse(message)?.let { response ->
                pending.remove(response.id)?.complete(response)
            }

            else -> Log.d(TAG, "Ignoring a message of unrecognised type")
        }
    }

    private fun peekType(message: String): String? = try {
        NexusJson.parseToJsonElement(message)
            .let { it as? kotlinx.serialization.json.JsonObject }
            ?.get("type")
            ?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content }
    } catch (_: Exception) {
        null
    }

    private fun decodeEvent(message: String): EventEnvelope? = try {
        NexusJson.decodeFromString(EventEnvelope.serializer(), message)
    } catch (_: Exception) {
        null
    }

    private fun decodeResponse(message: String): ResponseEnvelope? = try {
        NexusJson.decodeFromString(ResponseEnvelope.serializer(), message)
    } catch (_: Exception) {
        null
    }

    private fun failPending() {
        pending.keys.toList().forEach { id ->
            pending.remove(id)?.cancel(CancellationException("connection ended"))
        }
    }

    private fun closeSocket(reason: String) {
        socket?.close(NORMAL_CLOSURE, reason)
        socket = null
        failPending()
    }

    /**
     * Turns "the socket did not come up" into something specific, by asking what the phone itself
     * can see. Ordering matters: no network at all explains everything after it.
     */
    private fun classifyUnreachable(): FailureReason = when {
        !probe.hasNetwork() -> FailureReason.NoNetwork
        !probe.hasTailnetAddress() -> FailureReason.PrivateNetworkUnavailable
        else -> FailureReason.PcUnreachable
    }

    private fun backoffMs(attempt: Int): Long =
        (INITIAL_BACKOFF_MS shl (attempt - 1).coerceIn(0, 5)).coerceAtMost(MAX_BACKOFF_MS)

    @kotlinx.serialization.Serializable
    private data class AuthenticateParams(
        @kotlinx.serialization.SerialName("challenge_id") val challengeId: String,
        @kotlinx.serialization.SerialName("device_id") val deviceId: String,
        val proof: String,
    )

    companion object {
        private const val TAG = "NexusConnection"
        private const val NORMAL_CLOSURE = 1000

        const val DEFAULT_REQUEST_TIMEOUT_MS = 20_000L
        const val HANDSHAKE_TIMEOUT_MS = 15_000L
        const val INITIAL_BACKOFF_MS = 2_000L
        const val MAX_BACKOFF_MS = 30_000L

        fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            // Keeps a silent tailnet route from looking like a healthy connection. PC Core sends
            // WebSocket pings every 30s, so 60 is two missed intervals.
            .pingInterval(20, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .retryOnConnectionFailure(false)
            .build()
    }
}

/**
 * Enrollment: the one exchange that happens before a credential exists.
 *
 * Kept out of [PcConnectionManager] on purpose. The manager's whole job is to own an authenticated
 * session; enrollment has no identity, no session, and no reconnection — it is a single request
 * that either yields a credential or does not, after which the socket is finished.
 */
class PcEnrollmentClient(private val http: OkHttpClient = PcConnectionManager.defaultClient()) {

    suspend fun enroll(
        endpoint: NexusEndpoint,
        pcInstanceId: String,
        sessionId: String,
        pairingSecret: String,
        displayName: String,
    ): EnrollmentResult {
        val incoming = Channel<String>(Channel.BUFFERED)

        val live = http.newWebSocket(
            Request.Builder()
                .url(endpoint.webSocketUrl)
                .addHeader("Sec-WebSocket-Protocol", NexusProtocol.SUBPROTOCOL)
                .build(),
            object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    incoming.trySend(text)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    incoming.close()
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    incoming.close()
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    incoming.close()
                }
            },
        )

        try {
            val challenge = withTimeoutOrNull(PcConnectionManager.HANDSHAKE_TIMEOUT_MS) {
                readChallenge(incoming)
            } ?: throw NotConnectedException()

            val request = RequestEnvelope(
                id = "enroll-1",
                action = NexusProtocol.Actions.ENROLL,
                params = NexusJson.encodeToJsonElement(
                    EnrollParams.serializer(),
                    EnrollParams(
                        challengeId = challenge.challengeId,
                        sessionId = sessionId,
                        proof = NexusCrypto.signEnrollment(
                            pcInstanceId, sessionId, pairingSecret, challenge.challenge,
                        ),
                        displayName = displayName,
                    ),
                ),
            )

            if (!live.send(NexusJson.encodeToString(RequestEnvelope.serializer(), request))) {
                throw NotConnectedException()
            }

            val reply = withTimeoutOrNull(PcConnectionManager.HANDSHAKE_TIMEOUT_MS) {
                readResponse(incoming, request.id)
            } ?: throw NotConnectedException()

            if (!reply.success) {
                throw NexusRequestException(
                    reply.error ?: ProtocolError(NexusProtocol.Errors.PAIRING_FAILED, "Pairing failed."),
                )
            }

            return reply.result
                ?.let { NexusJson.decodeFromJsonElement(EnrollmentResult.serializer(), it) }
                ?: throw NexusRequestException(
                    ProtocolError(NexusProtocol.Errors.PAIRING_FAILED, "The PC returned no credential."),
                )
        } finally {
            live.cancel()
        }
    }

    private suspend fun readChallenge(incoming: Channel<String>): AuthChallenge? {
        for (message in incoming) {
            val event = try {
                NexusJson.decodeFromString(EventEnvelope.serializer(), message)
            } catch (_: Exception) {
                continue
            }

            if (event.eventName == NexusProtocol.Events.CHALLENGE) {
                return event.payload?.let { NexusJson.decodeFromJsonElement(AuthChallenge.serializer(), it) }
            }
        }
        return null
    }

    private suspend fun readResponse(incoming: Channel<String>, id: String): ResponseEnvelope? {
        for (message in incoming) {
            val response = try {
                NexusJson.decodeFromString(ResponseEnvelope.serializer(), message)
            } catch (_: Exception) {
                continue
            }

            if (response.type == NexusProtocol.MessageTypes.RESPONSE && response.id == id) return response
        }
        return null
    }

    @kotlinx.serialization.Serializable
    private data class EnrollParams(
        @kotlinx.serialization.SerialName("challenge_id") val challengeId: String,
        @kotlinx.serialization.SerialName("session_id") val sessionId: String,
        val proof: String,
        @kotlinx.serialization.SerialName("display_name") val displayName: String,
    )
}
