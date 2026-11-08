package dev.nexus.control.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.activity.compose.LocalActivity
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.platform.LocalContext
import androidx.fragment.app.FragmentActivity
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.security.BiometricOwnerConfirmation
import dev.nexus.control.update.AppUpdateState
import dev.nexus.control.update.UpdateCard
import dev.nexus.control.update.UpdateRequiredScreen
import dev.nexus.control.security.SecurityAlertPreference
import dev.nexus.control.security.SecurityAlertService
import dev.nexus.control.security.LockReason
import dev.nexus.control.security.SessionGateState
import kotlinx.coroutines.launch
import dev.nexus.control.remote.RemoteScreen
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController

/** The screens this app has. Adding a card to the home grid means adding a case here. */
enum class Destination(val route: String, val title: String) {
    Home("home", "Nexus"),
    Screen("screen", "Screen"),
    Files("files", "Files"),
    System("system", "System"),
    Security("security", "Security"),
    Assistant("assistant", "AI Assistant"),
    Pairing("pairing", "Pair"),
    Scanner("scanner", "Scan"),
}

/**
 * The app shell.
 *
 * Two things happen here that belong nowhere else: the paired-versus-unpaired decision, and tying
 * the connection to the foreground lifecycle. The second used to be the whole policy — "connect
 * while the app is being used" — because there was no background service. There is one now, it is
 * opt-in, and it exists precisely to keep the connection alive when the app is not being looked at,
 * so the rule is no longer unconditional. See [BackgroundPolicy] for what replaced it and why.
 */
@Composable
fun NexusApp(
    requestedDestination: String? = null,
    onDestinationHandled: () -> Unit = {},
    viewModel: AppViewModel = viewModel(factory = AppViewModel.Factory),
) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val pairing by viewModel.pairing.collectAsStateWithLifecycle()
    val navController = rememberNavController()

    val context = LocalContext.current

    val gate = viewModel.gate
    val gateState by gate.state.collectAsStateWithLifecycle()
    val updateState by viewModel.updates.state.collectAsStateWithLifecycle()

    // BiometricPrompt needs the hosting activity, so it is built here rather than in the container.
    // The gate itself never touches an Android UI type: it is handed a yes-or-no question to ask.
    val activity = LocalActivity.current as? FragmentActivity
    val scope = rememberCoroutineScope()

    val unlock: () -> Unit = {
        activity?.let { host ->
            scope.launch { gate.authenticate(BiometricOwnerConfirmation(host)) }
        }

        Unit
    }

    LifecycleEventEffect(Lifecycle.Event.ON_START) {
        // Before anything else on the way in. Whether this launch needs the owner again is a
        // decision about the session, not about the screen that happens to be showing.
        gate.onForeground()

        // `viewModel.isPairedNow`, never `state.isPaired`: the latter is still false this early,
        // which is what left the app showing "Disconnected" after being reopened.
        val paired = viewModel.isPairedNow

        if (paired) viewModel.connect()

        // Android stops services for reasons that have nothing to do with the owner's intent, and
        // the alert switch reads a stored preference rather than a running service. Reconciling the
        // two on open is what stops the switch claiming cover it is not providing.
        if (BackgroundPolicy.shouldStartAlertServiceOnStart(paired, SecurityAlertPreference.isEnabled(context))) {
            SecurityAlertService.start(context)
        }
    }

    // A tap on a security alert lands on the incident, or on the live screen. Only meaningful once
    // the phone is paired; an unpaired phone has nothing to show and stays on pairing.
    LaunchedEffect(requestedDestination, state.isPaired) {
        val destination = requestedDestination ?: return@LaunchedEffect

        if (state.isPaired) {
            navController.navigate(
                if (destination == SecurityAlertService.Destination.LIVE) {
                    Destination.Screen.route
                } else {
                    Destination.Security.route
                },
            )

            onDestinationHandled()
        }
    }

    LifecycleEventEffect(Lifecycle.Event.ON_STOP) {
        // The clock starts here; nothing is locked yet. Fifteen minutes of it is forgiven, because
        // answering a message and coming back is what using a phone looks like.
        gate.onBackground()

        // Unconditional until v0.7.1, which is what made the security alert impossible: the
        // foreground service held a socket this had already closed.
        if (BackgroundPolicy.shouldDisconnectOnStop(SecurityAlertPreference.isEnabled(context))) {
            viewModel.disconnect()
        }
    }

    // The prompt is raised as soon as the lock screen appears, rather than waiting for a tap. The
    // owner opened Nexus to use it; making them press one more button first would be ceremony.
    //
    // Only for the reasons that mean "time passed". A cancelled prompt and a refusal from the PC are
    // both states the owner has to act on, and re-raising the prompt on top of them would be a loop
    // they could not read.
    val autoPrompt = (gateState as? SessionGateState.Locked)?.reason in setOf(
        LockReason.Started, LockReason.ScreenLocked, LockReason.AwayTooLong,
    )

    LaunchedEffect(gateState, state.isPaired, activity) {
        if (state.isPaired && autoPrompt && activity != null) {
            unlock()
        }
    }

    // Pairing succeeded: the credential is stored and the session is coming up. Leave the pairing
    // flow behind entirely so back cannot return to a spent code.
    LaunchedEffect(pairing) {
        if (pairing is PairingProgress.Done) {
            navController.navigate(Destination.Home.route) {
                popUpTo(0) { inclusive = true }
            }
            viewModel.dismissPairingError()
        }
    }

    // One background for the whole app, painted here rather than per flow. Without it the unpaired
    // screens render on the window's own colour, which is not the theme's — a light-mode phone shows
    // light content on a dark ground. The safe-drawing inset belongs here too, since the activity
    // draws edge to edge.
    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        // The colour fills the whole window, including behind the system bars; only the content is
        // inset. Painting inside the insets instead would leave the bars showing the window's own
        // colour, which is not the theme's.
        Box(Modifier.safeDrawingPadding()) {
            when {
                // Pairing comes first: an unpaired phone has no session to lock and nothing to show
                // behind a lock screen.
                !state.isPaired -> UnpairedFlow(navController, pairing, viewModel)

                // Ahead of the lock: the PC will refuse everything but the update to this build, and
                // a fingerprint cannot change that.
                (state.connection as? ConnectionState.Connected)?.updateRequired == true -> UpdateRequiredScreen(
                    updateState,
                    viewModel.updates.installedVersion,
                    onCheck = { viewModel.updates.check() },
                    onUpdate = { viewModel.updates.update() },
                )

                gateState.isUnlocked -> PairedFlow(navController, state, viewModel, updateState)

                else -> LockScreen(
                    gateState,
                    state.connection,
                    unlock,
                    pairing = state.pairedPc,
                    footer = {
                        UpdateCard(
                            updateState,
                            viewModel.updates.installedVersion,
                            onCheck = { viewModel.updates.check() },
                            onUpdate = { viewModel.updates.update() },
                            compact = true,
                        )
                    },
                )
            }
        }
    }
}

