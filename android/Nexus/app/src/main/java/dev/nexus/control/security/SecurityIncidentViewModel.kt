package dev.nexus.control.security

import android.graphics.BitmapFactory
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.EvidenceSummary
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.SecuritySessionSummary
import dev.nexus.control.protocol.SecurityState
import dev.nexus.control.protocol.SessionDetail
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

data class SecurityIncidentUiState(
    val loading: Boolean = false,
    val error: String? = null,
    val state: SecurityState? = null,
    val active: SessionDetail? = null,
    val history: List<SecuritySessionSummary> = emptyList(),
    val opened: SessionDetail? = null,
    val busy: Boolean = false,

    /** Set when a confirmation was needed and not given, so the screen can explain rather than fail silently. */
    val confirmationMessage: String? = null,
)

/**
 * One piece of evidence, on screen.
 *
 * An image arrives decoded; a clip arrives as a file for the player. Both go through the same cache,
 * so opening the same item twice costs one download.
 */
data class ViewedEvidence(
    val summary: EvidenceSummary,
    val image: ImageBitmap? = null,

    /** The cached file, for video and audio. Null for images and for anything that failed. */
    val file: File? = null,

    val loading: Boolean = false,

    /** 0f..1f while bytes are arriving. */
    val progress: Float = 0f,

    /**
     * The PC no longer has this file. Held apart from [error] because "deleted or expired" is a
     * fact about the incident rather than a failure that retrying would fix.
     */
    val missing: Boolean = false,

    val error: String? = null,
)

/**
 * The Security screen's state: what is happening now, what happened before, and what the owner can
 * do about it.
 *
 * **The one thing it guards is acknowledgement.** `This was me` stands a security incident down, and
 * it does so from a device that might be sitting unlocked on the same desk as the PC. It therefore
 * goes through the phone's own owner confirmation first, and a refusal or an unconfigured device
 * both stop it — a check that passes when it cannot run would be worse than no check.
 *
 * `Not me` is deliberately not gated. It only ever increases suspicion, and making the owner
 * authenticate to raise an alarm is friction in exactly the wrong place.
 */
