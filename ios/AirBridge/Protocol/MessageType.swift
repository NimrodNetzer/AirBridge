import Foundation

/// All message type byte values used in the AirBridge wire protocol.
enum MessageType: UInt8 {
    case handshake       = 0x01
    case handshakeAck    = 0x02
    case pairingRequest  = 0x03
    case pairingResponse = 0x04
    case mirrorStart     = 0x20
    case mirrorFrame     = 0x21
    case mirrorStop      = 0x22
    case inputEvent      = 0x30
    case ping            = 0xF0
    case pong            = 0xF1
}