@Composable
private fun UnpairedFlow(
    navController: NavHostController,
    pairing: PairingProgress,
    viewModel: AppViewModel,
) {
    NavHost(navController, startDestination = Destination.Pairing.route) {
        composable(Destination.Pairing.route) {
            PairingScreen(
                progress = pairing,
                onScan = { navController.navigate(Destination.Scanner.route) },
                onSubmitCode = { viewModel.pair(it, defaultDeviceName()) },
                onDismissError = viewModel::dismissPairingError,
            )
        }

        composable(Destination.Scanner.route) {
            QrScannerScreen(
                onScanned = { code ->
                    navController.popBackStack()
                    viewModel.pair(code, defaultDeviceName())
                },
                onCancel = { navController.popBackStack() },
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PairedFlow(
    navController: NavHostController,
    state: AppUiState,
    viewModel: AppViewModel,
    updateState: AppUpdateState,
) {
    val entry by navController.currentBackStackEntryAsState()
    val current = Destination.entries.firstOrNull { it.route == entry?.destination?.route } ?: Destination.Home

    Scaffold(
        topBar = {
            // The remote screen gives the bar's height to the desktop; Android's back still leaves it.
            if (current != Destination.Home && current != Destination.Screen) {
                TopAppBar(
                    title = { Text(current.title) },
                    navigationIcon = {
                        IconButton(onClick = { navController.popBackStack() }) {
                            Icon(backArrow(), contentDescription = "Back")
                        }
                    },
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = MaterialTheme.colorScheme.background,
                    ),
                )
            }
        },
    ) { padding ->
        Surface(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding),
            color = MaterialTheme.colorScheme.background,
        ) {
            NavHost(navController, startDestination = Destination.Home.route) {
                composable(Destination.Home.route) {
                    HomeScreen(
                        state = state,
                        onOpen = { navController.navigate(it.route) },
                        onRetry = viewModel::retry,
                        onForget = viewModel::forgetPc,
                        updates = updateState,
                        installedVersion = viewModel.updates.installedVersion,
                        onCheckUpdates = { viewModel.updates.check() },
                        onUpdate = { viewModel.updates.update() },
                        clipboard = { ClipboardPanel(enabled = state.connection.isConnected) },
                    )
                }

                composable(Destination.Screen.route) { RemoteScreen() }
                composable(Destination.Files.route) { FilesScreen() }
                composable(Destination.System.route) { SystemScreen() }
                composable(Destination.Assistant.route) { AssistantScreen() }
                composable(Destination.Security.route) {
                    // The alert says "activity on your PC"; the natural next step is to look at it.
                    SecurityScreen(onViewLive = { navController.navigate(Destination.Screen.route) })
                }
            }
        }
    }
}

/**
 * The name the PC records for this phone. The device model, not anything identifying about the
 * owner — it appears in PC Core's logs and on its local page.
 */
private fun defaultDeviceName(): String =
    listOfNotNull(android.os.Build.MANUFACTURER, android.os.Build.MODEL)
        .joinToString(" ")
        .trim()
        .ifBlank { "Android phone" }

/** A back chevron, drawn rather than pulled in as a whole icon dependency. */
@Composable
private fun backArrow(): ImageVector {
    val tint = MaterialTheme.colorScheme.onBackground

    return androidx.compose.runtime.remember(tint) {
        ImageVector.Builder(
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f,
        ).apply {
            path(fill = SolidColor(tint)) {
                moveTo(15.4f, 4.6f)
                lineTo(7f, 12f)
                lineTo(15.4f, 19.4f)
                lineTo(16.8f, 17.9f)
                lineTo(10f, 12f)
                lineTo(16.8f, 6.1f)
                close()
            }
        }.build()
    }
}
