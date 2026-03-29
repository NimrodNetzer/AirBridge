package com.airbridge.app.core

import android.util.Log
import java.io.File
import java.io.FileWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Minimal append-only black-box logger that mirrors the Windows [AppLog] on Android.
 *
 * Writes timestamped lines to a local file so both sides can be compared side-by-side
 * after a failure. The log file rotates at ~500 KB.
 *
 * **Initialise once** in your Application subclass:
 * ```kotlin
 * AirBridgeLog.init(File(filesDir, "airbridge.log"))
 * ```
 *
 * All methods are safe to call from any thread.
 */
object AirBridgeLog {

    private const val ANDROID_TAG = "AirBridge"
    private const val MAX_LOG_BYTES = 500_000L

    @Volatile private var logFile: File? = null
    private val lock = Any()
    private val fmt  = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    /**
     * Call once from Application.onCreate() with the desired log file path.
     * Until [init] is called, messages are still forwarded to [android.util.Log]
     * but are not written to disk.
     */
    fun init(file: File) {
        file.parentFile?.mkdirs()
        // Rotate: clear if > 500 KB
        if (file.exists() && file.length() > MAX_LOG_BYTES) {
            file.writeText("")
        }
        logFile = file
        info("===== AirBridge session started =====")
    }

    fun debug(message: String) = write("DEBUG", message)
    fun info (message: String) = write("INFO ", message)
    fun warn (message: String) = write("WARN ", message)
    fun error(message: String, throwable: Throwable? = null) {
        val full = if (throwable != null) "$message — ${throwable.javaClass.simpleName}: ${throwable.message}" else message
        write("ERROR", full)
        throwable?.stackTraceToString()?.let { write("     ", it) }
    }

    // -------------------------------------------------------------------------

    private fun write(level: String, message: String) {
        val line = "${fmt.format(Date())} [$level] $message"

        // Always forward to Android logcat as well
        when (level.trim()) {
            "DEBUG" -> Log.d(ANDROID_TAG, message)
            "WARN"  -> Log.w(ANDROID_TAG, message)
            "ERROR" -> Log.e(ANDROID_TAG, message)
            else    -> Log.i(ANDROID_TAG, message)
        }

        val file = logFile ?: return
        synchronized(lock) {
            try {
                FileWriter(file, /* append = */ true).use { it.appendLine(line) }
            } catch (_: Exception) {
                // Never throw from a logger
            }
        }
    }
}
