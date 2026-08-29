package dev.nexus.control

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import dev.nexus.control.ui.NexusApp
import dev.nexus.control.ui.theme.NexusTheme

/**
 * The whole activity. One screen host, no fragments, no deep links.
 *
 * `singleTask` in the manifest keeps the app to a single instance, so returning to it after
 * scanning or after being backgrounded resumes the same connection state rather than starting a
 * second one alongside it.
 */
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        setContent {
            NexusTheme {
                NexusApp()
            }
        }
    }
}
