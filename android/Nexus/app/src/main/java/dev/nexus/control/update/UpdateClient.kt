package dev.nexus.control.update

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** What `update.check` answers. Only the fields the phone uses. */
@Serializable
data class UpdateCheckResult(
    @SerialName("running_version") val runningVersion: String = "",
    @SerialName("release_available") val releaseAvailable: Boolean = false,
    @SerialName("latest_version") val latestVersion: String? = null,
    @SerialName("latest_release") val latestRelease: Int? = null,
    @SerialName("pc_update_available") val pcUpdateAvailable: Boolean = false,
    val android: AndroidUpdateInfo? = null,
    @SerialName("minimum_android_version_code") val minimumAndroidVersionCode: Int = 0,
    val state: String = "idle",
    val message: String? = null,
    val problem: String? = null,
)

/** The APK the PC's current release offers. */
@Serializable
data class AndroidUpdateInfo(
    @SerialName("version_name") val versionName: String = "",
    @SerialName("version_code") val versionCode: Int = 0,
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    val sha256: String = "",
    @SerialName("signing_cert_sha256") val signingCertSha256: String = "",
    @SerialName("min_sdk") val minSdk: Int = 0,
    @SerialName("update_available") val updateAvailable: Boolean = false,
    val required: Boolean = false,
)

/** One range of the APK. */
@Serializable
data class AndroidPackageChunk(
    @SerialName("version_code") val versionCode: Int = 0,
    @SerialName("size_bytes") val sizeBytes: Long = 0,
    val sha256: String = "",
    val offset: Long = 0,
    @SerialName("chunk_bytes") val chunkBytes: Int = 0,
    @SerialName("end_of_file") val endOfFile: Boolean = false,
    val data: String? = null,
)

@Serializable
private data class UpdateCheckParams(
    @SerialName("client_version_code") val clientVersionCode: Int,
    val refresh: Boolean,
)

@Serializable
private data class GetPackageParams(
    @SerialName("version_code") val versionCode: Int,
    val offset: Long,
    @SerialName("max_bytes") val maxBytes: Int,
)

/** Where the downloader gets bytes from. An interface so the download can be tested without a PC. */
fun interface PackageChunkSource {
    suspend fun chunk(versionCode: Int, offset: Long, maxBytes: Int): AndroidPackageChunk
}

/** The two update calls, and nothing else. The decisions live in [UpdateManager]. */
class UpdateClient(private val connection: NexusCommandChannel) : PackageChunkSource {

    suspend fun check(clientVersionCode: Int, refresh: Boolean = false): UpdateCheckResult {
        val result = connection.request(
            NexusProtocol.Actions.UPDATE_CHECK,
            NexusJson.encodeToJsonElement(UpdateCheckParams.serializer(), UpdateCheckParams(clientVersionCode, refresh)),
        ) ?: return UpdateCheckResult()

        return NexusJson.decodeFromJsonElement(UpdateCheckResult.serializer(), result)
    }

    override suspend fun chunk(versionCode: Int, offset: Long, maxBytes: Int): AndroidPackageChunk {
        val result = connection.request(
            NexusProtocol.Actions.UPDATE_APK_GET,
            NexusJson.encodeToJsonElement(GetPackageParams.serializer(), GetPackageParams(versionCode, offset, maxBytes)),
            // A megabyte over a phone link on a bad day.
            timeoutMs = 60_000,
        ) ?: throw IllegalStateException("The PC returned no package data.")

        return NexusJson.decodeFromJsonElement(AndroidPackageChunk.serializer(), result)
    }
}
