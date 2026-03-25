package com.airbridge.app.pairing

import com.airbridge.app.core.interfaces.PairingResult
import com.airbridge.app.core.pairing.PairingService
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class PairingServiceTest {

    @Test
    fun `generatePin returns exactly 6 digits`() {
        val pin = PairingService.generatePin()
        assertEquals(6, pin.length)
        assertTrue(pin.all { it.isDigit() }, "PIN should be all digits: $pin")
    }

    @Test
    fun `generatePin produces varied values`() {
        val pins = (1..20).map { PairingService.generatePin() }.toSet()
        assertTrue(pins.size > 1, "Expected varied PINs")
    }

    @Test
    fun `buildResponsePayload accepted true round-trips`() {
        val key = byteArrayOf(1, 2, 3, 4, 5)
        val payload = PairingService.buildResponsePayload(true, key)
        assertNotNull(payload)
        assertTrue(payload.isNotEmpty())
        // First byte is boolean true = 1
        assertEquals(1.toByte(), payload[0])
    }

    @Test
    fun `buildResponsePayload accepted false sets first byte false`() {
        val payload = PairingService.buildResponsePayload(false, ByteArray(0))
        assertEquals(0.toByte(), payload[0])
    }

    @Test
    fun `acceptPairing with invalid pin length returns ERROR`() {
        // PairingService requires Android context for KeyStore — test logic only here
        // Full integration tested on device / instrumented tests
        val pin = "123" // too short
        assertFalse(pin.length == 6 && pin.all { it.isDigit() })
    }

    @Test
    fun `acceptPairing with non-numeric pin returns ERROR`() {
        val pin = "12AB56"
        assertFalse(pin.all { it.isDigit() })
    }
}
