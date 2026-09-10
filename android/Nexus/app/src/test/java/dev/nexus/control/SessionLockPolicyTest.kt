package dev.nexus.control

import dev.nexus.control.security.LockReason
import dev.nexus.control.security.SessionLockPolicy
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * When Nexus asks for the owner again.
 *
 * Every case here is a security decision, and the reason they are tested rather than trusted is
 * that each one is a single boolean somewhere in a lifecycle callback. The rule that matters most is
 * the one that says *no*: a short backgrounding must not prompt, because a prompt the owner sees
 * forty times a day is a prompt they stop reading.
 */
class SessionLockPolicyTest {

    private val minute = 60_000L
    private val now = 1_000_000_000L

    @Test
    fun `a cold launch always requires authentication`() {
        assertEquals(
            LockReason.Started,
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = null,
                leftForegroundAtMs = null,
                screenLockedSinceAuth = false,
                nowMs = now,
            ),
        )
    }

    @Test
    fun `a short backgrounding does not`() {
        assertNull(
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = now - 10 * minute,
                leftForegroundAtMs = now - 3 * minute,
                screenLockedSinceAuth = false,
                nowMs = now,
            ),
        )
    }

    @Test
    fun `fifteen minutes away does`() {
        assertEquals(
            LockReason.AwayTooLong,
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = now - 20 * minute,
                leftForegroundAtMs = now - 16 * minute,
                screenLockedSinceAuth = false,
                nowMs = now,
            ),
        )
    }

    /** Locking the phone is the owner deciding they are done, and beats any remaining grace. */
    @Test
    fun `the phone being locked requires authentication however briefly`() {
        assertEquals(
            LockReason.ScreenLocked,
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = now - 5_000,
                leftForegroundAtMs = now - 1_000,
                screenLockedSinceAuth = true,
                nowMs = now,
            ),
        )
    }

    /** Staying in the foreground is not a licence to stay unlocked forever. */
    @Test
    fun `a session older than the ceiling requires authentication even in the foreground`() {
        assertEquals(
            LockReason.AwayTooLong,
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = now - SessionLockPolicy.MAXIMUM_SESSION_MS - 1,
                leftForegroundAtMs = null,
                screenLockedSinceAuth = false,
                nowMs = now,
            ),
        )
    }

    /**
     * A clock that moved backwards is not evidence of freshness. Without this, changing the phone's
     * time by hand would turn a grace period into an indefinite one.
     */
    @Test
    fun `a clock that went backwards requires authentication`() {
        assertEquals(
            LockReason.AwayTooLong,
            SessionLockPolicy.requiresAuthentication(
                authenticatedAtMs = now + minute,
                leftForegroundAtMs = null,
                screenLockedSinceAuth = false,
                nowMs = now,
            ),
        )
    }

    @Test
    fun `the grace period is the fifteen minutes the PC also enforces`() {
        assertEquals(15 * minute, SessionLockPolicy.BACKGROUND_GRACE_MS)
    }
}
