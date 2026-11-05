import Foundation

/// Everything needed to move AIROOM to another Mac: the saved state plus every
/// attachment (pictures, avatars, files), decrypted from this Mac's Keychain key.
/// CLI sign-ins and API keys are never part of it.
public struct PortableExport: Codable, Equatable, Sendable {
    public var version: Int
    public var createdAt: Date
    public var state: SavedState
    /// Attachment bytes by attachment id (UUID string).
    public var attachments: [String: Data]

    public init(state: SavedState, attachments: [String: Data], createdAt: Date = .now) {
        version = 1
        self.createdAt = createdAt
        self.state = state
        self.attachments = attachments
    }
}

public enum PortableExportError: Error, LocalizedError, Equatable {
    case passwordTooShort(Int)
    case notAnExport
    case unsupportedVersion
    case wrongPasswordOrDamaged
    case tooLarge
    case cryptoUnavailable

    public var errorDescription: String? {
        switch self {
        case .passwordTooShort(let n): return "The password needs at least \(n) characters."
        case .notAnExport: return "This file is not an AIROOM export."
        case .unsupportedVersion: return "This export was made by a newer AIROOM. Update AIROOM first."
        case .wrongPasswordOrDamaged: return "Wrong password, or the file is damaged or was altered."
        case .tooLarge: return "The export is too large to open."
        case .cryptoUnavailable: return "Password encryption is only available on macOS."
        }
    }
}

/// File layout: "AIROOMX1\n" · salt (16 bytes) · PBKDF2 rounds (UInt32, big-endian) · AES-256-GCM box.
/// The key comes from the password with PBKDF2-HMAC-SHA256; the header is authenticated
/// with the payload, so changing the salt or rounds also fails to open.
public enum PortableExportCodec {
    public static let fileExtension = "airoomexport"
    public static let minimumPasswordLength = 8
    public static let defaultRounds: UInt32 = 600_000
    static let roundsRange: ClosedRange<UInt32> = 10_000...10_000_000
    static let magic = Data("AIROOMX1\n".utf8)
    static let saltLength = 16
    static let maximumBytes = 1024 * 1024 * 1024

    static var headerLength: Int { magic.count + saltLength + 4 }

    /// Whether the data starts like an export (no password needed).
    public static func looksLikeExport(_ data: Data) -> Bool { data.starts(with: magic) }

    public static func seal(_ export: PortableExport, password: String, rounds: UInt32 = defaultRounds) throws -> Data {
        guard password.count >= minimumPasswordLength else { throw PortableExportError.passwordTooShort(minimumPasswordLength) }
        guard roundsRange.contains(rounds) else { throw PortableExportError.unsupportedVersion }
        // Default date coding, same as the state file, so timestamps come back exactly.
        let plaintext = try JSONEncoder().encode(export)
        let salt = try randomBytes(saltLength)
        var header = magic + salt
        withUnsafeBytes(of: rounds.bigEndian) { header.append(contentsOf: $0) }
        return header + (try encrypt(plaintext, password: password, salt: salt, rounds: rounds, header: header))
    }

    public static func open(_ data: Data, password: String) throws -> PortableExport {
        guard data.count <= maximumBytes else { throw PortableExportError.tooLarge }
        guard looksLikeExport(data), data.count > headerLength else { throw PortableExportError.notAnExport }
        let bytes = [UInt8](data.prefix(headerLength))
        let salt = Data(bytes[magic.count ..< magic.count + saltLength])
        let rounds = bytes[(magic.count + saltLength)...].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
        guard roundsRange.contains(rounds) else { throw PortableExportError.unsupportedVersion }
        let plaintext = try decrypt(Data(data.dropFirst(headerLength)), password: password, salt: salt, rounds: rounds,
                                    header: Data(data.prefix(headerLength)))
        let export: PortableExport
        do { export = try JSONDecoder().decode(PortableExport.self, from: plaintext) }
        catch { throw PortableExportError.wrongPasswordOrDamaged }
        guard export.version == 1 else { throw PortableExportError.unsupportedVersion }
        return export
    }
}

#if os(macOS)
import CryptoKit
import CommonCrypto
import Security

extension PortableExportCodec {
    static func randomBytes(_ count: Int) throws -> Data {
        var bytes = Data(count: count)
        let status = bytes.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        guard status == errSecSuccess else { throw StateProtectionError.keychain(status) }
        return bytes
    }

    static func key(password: String, salt: Data, rounds: UInt32) throws -> SymmetricKey {
        // Same characters typed on another Mac may arrive in another Unicode form.
        let secret = Data(password.precomposedStringWithCanonicalMapping.utf8)
        var derived = [UInt8](repeating: 0, count: 32)
        let status = secret.withUnsafeBytes { pw in
            salt.withUnsafeBytes { s in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                     pw.baseAddress?.assumingMemoryBound(to: Int8.self), secret.count,
                                     s.baseAddress?.assumingMemoryBound(to: UInt8.self), salt.count,
                                     CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), rounds, &derived, derived.count)
            }
        }
        guard status == kCCSuccess else { throw PortableExportError.cryptoUnavailable }
        defer { derived.withUnsafeMutableBytes { _ = memset_s($0.baseAddress, $0.count, 0, $0.count) } }
        return SymmetricKey(data: derived)
    }

    static func encrypt(_ plaintext: Data, password: String, salt: Data, rounds: UInt32, header: Data) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: key(password: password, salt: salt, rounds: rounds), authenticating: header)
        guard let combined = box.combined else { throw StateProtectionError.invalidCiphertext }
        return combined
    }

    static func decrypt(_ ciphertext: Data, password: String, salt: Data, rounds: UInt32, header: Data) throws -> Data {
        do {
            let box = try AES.GCM.SealedBox(combined: ciphertext)
            return try AES.GCM.open(box, using: key(password: password, salt: salt, rounds: rounds), authenticating: header)
        } catch { throw PortableExportError.wrongPasswordOrDamaged }
    }
}
#else
extension PortableExportCodec {
    static func randomBytes(_ count: Int) throws -> Data { throw PortableExportError.cryptoUnavailable }
    static func encrypt(_ plaintext: Data, password: String, salt: Data, rounds: UInt32, header: Data) throws -> Data {
        throw PortableExportError.cryptoUnavailable
    }
    static func decrypt(_ ciphertext: Data, password: String, salt: Data, rounds: UInt32, header: Data) throws -> Data {
        throw PortableExportError.cryptoUnavailable
    }
}
#endif
