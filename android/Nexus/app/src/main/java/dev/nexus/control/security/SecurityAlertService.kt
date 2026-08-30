package dev.nexus.control.security

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import dev.nexus.control.MainActivity
import dev.nexus.control.NexusApplication
import dev.nexus.control.R
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.SecuritySessionSummary
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Keeps the phone reachable for a security alert.
 *
 * **Why a foreground service and not a push service.** A security alert has to arrive while the app
 * is closed, and Android will not wake an app for a network event on its own — the platform offers
 * exactly two options. Firebase Cloud Messaging would mean routing a signal about the owner's home
 * through Google's infrastructure and standing up a cloud backend for a system that has deliberately
 * had neither. A foreground service costs battery and shows a permanent notification, and needs no
 * server, no account, and no third party. For one owner watching one PC, that is the right trade —
 * and the permanent notification is honest rather than unfortunate: a phone that is holding a
 * connection open should say so.
 *
 * **It is opt-in and stoppable.** The owner turns it on, Android shows it in the notification
 * shade and in Settings, and stopping it stops the alerts. Nothing here restarts itself, hides
 * itself, or resists being killed.
 */
class SecurityAlertService : Service() {

    private var scope: CoroutineScope? = null
    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannels()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }

            ACTION_NOT_ME -> {
                resolveNotMe()
                return START_STICKY
            }
        }

        startWatching()
        return START_STICKY
    }

    private fun startWatching() {
        val notification = NotificationCompat.Builder(this, WATCH_CHANNEL)
            .setContentTitle("Nexus is watching your PC")
            .setContentText("You will be alerted if activity is detected while it is armed.")
            .setSmallIcon(R.drawable.ic_stat_nexus)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setContentIntent(openApp())
            .addAction(0, "Stop", service(ACTION_STOP))
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(WATCH_NOTIFICATION, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(WATCH_NOTIFICATION, notification)
        }

        if (job?.isActive == true) return

        val container = (application as NexusApplication).container
        val watching = CoroutineScope(SupervisorJob())
        scope = watching

        job = watching.launch {
            // The connection is the app's, not the service's: one authenticated session, whichever
            // part of the app happens to need it.
            container.repository.connect()

            container.connection.events.collect { event ->
                if (event.eventName != NexusProtocol.Events.SECURITY_SESSION_STARTED) return@collect

                val summary = event.payload?.let {
                    runCatching {
                        NexusJson.decodeFromJsonElement(SecuritySessionSummary.serializer(), it)
                    }.getOrNull()
                } ?: return@collect

                alert(summary)
            }
        }
    }

    /**
     * The one alert per incident.
     *
     * High importance, because it is the only notification this app ever raises and it means someone
     * is using the owner's PC. Progress on an open incident arrives on a different event that is
     * deliberately not turned into a notification.
     */
    private fun alert(summary: SecuritySessionSummary) {
        val notification = NotificationCompat.Builder(this, ALERT_CHANNEL)
            .setContentTitle("Activity detected on your PC")
            .setContentText("${summary.trigger.label} while Nexus was armed.")
            .setSmallIcon(R.drawable.ic_stat_nexus)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(true)
            .setContentIntent(openApp(Destination.SECURITY))
            .addAction(0, "View live", PendingIntent.getActivity(
                this,
                1,
                Intent(this, MainActivity::class.java)
                    .putExtra(EXTRA_DESTINATION, Destination.LIVE)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            ))
            // "This was me" opens the app rather than acting from here: acknowledging an incident
            // needs the owner's biometric confirmation, and a notification action cannot prompt for
            // one. Standing a security session down from a lock screen would defeat the point.
            .addAction(0, "This was me", openApp(Destination.SECURITY))
            .addAction(0, "Not me", service(ACTION_NOT_ME))
            .build()

        NotificationManagerCompat.from(this).notify(ALERT_NOTIFICATION, notification)
    }

    /**
     * Marks the incident as not the owner, straight from the notification.
     *
     * Unlike acknowledgement this needs no confirmation: it only ever raises suspicion, and asking
     * someone to authenticate before reporting an intruder is friction in the worst possible place.
     */
    private fun resolveNotMe() {
        val container = (application as NexusApplication).container

        scope?.launch {
            runCatching { SecurityIncidentClient(container.connection).notMe() }

            NotificationManagerCompat.from(this@SecurityAlertService).notify(
                ALERT_NOTIFICATION,
                NotificationCompat.Builder(this@SecurityAlertService, ALERT_CHANNEL)
                    .setContentTitle("Marked as not you")
                    .setContentText("The session stays open and evidence keeps recording.")
                    .setSmallIcon(R.drawable.ic_stat_nexus)
                    .setPriority(NotificationCompat.PRIORITY_HIGH)
                    .setAutoCancel(true)
                    .setContentIntent(openApp(Destination.SECURITY))
                    .build(),
            )
        }
    }

    private fun openApp(destination: String? = null): PendingIntent = PendingIntent.getActivity(
        this,
        if (destination == null) 0 else 2,
        Intent(this, MainActivity::class.java)
            .apply { destination?.let { putExtra(EXTRA_DESTINATION, it) } }
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun service(action: String): PendingIntent = PendingIntent.getService(
        this,
        action.hashCode(),
        Intent(this, SecurityAlertService::class.java).setAction(action),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun createChannels() {
        val manager = getSystemService(NotificationManager::class.java)

        manager.createNotificationChannel(
            NotificationChannel(
                WATCH_CHANNEL,
                "Watching for alerts",
                NotificationManager.IMPORTANCE_MIN,
            ).apply {
                description = "Shown while Nexus is holding a connection to your PC."
            },
        )

        manager.createNotificationChannel(
            NotificationChannel(
                ALERT_CHANNEL,
                "Security alerts",
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = "Activity detected on your PC while it was armed."
                enableVibration(true)
            },
        )
    }

    override fun onDestroy() {
        job?.cancel()
        scope?.cancel()
        scope = null
        super.onDestroy()
    }

    /** Where a notification tap should land. */
    object Destination {
        const val SECURITY = "security"
        const val LIVE = "live"
    }

    companion object {
        const val EXTRA_DESTINATION = "nexus.destination"

        private const val WATCH_CHANNEL = "nexus.watch"
        private const val ALERT_CHANNEL = "nexus.alerts"
        private const val WATCH_NOTIFICATION = 1
        private const val ALERT_NOTIFICATION = 2

        private const val ACTION_STOP = "nexus.stop"
        private const val ACTION_NOT_ME = "nexus.not_me"

        fun start(context: Context) =
            ContextCompat.startForegroundService(context, Intent(context, SecurityAlertService::class.java))

        fun stop(context: Context) =
            context.startService(Intent(context, SecurityAlertService::class.java).setAction(ACTION_STOP))
    }
}
