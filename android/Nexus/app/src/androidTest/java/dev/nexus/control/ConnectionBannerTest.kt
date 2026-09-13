package dev.nexus.control

import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.assertIsDisplayed
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.FailureReason
import dev.nexus.control.ui.ConnectionBanner
import dev.nexus.control.ui.theme.NexusTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The banner is the only UI worth an instrumented test in v0.5.
 *
 * Nexus cannot install Tailscale, wake the PC, or repair a credential — every failure it reports is
 * something the owner has to act on. A banner that said "could not connect" would leave them
 * guessing between four unrelated problems, so each state has to name its own. This is that
 * requirement, enforced.
 */
@RunWith(AndroidJUnit4::class)
class ConnectionBannerTest {

    @get:Rule
    val compose = createComposeRule()

    private fun show(state: ConnectionState) {
        compose.setContent {
            NexusTheme { ConnectionBanner(state = state, onRetry = {}) }
        }
    }

    @Test
    fun connectedSaysSoWithoutExplaining() {
        show(ConnectionState.Connected("d", "s", "pc", emptyList()))
        compose.onNodeWithText("Connected").assertIsDisplayed()
    }

    @Test
    fun noNetworkBlamesThePhonesConnection() {
        show(ConnectionState.Failed(FailureReason.NoNetwork, willRetry = true, attempt = 1))

        compose.onNodeWithText("No network").assertIsDisplayed()
        compose.onNodeWithText("This phone has no internet connection.").assertIsDisplayed()
    }

    @Test
    fun aMissingTailnetNamesTailscaleAndWhatToDo() {
        show(ConnectionState.Failed(FailureReason.PrivateNetworkUnavailable, willRetry = true, attempt = 1))

        compose.onNodeWithText("Tailscale not connected").assertIsDisplayed()
        compose.onNodeWithText("Open Tailscale on this phone and connect, then retry.").assertIsDisplayed()
    }

    @Test
    fun anUnreachablePcListsWhatToCheckOnThePc() {
        show(ConnectionState.Failed(FailureReason.PcUnreachable, willRetry = true, attempt = 3))

        compose.onNodeWithText("PC unreachable").assertIsDisplayed()
    }

    @Test
    fun authenticationFailureSaysToPairAgain() {
        // The one failure that will not fix itself, so it must not read like the others.
        show(ConnectionState.Failed(FailureReason.AuthenticationFailed, willRetry = false, attempt = 1))

        // Named for the credential, not "authentication", since v1.1.1: the owner's fingerprint prompt
        // is also called authentication, and it is not what failed here.
        compose.onNodeWithText("This phone is not the paired one").assertIsDisplayed()
    }

    @Test
    fun everyFailureOffersRetryAndNoneSaysUnknown() {
        // One composition, driven through every failure: a test rule can only set content once.
        val current = mutableStateOf<ConnectionState>(
            ConnectionState.Failed(FailureReason.NoNetwork, willRetry = true, attempt = 1),
        )

        compose.setContent {
            NexusTheme { ConnectionBanner(state = current.value, onRetry = {}) }
        }

        for (reason in FailureReason.entries) {
            current.value = ConnectionState.Failed(reason, willRetry = true, attempt = 1)
            compose.waitForIdle()

            compose.onNodeWithText("Retry").assertIsDisplayed()
            compose.onAllNodesWithText("Unknown error").assertCountEquals(0)
        }
    }
}
