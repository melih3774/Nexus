package dev.nexus.control.security

/** Why Nexus is asking for the owner again. Shown on the lock screen, so it has to be true. */
enum class LockReason {
    /** The app was launched, or Android recreated the process. Nothing carries over. */
    Started,

    /** The phone's screen went off, which is the owner putting it down. */
    ScreenLocked,

    /** The app was in the background long enough that the session should not be assumed. */
    AwayTooLong,

    /** The owner cancelled or failed the prompt. */
    NotAuthenticated,

    /** The PC withdrew the authorisation, or the window elapsed there. */
    SessionEnded,
}

/**
 * When Nexus must ask for the owner again, as one testable rule.
 *
 * **Why this is separated from everything that calls it.** The rule is a handful of conditions and
 * every one of them is a security decision — "the phone was locked", "it has been away long enough",
 * "we have never authenticated". Spread across a lifecycle callback, a broadcast receiver and a view
 * model, they would be three places to get it wrong and nowhere to test it. Here they are one
 * function with a clock passed in.
 */
object SessionLockPolicy {

    /**
     * How long backgrounding is forgiven.
     *
     * Fifteen minutes. Short backgrounding is the normal shape of using a phone — answering a
     * message, checking a code, taking a call — and demanding a fingerprint each time would train
     * the owner to tap past the prompt, which is worse than the interval being generous. The
     * Keystore key enforces the same fifteen minutes independently.
     */
    const val BACKGROUND_GRACE_MS: Long = 15 * 60 * 1000

    /**
     * Whether returning to the foreground needs authentication.
     *
     * @param authenticatedAtMs when the owner last satisfied the prompt, or null if never.
     * @param leftForegroundAtMs when the app was last backgrounded, or null if it never has been.
     * @param screenLockedSinceAuth whether the phone's screen went off since that authentication.
     */
    fun requiresAuthentication(
        authenticatedAtMs: Long?,
        leftForegroundAtMs: Long?,
        screenLockedSinceAuth: Boolean,
        nowMs: Long,
        graceMs: Long = BACKGROUND_GRACE_MS,
    ): LockReason? = when {
        authenticatedAtMs == null -> LockReason.Started

        // Deliberately ahead of the timer. Locking the phone is the owner deciding they are done
        // with it, and a five-second lock says that as clearly as an hour does.
        screenLockedSinceAuth -> LockReason.ScreenLocked

        leftForegroundAtMs != null && nowMs - leftForegroundAtMs > graceMs -> LockReason.AwayTooLong

        // A clock that went backwards - a manual time change, or a reboot - is not evidence that the
        // session is still fresh, and treating it as such is exactly how a grace period becomes
        // indefinite.
        nowMs < authenticatedAtMs -> LockReason.AwayTooLong

        nowMs - authenticatedAtMs > MAXIMUM_SESSION_MS -> LockReason.AwayTooLong

        else -> null
    }

    /**
     * A ceiling on one authentication, however active the owner is.
     *
     * Twelve hours, matching what PC Core will hold a ticket for. A session that has been alive
     * since this morning is still a session nobody has re-proved they are holding.
     */
    const val MAXIMUM_SESSION_MS: Long = 12 * 60 * 60 * 1000
}
