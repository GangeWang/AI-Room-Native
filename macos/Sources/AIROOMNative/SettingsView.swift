import SwiftUI
import AppKit
import AIROOMCore

private enum StudioSettingsPage: String, CaseIterable, Identifiable {
    case members, storage, about
    var id: String { rawValue }
}

struct SettingsView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @State private var page: StudioSettingsPage = .members
    @AppStorage("airoom.ui.appearance") private var appearance = "system"
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private func cliPath(for agent: Executor) -> Binding<String> {
        Binding(get: { model.archive.settings.configuredPath(for: agent) },
                set: { value in
                    model.updateSettings { settings in
                        settings.setPath(value, for: agent)
                    }
                })
    }
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                StudioPageHeading(eyebrow: "SETTINGS",
                    title: t("設定與連接", "Settings & Connections"),
                    subtitle: t("官方執行端、本機資料與介面偏好。", "Official CLI connections, local data and display preferences."))
                StudioPanel {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            Label(t("成員怎麼稱呼你", "What members call you"), systemImage: "person.text.rectangle")
                                .font(.subheadline.weight(.medium))
                            Spacer()
                            TextField(t("例如：老大、小明（留空則稱「使用者」）", "e.g. Alex (blank = \"the user\")"), text: Binding(
                                get: { model.archive.settings.userName },
                                set: { value in model.updateSettings { $0.userName = String(value.prefix(40)) } }
                            ))
                            .textFieldStyle(.roundedBorder).frame(width: 265)
                        }
                        // A member card can set its own form of address.
                        HStack(spacing: 16) {
                            ForEach(model.archive.members) { member in
                                HStack(spacing: 6) {
                                    StudioAvatar(agent: member.agent, size: 22)
                                    Text("\(member.name)：「\(member.address(ownerName: model.archive.settings.ownerName))」").font(.caption)
                                }
                            }
                            Spacer()
                            Text(t("單獨的稱呼在人物卡設定", "Set a member's own address on their card")).font(.caption2).foregroundStyle(.secondary)
                        }
                        .padding(.top, 6)
                    }
                }
                StudioPanel {
                    HStack {
                        Label(t("介面語言", "Interface Language"), systemImage: "globe")
                            .font(.subheadline.weight(.medium))
                        Spacer()
                        Picker(t("介面語言", "Language"), selection: Binding(
                            get: { model.archive.settings.language },
                            set: { value in model.updateSettings { $0.language = value } }
                        )) {
                            ForEach(InterfaceLanguage.allCases) { language in
                                Text(language.title).tag(language)
                            }
                        }
                        .labelsHidden().pickerStyle(.segmented).frame(width: 265)
                    }
                }
                Picker(t("設定頁面", "Settings Page"), selection: $page) {
                    Label(t("連接與版本", "Connections"), systemImage: "link").tag(StudioSettingsPage.members)
                    Label(t("資料與備份", "Data & Backup"), systemImage: "externaldrive").tag(StudioSettingsPage.storage)
                    Label(t("工作台", "Workbench"), systemImage: "info.circle").tag(StudioSettingsPage.about)
                }
                .pickerStyle(.segmented)

                switch page {
                case .members: connections
                case .storage: storage
                case .about: about
                }
            }
            .padding(26)
            .frame(maxWidth: 990)
            .frame(maxWidth: .infinity)
        }
    }

    private var connections: some View {
        VStack(spacing: 15) {
            ForEach(Executor.allCases) { agent in
                StudioPanel {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack(spacing: 12) {
                            Image(systemName: agent.symbol)
                                .font(.system(size: 19, weight: .semibold))
                                .frame(width: 41, height: 41)
                                .background(.quaternary, in: Circle())
                            VStack(alignment: .leading, spacing: 3) {
                                Text(agent.rawValue).font(.headline)
                                Text(agent == .codex
                                     ? t("官方 App Server（自動使用 ChatGPT App 內附 Codex）", "Official App Server (uses bundled ChatGPT Codex)")
                                     : agent == .deepseek
                                     ? t("自動使用 DeepSeek Harness.app 內附的 dsh（headless）", "Uses the dsh bundled with DeepSeek Harness.app (headless)")
                                     : agent == .antigravity
                                     ? t("Google 官方 Antigravity CLI（agy，headless）", "Google's official Antigravity CLI (agy, headless)")
                                     : agent == .zcode
                                     ? t("使用 ZCode 桌面版內附的 app-server（GLM）", "Uses the app-server inside the ZCode desktop app (GLM)")
                                     : t("自動使用 Claude Desktop 內附 Claude Code", "Auto-detect Claude Code bundled with Claude Desktop"))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text("ARM64").font(.caption2.monospaced()).foregroundStyle(.secondary)
                        }
                        HStack {
                            TextField(t("自動尋找 \(agent.executableName)", "Automatically find \(agent.executableName)"), text: cliPath(for: agent))
                                .textFieldStyle(.roundedBorder)
                            Button(t("選取…", "Browse…")) { chooseExecutable(for: agent) }
                        }
                        HStack(spacing: 11) {
                            Button { model.openVendorDesktop(executor: agent) } label: {
                                Label(t("開啟官方桌面 App", "Open Official Desktop App"), systemImage: "arrow.up.forward.app")
                            }
                            Button { model.checkVendorCLIAccount(for: agent) } label: {
                                Label(t("檢查 CLI 登入", "Check CLI Login"), systemImage: "checkmark.shield")
                            }
                            .disabled(model.officialCLIStatuses[agent] == .checking)
                            if model.officialCLIStatuses[agent] == .checking { ProgressView().controlSize(.small) }
                        }
                        if let status = model.officialCLIStatuses[agent], status != .checking {
                            Label(cliLoginDescription(status, for: agent), systemImage: status == .authenticated ? "checkmark.circle" : "info.circle")
                                .font(.caption).foregroundStyle(status == .authenticated ? Color.green : Color.secondary)
                        }
                        Text(agent == .deepseek
                             ? t("DeepSeek 留空會使用 DeepSeek Harness.app 內附的 dsh。API 金鑰請在 DeepSeek Harness 的設定裡填寫；AIROOM 不讀取也不保存金鑰，只檢查 DeepSeek Harness 是否已存過金鑰。", "Leave empty to use the dsh inside DeepSeek Harness.app. Set the API key in DeepSeek Harness; AIROOM never reads or stores it and only checks that one is saved.")
                             : agent == .zcode
                             ? t("留空會使用 /Applications/ZCode.app。請先在 ZCode 桌面版登入；執行時 AIROOM 會在記憶體裡解開桌面版存的登入權杖，只交給 ZCode 自己的程式，不儲存也不顯示。成員一律不能執行終端機指令。", "Leave empty to use /Applications/ZCode.app. Sign in in the ZCode app first; for each run AIROOM opens the app's saved sign-in in memory and hands it only to ZCode's own process, never storing or showing it. Members can never run terminal commands.")
                             : agent == .antigravity
                             ? t("留空會找 ~/.local/bin/agy。agy 要另外安裝，和 Antigravity App 共用登入；AIROOM 不讀取登入資料。登入檢查是看它能不能列出模型。", "Leave empty to use ~/.local/bin/agy. agy is installed separately and shares its sign-in with the Antigravity app; AIROOM never reads it. The login check asks agy to list models.")
                             : agent == .codex
                             ? t("Codex 留空會先找 ChatGPT.app 內附的 codex-cli，再找 Codex.app 或 PATH。使用 App Server JSON-RPC；不會讀取 Token，也不會自動匯入桌面對話。", "Leave Codex path empty to prefer bundled ChatGPT.app codex-cli, then Codex.app or PATH. Uses App Server JSON-RPC; no token scraping or desktop transcript import.")
                             : t("Claude 路徑留空會先找 Claude Desktop Code 分頁安裝的最新 Claude Code，再找 PATH。檢查 CLI 登入不會讀取 Token，也不代表桌面 App 對話已同步。", "Leave the Claude path empty to use the latest Claude Code installed by Claude Desktop’s Code tab, falling back to PATH. Login checks never read tokens or sync desktop conversations."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            StudioNotice(message: t("請只選擇你信任的官方 CLI。自訂路徑可能執行任意程式碼，外部 CLI 不受完整 OS 沙箱限制。", "Select trusted official CLIs only. Custom executables can run arbitrary code and are not protected by a full OS sandbox."))
        }
    }

    private func cliLoginDescription(_ result: OfficialCLIStatus, for agent: Executor) -> String {
        if agent == .deepseek {
            switch result {
            case .authenticated: return t("DeepSeek Harness 已存好 API 金鑰（AIROOM 不讀取金鑰內容）", "DeepSeek Harness has a saved API key (AIROOM never reads it)")
            case .notLoggedIn: return t("DeepSeek Harness 還沒有存 API 金鑰：請打開 DeepSeek Harness，在設定的模型頁填好。", "DeepSeek Harness has no saved API key: open it and set one on its Models page.")
            default: break
            }
        }
        if agent == .zcode, result == .notLoggedIn {
            return t("ZCode 尚未登入：請打開 ZCode 桌面版登入 Z.AI。", "ZCode is not signed in: open the ZCode app and sign in to Z.AI.")
        }
        if agent == .antigravity, result == .notLoggedIn {
            return t("agy 尚未登入：請在 Antigravity App 登入，或在終端機執行一次 agy。", "agy is not signed in: sign in in the Antigravity app, or run agy once in Terminal.")
        }
        switch result {
        case .authenticated: return t("官方 CLI 已登入（不代表桌面 App 的對話可讀取）", "Official CLI signed in (desktop conversations are not imported)")
        case .notLoggedIn: return t("官方 CLI 尚未登入：Claude Desktop 的登入不會分享給其他 App。請在終端機執行一次 claude auth login（Codex 為 codex login）。", "Official CLI is not signed in: Claude Desktop does not share its sign-in with other apps. Run `claude auth login` (or `codex login`) once in Terminal.")
        case .notConfirmed: return t("CLI 登入尚未確認；請在終端機使用官方登入流程", "CLI sign-in not confirmed; sign in using the official terminal flow")
        case .missingExecutable: return t("找不到 CLI；桌面 App 仍可獨立使用", "CLI not found; the desktop app can still work separately")
        case .timedOut: return t("CLI 登入檢查逾時，未變更任何登入資料", "CLI login check timed out; no credentials were changed")
        case .checking: return t("檢查中…", "Checking…")
        }
    }

    private var storage: some View {
        VStack(spacing: 15) {
            StudioPanel {
                VStack(alignment: .leading, spacing: 12) {
                    Label(t("預設唯讀工作目錄", "Default Read-only Workspace"), systemImage: "folder.badge.gearshape")
                        .font(.headline)
                    HStack {
                        TextField(t("預設使用 App 私有工作資料夾", "Use private app workspace by default"), text: Binding(
                            get: { model.archive.settings.workspacePath },
                            set: { value in model.updateSettings { $0.workspacePath = value } }
                        ))
                        .textFieldStyle(.roundedBorder)
                        Button(t("選取資料夾…", "Choose Folder…")) { chooseWorkspace() }
                    }
                    Text(t("任務使用過濾機密檔案的暫存副本；外部 CLI 仍可能讀取使用者有權存取的其他檔案。", "Tasks use a sanitized temporary copy, but external CLIs may still read other user-accessible files."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            StudioPanel { BackupsPanel() }
            StudioPanel { PortableExportPanel() }
        }
    }

    private var about: some View {
        VStack(spacing: 15) {
            UpdateSettingsPanel()
            StudioPanel {
                VStack(alignment: .leading, spacing: 15) {
                    Label("AIROOM Native", systemImage: "apple.logo").font(.title2.bold())
                    Divider()
                    HStack {
                        Text(t("外觀模式", "Appearance"))
                        Spacer()
                        Picker(t("外觀模式", "Appearance"), selection: $appearance) {
                            Text(t("系統", "System")).tag("system")
                            Text(t("亮色", "Light")).tag("light")
                            Text(t("暗色", "Dark")).tag("dark")
                        }.labelsHidden().pickerStyle(.segmented).frame(width: 250)
                    }
                    LabeledContent(t("介面", "Interface"), value: "SwiftUI / AppKit")
                    LabeledContent(t("架構", "Architecture"), value: t("原生 ARM64 · 無 Electron", "Native ARM64 · No Electron"))
                    LabeledContent(t("最低版本", "Minimum macOS"), value: "macOS 14")
                    LabeledContent(t("本機資料", "Local Storage"), value: "~/Library/Application Support/AIROOMNative/state.aesgcm")
                    StudioNotice(message: t("這是 offAice 的獨立原生實作。和原版的逐項對照見 docs/FEATURE_PARITY.md。", "This is an independent native implementation. See docs/FEATURE_PARITY.md for a feature-by-feature comparison."))
                }
            }
        }
    }

    private func chooseExecutable(for agent: Executor) {
        let picker = NSOpenPanel()
        picker.canChooseFiles = true
        picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false
        picker.message = t("選擇官方執行檔", "Select the official executable") + ": " + agent.rawValue
        if picker.runModal() == .OK, let path = picker.url?.path {
            model.updateSettings {
                $0.setPath(path, for: agent)
            }
        }
    }
    private func chooseWorkspace() {
        let picker = NSOpenPanel()
        picker.canChooseFiles = false
        picker.canChooseDirectories = true
        picker.allowsMultipleSelection = false
        picker.message = t("選擇工作資料夾", "Choose a working directory")
        if picker.runModal() == .OK, let path = picker.url?.path {
            model.updateSettings { $0.workspacePath = path }
        }
    }
}


/// Backups in the app's backup folder: look inside one before restoring it.
private struct BackupsPanel: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var files: [BackupFile] = []
    /// The backup being previewed, with what it holds or why it can't be read.
    @State private var selected: URL?
    @State private var summary: BackupSummary?
    @State private var failure: String?
    @State private var confirmingRestore = false
    @State private var deleting: BackupFile?
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    private func reload() { files = model.backupFiles() }

    private func inspect(_ url: URL) {
        selected = url
        switch model.previewBackup(url) {
        case .success(let value): summary = value; failure = nil
        case .failure(let error): summary = nil; failure = error.localizedDescription
        }
    }

    private func title(_ url: URL) -> String {
        files.first(where: { $0.url == url }).map { $0.createdAt.formatted(date: .abbreviated, time: .shortened) } ?? url.lastPathComponent
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label(t("加密備份與恢復", "Encrypted Backups & Restore"), systemImage: "externaldrive.badge.timemachine").font(.headline)
                Spacer()
                Button { reload() } label: { Label(t("重新整理", "Refresh"), systemImage: "arrow.clockwise") }.buttonStyle(.borderless)
            }
            HStack(spacing: 12) {
                Button(t("立即備份", "Create Backup")) { model.backup(); reload() }
                    .buttonStyle(.borderedProminent).disabled(!model.storageAvailable)
                Button(t("從其他檔案恢復…", "Restore from a file…")) { chooseFile() }
                    .disabled(!model.canRestore)
            }
            Text(t("備份包含話題、茶歇室、圓桌、專案、人物卡與記憶，用 Keychain 金鑰加密，目前與這台 Mac 綁定。附件與圖片留在原處，不在備份檔裡；官方 CLI 的登入憑證不包含在內。",
                   "Backups hold topics, lounge, roundtables, projects, member cards and memories, encrypted with the Keychain key and bound to this Mac. Attachments and pictures stay where they are and aren't in the file; CLI sign-ins are never included."))
                .font(.caption).foregroundStyle(.secondary)
            if files.isEmpty {
                Text(t("還沒有備份。可以先備份一份目前的資料。", "No backups yet. You can back up the current data first.")).font(.callout).foregroundStyle(.secondary)
            }
            ForEach(files) { file in
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(file.createdAt.formatted(date: .abbreviated, time: .shortened)).font(.callout.weight(.medium))
                        Text((file.beforeRestore ? t("恢復前自動備份", "Automatic, before a restore") : t("手動備份", "Manual"))
                             + " · " + ByteCountFormatter.string(fromByteCount: Int64(file.bytes), countStyle: .file))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(t("預覽", "Preview")) { inspect(file.url) }
                    Button(role: .destructive) { deleting = file } label: { Image(systemName: "trash") }
                        .help(t("刪除這份備份", "Delete this backup"))
                }
                .padding(8)
                .background(selected == file.url ? Color.accentColor.opacity(0.12) : Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
            }
            if let selected {
                preview(selected)
            }
        }
        .onAppear(perform: reload)
        .confirmationDialog(t("恢復 \(selected.map(title) ?? "") 的備份？", "Restore the backup from \(selected.map(title) ?? "")?"),
                            isPresented: $confirmingRestore) {
            Button(t("先備份目前資料，再恢復", "Back up current data, then restore"), role: .destructive) {
                guard let selected else { return }
                model.restore(from: selected)
                self.selected = nil; summary = nil
                reload()
            }
        } message: {
            Text(t("目前的資料會先自動備份，再換成這份備份的內容。成員登入不受影響；專案資料夾裡的檔案不會回到過去；排程與成員閒聊會先暫停。",
                   "Current data is backed up first, then replaced. Member sign-ins are untouched; project folders don't go back in time; schedules and member chat are paused."))
        }
        .confirmationDialog(t("刪除 \(deleting?.createdAt.formatted(date: .abbreviated, time: .shortened) ?? "") 的備份？", "Delete this backup?"),
                            isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button(t("刪除備份", "Delete backup"), role: .destructive) {
                guard let file = deleting else { return }
                model.deleteBackup(file.url)
                if selected == file.url { selected = nil; summary = nil }
                deleting = nil
                reload()
            }
        } message: {
            Text(t("刪除後無法復原。目前的資料不受影響。", "This can't be undone. The current data is not affected."))
        }
    }

    @ViewBuilder private func preview(_ url: URL) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            if let summary {
                Label(t("\(title(url)) · 已解密並讀取成功", "\(title(url)) · decrypted and read"), systemImage: "checkmark.seal")
                    .font(.subheadline.weight(.semibold)).foregroundStyle(.green)
                Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 4) {
                    row(t("成員工位", "Desks"), t("\(summary.deskTopics) 個話題，\(summary.deskMessages) 則訊息", "\(summary.deskTopics) topics, \(summary.deskMessages) messages"))
                    row(t("茶歇室", "Lounge"), t("\(summary.loungeTopics) 個話題，\(summary.loungeMessages) 則發言", "\(summary.loungeTopics) topics, \(summary.loungeMessages) messages"))
                    row(t("人物卡", "Member cards"), "\(summary.memberNames.joined(separator: "、"))" + t("（\(summary.memories) 條記憶）", " (\(summary.memories) memories)"))
                    row(t("圓桌與專案", "Roundtables & projects"), t("\(summary.roundtables) 個圓桌議題，\(summary.projects) 個專案", "\(summary.roundtables) roundtables, \(summary.projects) projects"))
                    row(t("排程", "Schedules"), t("\(summary.schedules) 個", "\(summary.schedules)"))
                    if !summary.ownerName.isEmpty { row(t("你的稱呼", "Your name"), summary.ownerName) }
                }
                .font(.caption)
                Button(t("恢復這份備份…", "Restore this backup…")) { confirmingRestore = true }
                    .disabled(!model.canRestore)
                if !model.canRestore {
                    Text(t("有成員正在工作時不能恢復；等工作結束或全部中止後再試。", "Can't restore while members are working; wait or stop everything first."))
                        .font(.caption2).foregroundStyle(.secondary)
                }
            } else {
                Label(t("無法讀取這份備份：\(failure ?? "")", "This backup can't be read: \(failure ?? "")"), systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
                Text(t("可能已損壞、被改過，或是用另一台 Mac 的金鑰加密的。目前的資料沒有任何改變。", "It may be damaged, altered, or sealed with another Mac's key. Nothing was changed."))
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
    }

    private func row(_ name: String, _ value: String) -> some View {
        GridRow {
            Text(name).foregroundStyle(.secondary)
            Text(value).textSelection(.enabled)
        }
    }

    private func chooseFile() {
        let picker = NSOpenPanel()
        picker.canChooseFiles = true
        picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false
        picker.message = t("選取加密備份檔", "Choose an encrypted backup")
        if picker.runModal() == .OK, let url = picker.url { inspect(url) }
    }
}

/// Moving to another Mac: everything, attachments included, sealed with a password
/// instead of this Mac's Keychain key.
private struct PortableExportPanel: View {
    @EnvironmentObject private var model: WorkbenchModel
    private enum Step: Identifiable {
        case export(URL), unlock(URL)
        var id: String {
            switch self { case .export(let url): return "export:" + url.path; case .unlock(let url): return "unlock:" + url.path }
        }
    }
    @State private var step: Step?
    @State private var opened: PortableExport?
    @State private var openedName = ""
    @State private var confirmingImport = false
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(t("搬到另一台 Mac：密碼匯出", "Move to Another Mac: Password Export"), systemImage: "key.horizontal").font(.headline)
            HStack(spacing: 12) {
                Button(t("匯出加密檔…", "Export…")) { chooseDestination() }
                    .buttonStyle(.borderedProminent).disabled(!model.storageAvailable)
                Button(t("從匯出檔匯入…", "Import…")) { chooseExport() }
                    .disabled(!model.canRestore)
            }
            Text(t("匯出檔包含話題、茶歇室、圓桌、專案、人物卡、記憶、附件與頭像，用你設定的密碼加密（PBKDF2 + AES-256-GCM），不依賴這台 Mac 的 Keychain，所以能在新的 Mac 匯入。官方 CLI 的登入憑證與 API 金鑰不包含在內，到新 Mac 要重新登入。",
                   "The export holds topics, lounge, roundtables, projects, member cards, memories, attachments and avatars, sealed with a password you choose (PBKDF2 + AES-256-GCM) instead of this Mac's Keychain key, so another Mac can import it. CLI sign-ins and API keys are never included; sign in again on the new Mac."))
                .font(.caption).foregroundStyle(.secondary)
            StudioNotice(icon: "lock.shield", message: t("密碼不會被儲存，也無法找回。忘記密碼，這個匯出檔就打不開。", "The password isn't stored and can't be recovered. Without it, the export can't be opened."))
            if let opened { preview(opened) }
        }
        .sheet(item: $step) { step in
            switch step {
            case .export(let url):
                PasswordSheet(language: model.archive.settings.language, confirming: true,
                              title: t("設定匯出密碼", "Set an Export Password"),
                              message: t("至少 \(PortableExportCodec.minimumPasswordLength) 個字。匯入時要輸入同一組密碼。", "At least \(PortableExportCodec.minimumPasswordLength) characters. You'll need it to import."),
                              action: t("匯出", "Export")) { password in
                    model.exportPortable(to: url, password: password) ? nil : model.statusMessage
                }
            case .unlock(let url):
                PasswordSheet(language: model.archive.settings.language, confirming: false,
                              title: t("輸入匯出密碼", "Enter the Export Password"),
                              message: url.lastPathComponent,
                              action: t("解密並預覽", "Decrypt & Preview")) { password in
                    switch model.openPortable(url, password: password) {
                    case .success(let value): opened = value; openedName = url.lastPathComponent; return nil
                    case .failure(let error): return describe(error)
                    }
                }
            }
        }
        .confirmationDialog(t("匯入 \(openedName)？", "Import \(openedName)?"), isPresented: $confirmingImport) {
            Button(t("先備份目前資料，再匯入", "Back up current data, then import"), role: .destructive) {
                guard let opened else { return }
                model.importPortable(opened)
                self.opened = nil
            }
        } message: {
            Text(t("目前的資料會先在這台 Mac 備份，再換成匯出檔的內容；附件會加入這台 Mac。排程與成員閒聊會先暫停。",
                   "Current data is backed up on this Mac first, then replaced; attachments are added here. Schedules and member chat are paused."))
        }
    }

    @ViewBuilder private func preview(_ export: PortableExport) -> some View {
        let summary = BackupSummary(export.state)
        VStack(alignment: .leading, spacing: 8) {
            Label(t("\(openedName) · 已解密並讀取成功", "\(openedName) · decrypted and read"), systemImage: "checkmark.seal")
                .font(.subheadline.weight(.semibold)).foregroundStyle(.green)
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 4) {
                row(t("匯出時間", "Exported"), export.createdAt.formatted(date: .abbreviated, time: .shortened))
                row(t("成員工位", "Desks"), t("\(summary.deskTopics) 個話題，\(summary.deskMessages) 則訊息", "\(summary.deskTopics) topics, \(summary.deskMessages) messages"))
                row(t("茶歇室", "Lounge"), t("\(summary.loungeTopics) 個話題，\(summary.loungeMessages) 則發言", "\(summary.loungeTopics) topics, \(summary.loungeMessages) messages"))
                row(t("人物卡", "Member cards"), summary.memberNames.joined(separator: "、") + t("（\(summary.memories) 條記憶）", " (\(summary.memories) memories)"))
                row(t("圓桌與專案", "Roundtables & projects"), t("\(summary.roundtables) 個圓桌議題，\(summary.projects) 個專案", "\(summary.roundtables) roundtables, \(summary.projects) projects"))
                row(t("附件與頭像", "Attachments"), t("\(export.attachments.count) 個", "\(export.attachments.count)"))
            }
            .font(.caption)
            HStack {
                Button(t("匯入這份資料…", "Import this data…")) { confirmingImport = true }
                    .disabled(!model.canRestore)
                Button(t("取消", "Cancel")) { opened = nil }
            }
            if !model.canRestore {
                Text(t("有成員正在工作時不能匯入；等工作結束或全部中止後再試。", "Can't import while members are working; wait or stop everything first."))
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
    }

    private func row(_ name: String, _ value: String) -> some View {
        GridRow { Text(name).foregroundStyle(.secondary); Text(value).textSelection(.enabled) }
    }

    private func describe(_ error: Error) -> String {
        switch error as? PortableExportError {
        case .wrongPasswordOrDamaged: return t("密碼不對，或檔案已損壞、被改過。目前的資料沒有任何改變。", "Wrong password, or the file is damaged or was altered. Nothing was changed.")
        case .notAnExport: return t("這不是 AIROOM 的匯出檔。", "This file is not an AIROOM export.")
        case .unsupportedVersion: return t("這個匯出檔來自較新的 AIROOM，請先更新。", "This export comes from a newer AIROOM. Update first.")
        case .tooLarge: return t("匯出檔太大，無法開啟。", "The export is too large to open.")
        case .passwordTooShort(let n): return t("密碼至少要 \(n) 個字。", "The password needs at least \(n) characters.")
        case .cryptoUnavailable: return t("這台電腦無法使用密碼加密。", "Password encryption isn't available here.")
        case nil: return error.localizedDescription
        }
    }

    private func chooseDestination() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "AIROOM-\(Date.now.formatted(.iso8601.year().month().day())).\(PortableExportCodec.fileExtension)"
        panel.message = t("匯出檔請存到外接硬碟或雲端硬碟，帶到新的 Mac。", "Save it somewhere you can reach from the new Mac.")
        if panel.runModal() == .OK, let url = panel.url { step = .export(url) }
    }

    private func chooseExport() {
        let picker = NSOpenPanel()
        picker.canChooseFiles = true
        picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false
        picker.message = t("選取 .\(PortableExportCodec.fileExtension) 匯出檔", "Choose an .\(PortableExportCodec.fileExtension) export")
        if picker.runModal() == .OK, let url = picker.url { opened = nil; step = .unlock(url) }
    }
}

/// Asks for a password; `submit` returns an error to show, or nil to close.
private struct PasswordSheet: View {
    var language: InterfaceLanguage
    var confirming: Bool
    var title: String
    var message: String
    var action: String
    var submit: (String) -> String?
    @Environment(\.dismiss) private var dismiss
    @State private var password = ""
    @State private var again = ""
    @State private var error: String?
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, language) }

    private var problem: String? {
        if password.count < PortableExportCodec.minimumPasswordLength { return nil }
        if confirming && !again.isEmpty && again != password { return t("兩次輸入的密碼不一樣。", "The passwords don't match.") }
        return nil
    }
    private var ready: Bool {
        password.count >= PortableExportCodec.minimumPasswordLength && (!confirming || again == password)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title).font(.headline)
            Text(message).font(.caption).foregroundStyle(.secondary)
            SecureField(t("密碼", "Password"), text: $password).textFieldStyle(.roundedBorder)
            if confirming {
                SecureField(t("再輸入一次", "Repeat password"), text: $again).textFieldStyle(.roundedBorder)
            }
            if let message = error ?? problem {
                Label(message, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange)
            }
            HStack {
                Spacer()
                Button(t("取消", "Cancel")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(action) {
                    error = submit(password)
                    if error == nil { password = ""; again = ""; dismiss() }
                }
                .keyboardShortcut(.defaultAction).disabled(!ready)
            }
        }
        .padding(20)
        .frame(width: 380)
    }
}
