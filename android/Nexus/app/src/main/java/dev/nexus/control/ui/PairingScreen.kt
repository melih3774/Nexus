package dev.nexus.control.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp

/**
 * The unpaired state.
 *
 * Two ways in, both ending at the same NEXUS2 payload. Scanning is the one the owner will use;
 * pasting exists because a camera can fail, a QR can be on the wrong screen, and a fallback that
 * needs no permission is worth having. Neither asks the owner to type an address or a secret — if
 * the code is valid it already contains both, and asking a person to transcribe a 256-bit key is a
 * way of guaranteeing typos rather than security.
 */
@Composable
fun PairingScreen(
    progress: PairingProgress,
    onScan: () -> Unit,
    onSubmitCode: (String) -> Unit,
    onDismissError: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var showPaste by remember { mutableStateOf(false) }
    var pasted by remember { mutableStateOf("") }

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(40.dp))
        NexusWordmark()

        Spacer(Modifier.height(12.dp))
        Text(
            "No PC paired",
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        Spacer(Modifier.height(40.dp))

        if (progress is PairingProgress.Working) {
            CircularProgressIndicator(strokeWidth = 2.dp)
            Spacer(Modifier.height(16.dp))
            Text(
                "Pairing over Tailscale…",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        } else {
            Button(
                onClick = onScan,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("Scan QR")
            }

            Spacer(Modifier.height(12.dp))

            OutlinedButton(
                onClick = { showPaste = !showPaste },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(if (showPaste) "Hide pairing code" else "Paste pairing code")
            }

            if (showPaste) {
                Spacer(Modifier.height(16.dp))

                OutlinedTextField(
                    value = pasted,
                    onValueChange = {
                        pasted = it
                        if (progress is PairingProgress.Failed) onDismissError()
                    },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("NEXUS2…") },
                    textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    singleLine = false,
                    minLines = 3,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                )

                Spacer(Modifier.height(12.dp))

                Button(
                    onClick = { onSubmitCode(pasted) },
                    enabled = pasted.isNotBlank(),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text("Pair")
                }
            }
        }

        if (progress is PairingProgress.Failed) {
            Spacer(Modifier.height(24.dp))

            Panel {
                Text(
                    "Pairing failed",
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.error,
                )
                Spacer(Modifier.height(6.dp))
                Text(progress.message, style = MaterialTheme.typography.bodyMedium)
            }
        }

        Spacer(Modifier.height(40.dp))

        Panel(title = "Before you start") {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Step("1", "Install Tailscale on this phone and on the PC, and sign both into your tailnet.")
                Step("2", "Start PC Core on the PC.")
                Step("3", "On the PC, begin pairing. It shows a QR code that expires in a few minutes.")
                Step("4", "Scan it here.")
            }

            Spacer(Modifier.height(12.dp))
            Text(
                "Nexus does not install or manage Tailscale, and never sees your tailnet account.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun Step(number: String, text: String) {
    androidx.compose.foundation.layout.Row {
        Text(
            "$number.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.padding(end = 8.dp),
        )
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}
