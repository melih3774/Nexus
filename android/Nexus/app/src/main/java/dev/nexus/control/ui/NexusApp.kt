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
    Pairing("pairing", "Pair"),
    Scanner("scanner", "Scan"),
}

/**
 * The app shell.
 *
 * Two things happen here that belong nowhere else: the paired-versus-unpaired decision, and tying
 * the connection to the foreground lifecycle. The second is what implements "connect while the app
 * is being used" — there is no background service, so `ON_START` and `ON_STOP` are the whole
 * policy.
 */
@Composable
fun NexusApp(viewModel: AppViewModel = viewModel(factory = AppViewModel.Factory)) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val pairing by viewModel.pairing.collectAsStateWithLifecycle()
    val navController = rememberNavController()

    LifecycleEventEffect(Lifecycle.Event.ON_START) {
        if (state.isPaired) viewModel.connect()
    }

    LifecycleEventEffect(Lifecycle.Event.ON_STOP) {
        viewModel.disconnect()
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
            if (state.isPaired) {
                PairedFlow(navController, state, viewModel)
            } else {
                UnpairedFlow(navController, pairing, viewModel)
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
) {
    val entry by navController.currentBackStackEntryAsState()
    val current = Destination.entries.firstOrNull { it.route == entry?.destination?.route } ?: Destination.Home

    Scaffold(
        topBar = {
            if (current != Destination.Home) {
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
                    )
                }

                composable(Destination.Screen.route) { RemoteScreen() }
                composable(Destination.Files.route) { FilesScreen() }
                composable(Destination.System.route) { SystemScreen() }
                composable(Destination.Security.route) { SecurityScreen() }
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
