package dev.nexus.control.remote

import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.protocol.MediaQualityResult
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import dev.nexus.control.protocol.SetMediaQualityParams
import kotlinx.coroutines.CancellationException

/**
 * Reads and changes the PC's streaming settings.
 *
 * Its own small class because two screens use it — the remote screen for the desktop and the Security
 * screen for the camera — and the settings are one document on the PC with a target in each request,
 * not two features.
 */
class MediaQualityClient(private val channel: NexusCommandChannel) {

    /** Null on a PC too old to have streaming settings, which the UI treats as "nothing to show". */
    suspend fun get(): MediaQualityResult? = try {
        channel.request(NexusProtocol.Actions.MEDIA_QUALITY_GET)
            ?.let { NexusJson.decodeFromJsonElement(MediaQualityResult.serializer(), it) }
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        null
    }

    /** Applies a change. A running stream picks it up on its next frame; nothing needs restarting. */
    suspend fun set(params: SetMediaQualityParams): MediaQualityResult? =
        channel.request(
            NexusProtocol.Actions.MEDIA_QUALITY_SET,
            NexusJson.encodeToJsonElement(SetMediaQualityParams.serializer(), params),
        )?.let { NexusJson.decodeFromJsonElement(MediaQualityResult.serializer(), it) }
}
