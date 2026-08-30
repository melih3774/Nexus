package dev.nexus.control.remote

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import dev.nexus.control.protocol.NexusKey
import dev.nexus.control.protocol.PointerButton

/**
 * The pointer-mode switch and the explicit mouse buttons.
 *
 * The buttons exist because gestures alone are not enough. A right click by long press is
 * discoverable but slow, and a middle click has no natural gesture at all — so rather than inventing
 * a chord nobody would remember, the awkward ones get a button.
 */
@Composable
fun PointerControls(
    mode: PointerModeUi,
    inputEnabled: Boolean,
    onModeChange: (PointerModeUi) -> Unit,
    onInputEnabledChange: (Boolean) -> Unit,
    onClick: (PointerButton) -> Unit,
    onDoubleClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            SingleChoiceSegmentedButtonRow {
                PointerModeUi.entries.forEachIndexed { index, entry ->
                    SegmentedButton(
                        selected = mode == entry,
                        onClick = { onModeChange(entry) },
                        shape = SegmentedButtonDefaults.itemShape(index, PointerModeUi.entries.size),
                    ) {
                        Text(if (entry == PointerModeUi.DIRECT) "Direct" else "Touchpad")
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Control", style = MaterialTheme.typography.labelLarge)
                Spacer(Modifier.width(8.dp))

                // The switch, not the connection, is what makes the phone a keyboard.
                Switch(checked = inputEnabled, onCheckedChange = onInputEnabledChange)
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilledTonalButton(onClick = { onClick(PointerButton.LEFT) }, enabled = inputEnabled) { Text("Left") }
            FilledTonalButton(onClick = onDoubleClick, enabled = inputEnabled) { Text("Double") }
            FilledTonalButton(onClick = { onClick(PointerButton.RIGHT) }, enabled = inputEnabled) { Text("Right") }
            FilledTonalButton(onClick = { onClick(PointerButton.MIDDLE) }, enabled = inputEnabled) { Text("Middle") }
        }
    }
}

/**
 * Typing, and the keys typing cannot express.
 *
 * Two halves for two problems. The text field produces characters — including ones no Windows key
 * corresponds to — and sends them as Unicode. The buttons produce key events, with modifiers that
 * latch, because there is no way to hold Ctrl and tap C at once on a touchscreen.
 */
@Composable
fun KeyboardControls(
    inputEnabled: Boolean,
    modifiers: Set<NexusKey>,
    onType: (String) -> Unit,
    onKey: (NexusKey) -> Unit,
    onToggleModifier: (NexusKey) -> Unit,
    onClearModifiers: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var draft by remember { mutableStateOf("") }

    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(
            value = draft,
            onValueChange = { draft = it },
            modifier = Modifier.fillMaxWidth(),
            enabled = inputEnabled,
            singleLine = true,
            label = { Text("Type on the PC") },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(
                onSend = {
                    // Sent on Send rather than per keystroke: one message for a word beats one for
                    // every letter, and it keeps composing characters intact.
                    if (draft.isNotEmpty()) {
                        onType(draft)
                        draft = ""
                    }
                },
            ),
        )

        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            for (key in MODIFIERS) {
                FilterChip(
                    selected = key in modifiers,
                    onClick = { onToggleModifier(key) },
                    enabled = inputEnabled,
                    label = { Text(key.label) },
                )
            }

            if (modifiers.isNotEmpty()) {
                TextButton(onClick = onClearModifiers) { Text("Clear") }
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            for (key in COMMON_KEYS) {
                FilledTonalButton(
                    onClick = { onKey(key) },
                    enabled = inputEnabled,
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 12.dp, vertical = 4.dp),
                ) {
                    Text(key.label)
                }
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally),
        ) {
            for (key in ARROWS) {
                FilledTonalButton(onClick = { onKey(key) }, enabled = inputEnabled) { Text(key.label) }
            }
        }
    }
}

private val MODIFIERS = listOf(NexusKey.CONTROL, NexusKey.ALT, NexusKey.SHIFT, NexusKey.META)

/**
 * The keys worth a permanent button.
 *
 * Chosen by what a remote session actually needs: escaping a dialog, moving between fields, sending
 * a line, correcting a mistake, and paging through a document. Function keys are reachable through
 * the same mechanism but do not earn space on a phone-sized toolbar.
 */
private val COMMON_KEYS = listOf(
    NexusKey.ESCAPE,
    NexusKey.TAB,
    NexusKey.ENTER,
    NexusKey.BACKSPACE,
    NexusKey.DELETE,
    NexusKey.HOME,
    NexusKey.END,
    NexusKey.PAGE_UP,
    NexusKey.PAGE_DOWN,
)

private val ARROWS = listOf(NexusKey.ARROW_LEFT, NexusKey.ARROW_UP, NexusKey.ARROW_DOWN, NexusKey.ARROW_RIGHT)
