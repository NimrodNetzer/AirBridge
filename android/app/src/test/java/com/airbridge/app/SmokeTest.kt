package com.airbridge.app

import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.protocol.MessageType
import com.airbridge.app.transport.protocol.ProtocolMessage
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * Smoke tests — verify all interfaces and models compile and are accessible.
 * No behavior is tested here; that happens per-module in later iterations.
 */
class SmokeTest {

    @Test
    fun `DeviceInfo can be constructed`() {
        val device = DeviceInfo(
            deviceId = "test-id",
            deviceName = "Test Phone",
            deviceType = DeviceType.ANDROID_PHONE,
            ipAddress = "192.168.1.100",
            port = ProtocolMessage.DEFAULT_PORT,
            isPaired = false
        )
        assertEquals("test-id", device.deviceId)
        assertEquals(DeviceType.ANDROID_PHONE, device.deviceType)
        assertFalse(device.isPaired)
    }

    @Test
    fun `ProtocolVersion is 1`() {
        assertEquals(1, ProtocolMessage.PROTOCOL_VERSION)
    }

    @Test
    fun `DefaultPort is correct`() {
        assertEquals(47821, ProtocolMessage.DEFAULT_PORT)
    }

    @Test
    fun `MessageType fromByte roundtrip works`() {
        assertEquals(MessageType.HANDSHAKE, MessageType.fromByte(0x01))
        assertEquals(MessageType.FILE_CHUNK, MessageType.fromByte(0x11))
        assertEquals(MessageType.MIRROR_FRAME, MessageType.fromByte(0x21))
        assertEquals(MessageType.ERROR, MessageType.fromByte(0xFF.toByte()))
    }

    @Test
    fun `MessageType fromByte throws on unknown value`() {
        assertThrows(IllegalArgumentException::class.java) {
            MessageType.fromByte(0x99.toByte())
        }
    }

    @Test
    fun `ProtocolMessage equality uses payload content`() {
        val a = ProtocolMessage(MessageType.PING, byteArrayOf(1, 2, 3))
        val b = ProtocolMessage(MessageType.PING, byteArrayOf(1, 2, 3))
        assertEquals(a, b)
    }

    @Test
    fun `All interface types are accessible`() {
        assertNotNull(Class.forName("com.airbridge.app.core.interfaces.IDeviceRegistry"))
        assertNotNull(Class.forName("com.airbridge.app.core.interfaces.IPairingService"))
        assertNotNull(Class.forName("com.airbridge.app.core.interfaces.ITransferSession"))
        assertNotNull(Class.forName("com.airbridge.app.core.interfaces.IMirrorSession"))
        assertNotNull(Class.forName("com.airbridge.app.transport.interfaces.IDiscoveryService"))
        assertNotNull(Class.forName("com.airbridge.app.transport.interfaces.IConnectionManager"))
        assertNotNull(Class.forName("com.airbridge.app.transport.interfaces.IMessageChannel"))
    }
}
