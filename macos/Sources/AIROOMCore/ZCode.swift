import Foundation

/// ZCode (GLM) runs headless through its own `zcode.cjs app-server`: a line protocol
/// on stdio (not JSON-RPC), driven the way the ZCode desktop app drives it. The
/// recipe was worked out on Windows (docs: windows/ZCODE-NOTES.md on PR #3).
///
/// Unlike the other programs, this needs AIROOM to hand the sign-in over: the
/// desktop keeps a ZCode token in `~/.zcode/v2/credentials.json`, and app-server
/// asks its host for it before each model request. AIROOM decrypts it in memory
/// for the run, passes it only to ZCode's own process over a pipe, and never
/// stores, logs or shows it.
public enum ZCode {
    /// The account provider of the owner's Z.AI Start Plan.
    public static let providerID = "account:zai-start-plan"
    public static let defaultModel = "GLM-5.3-Flash"
    public static let efforts = ["low", "medium", "high", "max"]
    static let fallbackModelIDs = ["GLM-5.3-Flash", "GLM-5.3", "GLM-5.2", "GLM-5-Turbo"]

    /// What a run needs from the desktop's shared state.
    public struct Auth: Sendable, CustomStringConvertible, CustomDebugStringConvertible {
        let token: String
        public let deviceMid: String
        public let builtinFile: URL
        public let basedOnRevision: String
        public let modelIDs: [String]
        public let appVersion: String
        // Never print the token, even by accident.
        public var description: String { "ZCode.Auth(builtin: \(builtinFile.lastPathComponent), models: \(modelIDs.count))" }
        public var debugDescription: String { description }
    }

    // MARK: Install

    /// The desktop app, in /Applications or ~/Applications.
    public static func appCandidates(home: String = NSHomeDirectory()) -> [String] {
        ["/Applications/ZCode.app/Contents/MacOS/ZCode", "\(home)/Applications/ZCode.app/Contents/MacOS/ZCode"]
    }

    /// `ZCode.app/Contents/MacOS/ZCode` → `ZCode.app/Contents/Resources/glm/zcode.cjs`.
    public static func script(for executable: URL) -> URL {
        executable.deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/glm/zcode.cjs")
    }

    /// The desktop build's version, which the plan gateway expects to see.
    public static func appVersion(for executable: URL) -> String {
        let plist = executable.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Info.plist")
        let info = (try? Data(contentsOf: plist)).flatMap { try? PropertyListSerialization.propertyList(from: $0, format: nil) } as? [String: Any]
        return info?["CFBundleShortVersionString"] as? String ?? "3.17.2"
    }

    // MARK: Shared state

    static func v2(_ home: URL) -> URL { home.appendingPathComponent(".zcode/v2", isDirectory: true) }

    /// Whether the desktop has signed in here. Opens the token to be sure, then drops it.
    public static func isSignedIn(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                  user: String = NSUserName()) -> Bool {
        loadAuth(home: home, user: user, appVersion: "") != nil
    }

