package dev.nexus.control

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
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
    )

    private val info = SystemInfo(
        machineName = "DESKTOP-33LGLD2",
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
        connection = ConnectionState.Connected("d", "s", pc.pcInstanceId, listOf("""C:\Users\Melih\Desktop""")),
        systemInfo = info,
    )

    @Test
    fun aConnectedPcShowsItsIdentityAndEveryAction() {
        render(connected())

        compose.onNodeWithText("Connected").assertIsDisplayed()
        compose.onNodeWithText("DESKTOP-33LGLD2").assertIsDisplayed()
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
    fun nothingCredentialShapedIsOnScreen() {
        render(connected())

        // The home screen holds PairedPc, which has no field that could carry a secret. The device
        // id is not secret but is not useful to display either, so it is not shown.
        compose.onNodeWithText(pc.deviceId).assertDoesNotExist()
        compose.onNodeWithText(pc.pcInstanceId).assertDoesNotExist()
    }

    @Test
    fun forgettingThePcAsksFirst() {
        render(connected())

        compose.onNodeWithText("Forget this PC").performClick()

        // The dialog explains that the PC keeps its own record — the phone is not the authority.
        compose.onNodeWithText("Forget this PC?").assertIsDisplayed()
        compose.onNodeWithText("Cancel").assertIsDisplayed()
    }
}
