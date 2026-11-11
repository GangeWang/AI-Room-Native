#if os(macOS)
import XCTest
@testable import AIROOMCore

/// Stands in for one Mac's Keychain key; another byte is another Mac.
private struct XORProtector: StateProtector {
    var key: UInt8
    func seal(_ plaintext: Data) throws -> Data { Data([key]) + Data(plaintext.map { $0 ^ key }) }
    func open(_ ciphertext: Data) throws -> Data {
        guard ciphertext.first == key else { throw StateProtectionError.invalidCiphertext }
        return Data(ciphertext.dropFirst().map { $0 ^ key })
    }
}

final class PortableExportTests: XCTestCase {
    private let rounds: UInt32 = 10_000
    private let password = "correct horse"

    private func store(mac key: UInt8) throws -> LocalStateStore {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("airoom-export-\(UUID())")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        return try LocalStateStore(root: dir, protector: XORProtector(key: key))
    }

    private lazy var fixture: SavedState = {
        var state = SavedState()
        state.settings.userName = "小明"
        state.members[0].name = "克勞迪婭"
        state.members[0].memories = [MemoryEntry(text: "喜歡烏龍茶", source: .manual)]
        state.conversations = [Conversation(agent: .claude, title: "t", messages: [ChatMessage(role: .user, body: "hi")])]
        return state
    }()
    private func sample() -> SavedState { fixture }

    func testExportMovesStateAndAttachmentsToAnotherMac() throws {
        let old = try store(mac: 0x11)
        try old.save(sample())
        let picture = UUID(), avatar = UUID()
        try old.storeAttachment(Data("png bytes".utf8), id: picture)
        try old.storeAttachment(Data("avatar".utf8), id: avatar)
        let file = old.root.appendingPathComponent("move.airoomexport")
        try old.exportPortable(password: password, rounds: rounds).write(to: file)

        let new = try store(mac: 0x22)
        try new.save(SavedState())
        let opened = try new.openPortable(file, password: password)
        XCTAssertEqual(opened.state, sample())
        XCTAssertEqual(opened.attachments.count, 2)

        let restored = try new.importPortable(opened)
        XCTAssertEqual(restored, sample())
        XCTAssertEqual(try new.load(), sample())
        XCTAssertEqual(try new.loadAttachment(id: picture), Data("png bytes".utf8))
        XCTAssertEqual(try new.loadAttachment(id: avatar), Data("avatar".utf8))
        // The previous data on the new Mac was kept as a backup first.
        XCTAssertEqual(new.backups().filter(\.beforeRestore).count, 1)
    }

    func testAnImportNeverBringsProgramPathsOrTheDefaultWorkFolder() throws {
        // A file from someone else: every program path points at their executable.
        var hostile = sample()
        for program in Executor.allCases { hostile.settings.setPath("/tmp/evil/\(program.executableName)", for: program) }
        hostile.settings.workspacePath = "/Users/victim"
        hostile.settings.userName = "小明"
        hostile.conversations[0].workFolder = "/Users/victim/project"
        XCTAssertEqual(hostile.machineSettings.count, Executor.allCases.count + 1)
        let data = try PortableExportCodec.seal(PortableExport(state: hostile, attachments: [:]), password: password, rounds: rounds)

        let mac = try store(mac: 0x33)
        var mine = SavedState()
        mine.settings.claudePath = "/opt/mine/claude"
        try mac.save(mine)
        let restored = try mac.importPortable(PortableExportCodec.open(data, password: password))
        for program in Executor.allCases { XCTAssertEqual(restored.settings.configuredPath(for: program), "", program.rawValue) }
        XCTAssertEqual(restored.settings.workspacePath, "")
        XCTAssertTrue(restored.machineSettings.isEmpty)
        XCTAssertEqual(try mac.load(), restored, "what is saved is the cleaned state")
        // Everything else comes over, and topic folders are listed for the owner to see.
        XCTAssertEqual(restored.settings.userName, "小明")
        XCTAssertEqual(restored.referencedFolders, ["/Users/victim/project"])
    }

    func testFileHoldsNoPlaintext() throws {
        let old = try store(mac: 0x11)
        try old.save(sample())
        let data = try old.exportPortable(password: password, rounds: rounds)
        XCTAssertTrue(PortableExportCodec.looksLikeExport(data))
        XCTAssertNil(data.range(of: Data("喜歡烏龍茶".utf8)))
        XCTAssertNil(data.range(of: Data(password.utf8)))
    }

    func testWrongPasswordAndTamperingFailWithoutChangingAnything() throws {
        let export = PortableExport(state: sample(), attachments: [:])
        let data = try PortableExportCodec.seal(export, password: password, rounds: rounds)
        XCTAssertThrowsError(try PortableExportCodec.open(data, password: "wrong password")) {
            XCTAssertEqual($0 as? PortableExportError, .wrongPasswordOrDamaged)
        }
        var flipped = data; flipped[flipped.count - 5] ^= 0x01
        XCTAssertThrowsError(try PortableExportCodec.open(flipped, password: password)) {
            XCTAssertEqual($0 as? PortableExportError, .wrongPasswordOrDamaged)
        }
        // The salt is authenticated too.
        var salted = data; salted[PortableExportCodec.magic.count] ^= 0x01
        XCTAssertThrowsError(try PortableExportCodec.open(salted, password: password))
        XCTAssertThrowsError(try PortableExportCodec.open(Data("not an export".utf8), password: password)) {
            XCTAssertEqual($0 as? PortableExportError, .notAnExport)
        }
        XCTAssertEqual(try PortableExportCodec.open(data, password: password), export)
    }

    func testRoundsOutsideTheAllowedRangeAreRefused() throws {
        let data = try PortableExportCodec.seal(PortableExport(state: sample(), attachments: [:]), password: password, rounds: rounds)
        var huge = data
        let at = PortableExportCodec.magic.count + PortableExportCodec.saltLength
        huge.replaceSubrange(at ..< at + 4, with: [0xFF, 0xFF, 0xFF, 0xFF])
        XCTAssertThrowsError(try PortableExportCodec.open(huge, password: password)) {
            XCTAssertEqual($0 as? PortableExportError, .unsupportedVersion)
        }
    }

    func testShortPasswordsAreRefused() {
        XCTAssertThrowsError(try PortableExportCodec.seal(PortableExport(state: sample(), attachments: [:]), password: "1234567", rounds: rounds)) {
            XCTAssertEqual($0 as? PortableExportError, .passwordTooShort(8))
        }
    }

    func testSamePasswordInAnotherUnicodeFormOpens() throws {
        let composed = "café-密碼-1234"          // é as one code point
        let decomposed = "cafe\u{301}-密碼-1234" // e + combining accent
        let data = try PortableExportCodec.seal(PortableExport(state: sample(), attachments: [:]), password: composed, rounds: rounds)
        XCTAssertEqual(try PortableExportCodec.open(data, password: decomposed).state, sample())
    }

    func testDefaultRoundsAreStrong() {
        XCTAssertGreaterThanOrEqual(PortableExportCodec.defaultRounds, 600_000)
    }
}
#endif
