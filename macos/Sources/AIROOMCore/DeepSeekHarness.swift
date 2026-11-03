import Foundation
import CryptoKit

/// DeepSeek Harness (`dsh`) runs one task per call through its headless
/// profile and prints newline-delimited JSON events. AIROOM never reads or
/// passes the API key: dsh loads it from its own credential store.
public enum DeepSeekHarness {
    /// The models the official route advertises; the id goes to DeepSeek as is.
    public static let models = [
        ModelChoice(id: "deepseek-flash", title: "DeepSeek-V4.1-Flash", efforts: [], isDefault: true),
        ModelChoice(id: "deepseek-v4-pro", title: "DeepSeek-V4-Pro", efforts: []),
    ]
    public static let defaultModel = "deepseek-flash"

    /// The launch-time overlay: the headless profile ships with its credential
    /// store off (it expects DEEPSEEK_API_KEY in the environment); turning it on
    /// lets dsh use the key saved in DeepSeek Harness. The model row is replaced whole.
    public static func patch(model: String) -> String {
        let chosen = ModelCatalog.sanitize(model).isEmpty ? defaultModel : ModelCatalog.sanitize(model)
        return """
        - id: credentials
          disabled: false
        - id: agent-default-model
          config:
            provider: deepseek-official
            model: "\(chosen.replacingOccurrences(of: "\"", with: ""))"

        """
    }

    /// DSH's own sandbox, by AIROOM access level. Read-only runs refuse edits
    /// (there is no approval channel), writing runs may edit the working folder.
    public static func permissionMode(_ access: ExecutionAccess) -> String {
        access == .stagedWrite ? "workspace-write" : "read-only"
    }

    public static func environment(base: [String: String], access: ExecutionAccess) -> [String: String] {
        var env = base
        env["DSH_PERMISSION_MODE"] = permissionMode(access)
        env["DSH_TELEMETRY_DISABLED"] = "1"
        return env
    }

    /// Whether DeepSeek Harness has saved credentials. Only the file's presence
    /// is checked; its contents are never read.
    public static func hasSavedCredentials(home: String = NSHomeDirectory()) -> Bool {
        FileManager.default.fileExists(atPath: (home as NSString).appendingPathComponent(".dsh/.credentials.yaml"))
    }

    /// A session dsh can't continue: unknown, or recorded in another folder.
    /// Reported before any session starts, so the model was never reached.
    public static func isUnknownSession(_ stderr: String) -> Bool {
        let text = stderr.lowercased()
        return text.contains("session") && (text.contains("unknown") || text.contains("not found") || text.contains("no such")
                                            || text.contains("was recorded in"))
    }

    /// Names a member's read-only copy of a folder, so it lands at the same path every run.
    public static func copyKey(member: MemberID, folder: URL) -> String {
        let digest = SHA256.hash(data: Data((member.rawValue + "\n" + folder.standardizedFileURL.path).utf8))
        return digest.prefix(8).map { String(format: "%02x", $0) }.joined()
    }
    public static let stableCopyID = UUID(uuidString: "D5D5D5D5-0000-4000-8000-000000000000")!
}
