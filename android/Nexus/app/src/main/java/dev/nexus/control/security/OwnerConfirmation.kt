package dev.nexus.control.security

import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.fragment.app.FragmentActivity
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/** What a confirmation attempt produced. */
enum class OwnerConfirmation {
    /** The device confirmed its owner. */
    CONFIRMED,

    /** The prompt was shown and not satisfied — cancelled, or the wrong face or finger. */
    DECLINED,

    /**
     * The device has no biometric and no screen lock configured.
     *
     * Treated as a refusal by everything that requires confirmation. A check that passes when it
     * cannot run is not a check, and this one guards standing down a security incident.
     */
    UNAVAILABLE,
}

/**
 * Asks the phone to confirm its owner before an action that matters.
 *
 * **Android owns the biometrics entirely.** This asks a yes-or-no question through `BiometricPrompt`
 * and receives a yes or a no. Nexus never sees a fingerprint or a face, never stores a template, and
 * never learns which method was used — the platform holds that data and protects it far better than
 * this app could.
 *
 * **What it authorises.** The phone has already proved it is the Primary Phone with its shared
 * secret; that is transport authentication and it happens on every connection. This is different: it
 * proves the person holding the phone right now is the owner. Without it, an unlocked phone left on
 * a desk could stand down a security incident about the person who picked it up.
 *
 * The Nexus shared secret is never involved. Nothing about the credential passes through the
 * biometric layer.
 */
interface OwnerConfirmationPrompt {
    fun isAvailable(): Boolean

    suspend fun confirm(title: String, subtitle: String): OwnerConfirmation
}

/**
 * The real prompt, backed by `BiometricPrompt`.
 *
 * Accepts a strong biometric or the device credential. The credential fallback matters: a phone
 * whose fingerprint sensor is wet or whose face is not recognised in the dark must still let the
 * owner answer an alarm, and a PIN the owner set is a real authentication factor.
 */
class BiometricOwnerConfirmation(private val activity: FragmentActivity) : OwnerConfirmationPrompt {

    private val allowed =
        BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL

    override fun isAvailable(): Boolean =
        BiometricManager.from(activity).canAuthenticate(allowed) == BiometricManager.BIOMETRIC_SUCCESS

    override suspend fun confirm(title: String, subtitle: String): OwnerConfirmation {
        if (!isAvailable()) return OwnerConfirmation.UNAVAILABLE

        return suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(
                activity,
                androidx.core.content.ContextCompat.getMainExecutor(activity),
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        if (continuation.isActive) continuation.resume(OwnerConfirmation.CONFIRMED)
                    }

                    override fun onAuthenticationError(code: Int, message: CharSequence) {
                        // Every error is a refusal. Distinguishing "cancelled" from "no hardware"
                        // for the caller would only invite a code path that treats one as a pass.
                        if (continuation.isActive) {
                            continuation.resume(
                                if (code == BiometricPrompt.ERROR_NO_DEVICE_CREDENTIAL) {
                                    OwnerConfirmation.UNAVAILABLE
                                } else {
                                    OwnerConfirmation.DECLINED
                                },
                            )
                        }
                    }

                    override fun onAuthenticationFailed() {
                        // A single non-matching finger. The prompt stays up; the platform decides
                        // when to give up, and that arrives as an error.
                    }
                },
            )

            val info = BiometricPrompt.PromptInfo.Builder()
                .setTitle(title)
                .setSubtitle(subtitle)
                .setAllowedAuthenticators(allowed)
                .build()

            prompt.authenticate(info)

            continuation.invokeOnCancellation { prompt.cancelAuthentication() }
        }
    }
}
