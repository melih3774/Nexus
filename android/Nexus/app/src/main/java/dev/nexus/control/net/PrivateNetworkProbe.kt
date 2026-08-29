package dev.nexus.control.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.core.content.getSystemService
import dev.nexus.control.protocol.NexusEndpoint
import java.net.NetworkInterface

/**
 * Answers the two questions that turn "could not connect" into something the owner can act on:
 * is there a network at all, and is this phone on a tailnet?
 *
 * The second question is answered by looking for an address in `100.64.0.0/10` on a local
 * interface. Nexus deliberately does not talk to the Tailscale app, query its status, or depend on
 * it being installed — it only observes whether the private network the owner set up exists. If it
 * does not, that is the owner's to fix, and this is how the app knows to say so.
 */
interface PrivateNetworkProbe {
    fun hasNetwork(): Boolean

    fun hasTailnetAddress(): Boolean
}

class AndroidPrivateNetworkProbe(context: Context) : PrivateNetworkProbe {

    private val appContext = context.applicationContext

    override fun hasNetwork(): Boolean {
        val manager = appContext.getSystemService<ConnectivityManager>() ?: return true
        val capabilities = manager.activeNetwork?.let(manager::getNetworkCapabilities) ?: return false
        return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    override fun hasTailnetAddress(): Boolean = try {
        NetworkInterface.getNetworkInterfaces()
            ?.asSequence()
            ?.filter { it.isUp }
            ?.flatMap { it.inetAddresses.asSequence() }
            ?.any { NexusEndpoint.isTailnetAddress(it.hostAddress) }
            ?: false
    } catch (_: Exception) {
        // Interface enumeration can fail on some devices; treating that as "unknown, assume yes"
        // keeps the app from blaming Tailscale for something it did not observe.
        true
    }
}
