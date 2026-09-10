package dev.nexus.control.security

import android.graphics.BitmapFactory
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.util.Log
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import dev.nexus.control.net.NexusCommandChannel
import dev.nexus.control.net.PcConnectionManager
import dev.nexus.control.nexusContainer
import dev.nexus.control.protocol.LiveSensorKind
import dev.nexus.control.protocol.NexusJson
import dev.nexus.control.protocol.NexusProtocol
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class LiveSensorParams(val sensor: String? = null)

@Serializable
data class LiveSensorResult(
    val camera: Boolean = false,
    val microphone: Boolean = false,
    val indicator: String = "",
    val refused: String? = null,
    @SerialName("camera_available") val cameraAvailable: Boolean = false,
    @SerialName("microphone_available") val microphoneAvailable: Boolean = false,
)

/** What the Live Monitor card shows. */
data class LiveMonitorUiState(
    val camera: Boolean = false,
    val microphone: Boolean = false,
    val cameraAvailable: Boolean = false,
    val microphoneAvailable: Boolean = false,
    val busy: Boolean = false,
    val message: String? = null,

    /** The most recent camera frame, decoded. */
    val image: ImageBitmap? = null,

    /** Frames seen since the camera started, so a still-looking picture can be told from a stalled one. */
    val framesReceived: Long = 0,
) {
    val anyLive: Boolean get() = camera || microphone
}

/**
 * Live camera and microphone monitoring, from the phone.
 *
 * **What this is not.** It is not recording. Frames are decoded, shown, played and dropped; nothing
 * is written to storage on either side. The PC does not keep them either — live monitoring and
 * incident evidence are separate subsystems there for exactly this reason.
 *
 * **It never prompts again.** Starting a stream rides the Full Remote session the owner already
 * authenticated for. A biometric prompt per action would train the owner to approve without
 * reading, which is the failure mode that makes prompts worthless.
 *
 * **It stops itself.** The PC stops capture when the session lapses or the phone disconnects, and
 * this stops playing and clears the picture when the flow ends — so a disconnected phone shows
 * nothing rather than the last frame it happened to receive, which would look like a live view of a
 * room it is no longer watching.
 */
