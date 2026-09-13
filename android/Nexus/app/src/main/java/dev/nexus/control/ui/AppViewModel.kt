package dev.nexus.control.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.data.PairedPc
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.PairingCode
import dev.nexus.control.protocol.SystemInfo
import dev.nexus.control.security.SessionGate
import dev.nexus.control.update.UpdateManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

/** Where pairing has got to. Drives the whole unpaired-to-paired journey. */
sealed interface PairingProgress {
    data object Idle : PairingProgress
    data object Working : PairingProgress
    data class Failed(val message: String) : PairingProgress
    data class Done(val pc: PairedPc) : PairingProgress
}

data class AppUiState(
    val pairedPc: PairedPc? = null,
    val connection: ConnectionState = ConnectionState.Idle,
    val systemInfo: SystemInfo? = null,
) {
    val isPaired: Boolean get() = pairedPc != null
}

/**
 * The state the whole app shares: whether a PC is paired, what the connection is doing, and enough
 * identification to show whose machine this is.
 *
 * Per-feature view models own their own screen's data. This one owns nothing a screen could own.
 */
class AppViewModel(
    private val repository: NexusRepository,

    /**
     * The lock, passed through rather than owned. It lives in the application container because it
     * outlives every screen; the shell needs a handle on it, and reaching into the container from a
     * composable would put an application reference where the view model already has one.
     */
    val gate: SessionGate,

    /** Updating this app. Held by the container for the same reason the gate is. */
    val updates: UpdateManager,
) : ViewModel() {

    private val systemInfo = MutableStateFlow<SystemInfo?>(null)

    private val _pairing = MutableStateFlow<PairingProgress>(PairingProgress.Idle)
    val pairing: StateFlow<PairingProgress> = _pairing.asStateFlow()

    val uiState: StateFlow<AppUiState> =
        combine(repository.pairedPc, repository.connectionState, systemInfo) { pc, connection, info ->
            AppUiState(pc, connection, info)
        }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), AppUiState())

    init {
        // A fresh session means a fresh identification: the PC may have restarted, or this may be
        // a different machine answering on the same address.
        viewModelScope.launch {
            repository.connectionState.collect { state ->
                if (state is ConnectionState.Connected) {
                    systemInfo.value = runCatching { repository.systemInfo() }.getOrNull()
                } else if (state !is ConnectionState.Authenticating) {
                    systemInfo.value = null
                }
            }
        }
    }

    /**
     * Whether a PC is paired, answered synchronously.
     *
     * **Not the same as `uiState.value.isPaired`, and the difference was a real bug.** [uiState] is
     * a `stateIn` whose first value is an empty [AppUiState], so `isPaired` reads false until the
     * combined flow emits — which happens after `ON_START` has already run. Opening the app
     * therefore skipped `connect()` and left it sitting on "Disconnected" until something made the
     * lifecycle start again, at which point it connected immediately.
     *
     * That is almost certainly what was being blamed on Tailscale: the owner opened Nexus, saw no
     * connection, went to check the VPN, came back — and coming back is what actually fixed it.
     *
     * The credential itself is loaded in `Application.onCreate`, before anything composes, so this
     * is already correct at the instant the lifecycle starts.
     */
    val isPairedNow: Boolean get() = repository.pairedPc.value != null

    /** Called when the app comes to the foreground. */
    fun connect() = repository.connect()

    /** Called when the app leaves the foreground: no background socket in v0.5. */
    fun disconnect() = repository.disconnect()

    fun retry() {
        repository.disconnect()
        repository.retryAfterRefusal()
    }

    fun pair(rawCode: String, displayName: String) {
        _pairing.value = PairingProgress.Working

        viewModelScope.launch {
            val code = PairingCode.parse(rawCode).getOrElse {
                _pairing.value = PairingProgress.Failed(it.message ?: "That pairing code cannot be used.")
                return@launch
            }

            // Checked before a socket is opened, so an endpoint the app would refuse never reaches
            // the network stack at all.
            code.endpoint().onFailure {
                _pairing.value = PairingProgress.Failed(endpointMessage(code, it))
                return@launch
            }

            repository.pair(code, displayName)
                .onSuccess { _pairing.value = PairingProgress.Done(it) }
                .onFailure { _pairing.value = PairingProgress.Failed(pairingMessage(it)) }
        }
    }

    fun dismissPairingError() {
        _pairing.value = PairingProgress.Idle
    }

    fun forgetPc() {
        repository.forgetPc()
        _pairing.value = PairingProgress.Idle
    }

    private fun endpointMessage(code: PairingCode, cause: Throwable): String =
        if (code.host == null) {
            "This pairing code has no PC address in it, which means Tailscale was not connected on " +
                "the PC when it was created. Connect Tailscale there and make a new code."
        } else {
            cause.message ?: "That address cannot be used."
        }

    private fun pairingMessage(cause: Throwable): String = when {
        cause is NexusRequestException && cause.error.code == NexusProtocol.Errors.PAIRING_FAILED ->
            "The PC refused the pairing code. Codes are single-use and expire after a few minutes — " +
                "start pairing again on the PC."

        cause is NexusRequestException && cause.error.code == NexusProtocol.Errors.ALREADY_PAIRED ->
            "That PC already has a paired phone. Unpair it on the PC first."

        else ->
            "Could not reach the PC over Tailscale. Check that Tailscale is connected on both this " +
                "phone and the PC, and that PC Core is running."
    }

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                AppViewModel(container.repository, container.gate, container.updates)
            }
        }
    }
}
