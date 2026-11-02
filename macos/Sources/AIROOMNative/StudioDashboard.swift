import SwiftUI
import AIROOMCore

struct StudioDashboard: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let onChat: (AgentKind) -> Void
    let onProject: () -> Void
    let onRoom: () -> Void
    let onMembers: () -> Void
    let onMemberCard: (AgentKind) -> Void
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                VStack(alignment: .leading, spacing: 17) {
                    Text(t("我們一起工作的房間", "A place to work together"))
                        .font(.system(size: 12, weight: .semibold)).tracking(2)
                        .foregroundStyle(StudioTheme.warning)
                    Text(t("歡迎來到 AIROOM。", "Welcome to AIROOM."))
                        .font(.system(size: 32, weight: .bold, design: .rounded))
                        .foregroundStyle(StudioTheme.ink(scheme))
                    Text(t("讓你的 AI 成員一起討論、獨立比較提案，或在你確認後將修改寫入專案。", "Talk with your AI teammates, compare independent ideas, and approve project changes before writeback."))
                        .font(.system(size: 15)).foregroundStyle(.secondary)
                        .frame(maxWidth: 750, alignment: .leading)
                    HStack(spacing: 10) {
                        Button(action: onProject) {
                            Label(t("打開專案工作區", "Open Projects"), systemImage: "folder.badge.plus")
                        }.buttonStyle(.borderedProminent)
                        Button(action: onRoom) { Label(t("前往茶歇室", "Visit Lounge"), systemImage: "cup.and.saucer") }
                        Button(action: onMembers) { Label(t("管理人物卡", "Manage Members"), systemImage: "person.crop.rectangle.stack") }
                    }
                }
                .padding(.top, 4)

                HStack(alignment: .top, spacing: 15) {
                    ForEach(AgentKind.allCases) { agent in
                        let card = model.archive.members.first(where: { $0.agent == agent })
                        VStack(alignment: .leading, spacing: 0) {
                            ZStack {
                                agent.studioColor.opacity(0.09)
                                StudioPortrait(agent: agent)
                                    .padding(.top, 14)
                                    .padding(.horizontal, 8)
                            }
                            .frame(height: 225)
                            .clipped()
                            VStack(alignment: .leading, spacing: 9) {
                                HStack(spacing: 8) {
                                    Circle().fill(model.busyAgents.contains(agent) ? StudioTheme.orange : StudioTheme.moss)
                                        .frame(width: 8, height: 8)
                                    Text(card?.name ?? agent.shortName).font(.title3.weight(.semibold))
                                    Spacer()
                                    Text(agent == .claude ? "Claude Code" : "Codex")
                                        .font(.caption2).foregroundStyle(.secondary)
                                }
                                Text(model.busyAgents.contains(agent)
                                     ? t("正在工作", "Working")
                                     : (card?.enabled == false ? t("尚未啟用", "Disabled") : t("準備就緒時可以接收訊息", "Available for new tasks")))
                                    .font(.caption).foregroundStyle(.secondary)
                                HStack {
                                    Button(t("人物卡", "Profile")) { onMemberCard(agent) }
                                    Spacer()
                                    Button { onChat(agent) } label: {
                                        Label(t("前往工位", "Open desk"), systemImage: "arrow.right")
                                    }
                                    .buttonStyle(.borderedProminent)
                                    .disabled(card?.enabled == false)
                                }
                                .font(.caption)
                            }
                            .padding(16)
                        }
                        .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 16))
                        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(StudioTheme.border(scheme)))
                        .frame(maxWidth: .infinity)
                    }
                }

                HStack(alignment: .top, spacing: 16) {
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 12) {
                            Label(t("開工前檢查", "Preflight Checklist"), systemImage: "checklist")
                                .font(.headline)
                            ForEach(AgentKind.allCases) { agent in
                                HStack {
                                    StudioAvatar(agent: agent, size: 27, running: model.busyAgents.contains(agent))
                                    Text(agent.rawValue).font(.subheadline)
                                    Spacer()
                                    Text(t("官方 CLI", "Official CLI"))
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            StudioNotice(message: t("首次使用請到「設定與連接」指定官方 CLI 的路徑，並在終端機登入。", "First configure each official CLI in Settings, then sign in using the CLI itself."))
                        }
                    }
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 13) {
                            Label(t("專案進度", "Project Activity"), systemImage: "square.stack.3d.up")
                                .font(.headline)
                            if model.archive.projects.isEmpty {
                                Text(t("還沒有專案。可以先新增一個資料夾。", "No projects yet. Add a project folder to begin."))
                                    .font(.subheadline).foregroundStyle(.secondary)
                            } else {
                                ForEach(model.archive.projects.prefix(4)) { project in
                                    HStack {
                                        VStack(alignment: .leading, spacing: 4) {
                                            Text(project.title).font(.subheadline.weight(.semibold))
                                            Text(project.originalFolder).font(.caption2)
                                                .foregroundStyle(.secondary).lineLimit(1)
                                        }
                                        Spacer()
                                        StudioPhasePill(phase: project.phase, language: lang)
                                    }
                                }
                            }
                            Button(t("查看專案", "View Projects"), action: onProject)
                        }
                    }
                }
                Text(t("所有工作台紀錄保存在本機。外部 AI CLI 可能需要連接其官方服務。", "Workbench history stays on this device. External AI CLIs may connect to their official services."))
                    .font(.caption2).foregroundStyle(.secondary)
            }
            .padding(28)
            .frame(maxWidth: 1100)
            .frame(maxWidth: .infinity)
        }
    }
}

