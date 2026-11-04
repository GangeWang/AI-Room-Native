import Foundation

/// A published AIROOM Native release.
public struct ReleaseInfo: Equatable, Sendable {
    /// "0.2.58" (the tag without its leading "v").
    public var version: String
    /// The release page on GitHub.
    public var page: URL
    /// The DMG attached to the release, when there is one.
    public var download: URL?
    public var publishedAt: Date?
}

/// Tells the owner a newer release exists. It only reads GitHub's public
/// "latest release" record: nothing about the owner is sent, and nothing is
/// downloaded or installed. Opening the release page is left to the owner.
public enum UpdateCheck {
    public static let repository = "GangeWang/AIROOMNative"
    public static let latestAPI = URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!
    public static let releasesPage = URL(string: "https://github.com/\(repository)/releases/latest")!
    /// Automatic checks happen at most this often.
    public static let interval: TimeInterval = 20 * 60 * 60

    /// Reads GitHub's release JSON. Links must point into this repository's
    /// releases; anything else is dropped rather than offered to the owner.
    public static func parse(_ data: Data) -> ReleaseInfo? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["draft"] as? Bool != true, object["prerelease"] as? Bool != true,
              let tag = object["tag_name"] as? String else { return nil }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        guard components(version) != nil else { return nil }
        let page = (object["html_url"] as? String).flatMap(URL.init(string:)).flatMap { isOurs($0) ? $0 : nil } ?? releasesPage
        let download = (object["assets"] as? [[String: Any]] ?? [])
            .compactMap { ($0["browser_download_url"] as? String).flatMap(URL.init(string:)) }
            .first { $0.pathExtension.lowercased() == "dmg" && isOurs($0) }
        let published = (object["published_at"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }
        return ReleaseInfo(version: version, page: page, download: download, publishedAt: published)
    }

    /// "0.2.60" is newer than "0.2.58"; anything unreadable is never newer.
    public static func isNewer(_ remote: String, than local: String) -> Bool {
        guard let a = components(remote), let b = components(local) else { return false }
        for index in 0..<max(a.count, b.count) {
            let x = index < a.count ? a[index] : 0, y = index < b.count ? b[index] : 0
            if x != y { return x > y }
        }
        return false
    }

    public static func isDue(lastChecked: Date?, now: Date = .now) -> Bool {
        guard let lastChecked else { return true }
        return now.timeIntervalSince(lastChecked) >= interval || now < lastChecked
    }

    /// One unauthenticated request to GitHub's public API; no identifying data is attached.
    public static func fetchLatest(timeout: TimeInterval = 10) async throws -> ReleaseInfo {
        var request = URLRequest(url: latestAPI, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("AIROOM-Native", forHTTPHeaderField: "User-Agent")
        let session = URLSession(configuration: .ephemeral)
        defer { session.finishTasksAndInvalidate() }
        let (data, response) = try await session.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200, let release = parse(data) else {
            throw URLError(.badServerResponse)
        }
        return release
    }

    static func components(_ version: String) -> [Int]? {
        let parts = version.split(separator: ".").map { Int($0) }
        guard !parts.isEmpty, parts.count <= 4, parts.allSatisfy({ $0 != nil }) else { return nil }
        return parts.compactMap { $0 }
    }

    static func isOurs(_ url: URL) -> Bool {
        url.scheme == "https" && url.host == "github.com" && url.path.hasPrefix("/\(repository)/releases/")
    }
}
