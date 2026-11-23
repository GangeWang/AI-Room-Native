// Bakes the subject masks for the built-in portraits, so the app can show the relief
// card faces without loading Vision at launch.
//
// Usage (from macos/):  swift Scripts/bake-portrait-masks.swift
// Re-run it whenever a built-in portrait in Sources/AIROOMNative/Resources changes.
//
// For each portrait it writes <name>-mask.png next to it: an 8-bit grayscale PNG, at most
// 720 px high (the same scale the app keeps cut-outs at), white where the figure is.
// It uses the same Vision request as the app's own cut-outs for uploaded portraits.
import Foundation
import Vision
import CoreImage
import AppKit

let maxHeight: CGFloat = 720
let portraits = ["claudia.webp", "gptia.webp", "gemini.webp", "ds.webp", "kimi.webp", "glm.jpg"]
let scriptDir = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
let resources = CommandLine.arguments.count > 1
    ? URL(fileURLWithPath: CommandLine.arguments[1])
    : scriptDir.appendingPathComponent("../Sources/AIROOMNative/Resources").standardizedFileURL
let context = CIContext()
var failed = false

for name in portraits {
    let source = resources.appendingPathComponent(name)
    guard let image = NSImage(contentsOf: source),
          let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
        print("skip \(name): not found"); continue
    }
    do {
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: cg)
        try handler.perform([request])
        guard let result = request.results?.first, !result.allInstances.isEmpty else {
            print("FAIL \(name): no subject found"); failed = true; continue
        }
        let buffer = try result.generateScaledMaskForImage(forInstances: result.allInstances, from: handler)
        var mask = CIImage(cvPixelBuffer: buffer)
        let scale = min(1, maxHeight / mask.extent.height)
        mask = mask.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let output = resources.appendingPathComponent((name as NSString).deletingPathExtension + "-mask.png")
        try context.writePNGRepresentation(of: mask, to: output, format: .L8,
                                           colorSpace: CGColorSpace(name: CGColorSpace.linearGray)!)
        let bytes = (try FileManager.default.attributesOfItem(atPath: output.path)[.size] as? Int) ?? 0
        print("\(output.lastPathComponent)  \(Int(mask.extent.width))×\(Int(mask.extent.height))  \(bytes / 1024) KB")
    } catch {
        print("FAIL \(name): \(error.localizedDescription)"); failed = true
    }
}
exit(failed ? 1 : 0)
