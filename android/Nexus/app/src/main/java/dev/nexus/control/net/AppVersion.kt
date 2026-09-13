package dev.nexus.control.net

import android.content.Context

/** Which build of Nexus this is, as Android installed it. */
data class AppVersion(val code: Int, val name: String) {

    override fun toString(): String = "$name ($code)"

    companion object {
        val UNKNOWN = AppVersion(0, "")

        /** Read from the installed package rather than build constants, so it is what Android sees. */
        fun of(context: Context): AppVersion = runCatching {
            val info = context.packageManager.getPackageInfo(context.packageName, 0)
            AppVersion(info.longVersionCode.toInt(), info.versionName.orEmpty())
        }.getOrDefault(UNKNOWN)
    }
}
