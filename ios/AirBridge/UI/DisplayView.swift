import SwiftUI
import UIKit
import AVFoundation
import CoreMedia
import Combine

// MARK: - DisplayHostView (UIKit)

/// UIKit view that hosts an AVSampleBufferDisplayLayer for H.264 frame rendering.
final class DisplayHostView: UIView {

    // MARK: - Layer

    let displayLayer = AVSampleBufferDisplayLayer()

    override static var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }

    var sampleBufferDisplayLayer: AVSampleBufferDisplayLayer {
        // Safe cast: layerClass guarantees this
        layer as! AVSampleBufferDisplayLayer
    }

    // MARK: - Touch relay

    var onTouch: ((_ normalizedX: Float, _ normalizedY: Float, _ kind: InputEventKind) -> Void)?

    // MARK: - Init

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        sampleBufferDisplayLayer.videoGravity = .resizeAspect
    }

    required init?(coder: NSCoder) { fatalError() }

    // MARK: - Touch handling

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        relay(touches: touches, kind: .touch)
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        relay(touches: touches, kind: .mouse)
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        relay(touches: touches, kind: .touch)
    }

    private func relay(touches: Set<UITouch>, kind: InputEventKind) {
        guard let touch = touches.first else { return }
        let point = touch.location(in: self)
        let nx = Float(point.x / bounds.width)
        let ny = Float(point.y / bounds.height)
        onTouch?(nx, ny, kind)
    }
}

// MARK: - DisplayView (SwiftUI bridge)

/// Full-screen SwiftUI view that wraps DisplayHostView and connects it to the session.
struct DisplayView: UIViewRepresentable {

    @ObservedObject var session: TabletDisplaySession

    // MARK: - UIViewRepresentable

    func makeCoordinator() -> Coordinator {
        Coordinator(session: session)
    }

    func makeUIView(context: Context) -> DisplayHostView {
        let view = DisplayHostView()
        view.onTouch = { x, y, kind in
            Task {
                await context.coordinator.session.sendTouchEvent(
                    normalizedX: x,
                    normalizedY: y,
                    kind: kind
                )
            }
        }
        context.coordinator.hostView = view
        return view
    }

    func updateUIView(_ uiView: DisplayHostView, context: Context) {
        // Enqueue the latest sample buffer whenever the session publishes one
        if let sampleBuffer = session.latestSampleBuffer {
            uiView.sampleBufferDisplayLayer.enqueue(sampleBuffer)
        }
    }

    // MARK: - Coordinator

    final class Coordinator: NSObject {
        let session: TabletDisplaySession
        var hostView: DisplayHostView?
        private var cancellable: AnyCancellable?

        init(session: TabletDisplaySession) {
            self.session = session
            super.init()
            // Observe latestSampleBuffer on the main thread and push to display layer
            cancellable = session.$latestSampleBuffer
                .compactMap { $0 }
                .receive(on: RunLoop.main)
                .sink { [weak self] sampleBuffer in
                    self?.hostView?.sampleBufferDisplayLayer.enqueue(sampleBuffer)
                }
        }
    }
}
