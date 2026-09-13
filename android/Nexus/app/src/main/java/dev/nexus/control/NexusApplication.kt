package dev.nexus.control

import android.app.Application
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.CreationExtras
import dev.nexus.control.data.CredentialStore
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.net.AndroidPrivateNetworkProbe
import dev.nexus.control.net.AppVersion
import dev.nexus.control.update.UpdateClient
import dev.nexus.control.update.UpdateManager
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.net.PcEnrollmentClient
import dev.nexus.control.security.EvidenceCache
import dev.nexus.control.security.OwnerKeyStore
import dev.nexus.control.security.OwnerSessionClient
import dev.nexus.control.security.SecurityIncidentClient
import dev.nexus.control.security.SessionGate
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

/**
 * The application's singletons, wired by hand.
 *
 * There is no dependency-injection framework here and that is a decision, not an omission: this is
 * one app with one graph of four objects, and a container that can be read top to bottom in ten
 * lines is worth more than the indirection a framework would add.
 *
 * The scope outlives every screen because the connection does: rotating the phone or moving between
 * Files and Security must not tear down a session and re-authenticate.
 */
class NexusContainer(context: Context) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val credentials = CredentialStore(context)

    /** This build, as Android installed it. Reported to the PC, and compared with what it offers. */
    val appVersion = AppVersion.of(context)

    val connection = PcConnectionManager(scope, AndroidPrivateNetworkProbe(context), appVersion = appVersion)

    val repository = NexusRepository(credentials, connection, PcEnrollmentClient())

    private val ownerKeys = OwnerKeyStore()

    /**
     * The lock, and the authority behind it.
     *
     * In the container rather than in a view model because it must outlive every screen and every
     * rotation: a lock that resets when the activity is recreated is not a lock, and one that
     * forgets its session ticket on rotation would ask for a fingerprint every time the phone was
     * turned sideways.
     */
    val gate = SessionGate(scope, connection.state, OwnerSessionClient(connection, ownerKeys))

    /** One client, so the incident screen and the evidence cache speak through the same channel. */
    val incidents = SecurityIncidentClient(connection)

    /**
     * Evidence files, on the way to being looked at.
     *
     * In `cacheDir` rather than `filesDir`: these are recordings of the owner's home, and the right
     * place for them is the one Android is allowed to empty without asking.
     */
    val evidenceCache = EvidenceCache(java.io.File(context.cacheDir, "nexus-evidence"), incidents)

    /**
     * Updating this app from its PC. In the container because an install outlives the screen that
     * started it: Android's confirmation takes the foreground, and the answer comes back to a receiver.
     */
    val updates = UpdateManager(context.applicationContext, scope, connection.state, UpdateClient(connection), appVersion)
}

class NexusApplication : Application() {

    lateinit var container: NexusContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = NexusContainer(this)
        container.repository.restore()

        // Screen off is the owner putting the phone down, and it is the one lock signal that does
        // not arrive through the activity lifecycle - Android does not stop the activity when the
        // screen goes off while the app is in front. Registered on the application rather than the
        // activity so it keeps working while the alert service holds the app alive in the
        // background.
        registerReceiver(
            object : BroadcastReceiver() {
                override fun onReceive(context: Context?, intent: Intent?) {
                    if (intent?.action == Intent.ACTION_SCREEN_OFF) {
                        container.gate.onScreenOff()
                    }
                }
            },
            IntentFilter(Intent.ACTION_SCREEN_OFF),
        )
    }
}

/** Reaches the container from a `ViewModel` factory without holding an Activity reference. */
val CreationExtras.nexusContainer: NexusContainer
    get() = (this[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY] as NexusApplication).container
