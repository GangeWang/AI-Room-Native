import Foundation
import AIROOMCore

/// AI Room's end of the bodies' local socket. It only hands each body its
/// settings and says who holds the floor; it never speaks or moves for anyone.
/// The socket lives in the per-user temporary folder, and a body must present
/// the show's random secret before it gets anything.
@MainActor
final class LiveBodyHub {
    let path: String
    let secret: String
    private let configs: [MemberID: LiveBodyConfig]
    private var listener: Int32 = -1
    private var links: [MemberID: BodySocket] = [:]
    private(set) var floor: MemberID?
    private var closed = false

    var onSpeech: ((MemberID, String, Bool) -> Void)?
    var onToken: ((MemberID, String) -> Void)?
    var onStatus: ((MemberID, String) -> Void)?

    init(configs: [MemberID: LiveBodyConfig]) throws {
        self.configs = configs
        secret = UUID().uuidString + UUID().uuidString
        let folder = FileManager.default.temporaryDirectory.path
        path = (folder as NSString).appendingPathComponent("airoom-body-\(UUID().uuidString.prefix(8)).sock")
        listener = try Self.listen(at: path)
        let fd = listener
        let hub = WeakHub(self)
        Thread {
            while true {
                let client = accept(fd, nil, nil)
                if client < 0 { return }
                let socket = BodySocket(fd: client)
                Thread {
                    socket.readLoop { message in
                        Task { @MainActor in hub.value?.received(message, from: socket) }
                    }
                    Task { @MainActor in hub.value?.dropped(socket) }
                }.start()
            }
        }.start()
    }

    var connected: Set<MemberID> { Set(links.keys) }

    func setFloor(_ member: MemberID?) {
        floor = member
        for (agent, link) in links { link.send(LiveBodyMessage(kind: .floor, granted: agent == member)) }
    }

    func stop() {
        guard !closed else { return }
        closed = true
        setFloor(nil)
        for link in links.values { link.close() }
        links = [:]
        if listener >= 0 { Darwin.close(listener); listener = -1 }
        unlink(path)
    }

    private func received(_ message: LiveBodyMessage, from socket: BodySocket) {
        guard !closed else { return }
        if message.kind == .hello {
            guard message.secret == secret, let raw = message.member,
                  let config = configs[MemberID(rawValue: raw)] else { socket.close(); return }
            let member = MemberID(rawValue: raw)
            links[member]?.close()
            links[member] = socket
            socket.member = member
            socket.send(LiveBodyMessage(kind: .config, config: config))
            socket.send(LiveBodyMessage(kind: .floor, granted: floor == member))
            return
        }
        // Everything else must come from an introduced body, about itself.
        guard let member = socket.member, links[member] === socket else { return }
        switch message.kind {
        case .speech: onSpeech?(member, message.text ?? "", message.done ?? false)
        case .token: if let token = message.token, !token.isEmpty { onToken?(member, token) }
        case .status: if let text = message.text { onStatus?(member, String(text.prefix(300))) }
        default: break
        }
    }

    private func dropped(_ socket: BodySocket) {
        guard let member = socket.member, links[member] === socket else { return }
        links[member] = nil
    }

    private static func listen(at path: String) throws -> Int32 {
        unlink(path)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw NativeCLIError.processFailed("socket() failed") }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        guard bytes.count < MemoryLayout.size(ofValue: address.sun_path) else {
            Darwin.close(fd); throw NativeCLIError.processFailed("socket path too long")
        }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        let size = socklen_t(MemoryLayout<sockaddr_un>.size)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, size) }
        }
        guard bound == 0, chmod(path, 0o600) == 0, Darwin.listen(fd, 8) == 0 else {
            Darwin.close(fd); unlink(path)
            throw NativeCLIError.processFailed("cannot listen on the body socket")
        }
        return fd
    }
}

private final class WeakHub: @unchecked Sendable {
    weak var value: LiveBodyHub?
    init(_ value: LiveBodyHub) { self.value = value }
}

/// One body's connection. Written from the main actor, read on its own thread.
final class BodySocket: @unchecked Sendable {
    private let fd: Int32
    private let lock = NSLock()
    private var isClosed = false
    /// Set once the body has introduced itself (main actor only).
    var member: MemberID?

    init(fd: Int32) { self.fd = fd }

    func send(_ message: LiveBodyMessage) {
        guard let data = try? message.line() else { return }
        lock.lock(); defer { lock.unlock() }
        guard !isClosed else { return }
        data.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                let written = write(fd, raw.baseAddress! + offset, raw.count - offset)
                if written <= 0 { return }
                offset += written
            }
        }
    }

    func close() {
        lock.lock(); defer { lock.unlock() }
        guard !isClosed else { return }
        isClosed = true
        shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }

    func readLoop(_ handle: (LiveBodyMessage) -> Void) {
        var buffer = Data()
        var chunk = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = read(fd, &chunk, chunk.count)
            if count <= 0 { return }
            buffer.append(contentsOf: chunk[0..<count])
            // A body sends short lines; anything huge is not a body.
            if buffer.count > 1_000_000 { close(); return }
            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = Data(buffer[buffer.startIndex..<newline])
                buffer.removeSubrange(buffer.startIndex...newline)
                if let message = LiveBodyMessage.parse(line) { handle(message) }
            }
        }
    }
}
