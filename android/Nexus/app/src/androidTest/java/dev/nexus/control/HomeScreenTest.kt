package dev.nexus.control

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.nexus.control.data.PairedPc
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.FailureReason
import dev.nexus.control.protocol.SystemInfo
import dev.nexus.control.ui.AppUiState
import dev.nexus.control.ui.Destination
import dev.nexus.control.ui.HomeScreen
import dev.nexus.control.ui.theme.NexusTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The control panel, rendered.
 *
 * A live PC is not reachable from this environment, so the screen is driven with state it would
 * have received rather than state fetched over a tailnet. That still catches the things worth
 * catching here: whether the actions exist, whether they are reachable only when a session does,
 * and whether anything credential-shaped ends up on screen.
 */
@RunWith(AndroidJUnit4::class)
class HomeScreenTest {

    @get:Rule
    val compose = createComposeRule()

    private val pc = PairedPc(
        pcInstanceId = "e2df2a34-40f2-4ab5-9789-bcfc38452c2b",
        deviceId = "bYWObWzU6kQ9UMMZT3kCog",
        endpoint = "100.101.102.103:47812",
        pairedAt = "2026-08-29T12:34:56.789+00:00",
        credentialFingerprint = "1a2b3c4d",
    )

    private val info = SystemInfo(
        machineName = "DESKTOP-TEST01",
        osDescription = "Microsoft Windows 10.0.26200",
        pcCoreVersion = "0.5.0",
        processorCount = 16,
    )

    private fun render(state: AppUiState, onOpen: (Destination) -> Unit = {}) {
        compose.setContent {
            NexusTheme {
                HomeScreen(state = state, onOpen = onOpen, onRetry = {}, onForget = {})
            }
        }
    }

    private fun connected() = AppUiState(
        pairedPc = pc,
        connection = ConnectionState.Connected("d", "s", pc.pcInstanceId, listOf("""C:\Users\Example\Desktop""")),
        systemInfo = info,
    )

    @Test
    fun aConnectedPcShowsItsIdentityAndEveryAction() {
        render(connected())

        compose.onNodeWithText("Connected").assertIsDisplayed()
        compose.onNodeWithText("DESKTOP-TEST01").assertIsDisplayed()
        compose.onNodeWithText("v0.5.0").assertIsDisplayed()

        for (action in listOf("Screen", "Files", "System", "Security")) {
            compose.onNodeWithText(action).assertIsDisplayed()
        }
    }

    @Test
    fun tappingAnActionOpensThatScreen() {
        var opened: Destination? = null
        render(connected()) { opened = it }

        compose.onNodeWithText("Security").performClick()

        assertEquals(Destination.Security, opened)
    }

    @Test
    fun actionsAreUnreachableWithoutASession() {
        // Every card needs a live session. Offering them while disconnected would mean a tap that
        // does nothing, or worse, one that looks like it worked.
        var opened: Destination? = null

        render(
            AppUiState(
                pairedPc = pc,
                connection = ConnectionState.Failed(FailureReason.PcUnreachable, willRetry = true, attempt = 2),
            ),
        ) { opened = it }

        compose.onNodeWithText("PC unreachable").assertIsDisplayed()
        compose.onNodeWithText("Files").performClick()

        assertEquals(null, opened)
    }

    @Test
    fun theCredentialAppearsOnlyAsItsFingerprint() {
        render(connected())

        // PairedPc has no field that could carry the secret. Since v1.1.1 the screen shows the two
        // identifiers PC Core names when it refuses a phone, and the credential as an eight-character
        // fingerprint, so a refusal can be checked against the PC's log. Identifiers, not secrets.
        compose.onNodeWithText(pc.deviceId).assertExists()
        compose.onNodeWithText(pc.pcInstanceId).assertExists()
        compose.onNodeWithText(pc.credentialFingerprint).assertExists()
    }

    @Test
    fun forgettingThePcAsksFirst() {
        render(connected())

        // Below the clipboard panel and the update card, so off the first screen.
        compose.onNodeWithText("Forget this PC").performScrollTo().performClick()

        // The dialog explains that the PC keeps its own record — the phone is not the authority.
        compose.onNodeWithText("Forget this PC?").assertIsDisplayed()
        compose.onNodeWithText("Cancel").assertIsDisplayed()
    }
}
