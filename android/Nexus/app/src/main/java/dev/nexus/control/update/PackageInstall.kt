package dev.nexus.control.update

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import dev.nexus.control.NexusApplication
import java.io.File
import java.security.MessageDigest

/**
 * Checks a downloaded APK is the Nexus update it claims to be, before Android is shown it.
 *
 * Android verifies the signature again at install, and refuses a different signer outright. These
 * checks exist so the owner is told *why* in words, before a confirmation dialog, rather than being
 * shown a system error after one - and so a package that is not Nexus is never offered at all.
 */
object ApkVerifier {

    /** Null when the package may be installed; otherwise the reason it may not. */
    fun problemWith(context: Context, apk: File, expected: AndroidUpdateInfo): String? {
        val pm = context.packageManager

        @Suppress("DEPRECATION")
        val archive = pm.getPackageArchiveInfo(apk.path, PackageManager.GET_SIGNING_CERTIFICATES)
            ?: return "The downloaded file is not a valid Android package."

        if (archive.packageName != context.packageName) {
            return "The downloaded package is not Nexus."
        }

        if (archive.longVersionCode != expected.versionCode.toLong()) {
            return "The downloaded package is not the version the PC described."
        }

        @Suppress("DEPRECATION")
        val installed = pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES)

        val archiveSigners = signers(archive)
        val installedSigners = signers(installed)

        if (archiveSigners.isEmpty() || archiveSigners.intersect(installedSigners).isEmpty()) {
            return "The update is signed by a different key than this app, so Android would refuse it. " +
                "It was not installed."
        }

        if (expected.signingCertSha256.isNotEmpty() && expected.signingCertSha256.lowercase() !in archiveSigners) {
            return "The update's signer does not match what the PC's release recorded."
        }

        return null
    }

    private fun signers(info: PackageInfo): Set<String> {
        val signing = info.signingInfo ?: return emptySet()

        val certificates = if (signing.hasMultipleSigners()) signing.apkContentsSigners else signing.signingCertificateHistory

        return certificates.orEmpty()
            .map { signature ->
                MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()).joinToString("") { "%02x".format(it) }
            }
            .toSet()
    }
}

/**
 * Hands a verified APK to Android's own package installer.
 *
 * A session install rather than an intent to a file: no content provider has to be exported to reach
 * the file. What does not change is who decides. The session is created with
 * `USER_ACTION_REQUIRED`, so Android always shows its confirmation - and, the first time, asks the
 * owner whether Nexus may install updates at all. Nexus cannot answer either question.
 */
object PackageInstallerBridge {

    private const val TAG = "NexusUpdateInstall"

    fun install(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller

        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(apk.length())
            setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_REQUIRED)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                setPackageSource(PackageInstaller.PACKAGE_SOURCE_LOCAL_FILE)
            }
        }

        val sessionId = installer.createSession(params)

        try {
            installer.openSession(sessionId).use { session ->
                session.openWrite("nexus.apk", 0, apk.length()).use { out ->
                    apk.inputStream().use { it.copyTo(out) }
                    session.fsync(out)
                }

                val callback = PendingIntent.getBroadcast(
                    context,
                    sessionId,
                    Intent(context, UpdateInstallReceiver::class.java).setPackage(context.packageName),
                    // Mutable because the installer fills in the status; explicit, so nothing else can receive it.
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
                )

                session.commit(callback.intentSender)
            }
        } catch (e: Exception) {
            Log.w(TAG, "The install session could not be committed: ${e.javaClass.simpleName}")
            runCatching { installer.abandonSession(sessionId) }
            throw e
        }
    }
}

/**
 * What PackageInstaller says back. Usually the first answer is "the owner must confirm", with the
 * intent for that dialog; a success is rarely heard here, because installing Nexus replaces the
 * process that would hear it. [UpdateManager] notices that on the next start instead.
 */
class UpdateInstallReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        val updates = (context.applicationContext as? NexusApplication)?.container?.updates

        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            val confirm = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra(Intent.EXTRA_INTENT)
            }

            if (confirm != null) {
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                runCatching { context.startActivity(confirm) }
                    .onFailure { updates?.onInstallFailed("Android did not show the install confirmation. Open Nexus and try again.") }
            }

            return
        }

        when (status) {
            PackageInstaller.STATUS_SUCCESS -> updates?.onInstalled()
            PackageInstaller.STATUS_FAILURE_ABORTED -> updates?.onInstallFailed("The update was cancelled.")
            PackageInstaller.STATUS_FAILURE_CONFLICT ->
                updates?.onInstallFailed("Android refused the update because it conflicts with the installed app (usually a different signing key).")
            PackageInstaller.STATUS_FAILURE_INCOMPATIBLE ->
                updates?.onInstallFailed("This phone cannot install that version of Nexus.")
            PackageInstaller.STATUS_FAILURE_STORAGE ->
                updates?.onInstallFailed("There is not enough storage on this phone to install the update.")
            else -> updates?.onInstallFailed("Android could not install the update" + (message?.let { ": $it" } ?: "."))
        }
    }
}
