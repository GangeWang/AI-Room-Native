import Foundation

/// The Antigravity CLI (`agy`) runs one turn per call and prints newline-delimited
/// JSON events. It signs in through its own keyring entry, shared with the
/// Antigravity app; AIROOM never reads or passes those credentials.
public enum Antigravity {
    /// Settings for this run only, so a looser choice saved for `agy` itself can't
    /// widen what a member may do. Actions that need approval are refused: headless
    /// runs have nobody to ask.
    static let runSettings = #"{"toolPermission":"request-review","allowNonWorkspaceAccess":false}"#

    /// Plan mode can't change files, and the terminal sandbox holds in every mode.
    /// The prompt arrives on stdin, so it never shows in the process list.
    public static func arguments(access: ExecutionAccess, options: CLIRunOptions) -> [String] {
        var agy = ["--input-format", "stream-json", "--output-format", "stream-json",
                   "--sandbox", "--disable-slash-commands", "--settings", runSettings,
                   "--mode", access == .stagedWrite ? "accept-edits" : "plan"]
        let model = ModelCatalog.sanitize(options.model)
        if !model.isEmpty { agy += ["--model", model] }
        if let resume = options.session?.resumeID { agy += ["--conversation", resume] }
        return agy
    }

    /// One stream-json user message, on one line.
    public static func userMessage(prompt: String) throws -> Data {
        var line = try JSONSerialization.data(withJSONObject: ["event": "user", "message": ["content": prompt]])
        line.append(0x0A)
        return line
    }

    /// `agy models` prints "slug<TAB>Title" lines, after a progress line.
    public static func parseModels(_ output: String) -> [ModelChoice] {
        output.split(whereSeparator: \.isNewline).compactMap { line in
            let parts = line.split(separator: "\t", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 2, !parts[0].isEmpty, !parts[0].contains(" ") else { return nil }
            return ModelChoice(id: parts[0], title: parts[1], efforts: [])
        }
    }

    /// `agy` has no status command: it lists models only once signed in.
    public static func status(modelsOutput: Data) -> OfficialCLIStatus {
        parseModels(String(decoding: modelsOutput, as: UTF8.self)).isEmpty ? .notLoggedIn : .authenticated
    }

    /// A conversation id `agy` doesn't know starts a new conversation with only a
    /// warning, so a member would carry on without their context.
    public static func lostConversation(resumeID: String?, reported: String?) -> Bool {
        guard let resumeID else { return false }
        return reported != resumeID
    }
}
