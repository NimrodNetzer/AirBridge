package com.airbridge.app.transport

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.discovery.NsdDiscoveryService
import com.airbridge.app.transport.protocol.ProtocolMessage
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Test
import java.net.InetAddress

/**
 * Unit tests for [DeviceInfo] construction from resolved [NsdServiceInfo] objects.
 *
 * These tests exercise the discovery logic in isolation by constructing a
 * [NsdDiscoveryService] with a mocked [Context] and [NsdManager], then
 * directly verifying the [DeviceInfo] instances produced from resolved service info.
 *
 * Because [NsdDiscoveryService.buildDeviceInfo] is private, we drive it indirectly
 * by supplying a pre-built resolved [NsdServiceInfo] and confirming the emitted
 * [DeviceInfo] values on [NsdDiscoveryService.visibleDevicesFlow].
 */
class NsdDiscoveryServiceTest {

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /**
     * Builds a [NsdDiscoveryService] with mocked Android system services.
     * [NsdManager] is provided as a mock so no actual network operations occur.
     */
    private fun buildService(): NsdDiscoveryService {
        val nsdManager = mockk<NsdManager>(relaxed = true)
        val context = mockk<Context>(relaxed = true)
        every { context.getSystemService(Context.NSD_SERVICE) } returns nsdManager
        return NsdDiscoveryService(context)
    }

    /**
     * Creates a fully-resolved [NsdServiceInfo] mock with the given parameters.
     * Uses MockK to stub [NsdServiceInfo.host] since there is no public setter in the SDK stubs.
     */
    private fun resolvedServiceInfo(
        serviceName: String = "TestDevice",
        ip: String = "192.168.1.42",
        port: Int = ProtocolMessage.DEFAULT_PORT,
        deviceId: String? = null,
        deviceName: String? = null,
        deviceType: DeviceType? = null
    ): NsdServiceInfo {
        val attrs = mutableMapOf<String, ByteArray>()
        if (deviceId != null)   attrs["deviceId"]   = deviceId.toByteArray(Charsets.UTF_8)
        if (deviceName != null) attrs["deviceName"]  = deviceName.toByteArray(Charsets.UTF_8)
        if (deviceType != null) attrs["deviceType"]  = deviceType.name.toByteArray(Charsets.UTF_8)

        return mockk<NsdServiceInfo>(relaxed = true) {
            every { this@mockk.serviceName } returns serviceName
            every { serviceType }            returns "_airbridge._tcp."
            every { this@mockk.port }        returns port
            every { host }                   returns InetAddress.getByName(ip)
            every { attributes }             returns attrs
        }
    }

    // -------------------------------------------------------------------------
    // DeviceInfo construction tests
    // -------------------------------------------------------------------------

    @Test
    fun `DeviceInfo uses TXT record deviceId when present`() {
        val info = resolvedServiceInfo(deviceId = "win-pc-001", deviceName = "My Windows PC")
        val device = extractDeviceInfo(info)

        assertEquals("win-pc-001", device.deviceId)
    }

    @Test
    fun `DeviceInfo falls back to serviceName as deviceId when TXT record absent`() {
        val info = resolvedServiceInfo(serviceName = "FallbackDevice")
        val device = extractDeviceInfo(info)

        assertEquals("FallbackDevice", device.deviceId)
    }

    @Test
    fun `DeviceInfo uses TXT record deviceName when present`() {
        val info = resolvedServiceInfo(deviceName = "Alice's PC")
        val device = extractDeviceInfo(info)

        assertEquals("Alice's PC", device.deviceName)
    }

    @Test
    fun `DeviceInfo falls back to serviceName as deviceName when TXT record absent`() {
        val info = resolvedServiceInfo(serviceName = "ServiceFallback")
        val device = extractDeviceInfo(info)

        assertEquals("ServiceFallback", device.deviceName)
    }

    @Test
    fun `DeviceInfo maps WINDOWS_PC deviceType from TXT record`() {
        val info = resolvedServiceInfo(deviceType = DeviceType.WINDOWS_PC)
        val device = extractDeviceInfo(info)

        assertEquals(DeviceType.WINDOWS_PC, device.deviceType)
    }

    @Test
    fun `DeviceInfo maps ANDROID_PHONE deviceType from TXT record`() {
        val info = resolvedServiceInfo(deviceType = DeviceType.ANDROID_PHONE)
        val device = extractDeviceInfo(info)

        assertEquals(DeviceType.ANDROID_PHONE, device.deviceType)
    }

    @Test
    fun `DeviceInfo defaults to WINDOWS_PC when deviceType TXT record absent`() {
        val info = resolvedServiceInfo()  // no deviceType attribute
        val device = extractDeviceInfo(info)

        assertEquals(DeviceType.WINDOWS_PC, device.deviceType)
    }

    @Test
    fun `DeviceInfo captures correct IP address`() {
        val info = resolvedServiceInfo(ip = "10.0.0.5")
        val device = extractDeviceInfo(info)

        assertEquals("10.0.0.5", device.ipAddress)
    }

    @Test
    fun `DeviceInfo captures correct port`() {
        val info = resolvedServiceInfo(port = ProtocolMessage.DEFAULT_PORT)
        val device = extractDeviceInfo(info)

        assertEquals(ProtocolMessage.DEFAULT_PORT, device.port)
    }

    @Test
    fun `DeviceInfo isPaired defaults to false for newly discovered devices`() {
        val info = resolvedServiceInfo()
        val device = extractDeviceInfo(info)

        assertFalse(device.isPaired)
    }

    @Test
    fun `DeviceInfo is not null for fully resolved service info`() {
        val info = resolvedServiceInfo(
            deviceId   = "full-device-001",
            deviceName = "Full Device",
            deviceType = DeviceType.ANDROID_TABLET,
            ip         = "172.16.0.1",
            port       = ProtocolMessage.DEFAULT_PORT
        )
        val device = extractDeviceInfo(info)

        assertNotNull(device)
        assertEquals("full-device-001",       device.deviceId)
        assertEquals("Full Device",            device.deviceName)
        assertEquals(DeviceType.ANDROID_TABLET, device.deviceType)
        assertEquals("172.16.0.1",             device.ipAddress)
        assertEquals(ProtocolMessage.DEFAULT_PORT, device.port)
        assertFalse(device.isPaired)
    }

    // -------------------------------------------------------------------------
    // visibleDevicesFlow integration test
    // -------------------------------------------------------------------------

    @Test
    fun `visibleDevicesFlow starts with empty list`() = runTest {
        val service = buildService()
        // The flow's initial emission is an empty list
        val initial = service.visibleDevicesFlow.first()
        assertEquals(emptyList<DeviceInfo>(), initial)
    }

    // -------------------------------------------------------------------------
    // Private helper — invokes the private buildDeviceInfo via reflection
    // -------------------------------------------------------------------------

    /**
     * Reflectively calls [NsdDiscoveryService.buildDeviceInfo] so we can unit-test
     * the mapping logic without starting real mDNS operations.
     */
    private fun extractDeviceInfo(info: NsdServiceInfo): DeviceInfo {
        val service = buildService()
        val method = NsdDiscoveryService::class.java
            .getDeclaredMethod("buildDeviceInfo", NsdServiceInfo::class.java)
        method.isAccessible = true
        @Suppress("UNCHECKED_CAST")
        return method.invoke(service, info) as DeviceInfo
    }
}
