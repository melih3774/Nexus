package dev.nexus.control.data

import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.net.PcEnrollmentClient
import dev.nexus.control.protocol.CaptureScreenResult
import dev.nexus.control.protocol.EventEnvelope
import dev.nexus.control.protocol.ListFilesResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.PairingCode
import dev.nexus.control.protocol.ProcessList
import dev.nexus.control.protocol.SecurityCommandResult
import dev.nexus.control.protocol.SecurityStateResult
import dev.nexus.control.protocol.StatResult
import dev.nexus.control.protocol.SystemInfo
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.DeserializationStrategy
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/**
 * What the app knows about the PC, and how it asks the PC for more.
 *
 * One object, sitting between the screens and the connection. It is not a layer for its own sake:
 * pairing writes a credential and starts a session, screens make requests on that session, and
 * forgetting the PC has to do both in the right order. Spreading that across view models would put
 * the same three-line sequence in four places.
 */
class NexusRepository(
    private val credentials: CredentialStore,
    private val connection: PcConnectionManager,
    private val enrollment: PcEnrollmentClient,
) {
    private val _pairedPc = MutableStateFlow<PairedPc?>(null)

    /** The paired PC, credential-free. Null when this phone is not paired with anything. */
    val pairedPc: StateFlow<PairedPc?> = _pairedPc.asStateFlow()

    val connectionState: StateFlow<ConnectionState> get() = connection.state

    val events: SharedFlow<EventEnvelope> get() = connection.events

    /** The roots PC Core said this session may browse, learned at authentication. */
    val allowedRoots: List<String>
        get() = (connection.state.value as? ConnectionState.Connected)?.allowedRoots.orEmpty()

    /** Loads any stored credential. Call once, before the first screen renders. */
    fun restore() {
        _pairedPc.value = credentials.load()?.describe()
    }

    /** Opens the connection if this phone is paired. Safe to call repeatedly. */
    fun connect() {
        credentials.load()?.let(connection::connect)
    }

    fun disconnect() = connection.disconnect()

    /**
     * Completes pairing: enroll over the tailnet, store what comes back, connect.
     *
     * The credential is sealed before the connection is attempted. If the app died between those
     * two steps the owner would have a paired phone that simply needs opening again — whereas
     * connecting first and storing after could burn a single-use pairing code and keep nothing.
     */
    suspend fun pair(code: PairingCode, displayName: String): Result<PairedPc> {
        val endpoint = code.endpoint().getOrElse { return Result.failure(it) }

        return try {
            val result = enrollment.enroll(
                endpoint = endpoint,
                pcInstanceId = code.pcInstanceId,
                sessionId = code.sessionId,
                pairingSecret = code.pairingSecret,
                displayName = displayName,
            )

            val credential = NexusCredential(
                pcInstanceId = result.pcInstanceId,
                deviceId = result.deviceId,
                sharedSecret = result.sharedSecret,
                host = endpoint.host,
                port = endpoint.port,
                pairedAt = result.pairedAtUtc,
            )

            credentials.save(credential)
            _pairedPc.value = credential.describe()

            connection.connect(credential)
            Result.success(credential.describe())
        } catch (e: Exception) {
            Result.failure(e)
        }
    }

    /**
     * Forgets the PC on this phone.
     *
     * Local only, by design. The phone is a controller, not the authority over who is trusted: the
     * PC keeps its record of the Primary Phone until the owner unpairs there, which is the same
     * boundary that stops a stolen phone revoking or re-issuing credentials.
     */
    fun forgetPc() {
        connection.disconnect()
        credentials.clear()
        _pairedPc.value = null
    }

    // --- Capabilities -------------------------------------------------------------------------

    suspend fun systemInfo(): SystemInfo =
        call(NexusProtocol.Actions.SYSTEM_INFO, null, SystemInfo.serializer())

    suspend fun processes(): ProcessList =
        call(NexusProtocol.Actions.LIST_PROCESSES, null, ProcessList.serializer(), timeoutMs = 30_000)

    suspend fun listFiles(path: String, includeHidden: Boolean = false): ListFilesResult = call(
        NexusProtocol.Actions.LIST_FILES,
        buildJsonObject {
            put("path", JsonPrimitive(path))
            put("include_hidden", JsonPrimitive(includeHidden))
        },
        ListFilesResult.serializer(),
        timeoutMs = 30_000,
    )

    suspend fun stat(path: String): StatResult = call(
        NexusProtocol.Actions.STAT_FILE,
        buildJsonObject { put("path", JsonPrimitive(path)) },
        StatResult.serializer(),
    )

    suspend fun captureScreen(maxWidth: Int): CaptureScreenResult = call(
        NexusProtocol.Actions.CAPTURE_SCREEN,
        buildJsonObject { put("max_width", JsonPrimitive(maxWidth)) },
        CaptureScreenResult.serializer(),
        // A screenshot is a large base64 payload over a phone link; it gets its own allowance.
        timeoutMs = 45_000,
    )

    suspend fun securityState(): SecurityStateResult =
        call(NexusProtocol.Actions.GET_SECURITY_STATE, null, SecurityStateResult.serializer())

    suspend fun arm(): SecurityCommandResult =
        call(NexusProtocol.Actions.ARM, null, SecurityCommandResult.serializer())

    suspend fun disarm(): SecurityCommandResult =
        call(NexusProtocol.Actions.DISARM, null, SecurityCommandResult.serializer())

    private suspend fun <T> call(
        action: String,
        params: JsonObject?,
        serializer: DeserializationStrategy<T>,
        timeoutMs: Long = PcConnectionManager.DEFAULT_REQUEST_TIMEOUT_MS,
    ): T {
        val result: JsonElement = connection.request(action, params, timeoutMs)
            ?: throw IllegalStateException("$action returned no result")

        return NexusJson.decodeFromJsonElement(serializer, result)
    }
}
