package dev.nexus.control.security

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.edit

/**
 * The switch that decides whether this phone can be alerted.
 *
 * Off by default, and honest about the cost. Holding a connection open is the only way Android will
 * wake an app for a network event without a push service, and the owner should choose that knowingly
 * rather than discover a permanent notification they did not ask for.
 */
@Composable
fun SecurityAlertToggle(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(SecurityAlertPreference.isEnabled(context)) }

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        // Without the permission the service could run and post nothing, which would be worse than
        // not running: the owner would believe they were covered.
        if (granted) {
            SecurityAlertPreference.setEnabled(context, true)
            SecurityAlertService.start(context)
            enabled = true
        }
    }

    Column(modifier.fillMaxWidth()) {
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("Alert me on this phone", style = MaterialTheme.typography.bodyLarge)

            Switch(
                checked = enabled,
                onCheckedChange = { wanted ->
                    if (!wanted) {
                        SecurityAlertPreference.setEnabled(context, false)
                        SecurityAlertService.stop(context)
                        enabled = false
                        return@Switch
                    }

                    if (needsNotificationPermission(context)) {
                        permission.launch(Manifest.permission.POST_NOTIFICATIONS)
                    } else {
                        SecurityAlertPreference.setEnabled(context, true)
                        SecurityAlertService.start(context)
                        enabled = true
                    }
                },
            )
        }

        Spacer(Modifier.height(4.dp))

        Text(
            "Keeps a connection to your PC so an alert can arrive while the app is closed. Nexus " +
                "shows a permanent notification while this is on. The alert comes from your PC over " +
                "Tailscale, not through a push service.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

private fun needsNotificationPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
        PackageManager.PERMISSION_GRANTED

/**
 * Remembers whether the owner turned alerts on.
 *
 * A plain preference rather than encrypted storage: it holds one boolean about this app's own
 * behaviour, and nothing about the PC or the credential.
 */
object SecurityAlertPreference {
    private const val FILE = "nexus.alerts"
    private const val KEY = "enabled"

    fun isEnabled(context: Context): Boolean =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getBoolean(KEY, false)

    fun setEnabled(context: Context, enabled: Boolean) =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit { putBoolean(KEY, enabled) }
}