struct StudioMemberSheet: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let agent: AgentKind
    let onDesk: () -> Void
    let onManage: () -> Void
    private func t(_ zh: String, _ en: String) -> String {
        StudioI18n.t(zh, en, model.archive.settings.language)
    }
    var body: some View {
        let member = model.archive.members.first(where: { $0.agent == agent })
        VStack(spacing: 0) {
            ZStack {
                agent.studioColor.opacity(0.08)
                StudioPortrait(agent: agent).padding(.top, 14)
            }
            .frame(height: 210).clipped()
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(member?.name ?? agent.shortName).font(.title2.bold())
                    Spacer()
                    Text(agent.rawValue).foregroundStyle(.secondary)
                }
                Text(member?.persona.isEmpty == false ? (member?.persona ?? "") : t("這張人物卡尚未設定個性與分工。", "No persona or role has been configured."))
                    .foregroundStyle(.secondary)
                    .lineLimit(4)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Button(t("編輯人物卡", "Edit Profile"), action: onManage)
                    Spacer()
                    Button(t("前往成員工位", "Open Member Desk"), action: onDesk)
                        .buttonStyle(.borderedProminent)
                }
            }
            .padding(20)
        }
        .background(StudioTheme.surface(scheme))
    }
}

struct StudioAttentionView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let onProject: () -> Void
    let onChat: (AgentKind) -> Void
    private func t(_ zh: String, _ en: String) -> String {
        StudioI18n.t(zh, en, model.archive.settings.language)
    }
    private var projects: [ProjectRecord] {
        model.archive.projects.filter { $0.phase == .reviewing || $0.phase == .failed }
    }
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                StudioPageHeading(eyebrow: "ATTENTION", title: t("待你處理", "Needs Attention"),
                    subtitle: t("這裡列出尚待驗收或發生錯誤的專案。", "Projects that need approval or investigation."))
                if projects.isEmpty {
                    StudioEmptyState(icon: "checkmark.circle", title: t("目前沒有待辦", "All clear"),
                                     detail: t("需要驗收的專案會出現在這裡。", "Projects awaiting your review will appear here."))
                }
                ForEach(projects) { project in
                    StudioPanel {
                        HStack(spacing: 14) {
                            Image(systemName: project.phase == .reviewing ? "doc.text.magnifyingglass" : "exclamationmark.triangle")
                                .font(.title2).foregroundStyle(StudioTheme.orange)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(project.title).font(.headline)
                                Text(project.phase == .reviewing ? t("變更待驗收", "Review changes") : t("執行失敗，請檢查結果", "Execution failed; inspect result"))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(t("開啟專案", "Open Project"), action: onProject)
                        }
                    }
                }
                StudioNotice(message: t("這個頁面不會自動批准寫回或重送失敗的任務。", "Nothing here automatically approves writeback or retries an uncertain task."))
            }.padding(26).frame(maxWidth: 900).frame(maxWidth: .infinity)
        }
    }
}
