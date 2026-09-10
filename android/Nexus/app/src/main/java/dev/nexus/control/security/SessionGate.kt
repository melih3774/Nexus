package dev.nexus.control.security

import android.util.Log
import dev.nexus.control.net.ConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** What the app is allowed to show and do right now. */
sealed interface SessionGateState {

    /** Nothing but the lock screen. Screen, control, files and security are all unavailable. */
    data class Locked(val reason: LockReason, val message: String? = null) : SessionGateState

    /** The system prompt is on screen. */
    data object Authenticating : SessionGateState

    /**
     * The owner authenticated here. [remoteAuthorized] says whether the PC has agreed as well —
     * false while the connection is still coming up, or if the PC refused.
     */
    data class Unlocked(val remoteAuthorized: Boolean, val message: String? = null) : SessionGateState

    val isUnlocked: Boolean get() = this is Unlocked
}

/**
 * The lock, and the thing that turns a fingerprint into authority over the PC.
 *
 * **Two halves that must not drift apart.** Locking the app is a UI decision and could be faked by
 * anything with a debugger; authority over the PC is a decision PC Core makes, and it makes it on a
 * signature from a key Android will not release without a real prompt. This class is where the two
 * are joined: unlocking always tries to open the remote session, and locking always tries to close
 * it. If the second half fails, the first does not pretend otherwise — the state carries
 * `remoteAuthorized` and the UI says so.
 *
 * **Why locking is eager and unlocking is not.** A lock is applied here immediately and told to the
 * PC afterwards, best effort, because the PC expires the session on its own anyway and a phone that
 * cannot reach its PC must still be able to lock. An unlock is the other way round: the app is only
 * unlocked once the prompt has actually succeeded, and the PC is only asked afterwards.
 */
