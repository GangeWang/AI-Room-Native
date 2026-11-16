#if os(macOS)
import Foundation
import Darwin

/// Starts native VTube Studio instances in a fixed order.
///
/// Every instance of the app takes the first free API port from 8001 up and
/// names its NDI output by start order too ("Live2D Camera NDI5", then
/// "… NDI5 2"). Starting one, waiting for its port, then starting the next
/// keeps ports and OBS sources the same every time.
public struct VTubeStudioLauncher: Sendable {
    public static let bundleIdentifier = "com.Denchi.VTube-Studio"

    public enum Failure: Error, LocalizedError, Equatable {
        case notInstalled
        case launchFailed(String)
        case portNeverOpened(Int)

        public var errorDescription: String? {
            switch self {
            case .notInstalled:
                return "找不到原生版 VTube Studio（/Applications/VTubeStudio.app）。"
            case .launchFailed(let reason):
                return "無法開啟 VTube Studio：\(reason)"
            case .portNeverOpened(let port):
                return "VTube Studio 已開啟，但連接埠 \(port) 一直沒有回應。請在 VTS 設定開啟「Start API」（兩個實例共用設定，開一次即可），再按一次「開啟 VTube Studio」。"
            }
        }
    }

    public var isListening: @Sendable (Int) async -> Bool
    public var launch: @Sendable (URL) throws -> Void
    public var sleep: @Sendable (TimeInterval) async -> Void

    public init(isListening: @escaping @Sendable (Int) async -> Bool = { Self.portIsOpen($0) },
                launch: @escaping @Sendable (URL) throws -> Void = Self.openNewInstance,
                sleep: @escaping @Sendable (TimeInterval) async -> Void = { try? await Task.sleep(nanoseconds: UInt64($0 * 1_000_000_000)) }) {
        self.isListening = isListening
        self.launch = launch
        self.sleep = sleep
    }

    /// The native (non-Steam) build, checked by bundle id.
    public static func installedApp(candidates: [URL] = [
        URL(fileURLWithPath: "/Applications/VTubeStudio.app"),
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications/VTubeStudio.app"),
    ]) -> URL? {
        candidates.first { Bundle(url: $0)?.bundleIdentifier == bundleIdentifier }
    }

    /// Opens one instance per port that is not answering yet, lowest port
    /// first, waiting for each before the next. Returns the ports it started.
    @discardableResult
    public func start(ports: [Int], app: URL, timeout: TimeInterval = 60,
                      progress: @escaping @Sendable (Int) async -> Void = { _ in }) async throws -> [Int] {
        var started: [Int] = []
        for port in Array(Set(ports)).sorted() {
            if await isListening(port) { continue }
            await progress(port)
            try launch(app)
            var waited: TimeInterval = 0
            while !(await isListening(port)) {
                guard waited < timeout else { throw Failure.portNeverOpened(port) }
                await sleep(0.5); waited += 0.5
            }
            started.append(port)
        }
        return started
    }

    /// `open -n` starts a separate instance; `--nosteam` skips Steam.
    @Sendable public static func openNewInstance(_ app: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        process.arguments = ["-n", app.path, "--args", "--nosteam"]
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        do { try process.run() } catch { throw Failure.launchFailed(error.localizedDescription) }
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let text = String(decoding: errors.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            throw Failure.launchFailed(text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    /// Whether something accepts TCP connections on 127.0.0.1:`port`.
    public static func portIsOpen(_ port: Int) -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(clamping: port)).bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
#endif
