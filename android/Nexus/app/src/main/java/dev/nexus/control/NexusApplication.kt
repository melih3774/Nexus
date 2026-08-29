package dev.nexus.control

import android.app.Application
import android.content.Context
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.CreationExtras
import dev.nexus.control.data.CredentialStore
import dev.nexus.control.data.NexusRepository
import dev.nexus.control.net.AndroidPrivateNetworkProbe
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.net.PcEnrollmentClient
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

    val connection = PcConnectionManager(scope, AndroidPrivateNetworkProbe(context))

    val repository = NexusRepository(credentials, connection, PcEnrollmentClient())
}

class NexusApplication : Application() {

    lateinit var container: NexusContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = NexusContainer(this)
        container.repository.restore()
    }
}

/** Reaches the container from a `ViewModel` factory without holding an Activity reference. */
val CreationExtras.nexusContainer: NexusContainer
    get() = (this[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY] as NexusApplication).container