class SecurityIncidentViewModel(
    private val repository: NexusRepository,
    private val incidents: SecurityIncidentClient,
    private val evidenceCache: EvidenceCache,
) : ViewModel() {

    private val _state = MutableStateFlow(SecurityIncidentUiState())
    val state: StateFlow<SecurityIncidentUiState> = _state.asStateFlow()

    private val _evidence = MutableStateFlow<ViewedEvidence?>(null)
    val evidence: StateFlow<ViewedEvidence?> = _evidence.asStateFlow()

    init {
        // A live incident changes under the owner while they are looking at it: more evidence, a new
        // timeline entry, or a resolution made from the PC. Refreshing on the event keeps the screen
        // honest without polling.
        viewModelScope.launch {
            repository.events.collect { event ->
                when (event.eventName) {
                    NexusProtocol.Events.SECURITY_SESSION_STARTED,
                    NexusProtocol.Events.SECURITY_SESSION_UPDATED,
                    NexusProtocol.Events.SECURITY_SESSION_RESOLVED,
                    NexusProtocol.Events.SECURITY_STATE_CHANGED,
                    -> refresh(quiet = true)
                }
            }
        }
    }

    fun refresh(quiet: Boolean = false) {
        if (!quiet) {
            _state.value = _state.value.copy(loading = true, error = null)
        }

        viewModelScope.launch {
            try {
                val current = incidents.getSession()
                val history = incidents.listSessions()

                _state.value = _state.value.copy(
                    loading = false,
                    error = null,
                    state = current.securityState,
                    active = if (current.hasActiveSession) current.session else null,
                    history = history.sessions,
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(loading = false, error = describe(e))
            }
        }
    }

    fun open(sessionId: String) {
        _state.value = _state.value.copy(busy = true, error = null)

        viewModelScope.launch {
            try {
                _state.value = _state.value.copy(
                    busy = false,
                    opened = incidents.getSession(sessionId).session,
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(busy = false, error = describe(e))
            }
        }
    }

    fun closeOpened() {
        _state.value = _state.value.copy(opened = null)
    }

    /**
     * Acknowledges the incident, behind the phone's own owner confirmation.
     *
     * The confirmation runs first and its result is checked before anything is sent. An unavailable
     * prompt is a refusal, not a bypass.
     */
    fun thisWasMe(prompt: OwnerConfirmationPrompt) {
        _state.value = _state.value.copy(busy = true, error = null, confirmationMessage = null)

        viewModelScope.launch {
            val confirmation = try {
                prompt.confirm(
                    title = "Confirm it was you",
                    subtitle = "This ends the security session on your PC.",
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                OwnerConfirmation.DECLINED
            }

            if (confirmation != OwnerConfirmation.CONFIRMED) {
                _state.value = _state.value.copy(
                    busy = false,
                    confirmationMessage = when (confirmation) {
                        OwnerConfirmation.UNAVAILABLE ->
                            "Set up a screen lock or biometric on this phone to acknowledge an incident."

                        else -> "Not confirmed. The security session is still active."
                    },
                )
                return@launch
            }

            resolve { incidents.thisWasMe() }
        }
    }

    /**
     * Marks the incident as someone else.
     *
     * Not gated: it only ever raises suspicion, and asking the owner to authenticate before they can
     * report an intruder would be friction in the worst possible place.
     */
    fun notMe() {
        _state.value = _state.value.copy(busy = true, error = null, confirmationMessage = null)
        viewModelScope.launch { resolve { incidents.notMe() } }
    }

    private suspend fun resolve(action: suspend () -> Unit) {
        try {
            action()
            _state.value = _state.value.copy(busy = false)
            refresh(quiet = true)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            _state.value = _state.value.copy(busy = false, error = describe(e))
        }
    }

    /**
     * Fetches one piece of evidence and prepares it for display.
     *
     * Everything goes through the cache, which writes the bytes to a file and checks them against
     * the digest the PC recorded. Images are then decoded; video and audio are handed to the player
     * as a file, because `MediaPlayer` reads from a file descriptor and there is no local server to
     * point it at.
     *
     * Held one at a time. A session can hold dozens of items and keeping them all decoded would put
     * tens of megabytes of the owner's home on the heap for no reason.
     *
     * The session id is passed in by the caller rather than read from state, so an incident opened
     * from history works exactly as the active one does — resolving an incident decides what
     * happened, not whether its evidence can still be looked at.
     */
    fun viewEvidence(sessionId: String, summary: EvidenceSummary) {
        _evidence.value = ViewedEvidence(summary, loading = true)

        viewModelScope.launch {
            try {
                val fetched = evidenceCache.fetch(sessionId, summary) { progress ->
                    // Only while this item is still the one on screen: a slow clip that the owner
                    // closed must not overwrite whatever they opened next.
                    _evidence.value?.let { current ->
                        if (current.summary.evidenceId == summary.evidenceId && current.loading) {
                            _evidence.value = current.copy(progress = progress)
                        }
                    }
                }

                if (_evidence.value?.summary?.evidenceId != summary.evidenceId) return@launch

                _evidence.value = when (fetched) {
                    is EvidenceFetch.Missing ->
                        ViewedEvidence(summary, missing = true, error = fetched.reason)

                    is EvidenceFetch.Failed ->
                        ViewedEvidence(summary, error = fetched.reason)

                    is EvidenceFetch.Ready -> present(summary, fetched.file)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _evidence.value = ViewedEvidence(summary, error = describe(e))
            }
        }
    }

    /** Decodes an image, or hands a clip through as a file. */
    private suspend fun present(summary: EvidenceSummary, file: File): ViewedEvidence {
        if (summary.kind.isPlayable) {
            return ViewedEvidence(summary, file = file, progress = 1f)
        }

        val bitmap = withContext(Dispatchers.IO) {
            try {
                BitmapFactory.decodeFile(file.absolutePath)
            } catch (e: OutOfMemoryError) {
                null
            }
        }

        return ViewedEvidence(
            summary,
            image = bitmap?.asImageBitmap(),
            file = file,
            progress = 1f,
            error = if (bitmap == null) "The image could not be decoded." else null,
        )
    }

    fun closeEvidence() {
        _evidence.value = null
    }

    fun dismissMessage() {
        _state.value = _state.value.copy(error = null, confirmationMessage = null)
    }

    private fun describe(cause: Throwable): String = when {
        cause is NotConnectedException -> "Not connected to the PC."

        cause is NexusRequestException -> when (cause.error.code) {
            NexusProtocol.Errors.NOT_FOUND -> "That incident is no longer on the PC."

            NexusProtocol.Errors.NOT_AUTHORIZED ->
                "This phone is not permitted to do that. Security settings stay on the PC."

            else -> cause.error.message.ifBlank { "The PC refused that." }
        }

        else -> "Something went wrong talking to the PC."
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                SecurityIncidentViewModel(
                    container.repository,
                    container.incidents,
                    container.evidenceCache,
                )
            }
        }
    }
}
