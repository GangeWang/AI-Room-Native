import SwiftUI
import AppKit
import AIROOMCore

/// Tells the owner a newer release is on GitHub. Nothing is downloaded or
/// installed: the reminder only opens the release page.
extension WorkbenchModel {
    private static let lastCheckKey = "AIROOM.lastUpdateCheck"
    private static let skippedKey = "AIROOM.skippedUpdate"

    private func tr(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, archive.settings.language) }

    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    /// Automatic checks run at most once a day while the setting is on; a manual
    /// check always runs and reports its result.
    /// `announce` also pops the result up (used by the menu command).
    func checkForUpdates(manual: Bool, announce: Bool = false, now: Date = .now) {
        let defaults = UserDefaults.standard
        if !manual {
            guard stateLoaded, archive.settings.checkForUpdates,
                  UpdateCheck.isDue(lastChecked: defaults.object(forKey: Self.lastCheckKey) as? Date, now: now) else { return }
        }
        guard !updateChecking else { return }
        updateChecking = true
        if manual { updateStatus = tr("正在檢查……", "Checking…") }
        Task { [weak self] in
            let result = await Task.detached { try? await UpdateCheck.fetchLatest() }.value
            guard let self else { return }
            self.updateChecking = false
            guard let release = result else {
                if manual { self.updateStatus = self.tr("無法連到 GitHub，請稍後再試。", "Couldn't reach GitHub. Try again later.") }
                if announce { self.statusMessage = self.updateStatus }
                return
            }
            defaults.set(now, forKey: Self.lastCheckKey)
            let newer = UpdateCheck.isNewer(release.version, than: self.currentVersion)
            // A skipped version stays quiet on automatic checks, not when the owner asks.
            let skipped = defaults.string(forKey: Self.skippedKey) == release.version
            self.availableUpdate = newer && (manual || !skipped) ? release : nil
            if manual {
                self.updateStatus = newer
                    ? self.tr("有新版本 \(release.version)。", "Version \(release.version) is available.")
                    : self.tr("已是最新版本（\(self.currentVersion)）。", "You're up to date (\(self.currentVersion)).")
            }
            // A newer version shows as the banner; otherwise say so.
            if announce && !newer { self.statusMessage = self.updateStatus }
        }
    }

    /// Opens the release page in the browser; the owner downloads and installs it.
    func openUpdatePage() {
        NSWorkspace.shared.open(availableUpdate?.page ?? UpdateCheck.releasesPage)
    }

    /// No reminder for this version again (a later one still shows).
    func skipUpdate() {
        if let version = availableUpdate?.version { UserDefaults.standard.set(version, forKey: Self.skippedKey) }
        availableUpdate = nil
    }

    func dismissUpdate() { availableUpdate = nil }
}

/// The strip above every page when a newer release exists.
struct UpdateBanner: View {
    @EnvironmentObject private var model: WorkbenchModel
    let release: ReleaseInfo
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(StudioTheme.orange)
            Text(t("有新版本 \(release.version)（目前 \(model.currentVersion)）。下載 DMG 後拖進「應用程式」取代即可，資料會保留。",
                   "Version \(release.version) is available (you have \(model.currentVersion)). Download the DMG and replace the app; your data stays."))
                .font(.callout).lineLimit(2)
            Spacer(minLength: 8)
            Button(t("前往下載", "Download")) { model.openUpdatePage() }.buttonStyle(.borderedProminent)
            Button(t("略過這版", "Skip this version")) { model.skipUpdate() }
            Button { model.dismissUpdate() } label: { Image(systemName: "xmark") }
                .buttonStyle(.borderless).help(t("稍後再提醒", "Remind me later"))
        }
        .padding(.horizontal, 16).padding(.vertical, 9)
        .background(StudioTheme.orange.opacity(0.12))
    }
}

/// Settings → 工作台: the update reminder's switch and a manual check.
struct UpdateSettingsPanel: View {
    @EnvironmentObject private var model: WorkbenchModel
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        StudioPanel {
            VStack(alignment: .leading, spacing: 10) {
                Label(t("版本與更新", "Version & Updates"), systemImage: "arrow.down.circle").font(.headline)
                Text(t("目前版本 \(model.currentVersion)", "Current version \(model.currentVersion)")).font(.callout)
                Toggle(t("每天檢查一次 GitHub 上有沒有新版本", "Check GitHub for a new version once a day"),
                       isOn: Binding(get: { model.archive.settings.checkForUpdates },
                                     set: { value in model.updateSettings { $0.checkForUpdates = value } }))
                    .toggleStyle(.switch)
                HStack(spacing: 10) {
                    Button(t("立即檢查", "Check Now")) { model.checkForUpdates(manual: true) }
                        .disabled(model.updateChecking)
                    if model.updateChecking { ProgressView().controlSize(.small) }
                    if let status = model.updateStatus { Text(status).font(.caption).foregroundStyle(.secondary) }
                    if model.availableUpdate != nil {
                        Button(t("前往下載", "Download")) { model.openUpdatePage() }
                    }
                }
                Text(t("只讀取 GitHub 公開的最新版本資訊，不會傳送你的任何資料，也不會自動下載或安裝。",
                       "Only reads GitHub's public latest-release record. Nothing about you is sent, and nothing is downloaded or installed automatically."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
