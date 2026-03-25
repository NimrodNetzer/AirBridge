# AirBridge Protocol — Version 1 Specification

## Overview

The AirBridge protocol is a binary framing protocol over TCP with TLS 1.3. It runs entirely on the local network — no internet relay, no cloud.

**Protocol version:** 1
**Transport:** TCP + TLS 1.3
**Port (default):** 47821
**Discovery:** mDNS service type `_airbridge._tcp.local`

---

## Wire Format

Every message on the wire follows this frame structure:

```
┌─────────────────────────────────────────────────────┐
│  Length (4 bytes, big-endian uint32)                 │
│  Type   (1 byte, MessageType enum)                   │
│  Payload (variable, Protobuf-encoded)                │
└─────────────────────────────────────────────────────┘
```

- **Length** — total byte count of `Type + Payload` (does NOT include the 4-byte length field itself)
- **Type** — identifies the message type (see enum below)
- **Payload** — Protobuf-serialized message body (defined in `messages.proto`)

Maximum message size: **64 MB** (enforced by receiver; larger payloads must use chunking)

---

## Message Types

| Value | Name | Direction | Description |
|-------|------|-----------|-------------|
| 0x01 | `Handshake` | Both | Protocol version negotiation + device identity |
| 0x02 | `HandshakeAck` | Both | Handshake acknowledgement + pairing status |
| 0x03 | `PairingRequest` | Client → Host | Request pairing with PIN |
| 0x04 | `PairingResponse` | Host → Client | Accept or reject pairing |
| 0x10 | `FileTransferStart` | Sender | Begin a file transfer session |
| 0x11 | `FileChunk` | Sender | A chunk of file data |
| 0x12 | `FileTransferAck` | Receiver | Acknowledge receipt of chunk(s) |
| 0x13 | `FileTransferEnd` | Sender | Signal transfer complete, include final hash |
| 0x20 | `MirrorStart` | Client → Host | Request screen mirror session |
| 0x21 | `MirrorFrame` | Android → Windows | Encoded H.264 video frame |
| 0x22 | `MirrorStop` | Both | End mirror session |
| 0x30 | `InputEvent` | Windows → Android | Mouse / keyboard / touch input relay |
| 0x40 | `ClipboardSync` | Both | Sync clipboard content |
| 0xF0 | `Ping` | Both | Keepalive |
| 0xF1 | `Pong` | Both | Keepalive response |
| 0xFF | `Error` | Both | Protocol or application error |

---

## Connection & Handshake Sequence

```
Client                                    Host (Windows)
  │                                           │
  │──── TCP connect (port 47821) ────────────►│
  │◄─── TLS 1.3 handshake ──────────────────►│
  │                                           │
  │──── Handshake {version=1, deviceId} ─────►│
  │◄─── HandshakeAck {version=1, paired?} ───│
  │                                           │
  │  (if not paired)                          │
  │──── PairingRequest {publicKey, pin} ─────►│
  │◄─── PairingResponse {accept/reject} ─────│
  │                                           │
  │  (if paired — mutual TLS cert already)    │
  │  ──── ready to exchange feature messages ─│
```

---

## Pairing Model (TOFU)

1. First connection: client sends its Ed25519 public key + a user-visible PIN
2. Host displays PIN to user; user confirms on both devices
3. On confirmation: host stores client's public key; client stores host's public key
4. All future connections use stored keys for mutual TLS authentication — no PIN needed
5. Pairing can be revoked from either device (removes stored key)

**Security properties:**
- No central authority — fully peer-to-peer
- MITM protection via PIN confirmation on both sides
- Revoking pairing on one device immediately blocks reconnection

---

## File Transfer Flow

```
Sender                              Receiver
  │──── FileTransferStart ──────────►│
  │        {sessionId, filename,      │
  │         totalSize, chunkSize,     │
  │         sha256}                   │
  │                                   │
  │──── FileChunk ──────────────────►│  (repeat)
  │        {sessionId, index,         │
  │         data, isLast}             │
  │                                   │
  │◄─── FileTransferAck ─────────────│  (per chunk or batched)
  │        {sessionId, nextExpected}  │
  │                                   │
  │──── FileTransferEnd ────────────►│
  │        {sessionId, sha256}        │
  │                                   │
  │◄─── FileTransferAck ─────────────│  (final confirmation)
```

- Chunk size default: **256 KB**
- Resume: receiver sends `nextExpected` chunk index; sender resumes from there
- Integrity: SHA-256 of full file verified by receiver on completion

---

## Mirror Frame Flow

```
Android                             Windows
  │──── MirrorStart ───────────────►│
  │        {sessionId, width,        │
  │         height, fps, codec}      │
  │                                  │
  │──── MirrorFrame ───────────────►│  (continuous)
  │        {sessionId, pts,          │
  │         keyframe, data}          │
  │                                  │
  │◄─── InputEvent ─────────────────│  (user input)
  │        {type, x, y, keycode}     │
  │                                  │
  │◄─── MirrorStop ─────────────────│
```

- Codec: H.264 baseline (required), H.265 (optional, negotiated in `MirrorStart`)
- Frame data is raw NAL units, not containerized
- PTS (presentation timestamp) in microseconds

---

## Versioning Rules

- Protocol version is a single integer, currently `1`
- `Handshake` includes the sender's protocol version
- If versions differ, the lower version is used (backward compatible changes only)
- If the lower version cannot be supported, `Error` message is sent with code `VERSION_MISMATCH` and connection is closed
- Breaking changes MUST increment the protocol version

---

## Error Codes

| Code | Name | Meaning |
|------|------|---------|
| 1 | `VERSION_MISMATCH` | Protocol versions incompatible |
| 2 | `NOT_PAIRED` | Operation requires pairing |
| 3 | `PAIRING_REJECTED` | User rejected pairing |
| 4 | `TRANSFER_INTERRUPTED` | File transfer session lost |
| 5 | `MIRROR_INTERRUPTED` | Mirror session lost |
| 6 | `INVALID_MESSAGE` | Malformed or unexpected message |
| 7 | `UNAUTHORIZED` | Key mismatch or authentication failure |
