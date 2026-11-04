import XCTest
@testable import AIROOMCore

final class UpdateCheckTests: XCTestCase {
    func release(tag: String = "v0.2.60", page: String = "https://github.com/GangeWang/AIROOMNative/releases/tag/v0.2.60",
                 asset: String = "https://github.com/GangeWang/AIROOMNative/releases/download/v0.2.60/AIROOMNative-0.2.60.dmg",
                 prerelease: Bool = false) -> Data {
        let object: [String: Any] = [
            "tag_name": tag, "html_url": page, "draft": false, "prerelease": prerelease,
            "published_at": "2026-11-05T01:00:00Z",
            "assets": [["name": "notes.txt", "browser_download_url": "https://github.com/GangeWang/AIROOMNative/releases/download/v0.2.60/notes.txt"],
                       ["name": "dmg", "browser_download_url": asset]],
        ]
        return try! JSONSerialization.data(withJSONObject: object)
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
