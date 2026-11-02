import Foundation

public struct SavedState: Codable, Equatable, Sendable {
    public var settings: NativeSettings
    public var conversations: [Conversation]
    public var members: [MemberCard]
    public var rooms: [TeamRoom]
    public var projects: [ProjectRecord]
    public var roundtables: [StoredRoundtable]
    public var schedules: [ScheduledActivity]
    public var autoChat: AutoChatSettings
    public var teamWorkflows: [TeamProjectWorkflow]
    public var liveShow: LiveShowSettings
    public init(settings: NativeSettings = NativeSettings(), conversations: [Conversation] = [],
                members: [MemberCard] = MemberCard.defaults, rooms: [TeamRoom] = [TeamRoom()],
                projects: [ProjectRecord] = [], roundtables: [StoredRoundtable] = [],
                schedules: [ScheduledActivity] = [], autoChat: AutoChatSettings = AutoChatSettings(),
                teamWorkflows: [TeamProjectWorkflow] = [], liveShow: LiveShowSettings = LiveShowSettings()) {
        self.settings = settings
        self.conversations = conversations
        self.members = members
        self.rooms = rooms
        self.projects = projects
        self.roundtables = roundtables
        self.schedules = schedules
        self.autoChat = autoChat
        self.teamWorkflows = teamWorkflows
        self.liveShow = liveShow
    }
    private enum CodingKeys: String, CodingKey { case settings, conversations, members, rooms, projects, roundtables, schedules, autoChat, teamWorkflows, liveShow }
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        settings = try container.decodeIfPresent(NativeSettings.self, forKey: .settings) ?? NativeSettings()
        conversations = try container.decodeIfPresent([Conversation].self, forKey: .conversations) ?? []
        members = try container.decodeIfPresent([MemberCard].self, forKey: .members) ?? MemberCard.defaults
        rooms = try container.decodeIfPresent([TeamRoom].self, forKey: .rooms) ?? [TeamRoom()]
        projects = try container.decodeIfPresent([ProjectRecord].self, forKey: .projects) ?? []
        roundtables = try container.decodeIfPresent([StoredRoundtable].self, forKey: .roundtables) ?? []
        schedules = try container.decodeIfPresent([ScheduledActivity].self, forKey: .schedules) ?? []
        autoChat = try container.decodeIfPresent(AutoChatSettings.self, forKey: .autoChat) ?? AutoChatSettings()
        teamWorkflows = try container.decodeIfPresent([TeamProjectWorkflow].self, forKey: .teamWorkflows) ?? []
        liveShow = try container.decodeIfPresent(LiveShowSettings.self, forKey: .liveShow) ?? LiveShowSettings()
    }
}

public final class LocalStateStore {
    public let root: URL
    private let protector: (any StateProtector)?
    private let magic = Data("AIROOM1\n".utf8)
    private var protectedState: URL { root.appendingPathComponent("state.aesgcm") }
    private var legacyState: URL { root.appendingPathComponent("state.json") }

    public init(root: URL? = nil, protector customProtector: (any StateProtector)? = nil) throws {
        if let root { self.root = root }
        else {
            let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            self.root = base.appendingPathComponent("AIROOMNative", isDirectory: true)
        }
        #if os(macOS)
        self.protector = customProtector ?? KeychainStateProtector()
        #else
        self.protector = customProtector // Foundation tests only; shipping app is macOS-only.
        #endif
        try FileManager.default.createDirectory(at: self.root, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: self.root.path)
    }

    private func decode(_ data: Data) throws -> SavedState {
        if data.starts(with: magic) {
            guard let protector else { throw StateProtectionError.encryptionUnavailable }
            return try JSONDecoder().decode(SavedState.self, from: protector.open(Data(data.dropFirst(magic.count))))
        }
        guard protector == nil else { throw StateProtectionError.invalidCiphertext }
        return try JSONDecoder().decode(SavedState.self, from: data)
    }

    private func encode(_ state: SavedState) throws -> Data {
        let plaintext = try JSONEncoder().encode(state)
        guard let protector else { return plaintext }
        return magic + (try protector.seal(plaintext))
    }

    private func protectedWrite(_ data: Data, to file: URL) throws {
        try data.write(to: file, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }

    public func load() throws -> SavedState {
        let fm = FileManager.default
        if fm.fileExists(atPath: protectedState.path) {
            return try decode(Data(contentsOf: protectedState))
        }
        if fm.fileExists(atPath: legacyState.path) {
            let value = try JSONDecoder().decode(SavedState.self, from: Data(contentsOf: legacyState))
            if protector != nil {
                try save(value) // Fail closed: never erase legacy data before verifying encrypted write.
                _ = try decode(Data(contentsOf: protectedState))
                try fm.removeItem(at: legacyState)
            }
            return value
        }
        return SavedState()
    }

    public func save(_ state: SavedState) throws {
        try protectedWrite(try encode(state), to: protector == nil ? legacyState : protectedState)
    }

    /// Backups contain conversations and metadata, never official CLI credentials.
    /// On macOS they are encrypted with the device-bound Keychain key and cannot be
    /// restored on another Mac without an explicit export/rewrapping design.
    public func backup() throws -> URL {
        let target = root.appendingPathComponent("backups", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: target.path)
        let date = ISO8601DateFormatter().string(from: .now).replacingOccurrences(of: ":", with: "-")
        let file = target.appendingPathComponent("AIROOM-backup-\(date)\(protector == nil ? ".json" : ".airoombackup")")
        try protectedWrite(try encode(load()), to: file)
        return file
    }

    public func restore(from file: URL) throws -> SavedState {
        let data = try Data(contentsOf: file)
        guard data.count <= 50 * 1024 * 1024 else { throw WorkspaceError.exceedsLimit(file.lastPathComponent) }
        let restored = try decode(data)
        _ = try backup()
        try save(restored)
        return restored
    }
}
