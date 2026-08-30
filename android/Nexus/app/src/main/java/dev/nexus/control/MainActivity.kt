package dev.nexus.control

import android.content.Intent
import android.os.Bundle
import androidx.fragment.app.FragmentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.mutableStateOf
import dev.nexus.control.security.SecurityAlertService
import dev.nexus.control.ui.NexusApp
import dev.nexus.control.ui.theme.NexusTheme

/**
 * The whole activity. One screen host, no fragments, no deep links.
 *
 * `singleTask` in the manifest keeps the app to a single instance, so returning to it after
 * scanning or after being backgrounded resumes the same connection state rather than starting a
 * second one alongside it.
 */
/**
 * A [FragmentActivity] rather than a plain ComponentActivity, because `BiometricPrompt` needs one.
 * That is the only reason; nothing here uses fragments.
 */
class MainActivity : FragmentActivity() {

    /**
     * Where a security notification asked the app to open.
     *
     * State rather than a one-shot read: `singleTask` delivers a second notification tap to
     * [onNewIntent] on the running activity, and the screen has to move then too.
     */
    private val requested = mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        requested.value = intent?.getStringExtra(SecurityAlertService.EXTRA_DESTINATION)

        setContent {
            NexusTheme {
                NexusApp(
                    requestedDestination = requested.value,
                    onDestinationHandled = { requested.value = null },
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        requested.value = intent.getStringExtra(SecurityAlertService.EXTRA_DESTINATION)
    }
}