    /// Nil when the desktop has never signed in, or its files can't be read.
    public static func loadAuth(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                user: String = NSUserName(), appVersion: String) -> Auth? {
        let dir = v2(home)
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("credentials.json")),
              let record = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let sealed = record["zcodejwttoken"] as? String,
              let token = try? decrypt(sealed, home: home.path, user: user),
              !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let builtin = newestBuiltinFile(v2: dir),
              let basedOn = try? basedOnRevision(builtin) else { return nil }
        let telemetry = (try? Data(contentsOf: dir.appendingPathComponent("telemetry-state.json")))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        let models = builtinModelIDs(builtin, provider: providerID) ?? fallbackModelIDs
        return Auth(token: token, deviceMid: telemetry?["deviceMid"] as? String ?? "", builtinFile: builtin,
                    basedOnRevision: basedOn, modelIDs: models.isEmpty ? fallbackModelIDs : models, appVersion: appVersion)
    }

    /// The models of the plan, read from the builtin provider config (no sign-in needed).
    public static func modelChoices(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [ModelChoice] {
        let ids = newestBuiltinFile(v2: v2(home)).flatMap { builtinModelIDs($0, provider: providerID) } ?? fallbackModelIDs
        return (ids.isEmpty ? fallbackModelIDs : ids).map { ModelChoice(id: $0, title: $0, efforts: efforts, isDefault: $0 == defaultModel) }
    }

    /// The newest builtin provider config the desktop downloaded:
    /// `~/.zcode/v2/runtime/provider/<platform>/<version>/endpoint-*/zcode-builtin.json`.
    static func newestBuiltinFile(v2: URL) -> URL? {
        let root = v2.appendingPathComponent("runtime/provider", isDirectory: true)
        guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return nil }
        var best: (URL, Date)?
        for case let file as URL in walker where file.lastPathComponent == "zcode-builtin.json" {
            let date = (try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            if best == nil || date > best!.1 { best = (file, date) }
        }
        return best?.0
    }

    /// `zcode-builtin:<revision>:<sha256 of the file's full path>`: the registry drops
    /// a pushed account config whose basedOn doesn't match the file it loaded.
    static func basedOnRevision(_ file: URL) throws -> String {
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any]
        let revision = (object?["revision"] as? NSNumber)?.intValue ?? 0
        return "zcode-builtin:\(revision):\(sha256Hex(Data(file.standardizedFileURL.path.utf8)))"
    }

    static func builtinModelIDs(_ file: URL, provider: String) -> [String]? {
        guard let data = try? Data(contentsOf: file),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rules = ((object["config"] as? [String: Any])?["providerConfigRules"] as? [String: Any])?["providerRules"] as? [[String: Any]],
              let rule = rules.first(where: { $0["providerId"] as? String == provider }),
              let ids = (rule["config"] as? [String: Any])?["builtinModelIds"] as? [String] else { return nil }
        return ids.filter { !$0.isEmpty }
    }

    // MARK: Access

    /// Read-only tasks run in ZCode's read-only "plan" mode; writing tasks in "edit",
    /// which applies edits. Anything that still asks for permission (shell commands
    /// above all) is refused: AIROOM answers every permission request with a denial.
    public static func mode(for access: ExecutionAccess) -> String { access == .stagedWrite ? "edit" : "plan" }

    // MARK: Turn messages

    /// The reply of the turn: an assistant message created after the turn's own user
    /// message, with a completion time and some text. This server build never sends
    /// `turn.completed`, so the message list is the completion signal.
    static func turnResult(_ snapshot: [String: Any]) -> (done: Bool, text: String) {
        let messages = snapshot["messages"] as? [[String: Any]] ?? []
        var lastUser = Int64.min
        for message in messages {
            let info = message["info"] as? [String: Any]
            if info?["role"] as? String == "user", let created = ((info?["time"] as? [String: Any])?["created"] as? NSNumber)?.int64Value {
                lastUser = max(lastUser, created)
            }
        }
        var done = false, text = ""
        for message in messages {
            guard let info = message["info"] as? [String: Any], info["role"] as? String == "assistant" else { continue }
            let time = info["time"] as? [String: Any]
            let created = (time?["created"] as? NSNumber)?.int64Value ?? .min
            let body = textParts(message)
            guard created > lastUser, !body.isEmpty else { continue }
            text = body
            done = time?["completed"] is NSNumber
        }
        return (done, text)
    }

    static func textParts(_ message: [String: Any]) -> String {
        (message["parts"] as? [[String: Any]] ?? [])
            .filter { $0["type"] as? String == "text" }
            .compactMap { $0["text"] as? String }
            .joined()
    }

    // MARK: Crypto

    static func sha256Hex(_ data: Data) -> String { SHA256Digest.hex(data) }
}

#if canImport(CryptoKit)
import CryptoKit

enum SHA256Digest {
    static func hex(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
}

extension ZCode {
    /// Values are `enc:v1:<iv>.<tag>.<data>` (base64url), AES-256-GCM under
    /// sha256("zcode-credential-fallback:<platform>:<home>:<user>"), the key ZCode
    /// itself uses when no secret is configured. Plain values pass through.
    static func decrypt(_ value: String, home: String, user: String, platform: String = "darwin") throws -> String {
        guard value.hasPrefix("enc:v1:") else { return value }
        let parts = value.dropFirst("enc:v1:".count).split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, let iv = base64URL(parts[0]), let tag = base64URL(parts[1]),
              let body = base64URL(parts[2]), iv.count == 12, tag.count == 16 else { throw StateProtectionError.invalidCiphertext }
        let key = SymmetricKey(data: SHA256.hash(data: Data("zcode-credential-fallback:\(platform):\(home):\(user)".utf8)))
        let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: iv), ciphertext: body, tag: tag)
        guard let text = String(data: try AES.GCM.open(box, using: key), encoding: .utf8) else { throw StateProtectionError.invalidCiphertext }
        return text
    }

    /// For tests: seal a value the way ZCode does.
    static func encrypt(_ value: String, home: String, user: String, platform: String = "darwin") throws -> String {
        let key = SymmetricKey(data: SHA256.hash(data: Data("zcode-credential-fallback:\(platform):\(home):\(user)".utf8)))
        let box = try AES.GCM.seal(Data(value.utf8), using: key)
        func url(_ data: Data) -> String {
            data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
        }
        return "enc:v1:\(url(Data(box.nonce))).\(url(box.tag)).\(url(box.ciphertext))"
    }

    static func base64URL(_ text: Substring) -> Data? {
        var value = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while value.count % 4 != 0 { value += "=" }
        return Data(base64Encoded: value)
    }
}
#else
enum SHA256Digest { static func hex(_ data: Data) -> String { "" } }
extension ZCode {
    static func decrypt(_ value: String, home: String, user: String, platform: String = "linux") throws -> String {
        throw StateProtectionError.encryptionUnavailable
    }
}
#endif
