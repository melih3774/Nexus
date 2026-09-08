package dev.nexus.control

import dev.nexus.control.protocol.SecurityState
import dev.nexus.control.ui.StateControl
import dev.nexus.control.ui.stateControlFor
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * What the Security screen may offer while an incident is open.
 *
 * **The bug.** During a live SECURITY_SESSION the screen showed a single full-width button in the
 * same position as the Arm control, labelled "Disarm". Pressing it sent `security.disarm`, which
 * PC Core accepts from SECURITY_SESSION, so the incident closed and the state went back to
 * OWNER_PRESENT — with no biometric prompt, and with the incident recorded as never having been
 * answered by anyone. On a real phone it was pressed by reflex, as the Arm control.
 *
 * PC Core was not at fault: `security.arm` is already refused from SECURITY_SESSION, and
 * `security.disarm` is genuinely a legitimate transition for an owner sitting at the machine. The
 * defect was that the phone offered it as an unlabelled, unverified alternative to answering the
 * question — so the rule about what may be offered is asserted here.
 */
class SecurityStateControlTest {

    @Test
    fun `owner present offers arm`() {
        assertEquals(StateControl.ARM, stateControlFor(SecurityState.OWNER_PRESENT, hasOpenIncident = false))
    }

    @Test
    fun `armed offers disarm`() {
        // Standing down before anything happened is not answering an incident: there is none.
        assertEquals(StateControl.DISARM, stateControlFor(SecurityState.ARMED, hasOpenIncident = false))
    }

    /** The regression. An open incident must not be closable from the state control. */
    @Test
    fun `an open incident is answered and never armed or disarmed away`() {
        val control = stateControlFor(SecurityState.SECURITY_SESSION, hasOpenIncident = true)

        assertEquals(StateControl.ANSWER_INCIDENT, control)
        assertNotEquals("disarm must not stand a live incident down", StateControl.DISARM, control)
        assertNotEquals("arm must not acknowledge a live incident", StateControl.ARM, control)
    }

    /**
     * The other half of the rule: never trap the owner.
     *
     * SECURITY_SESSION with nothing to answer is reachable — the incident can be resolved on the PC,
     * or its record pruned by retention — and the phone must still be able to stand the state down.
     */
    @Test
    fun `a security session with nothing left to answer can still be stood down`() {
        assertEquals(
            StateControl.DISARM,
            stateControlFor(SecurityState.SECURITY_SESSION, hasOpenIncident = false),
        )
    }

    @Test
    fun `an unknown state offers nothing`() {
        // Before the first response arrives. Guessing here would mean offering a control for a
        // state the PC may not be in.
        assertEquals(StateControl.NONE, stateControlFor(null, hasOpenIncident = false))
        assertEquals(StateControl.NONE, stateControlFor(null, hasOpenIncident = true))
    }

    @Test
    fun `an incident never changes what the other states offer`() {
        // hasOpenIncident is only meaningful in SECURITY_SESSION. If a stale incident were left in
        // the UI state, it must not be able to alter the OWNER_PRESENT or ARMED controls.
        assertEquals(StateControl.ARM, stateControlFor(SecurityState.OWNER_PRESENT, hasOpenIncident = true))
        assertEquals(StateControl.DISARM, stateControlFor(SecurityState.ARMED, hasOpenIncident = true))
    }
}
