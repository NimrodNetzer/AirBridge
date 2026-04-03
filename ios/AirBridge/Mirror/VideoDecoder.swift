import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo
import AVFoundation

/// H.264 software/hardware decoder using VideoToolbox.
/// Accepts raw NAL units (no Annex-B start codes), maintains SPS/PPS state,
/// and returns CMSampleBuffers suitable for enqueueing to AVSampleBufferDisplayLayer.
final class VideoDecoder {

    // MARK: - State

    private var sps: Data?
    private var pps: Data?
    private var formatDescription: CMVideoFormatDescription?

    // MARK: - Public API

    /// Decodes a single NAL unit.
    /// - Parameters:
    ///   - nalData: Raw NAL unit bytes (no Annex-B start code, no AVCC length prefix).
    ///   - pts: Presentation timestamp in microseconds.
    ///   - isKeyFrame: True if this is an IDR / keyframe.
    /// - Returns: A `CMSampleBuffer` ready for display, or nil if the NAL unit was
    ///   parameter data (SPS/PPS) that updated the format description.
    func decode(nalData: Data, pts: CMTime, isKeyFrame: Bool) -> CMSampleBuffer? {
        guard !nalData.isEmpty else { return nil }

        let nalType = nalData[0] & 0x1F

        switch nalType {
        case 7: // SPS
            sps = nalData
            rebuildFormatDescription()
            return nil
        case 8: // PPS
            pps = nalData
            rebuildFormatDescription()
            return nil
        default:
            break
        }

        guard let formatDesc = formatDescription else {
            // Cannot decode without a format description
            return nil
        }

        return makeSampleBuffer(nalData: nalData, pts: pts, formatDesc: formatDesc)
    }

    // MARK: - Format description

    private func rebuildFormatDescription() {
        guard let sps = sps, let pps = pps else { return }

        var desc: CMVideoFormatDescription?
        sps.withUnsafeBytes { spsPtr in
            pps.withUnsafeBytes { ppsPtr in
                guard let spsBase = spsPtr.baseAddress,
                      let ppsBase = ppsPtr.baseAddress else { return }
                var paramPtrs: [UnsafePointer<UInt8>] = [
                    spsBase.assumingMemoryBound(to: UInt8.self),
                    ppsBase.assumingMemoryBound(to: UInt8.self)
                ]
                var paramSizes: [Int] = [sps.count, pps.count]
                CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: nil,
                    parameterSetCount: 2,
                    parameterSetPointers: &paramPtrs,
                    parameterSetSizes: &paramSizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &desc
                )
            }
        }
        formatDescription = desc
    }

    // MARK: - Sample buffer

    private func makeSampleBuffer(nalData: Data,
                                  pts: CMTime,
                                  formatDesc: CMVideoFormatDescription) -> CMSampleBuffer? {
        // Build AVCC block: [4-byte big-endian NAL length][NAL bytes]
        var blockData = Data()
        var nalLength = UInt32(nalData.count).bigEndian
        withUnsafeBytes(of: &nalLength) { blockData.append(contentsOf: $0) }
        blockData.append(nalData)

        // Create CMBlockBuffer
        var blockBuffer: CMBlockBuffer?
        var status = blockData.withUnsafeMutableBytes { ptr -> OSStatus in
            CMBlockBufferCreateWithMemoryBlock(
                allocator: nil,
                memoryBlock: ptr.baseAddress,
                blockLength: blockData.count,
                blockAllocator: kCFAllocatorNull,
                customBlockSource: nil,
                offsetToData: 0,
                dataLength: blockData.count,
                flags: 0,
                blockBufferOut: &blockBuffer
            )
        }
        guard status == noErr, let block = blockBuffer else { return nil }

        // Timing info
        var timingInfo = CMSampleTimingInfo(
            duration: CMTime.invalid,
            presentationTimeStamp: pts,
            decodeTimeStamp: CMTime.invalid
        )

        // Create CMSampleBuffer
        var sampleBuffer: CMSampleBuffer?
        status = CMSampleBufferCreateReady(
            allocator: nil,
            dataBuffer: block,
            formatDescription: formatDesc,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timingInfo,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr else { return nil }

        // Mark as ready for display
        if let sb = sampleBuffer {
            if let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: true) {
                let count = CFArrayGetCount(attachments)
                if count > 0, let rawDict = CFArrayGetValueAtIndex(attachments, 0) {
                    let dict = Unmanaged<CFMutableDictionary>
                        .fromOpaque(rawDict)
                        .takeUnretainedValue()
                    CFDictionarySetValue(
                        dict,
                        Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately as AnyObject).toOpaque(),
                        Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
                    )
                }
            }
        }
        return sampleBuffer
    }
}
