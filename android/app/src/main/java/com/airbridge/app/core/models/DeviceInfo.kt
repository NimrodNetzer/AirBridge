package com.airbridge.app.core.models

/** Device type classification — mirrors Windows enum. */
enum class DeviceType {
    WINDOWS_PC,
    ANDROID_PHONE,
    ANDROID_TABLET
}

/**
 * Represents a discovered or paired remote device.
 * Immutable data class — update by copy.
 */
data class DeviceInfo(
    val deviceId: String,
    val deviceName: String,
    val deviceType: DeviceType,
    val ipAddress: String,
    val port: Int,
    val isPaired: Boolean
)
