import XCTest
@testable import AIROOMCore

final class UpdateCheckTests: XCTestCase {
    func object(tag: String = "v0.2.60", page: String = "https://github.com/GangeWang/AIROOMNative/releases/tag/v0.2.60",
                asset: String? = "https://github.com/GangeWang/AIROOMNative/releases/download/v0.2.60/AIROOMNative-0.2.60.dmg",
                prerelease: Bool = false) -> [String: Any] {
        var assets: [[String: Any]] = [["name": "notes.txt", "browser_download_url": "https://github.com/GangeWang/AIROOMNative/releases/download/v0.2.60/notes.txt"]]
        if let asset { assets.append(["name": "dmg", "browser_download_url": asset]) }
        return ["tag_name": tag, "html_url": page, "draft": false, "prerelease": prerelease,
                "published_at": "2026-11-05T01:00:00Z", "assets": assets]
    }

    func release(tag: String = "v0.2.60", page: String = "https://github.com/GangeWang/AIROOMNative/releases/tag/v0.2.60",
                 asset: String = "https://github.com/GangeWang/AIROOMNative/releases/download/v0.2.60/AIROOMNative-0.2.60.dmg",
                 prerelease: Bool = false) -> Data {
        try! JSONSerialization.data(withJSONObject: object(tag: tag, page: page, asset: asset, prerelease: prerelease))
    }

    private func dmg(_ tag: String, _ version: String) -> String {
        "https://github.com/GangeWang/AIROOMNative/releases/download/\(tag)/AIROOMNative-\(version).dmg"
    }

    func testAWindowsReleaseNeverHidesTheNewestMacRelease() throws {
        // GitHub lists newest first; here a Windows release is newer and higher-numbered.
        let list: [[String: Any]] = [
            object(tag: "windows-v1.0.0", asset: "https://github.com/GangeWang/AIROOMNative/releases/download/windows-v1.0.0/AIROOM-Setup.exe"),
            object(tag: "v0.2.65", asset: dmg("v0.2.65", "0.2.65")),
            object(tag: "v0.2.70", asset: nil),           // no DMG: nothing to install
            object(tag: "v0.2.99", asset: dmg("v0.2.99", "0.2.99"), prerelease: true),
            object(tag: "v0.2.64", asset: dmg("v0.2.64", "0.2.64")),
        ]
        let newest = try XCTUnwrap(UpdateCheck.newest(JSONSerialization.data(withJSONObject: list)))
        XCTAssertEqual(newest.version, "0.2.65")
        XCTAssertEqual(newest.download?.lastPathComponent, "AIROOMNative-0.2.65.dmg")
    }

    func testMacTagsMayCarryAPlatformPrefix() throws {
        let list = [object(tag: "macos-v0.3.1", asset: dmg("macos-v0.3.1", "0.3.1")), object(tag: "v0.2.64", asset: dmg("v0.2.64", "0.2.64"))]
        XCTAssertEqual(UpdateCheck.newest(try JSONSerialization.data(withJSONObject: list))?.version, "0.3.1")
        XCTAssertEqual(UpdateCheck.macVersion(tag: "macos-v0.3.1"), "0.3.1")
        XCTAssertEqual(UpdateCheck.macVersion(tag: "v0.2.64"), "0.2.64")
        XCTAssertNil(UpdateCheck.macVersion(tag: "windows-v1.0.0"))
        XCTAssertNil(UpdateCheck.macVersion(tag: "0.2.64"))
        XCTAssertNil(UpdateCheck.macVersion(tag: "vnext"))
    }

    func testAListWithNoMacReleaseOffersNothing() throws {
        let list = [object(tag: "windows-v1.0.0", asset: nil)]
        XCTAssertNil(UpdateCheck.newest(try JSONSerialization.data(withJSONObject: list)))
        XCTAssertNil(UpdateCheck.newest(Data("{}".utf8)), "a single object is not a list")
    }

    func testReadsTheLatestRelease() throws {
        let info = try XCTUnwrap(UpdateCheck.parse(release()))
        XCTAssertEqual(info.version, "0.2.60")
        XCTAssertEqual(info.page.absoluteString, "https://github.com/GangeWang/AIROOMNative/releases/tag/v0.2.60")
        XCTAssertEqual(info.download?.lastPathComponent, "AIROOMNative-0.2.60.dmg")
        XCTAssertNotNil(info.publishedAt)
    }

    func testOnlyLinksIntoThisRepositoryAreOffered() throws {
        let info = try XCTUnwrap(UpdateCheck.parse(release(page: "https://evil.example/releases/x",
                                                           asset: "https://evil.example/AIROOMNative.dmg")))
        XCTAssertEqual(info.page, UpdateCheck.releasesPage, "a foreign page falls back to the releases page")
        XCTAssertNil(info.download)
        XCTAssertNil(UpdateCheck.parse(release(page: "http://github.com/GangeWang/AIROOMNative/releases/x")).flatMap { $0.page == UpdateCheck.releasesPage ? nil : $0 })
    }

    func testPrereleasesAndOddTagsAreIgnored() {
        XCTAssertNil(UpdateCheck.parse(release(prerelease: true)))
        XCTAssertNil(UpdateCheck.parse(release(tag: "nightly")))
        XCTAssertNil(UpdateCheck.parse(release(tag: "windows-v0.2.60")))
        XCTAssertNil(UpdateCheck.parse(Data("not json".utf8)))
    }

    func testVersionComparison() {
        XCTAssertTrue(UpdateCheck.isNewer("0.2.60", than: "0.2.58"))
        XCTAssertTrue(UpdateCheck.isNewer("0.2.100", than: "0.2.99"), "numbers, not text")
        XCTAssertTrue(UpdateCheck.isNewer("0.3", than: "0.2.99"))
        XCTAssertFalse(UpdateCheck.isNewer("0.2.58", than: "0.2.58"))
        XCTAssertFalse(UpdateCheck.isNewer("0.2.57", than: "0.2.58"))
        XCTAssertFalse(UpdateCheck.isNewer("0.2.58", than: "0.2.58.0"))
        XCTAssertFalse(UpdateCheck.isNewer("abc", than: "0.2.58"))
    }

    func testAutomaticChecksRunAtMostOnceADay() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertTrue(UpdateCheck.isDue(lastChecked: nil, now: now))
        XCTAssertFalse(UpdateCheck.isDue(lastChecked: now.addingTimeInterval(-3_600), now: now))
        XCTAssertTrue(UpdateCheck.isDue(lastChecked: now.addingTimeInterval(-21 * 3_600), now: now))
        XCTAssertTrue(UpdateCheck.isDue(lastChecked: now.addingTimeInterval(3_600), now: now), "a clock moved back still checks")
    }

    func testTheSettingDefaultsToOnAndOldSettingsKeepIt() throws {
        XCTAssertTrue(NativeSettings().checkForUpdates)
        let old = try JSONDecoder().decode(NativeSettings.self, from: Data(#"{"claudePath":"","codexPath":""}"#.utf8))
        XCTAssertTrue(old.checkForUpdates)
        var off = NativeSettings(); off.checkForUpdates = false
        XCTAssertFalse(try JSONDecoder().decode(NativeSettings.self, from: JSONEncoder().encode(off)).checkForUpdates)
    }

    /// Reads the real GitHub record (no data is sent). Skipped unless AIROOM_LIVE_CLI=1.
    func testTheLiveReleaseRecordParses() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["AIROOM_LIVE_CLI"] == "1")
        let info = try await UpdateCheck.fetchLatest()
        XCTAssertNotNil(UpdateCheck.components(info.version))
        XCTAssertNotNil(info.download)
    }
}
