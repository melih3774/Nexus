package dev.nexus.control.update

import android.content.Context
import android.util.Log
import dev.nexus.control.net.AppVersion
import dev.nexus.control.net.ConnectionState
import dev.nexus.control.net.NexusRequestException
import dev.nexus.control.net.NotConnectedException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.File

/** Where updating this app has got to. */
sealed interface AppUpdateState {

    /** Not asked yet, or not connected. */
    data object Unknown : AppUpdateState

    data object Checking : AppUpdateState

    data class UpToDate(val pcHasRelease: Boolean) : AppUpdateState

    /** A newer build is on the PC. [required] when the PC will serve nothing else until it is installed. */
    data class Available(val info: AndroidUpdateInfo, val required: Boolean) : AppUpdateState

    data class Downloading(val info: AndroidUpdateInfo, val progress: Float) : AppUpdateState

    /** Handed to Android; its confirmation is on screen, or about to be. */
    data class Installing(val info: AndroidUpdateInfo) : AppUpdateState

    data class Failed(val message: String, val info: AndroidUpdateInfo?) : AppUpdateState

    /** Shown once, on the first start after an in-place update. */
    data class Updated(val versionName: String) : AppUpdateState
}

/**
 * The phone's side of updating itself: ask the PC, fetch, verify, hand to Android.
 *
 * **What decides "newer".** The PC compares its release's versionCode with the one this app reports,
 * and says so. The phone checks the same comparison again before installing, and Android checks it a
 * third time, because a downgrade is refused however the first two went.
 *
 * **What it will not do.** Install anything without Android's confirmation, install a package signed
 * by another key, or install one that does not hash to what the PC's release recorded.
 */
class UpdateManager(
    private val context: Context,
    private val scope: CoroutineScope,
    private val connection: StateFlow<ConnectionState>,
    private val client: UpdateClient,
    val installedVersion: AppVersion,
    private val downloader: ApkDownloader = ApkDownloader(File(context.cacheDir, "nexus-update"), client),
) {
    private val _state = MutableStateFlow<AppUpdateState>(AppUpdateState.Unknown)
    val state: StateFlow<AppUpdateState> = _state.asStateFlow()

    private var work: Job? = null

    init {
        noticeCompletedUpdate()

        scope.launch {
            var lastSession: String? = null

            connection.collect { state ->
                // Once per session: a reconnect is a new chance for the PC to have published something.
                if (state is ConnectionState.Connected && state.sessionId != lastSession) {
                    lastSession = state.sessionId
                    if (_state.value.isIdle()) check(refresh = false)
                }
            }
        }
    }

    fun check(refresh: Boolean = true) {
        if (work?.isActive == true) return

        work = scope.launch {
            val keepUpdated = _state.value as? AppUpdateState.Updated
            _state.value = AppUpdateState.Checking

            _state.value = try {
                val result = client.check(installedVersion.code, refresh)
                val android = result.android

                when {
                    android != null && android.versionCode > installedVersion.code ->
                        AppUpdateState.Available(android, required = android.required)

                    else -> keepUpdated ?: AppUpdateState.UpToDate(result.releaseAvailable)
                }
            } catch (e: NotConnectedException) {
                AppUpdateState.Unknown
            } catch (e: NexusRequestException) {
                AppUpdateState.Failed("The PC could not check for updates: ${e.error.message}", null)
            } catch (e: Exception) {
                Log.w(TAG, "Update check failed: ${e.javaClass.simpleName}")
                AppUpdateState.Unknown
            }
        }
    }

    /** The owner tapped Update. */
    fun update() {
        val info = when (val current = _state.value) {
            is AppUpdateState.Available -> current.info
            is AppUpdateState.Failed -> current.info
            else -> null
        } ?: return

        if (work?.isActive == true) return

        work = scope.launch {
            _state.value = AppUpdateState.Downloading(info, 0f)

            when (val download = downloader.download(info) { progress -> _state.value = AppUpdateState.Downloading(info, progress) }) {
                is ApkDownload.Failed -> {
                    _state.value = AppUpdateState.Failed(download.reason, info)
                    return@launch
                }

                is ApkDownload.Ready -> {
                    ApkVerifier.problemWith(context, download.file, info)?.let { problem ->
                        download.file.delete()
                        _state.value = AppUpdateState.Failed(problem, null)
                        return@launch
                    }

                    _state.value = AppUpdateState.Installing(info)
                    markInstallStarted(info)

                    try {
                        PackageInstallerBridge.install(context, download.file)
                    } catch (e: Exception) {
                        _state.value = AppUpdateState.Failed("Android's installer could not be started (${e.javaClass.simpleName}).", info)
                    }
                }
            }
        }
    }

    fun onInstalled() {
        // Rarely reached: the new build replaces this process first. The next start says so instead.
        _state.value = AppUpdateState.Updated(installedVersion.name)
    }

    fun onInstallFailed(message: String) {
        val info = (_state.value as? AppUpdateState.Installing)?.info
        clearInstallMarker()
        _state.value = AppUpdateState.Failed(message, info)
    }

    /** Whether an old state can be replaced by a fresh check without losing something the owner is looking at. */
    private fun AppUpdateState.isIdle(): Boolean =
        this is AppUpdateState.Unknown || this is AppUpdateState.UpToDate || this is AppUpdateState.Updated

    /**
     * An install replaces this process, so success is recognised on the way back in: the build that
     * was being installed is now the one running.
     */
    private fun noticeCompletedUpdate() {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val pending = prefs.getInt(KEY_PENDING_CODE, 0)

        if (pending != 0 && installedVersion.code >= pending) {
            _state.value = AppUpdateState.Updated(installedVersion.name)
        }

        if (pending != 0) clearInstallMarker()
    }

    private fun markInstallStarted(info: AndroidUpdateInfo) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putInt(KEY_PENDING_CODE, info.versionCode).apply()
    }

    private fun clearInstallMarker() {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(KEY_PENDING_CODE).apply()
    }

    private companion object {
        const val TAG = "NexusUpdate"
        const val PREFS = "nexus-update"
        const val KEY_PENDING_CODE = "pending_version_code"
    }
}