class SessionGate(
    private val scope: CoroutineScope,
    private val connection: StateFlow<ConnectionState>,
    private val client: OwnerSessionClient,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val _state = MutableStateFlow<SessionGateState>(SessionGateState.Locked(LockReason.Started))
    val state: StateFlow<SessionGateState> = _state.asStateFlow()

    private var authenticatedAtMs: Long? = null
    private var leftForegroundAtMs: Long? = null
    private var screenLockedSinceAuth: Boolean = false

    /** Held in memory only. Process death loses it, and that is the feature. */
    private var ticket: String? = null

    private var keepAlive: Job? = null
    private var establishing: Job? = null

    init {
        scope.launch {
            connection.collect { state ->
                // Every fresh session needs its own authority: the grant belonged to a connection
                // that no longer exists, and the ticket is what carries the owner's authentication
                // across without asking them again.
                if (state is ConnectionState.Connected && _state.value.isUnlocked) {
                    establish(state)
                }
            }
        }
    }

    /** The app came to the foreground. Decides whether the owner has to prove themselves again. */
    fun onForeground() {
        val reason = SessionLockPolicy.requiresAuthentication(
            authenticatedAtMs = authenticatedAtMs,
            leftForegroundAtMs = leftForegroundAtMs,
            screenLockedSinceAuth = screenLockedSinceAuth,
            nowMs = clock(),
        )

        leftForegroundAtMs = null

        if (reason != null) {
            lock(reason)
            return
        }

        startKeepAlive()
        (connection.value as? ConnectionState.Connected)?.let { establish(it) }
    }

    /** The app left the foreground. The clock starts; nothing is locked yet. */
    fun onBackground() {
        leftForegroundAtMs = clock()
        keepAlive?.cancel()
        keepAlive = null
    }

    /**
     * The phone's screen went off.
     *
     * Locks at once rather than starting a timer. Turning the screen off is the owner putting the
     * phone down, and a phone on a table with a live remote session is the situation the whole lock
     * exists for.
     */
    fun onScreenOff() {
        screenLockedSinceAuth = true

        if (_state.value.isUnlocked) {
            lock(LockReason.ScreenLocked)
        }
    }

    /** The owner asked to lock, or something decided the session should end. */
    fun lock(reason: LockReason, message: String? = null) {
        ticket = null
        authenticatedAtMs = null
        keepAlive?.cancel()
        keepAlive = null
        establishing?.cancel()
        establishing = null

        _state.value = SessionGateState.Locked(reason, message)

        scope.launch { client.lock() }
    }

    /**
     * Runs the system prompt and, if it succeeds, opens the session on the PC.
     *
     * The prompt is Android's own: Nexus asks a yes-or-no question and never sees a fingerprint, a
     * face or a PIN. A refusal leaves the app open and locked, which is deliberate — the owner can
     * see that their PC is there and try again, and nothing about the machine is reachable until
     * they do.
     */
    suspend fun authenticate(prompt: OwnerConfirmationPrompt) {
        if (_state.value is SessionGateState.Authenticating) {
            return
        }

        _state.value = SessionGateState.Authenticating

        val outcome = prompt.confirm(
            title = "Unlock Nexus",
            subtitle = "Confirm it is you before controlling your PC",
        )

        when (outcome) {
            OwnerConfirmation.CONFIRMED -> {
                authenticatedAtMs = clock()
                screenLockedSinceAuth = false

                val connected = connection.value as? ConnectionState.Connected

                if (connected == null) {
                    // Unlocked locally with nothing to unlock remotely yet. The connection is coming
                    // up, and the collector below opens the session the moment it does.
                    _state.value = SessionGateState.Unlocked(remoteAuthorized = false)
                } else {
                    // Deliberately still Authenticating while the PC is asked. Unlocking the UI
                    // first would show the owner a home screen whose every action the PC would
                    // refuse for the next few hundred milliseconds.
                    establish(connected)
                    establishing?.join()
                }

                startKeepAlive()
            }

            OwnerConfirmation.DECLINED ->
                _state.value = SessionGateState.Locked(LockReason.NotAuthenticated)

            OwnerConfirmation.UNAVAILABLE ->
                _state.value = SessionGateState.Locked(
                    LockReason.NotAuthenticated,
                    "This phone has no biometric or screen lock set up. Nexus cannot confirm who is " +
                        "holding it, so it will not control your PC. Set a screen lock in Android settings.",
                )
        }
    }

    private fun establish(connected: ConnectionState.Connected) {
        establishing?.cancel()

        establishing = scope.launch {
            val held = ticket

            val (result, issued) = if (held != null) {
                client.resume(held).let { (outcome, fresh) ->
                    if (outcome is OwnerSessionResult.NeedsAuthentication) {
                        // The ticket is spent. The Keystore key may still be inside its window, in
                        // which case this costs the owner nothing.
                        client.authorize(connected.pcInstanceId, connected.deviceId)
                    } else {
                        outcome to fresh
                    }
                }
            } else {
                client.authorize(connected.pcInstanceId, connected.deviceId)
            }

            when (result) {
                OwnerSessionResult.Authorized -> {
                    ticket = issued ?: ticket
                    _state.value = SessionGateState.Unlocked(remoteAuthorized = true)
                }

                OwnerSessionResult.NeedsAuthentication -> {
                    Log.d(TAG, "The owner key needs a fresh prompt.")
                    lock(LockReason.AwayTooLong)
                }

                OwnerSessionResult.KeyRejected -> lock(
                    LockReason.SessionEnded,
                    "Your PC does not recognise this phone's security key. That happens after a new " +
                        "fingerprint is enrolled or Nexus is reinstalled. Clear the old key on the PC, " +
                        "then unlock again.",
                )

                is OwnerSessionResult.Failed ->
                    // Connected, and refused. That is a fact the owner needs rather than a home
                    // screen whose buttons all fail, so it goes back to the lock screen with the
                    // reason on it.
                    lock(LockReason.SessionEnded, result.message)
            }
        }
    }

    /**
     * Keeps the PC's fifteen-minute window open while the owner is actually looking at the app.
     *
     * The window slides on activity, and watching a stream is not activity as far as the request
     * path is concerned — a phone showing a desktop for twenty minutes sends nothing. Without this,
     * the picture would stop mid-session for a reason nobody could see. It runs only in the
     * foreground, so backgrounding still expires the session on schedule.
     */
    private fun startKeepAlive() {
        keepAlive?.cancel()

        keepAlive = scope.launch {
            while (true) {
                delay(KEEP_ALIVE_MS)

                if (!_state.value.isUnlocked) {
                    return@launch
                }

                client.status()
            }
        }
    }

    private companion object {
        const val TAG = "NexusSessionGate"

        /** Comfortably inside the PC's fifteen minutes, and rare enough to cost nothing. */
        const val KEEP_ALIVE_MS = 4 * 60 * 1000L
    }
}