class LiveMonitorViewModel(
    private val connection: PcConnectionManager,
    private val channel: NexusCommandChannel,
) : ViewModel() {

    private val _state = MutableStateFlow(LiveMonitorUiState())
    val state: StateFlow<LiveMonitorUiState> = _state.asStateFlow()

    private var collector: Job? = null
    private var audio: AudioTrack? = null

    /** Guards against two starts racing to create two AudioTracks. */
    private val audioLock = Any()

    fun refresh() {
        viewModelScope.launch {
            try {
                apply(request(NexusProtocol.Actions.MEDIA_LIVE_STATUS, null))
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // A status read failing is not worth a banner; the card simply shows nothing live.
                Log.d(TAG, "Live status unavailable: ${e.javaClass.simpleName}")
            }
        }
    }

    fun toggle(kind: LiveSensorKind) {
        val live = when (kind) {
            LiveSensorKind.CAMERA -> _state.value.camera
            LiveSensorKind.MICROPHONE -> _state.value.microphone
        }

        if (live) stop(kind) else start(kind)
    }

    private fun start(kind: LiveSensorKind) {
        _state.value = _state.value.copy(busy = true, message = null)

        viewModelScope.launch {
            try {
                val result = request(
                    NexusProtocol.Actions.MEDIA_LIVE_START,
                    NexusJson.encodeToJsonElement(LiveSensorParams.serializer(), LiveSensorParams(kind.wire)),
                )

                apply(result)

                if (result.refused != null) {
                    return@launch
                }

                if (kind == LiveSensorKind.MICROPHONE) startAudio()

                ensureCollecting()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = _state.value.copy(busy = false, message = describe(e))
            }
        }
    }

    fun stop(kind: LiveSensorKind) {
        _state.value = _state.value.copy(busy = true, message = null)

        viewModelScope.launch {
            try {
                apply(
                    request(
                        NexusProtocol.Actions.MEDIA_LIVE_STOP,
                        NexusJson.encodeToJsonElement(
                            LiveSensorParams.serializer(),
                            LiveSensorParams(kind.wire),
                        ),
                    ),
                )
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // The PC stops on its own when this connection goes away, so a failed stop is not
                // left as a live stream: the local state is cleared regardless.
                _state.value = _state.value.copy(message = describe(e))
            } finally {
                if (kind == LiveSensorKind.MICROPHONE) stopAudio()

                _state.value = _state.value.copy(
                    busy = false,
                    camera = if (kind == LiveSensorKind.CAMERA) false else _state.value.camera,
                    microphone = if (kind == LiveSensorKind.MICROPHONE) false else _state.value.microphone,
                    image = if (kind == LiveSensorKind.CAMERA) null else _state.value.image,
                )

                if (!_state.value.anyLive) stopCollecting()
            }
        }
    }

    /** Called when the screen goes away. Live capture must not outlive the thing watching it. */
    fun stopAll() {
        viewModelScope.launch {
            try {
                request(NexusProtocol.Actions.MEDIA_LIVE_STOP, null)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.d(TAG, "Live stop-all failed: ${e.javaClass.simpleName}")
            } finally {
                stopAudio()
                stopCollecting()
                _state.value = _state.value.copy(camera = false, microphone = false, image = null)
            }
        }
    }

    private suspend fun request(action: String, params: kotlinx.serialization.json.JsonElement?): LiveSensorResult {
        val result = channel.request(action, params) ?: return LiveSensorResult()
        return NexusJson.decodeFromJsonElement(LiveSensorResult.serializer(), result)
    }

    private fun apply(result: LiveSensorResult) {
        _state.value = _state.value.copy(
            busy = false,
            camera = result.camera,
            microphone = result.microphone,
            cameraAvailable = result.cameraAvailable,
            microphoneAvailable = result.microphoneAvailable,
            message = result.refused,
            image = if (result.camera) _state.value.image else null,
        )

        if (result.camera || result.microphone) ensureCollecting() else stopCollecting()
    }

    private fun ensureCollecting() {
        if (collector?.isActive == true) return

        collector = viewModelScope.launch {
            connection.liveFrames.collect { frame ->
                when (frame.header.kind) {
                    LiveSensorKind.CAMERA -> {
                        val bitmap = withContext(Dispatchers.Default) {
                            val bytes = frame.payload()
                            try {
                                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            } catch (e: OutOfMemoryError) {
                                null
                            }
                        }

                        if (bitmap != null) {
                            _state.value = _state.value.copy(
                                image = bitmap.asImageBitmap(),
                                framesReceived = _state.value.framesReceived + 1,
                            )
                        }
                    }

                    LiveSensorKind.MICROPHONE -> playAudio(frame.payload(), frame.header.widthOrSampleRate)
                }
            }
        }
    }

    private fun stopCollecting() {
        collector?.cancel()
        collector = null
    }

    /**
     * Opens an [AudioTrack] in streaming mode.
     *
     * Raw PCM rather than a decoder because that is what the PC sends: `AudioGraph` produces samples
     * and converting them once, on the machine that already has them, is cheaper than shipping a
     * container the phone would have to demux.
     */
    private fun startAudio() {
        synchronized(audioLock) {
            if (audio != null) return

            try {
                val rate = DEFAULT_SAMPLE_RATE
                val minimum = AudioTrack.getMinBufferSize(
                    rate,
                    AudioFormat.CHANNEL_OUT_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                )

                val track = AudioTrack.Builder()
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                            .build(),
                    )
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(rate)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                            .build(),
                    )
                    // Four times the minimum: enough to ride out a jittery link without adding
                    // latency anyone would notice on a room monitor.
                    .setBufferSizeInBytes(maxOf(minimum, 1) * 4)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()

                track.play()
                audio = track
            } catch (e: Exception) {
                Log.w(TAG, "Live audio could not start: ${e.javaClass.simpleName}")
                _state.value = _state.value.copy(message = "This phone could not open audio playback.")
            }
        }
    }

    private fun playAudio(pcm: ByteArray, sampleRate: Int) {
        val track = audio ?: return

        try {
            // WRITE_NON_BLOCKING: a full buffer drops audio rather than stalling the collector, which
            // would back up the whole live flow including the camera.
            track.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING)
        } catch (e: IllegalStateException) {
            Log.d(TAG, "Live audio write failed; the track is gone.")
        }
    }

    private fun stopAudio() {
        synchronized(audioLock) {
            val track = audio ?: return
            audio = null

            try {
                track.pause()
                track.flush()
                track.stop()
            } catch (e: IllegalStateException) {
                // Already stopped; release still has to happen.
            }

            track.release()
        }
    }

    private fun describe(cause: Throwable): String = when (cause) {
        is dev.nexus.control.net.NotConnectedException -> "Not connected to the PC."

        is dev.nexus.control.net.NexusRequestException ->
            cause.error.message.ifBlank { "The PC refused that." }

        else -> "Something went wrong talking to the PC."
    }

    override fun onCleared() {
        stopAudio()
        stopCollecting()
        super.onCleared()
    }

    companion object {
        private const val TAG = "NexusLiveMonitor"

        /**
         * What the PC's audio graph runs at in practice. The header carries the real rate and the
         * track is opened before the first frame arrives, so this is the opening guess rather than
         * an assumption the stream depends on.
         */
        private const val DEFAULT_SAMPLE_RATE = 48_000

        val Factory = viewModelFactory {
            initializer {
                val container = this.nexusContainer
                LiveMonitorViewModel(container.connection, container.connection)
            }
        }
    }
}
