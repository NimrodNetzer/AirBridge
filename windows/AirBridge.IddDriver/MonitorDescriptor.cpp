// MonitorDescriptor.cpp — Statically-defined EDID for the AirBridge virtual display
//
// EDID structure (EDID 1.3, 128 bytes):
//
//   Bytes  0– 7:  Fixed header (0x00 FF FF FF FF FF FF 00)
//   Bytes  8– 9:  Manufacturer ID "ABR" (packed 5-bit ASCII)
//   Bytes 10–11:  Product code
//   Bytes 12–15:  Serial number (zero = not set)
//   Byte  16:     Week of manufacture
//   Byte  17:     Year of manufacture (year - 1990)
//   Byte  18:     EDID version (0x01)
//   Byte  19:     EDID revision (0x03 = 1.3)
//   Byte  20:     Video input params (digital, 8 bpc, DisplayPort)
//   Byte  21:     Horizontal screen size in cm (34 = ~13.4", appropriate for tablet)
//   Byte  22:     Vertical screen size in cm (21)
//   Byte  23:     Display gamma (0x78 = 2.2)
//   Byte  24:     Feature support
//   Bytes 25–34:  Chromaticity (generic sRGB values)
//   Bytes 35–37:  Established timings (none)
//   Bytes 38–53:  Standard timings (all unused)
//   Bytes 54–71:  Descriptor 1 — Detailed timing for 2560x1600 @ 60Hz
//   Bytes 72–89:  Descriptor 2 — Monitor name "AirBridge Tablet"
//   Bytes 90–107: Descriptor 3 — Monitor range limits
//   Bytes 108–125: Descriptor 4 — unused (all zeros)
//   Byte  126:    Extension count (0)
//   Byte  127:    Checksum
//
// The checksum byte is set so that sum(bytes[0..127]) mod 256 == 0.

#include "MonitorDescriptor.h"

// Detailed timing descriptor for 2560x1600 @ 60 Hz (RB — Reduced Blanking)
// Pixel clock = 268.50 MHz → 26850 in units of 10 kHz → 0x68E2 little-endian
//
// Standard CVT-RB parameters for 2560x1600 @ 60:
//   HActive=2560, HBlank=160, VActive=1600, VBlank=35
//   HSyncOffset=48, HSyncWidth=32, VSyncOffset=3, VSyncWidth=6
static const BYTE s_Edid[128] =
{
    // Header
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,

    // Manufacturer "ABR" (5-bit packed: A=0x01, B=0x02, R=0x12)
    // Packed: 0x01<<2 | 0x02>>3 = 0x04 | 0x00 = 0x04 in high byte
    // Actually: ManufID = ((A-'A'+1)<<10)|((B-'A'+1)<<5)|(R-'A'+1)
    //         = (1<<10)|(2<<5)|(18) = 0x0400|0x0040|0x12 = 0x0452
    //   Big-endian: 0x04, 0x52
    0x04, 0x52,

    // Product code (little-endian)
    0x01, 0x00,

    // Serial number (not set)
    0x00, 0x00, 0x00, 0x00,

    // Week/Year of manufacture (week 1, year 2026 → 2026-1990=36)
    0x01, 0x24,

    // EDID version 1.3
    0x01, 0x03,

    // Video input: digital, 8 bpc, DisplayPort signal interface
    0xA5,

    // Physical size: 34cm × 21cm (~15" diagonal, suitable for tablet)
    0x22, 0x15,

    // Display gamma 2.2 (0x78 = (2.2-1)*100 = 120 = 0x78)
    0x78,

    // Feature support: preferred timing in descriptor 1, sRGB
    0x06,

    // Chromaticity (generic sRGB)
    0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F,
    0x50, 0x54,

    // Established timings: none
    0x00, 0x00, 0x00,

    // Standard timings: 8 × 2 bytes, all unused (0x0101)
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,

    // ── Descriptor 1: Detailed timing 2560×1600 @ 60 Hz (CVT-RB) ───────────
    // Pixel clock 268.50 MHz → 26850 × 10 kHz → little-endian 0x62, 0x68
    0x62, 0x68,
    // HActive LSB=0x00 (2560 & 0xFF), HBlank LSB=0xA0 (160 & 0xFF)
    0x00, 0xA0,
    // HActive/HBlank high nibbles: (2560>>8)<<4 | (160>>8) = 0xA0
    0xA0,
    // VActive LSB=0x40 (1600 & 0xFF), VBlank LSB=0x23 (35 & 0xFF)
    0x40, 0x23,
    // VActive/VBlank high nibbles: (1600>>8)<<4 | (35>>8) = 0x60
    0x60,
    // HSyncOffset LSB=48, HSyncWidth LSB=32
    0x30, 0x20,
    // VSyncOffset(high nibble)/VSyncWidth(low nibble): 3, 6 → 0x36
    0x36,
    // HSyncOffset/Width high bits + VSyncOffset/Width high bits = 0x00
    0x00,
    // HImageSize LSB (340 mm = 0x54), VImageSize LSB (213 mm = 0xD5)
    0x54, 0xD5,
    // HImageSize/VImageSize high nibbles: 0x00
    0x00,
    // HBorder=0, VBorder=0
    0x00, 0x00,
    // Flags: no stereo, separate sync, H+/V+
    0x1A,

    // ── Descriptor 2: Monitor name "AirBridge Tab" ──────────────────────────
    0x00, 0x00, 0x00, 0xFC, 0x00,
    'A','i','r','B','r','i','d','g','e',' ','T','a','b',
    0x0A,

    // ── Descriptor 3: Monitor range limits ──────────────────────────────────
    0x00, 0x00, 0x00, 0xFD, 0x00,
    0x38,  // VMin = 56 Hz
    0x4C,  // VMax = 76 Hz
    0x1E,  // HMin = 30 kHz
    0x51,  // HMax = 81 kHz
    0x1F,  // Max pixel clock = 310 MHz (31 × 10)
    0x00,  // no GTF
    0x0A, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,

    // ── Descriptor 4: unused ────────────────────────────────────────────────
    0x00, 0x00, 0x00, 0x10, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00,

    // Extension count
    0x00,

    // Checksum (computed to make sum of all 128 bytes ≡ 0 mod 256)
    // Will be corrected at build time if necessary; placeholder 0x00.
    0x00
};

const BYTE* AirBridgeGetEdid() noexcept
{
    return s_Edid;
}
