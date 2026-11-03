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
    private func cliPath(for agent: AgentKind) -> Binding<String> {
        Binding(get: { model.archive.settings.configuredPath(for: agent) },
                set: { value in
                    model.updateSettings { settings in
                        if agent == .claude { settings.claudePath = value }
                        else { settings.codexPath = value }
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
            ForEach(AgentKind.allCases) { agent in
                StudioPanel {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack(spacing: 12) {
                            StudioAvatar(agent: agent, size: 41)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(agent.rawValue).font(.headline)
                                Text(agent == .codex
                                     ? t("官方 App Server（自動使用 ChatGPT App 內附 Codex）", "Official App Server (uses bundled ChatGPT Codex)")
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
                            Button { model.openVendorDesktop(for: agent) } label: {
                                Label(t("開啟官方桌面 App", "Open Official Desktop App"), systemImage: "arrow.up.forward.app")
                            }
                            Button { model.checkVendorCLIAccount(for: agent) } label: {
                                Label(t("檢查 CLI 登入", "Check CLI Login"), systemImage: "checkmark.shield")
                            }
                            .disabled(model.officialCLIStatuses[agent] == .checking)
                            if model.officialCLIStatuses[agent] == .checking { ProgressView().controlSize(.small) }
                        }
                        if let status = model.officialCLIStatuses[agent], status != .checking {
                            Label(cliLoginDescription(status), systemImage: status == .authenticated ? "checkmark.circle" : "info.circle")
                                .font(.caption).foregroundStyle(status == .authenticated ? Color.green : Color.secondary)
                        }
                        Text(agent == .codex
                             ? t("Codex 留空會先找 ChatGPT.app 內附的 codex-cli，再找 Codex.app 或 PATH。使用 App Server JSON-RPC；不會讀取 Token，也不會自動匯入桌面對話。", "Leave Codex path empty to prefer bundled ChatGPT.app codex-cli, then Codex.app or PATH. Uses App Server JSON-RPC; no token scraping or desktop transcript import.")
                             : t("Claude 路徑留空會先找 Claude Desktop Code 分頁安裝的最新 Claude Code，再找 PATH。檢查 CLI 登入不會讀取 Token，也不代表桌面 App 對話已同步。", "Leave the Claude path empty to use the latest Claude Code installed by Claude Desktop’s Code tab, falling back to PATH. Login checks never read tokens or sync desktop conversations."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            StudioNotice(message: t("請只選擇你信任的官方 CLI。自訂路徑可能執行任意程式碼，外部 CLI 不受完整 OS 沙箱限制。", "Select trusted official CLIs only. Custom executables can run arbitrary code and are not protected by a full OS sandbox."))
        }
    }

    private func cliLoginDescription(_ result: OfficialCLIStatus) -> String {
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
            StudioPanel {
                VStack(alignment: .leading, spacing: 12) {
                    Label(t("加密備份與恢復", "Encrypted Backups & Restore"), systemImage: "externaldrive.badge.timemachine")
                        .font(.headline)
                    HStack(spacing: 12) {
                        Button(t("立即備份", "Create Backup")) { model.backup() }
                            .buttonStyle(.borderedProminent)
                        Button(t("從備份檔恢復…", "Restore Backup…")) { chooseBackup() }
                            .disabled(model.projectBusy || model.roomRunning || !model.busyAgents.isEmpty)
                    }
                    Text(t("資料使用 Keychain 金鑰加密，備份目前與本機綁定。官方 CLI 的登入憑證不包含在內。", "Data is encrypted using a Keychain-managed key. Backups are device-bound and exclude vendor CLI credentials."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            StudioNotice(icon: "lock.shield", message: t("如果更換裝置或遺失 Keychain 金鑰，目前的加密備份無法直接在另一台 Mac 還原。", "If the Keychain key is lost, encrypted backups cannot currently be restored on another Mac."))
        }
    }

    private var about: some View {
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
                StudioNotice(message: t("這是 offAice 的獨立原生實作。原版所有多人工作流程尚未完整移植。", "This is an independent native implementation. Not all original multi-agent workflows are available."))
            }
        }
    }

    private func chooseBackup() {
        let picker = NSOpenPanel()
        picker.canChooseFiles = true
        picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false
        picker.message = t("選取加密備份檔", "Choose an encrypted backup")
        if picker.runModal() == .OK, let url = picker.url { model.restore(from: url) }
    }
    private func chooseExecutable(for agent: AgentKind) {
        let picker = NSOpenPanel()
        picker.canChooseFiles = true
        picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false
        picker.message = t("選擇官方執行檔", "Select the official executable") + ": " + agent.rawValue
        if picker.runModal() == .OK, let path = picker.url?.path {
            model.updateSettings {
                if agent == .claude { $0.claudePath = path }
                else { $0.codexPath = path }
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
