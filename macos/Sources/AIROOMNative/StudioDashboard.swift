import SwiftUI
import AIROOMCore

struct StudioDashboard: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let onChat: (MemberID) -> Void
    let onProject: () -> Void
    let onRoom: () -> Void
    let onMembers: () -> Void
    let onMemberCard: (MemberID) -> Void
    var onSettings: () -> Void = {}
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                hero

                // Wraps into rows on a narrow window instead of squeezing each card to a sliver.
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 172, maximum: 260), spacing: 15, alignment: .top)],
                          alignment: .leading, spacing: 15) {
                    ForEach(model.visibleAgents) { agent in
                        let card = model.archive.members.first(where: { $0.agent == agent })
                        VStack(alignment: .leading, spacing: 0) {
                            // Card face in relief: cut-out figure over its own pressed silhouette.
                            StudioReliefPortrait(agent: agent)
                            .frame(height: 232)
                            .clipped()
                            // Clipping only hides overflow; this also keeps it from taking clicks.
                            .contentShape(Rectangle())
                            .overlay(alignment: .bottom) {
                                Rectangle().fill(StudioTheme.hairline(scheme)).frame(height: 1)
                            }
                            VStack(alignment: .leading, spacing: 9) {
                                HStack(spacing: 8) {
                                    Circle().fill(model.busyAgents.contains(agent) ? StudioTheme.orange : StudioTheme.moss)
                                        .frame(width: 8, height: 8)
                                    Text(card?.name ?? agent.shortName).font(.system(.title3, design: .rounded).weight(.semibold))
                                        .lineLimit(1)
                                    Spacer(minLength: 4)
                                    HStack(spacing: 4) {
                                        ExecutorLogo(executor: agent.executor, size: 13)
                                        Text(agent.executor.rawValue).lineLimit(1)
                                    }
                                        .font(.caption2.weight(.medium)).foregroundStyle(.secondary)
                                        .padding(.leading, 3).padding(.trailing, 6).padding(.vertical, 2)
                                        .background(StudioTheme.subtleFill(scheme), in: Capsule())
                                }
                                Text(model.busyAgents.contains(agent)
                                     ? t("正在工作", "Working")
                                     : (card?.enabled == false ? t("尚未啟用", "Disabled") : t("準備就緒時可以接收訊息", "Available for new tasks")))
                                    .font(.caption).foregroundStyle(.secondary)
                                if let motto = card?.motto, !motto.isEmpty {
                                    Text("「\(motto)」").font(.caption).italic().foregroundStyle(.secondary).lineLimit(2)
                                }
                                HStack {
                                    Button(t("人物卡", "Profile")) { onMemberCard(agent) }
                                    Spacer(minLength: 6)
                                    Button { onChat(agent) } label: {
                                        Label(t("前往工位", "Open desk"), systemImage: "arrow.right")
                                    }
                                    .buttonStyle(.studioPrimary)
                                    .disabled(card?.enabled == false)
                                }
                                .controlSize(.small)
                            }
                            .padding(16)
                        }
                        .clipShape(RoundedRectangle(cornerRadius: StudioMetrics.radiusPanel, style: .continuous))
                        .studioCard(radius: StudioMetrics.radiusPanel)
                        .studioBevel(radius: StudioMetrics.radiusPanel)
                        .frame(maxWidth: .infinity)
                    }
                }

                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .top, spacing: 16) { preflightPanel; projectPanel }
                    VStack(alignment: .leading, spacing: 16) { preflightPanel; projectPanel }
                }
                Text(t("所有工作台紀錄保存在本機。外部 AI CLI 可能需要連接其官方服務。", "Workbench history stays on this device. External AI CLIs may connect to their official services."))
                    .font(.caption2).foregroundStyle(.secondary)
            }
            .padding(StudioMetrics.page)
            .frame(maxWidth: 1100)
            .frame(maxWidth: .infinity)
        }
    }

    private var greeting: String {
        let name = model.archive.settings.userName.trimmingCharacters(in: .whitespacesAndNewlines)
        let hour = Calendar.current.component(.hour, from: Date())
        let word: String
        switch hour {
        case 5..<11: word = t("早安", "Good morning")
        case 11..<17: word = t("午安", "Good afternoon")
        case 17..<22: word = t("晚上好", "Good evening")
        default: word = t("夜深了", "Late night")
        }
        return name.isEmpty ? word : word + t("，", ", ") + name
    }

    /// A banner lit by the same time-of-day artwork as the lounge, with the studio's pulse below it.
    private var hero: some View {
        let shape = RoundedRectangle(cornerRadius: 22, style: .continuous)
        return VStack(alignment: .leading, spacing: 14) {
            StudioEyebrow(text: greeting + t(" · 我們一起工作的房間", " · A place to work together"))
            Text(t("歡迎來到 AIROOM。", "Welcome to AIROOM."))
                .font(StudioType.heroTitle)
                .foregroundStyle(StudioTheme.ink(scheme))
            Text(t("讓你的 AI 成員一起討論、獨立比較提案，或在你確認後將修改寫入專案。", "Talk with your AI teammates, compare independent ideas, and approve project changes before writeback."))
                .font(.system(size: 15)).foregroundStyle(.secondary)
                .lineSpacing(2)
                .frame(maxWidth: 560, alignment: .leading)
            HStack(spacing: 10) {
                Button(action: onProject) {
                    Label(t("打開專案工作區", "Open Projects"), systemImage: "folder.badge.plus")
                }.buttonStyle(.studioPrimary)
                Button(action: onRoom) { Label(t("前往茶歇室", "Visit Lounge"), systemImage: "cup.and.saucer") }
                Button(action: onMembers) { Label(t("管理人物卡", "Manage Members"), systemImage: "person.crop.rectangle.stack") }
            }
            .padding(.top, 2)
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 10) { statChips }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 8, alignment: .leading)],
                          alignment: .leading, spacing: 8) { statChips }
            }
            .padding(.top, 6)
        }
        .padding(28)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            TimelineView(.periodic(from: .now, by: 300)) { context in
                ZStack {
                    StudioTheme.surface(scheme)
                    Color.clear
                        .overlay {
                            if let art = StudioAsset.image(StudioAsset.backdrop(for: context.date)) {
                                Image(nsImage: art).resizable().scaledToFill()
                                    .opacity(scheme == .dark ? 0.55 : 0.7)
                            }
                        }
                        .clipped()
                    // Paper fades in from the left so the copy always sits on a calm field.
                    LinearGradient(stops: [
                        .init(color: StudioTheme.surface(scheme), location: 0),
                        .init(color: StudioTheme.surface(scheme).opacity(0.92), location: 0.42),
                        .init(color: StudioTheme.surface(scheme).opacity(0.35), location: 0.78),
                        .init(color: StudioTheme.surface(scheme).opacity(0.15), location: 1)
                    ], startPoint: .leading, endPoint: .trailing)
                    RadialGradient(colors: [StudioTheme.amber.opacity(0.18), .clear],
                                   center: .topLeading, startRadius: 0, endRadius: 420)
                }
                .accessibilityHidden(true)
                .allowsHitTesting(false)
            }
        }
        .clipShape(shape)
        .overlay(shape.strokeBorder(StudioTheme.border(scheme), lineWidth: 1))
        .shadow(color: StudioTheme.shadow(scheme), radius: 16, y: 6)
    }

    @ViewBuilder private var statChips: some View {
        statChip(value: model.visibleAgents.count, label: t("位成員", "members"), icon: "person.2.fill", tint: StudioTheme.plum)
        statChip(value: model.busyAgents.count, label: t("正在工作", "working"), icon: "bolt.fill", tint: StudioTheme.orange)
        statChip(value: model.attentionItems.count, label: t("待你處理", "need you"), icon: "checklist", tint: StudioTheme.rose)
        statChip(value: model.archive.projects.count, label: t("個專案", "projects"), icon: "folder.fill", tint: StudioTheme.sky)
    }

    private func statChip(value: Int, label: String, icon: String, tint: Color) -> some View {
        HStack(spacing: 8) {
            StudioIconTile(symbol: icon, tint: tint, size: 22)
            Text("\(value)").font(.system(size: 17, weight: .bold, design: .rounded)).monospacedDigit()
                .foregroundStyle(StudioTheme.ink(scheme))
            Text(label).font(.caption).foregroundStyle(.secondary).lineLimit(1)
        }
        .padding(.leading, 6).padding(.trailing, 12).padding(.vertical, 6)
        .background(.regularMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(StudioTheme.border(scheme).opacity(0.7), lineWidth: 1))
        .accessibilityElement(children: .combine)
    }

    private var preflightPanel: some View {
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 12) {
                            StudioSectionTitle(t("開工前檢查", "Preflight Checklist"), icon: "checklist")
                            ForEach(model.memberIDs) { agent in preflightRow(agent) }
                            StudioNotice(message: t("「去登入」會在終端機打開官方程式自己的登入流程；AIROOM 不接觸帳號密碼或 Token。", "\"Sign in\" opens the official CLI's own login in Terminal; AIROOM never handles passwords or tokens."))
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
    }

    private var projectPanel: some View {
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 13) {
                            StudioSectionTitle(t("專案進度", "Project Activity"), icon: "square.stack.3d.up")
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
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
    }
}

struct StudioMemberSheet: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @Environment(\.dismiss) private var dismiss
    let agent: MemberID
    let onDesk: () -> Void
    let onManage: () -> Void
    private func t(_ zh: String, _ en: String) -> String {
        StudioI18n.t(zh, en, model.archive.settings.language)
    }
    var body: some View {
        let member = model.archive.members.first(where: { $0.agent == agent })
        VStack(spacing: 0) {
            StudioReliefPortrait(agent: agent)
                .frame(height: 230).clipped()
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(member?.name ?? agent.shortName).font(.title2.bold())
                    Spacer()
                    Text(agent.executor.rawValue).foregroundStyle(.secondary)
                }
                Text(member?.persona.isEmpty == false ? (member?.persona ?? "") : t("這張人物卡尚未設定個性與分工。", "No persona or role has been configured."))
                    .foregroundStyle(.secondary)
                    .lineLimit(4)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Button(t("編輯人物卡", "Edit Profile"), action: onManage)
                    Spacer()
                    Button(t("前往成員工位", "Open Member Desk"), action: onDesk)
                        .buttonStyle(.studioPrimary)
                }
            }
            .padding(20)
        }
        .background(StudioTheme.surface(scheme))
        // A way back out for a card opened by accident; Esc closes it too.
        .overlay(alignment: .topLeading) {
            Button { dismiss() } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 12, weight: .bold))
                    .frame(width: 26, height: 26)
                    .background(.regularMaterial, in: Circle())
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .help(t("關閉", "Close"))
            .accessibilityLabel(t("關閉", "Close"))
            .padding(12)
        }
    }
}

extension StudioDashboard {
    @ViewBuilder func preflightRow(_ agent: MemberID) -> some View {
        let executor = model.executor(of: agent)
        let status = model.officialCLIStatuses[executor]
        HStack(spacing: 8) {
            StudioAvatar(agent: agent, size: 27, running: model.busyAgents.contains(agent))
            Text(model.memberName(agent)).font(.subheadline.weight(.medium))
            Spacer()
            switch status {
            case .authenticated:
                StudioStatusBadge(text: t("已就緒", "Ready"), tint: StudioTheme.moss, icon: "checkmark")
            case .notLoggedIn:
                StudioStatusBadge(text: t("需要登入", "Sign-in needed"), tint: StudioTheme.orange)
                Button(t("去登入", "Sign in")) { model.openOfficialLogin(for: executor) }.controlSize(.small)
                Button { model.checkVendorCLIAccount(for: executor) } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless).help(t("重新檢查", "Check again"))
            case .missingExecutable:
                StudioStatusBadge(text: t("缺少程式", "CLI missing"), tint: .red)
                Button(t("設定路徑", "Set path"), action: onSettings).controlSize(.small)
            case .checking, nil:
                ProgressView().controlSize(.mini)
                Text(t("檢查中", "Checking")).font(.caption).foregroundStyle(.secondary)
            case .notConfirmed, .timedOut:
                StudioStatusBadge(text: t("無法確認", "Unconfirmed"), tint: StudioTheme.slate)
                Button { model.checkVendorCLIAccount(for: executor) } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless).help(t("重新檢查", "Check again"))
            }
        }
    }
}

struct StudioAttentionView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let onOpen: (AttentionItem.Target) -> Void
    private func t(_ zh: String, _ en: String) -> String {
        StudioI18n.t(zh, en, model.archive.settings.language)
    }
    private func icon(_ kind: AttentionKind) -> String {
        switch kind {
        case .question: return "questionmark.bubble"
        case .decision: return "hand.raised"
        case .unknown: return "exclamationmark.triangle"
        case .login: return "person.crop.circle.badge.exclamationmark"
        case .missing: return "xmark.circle"
        case .acceptance: return "doc.text.magnifyingglass"
        }
    }
    var body: some View {
        let items = model.attentionItems
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                StudioPageHeading(eyebrow: "ATTENTION", title: t("待你處理", "Needs Attention"),
                    subtitle: t("成員在等你回答、需要你點名或登入、有結果要核對、有成品等你驗收，都會列在這裡。", "Questions for you, picks, sign-ins, results to check and work to accept."))
                if items.isEmpty {
                    StudioEmptyState(icon: "checkmark.circle", title: t("目前沒有待辦", "All clear"),
                                     detail: t("需要你處理的事會出現在這裡。", "Anything that needs you will show up here."))
                }
                ForEach(items) { item in
                    StudioPanel {
                        HStack(spacing: 14) {
                            Image(systemName: icon(item.kind)).font(.title2).foregroundStyle(StudioTheme.orange)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(item.title).font(.headline)
                                Text(item.detail).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                            }
                            Spacer()
                            Button(t("忽略", "Ignore")) { model.ignoreAttention(item) }
                                .help(t("只隱藏這則提醒，原本的工作和狀態都保留；內容有變化時會再出現。",
                                        "Only hides this reminder; the work stays as it is. It comes back if it changes."))
                                .disabled(!model.storageAvailable)
                            Button(t("前往", "Open")) { onOpen(item.target) }
                        }
                    }
                }
                if model.ignoredAttentionCount > 0 {
                    Button(t("重新顯示已忽略的 \(model.ignoredAttentionCount) 則", "Show \(model.ignoredAttentionCount) ignored")) {
                        model.showIgnoredAttention()
                    }
                    .buttonStyle(.borderless).font(.caption)
                }
                StudioNotice(message: t("這個頁面只負責帶你過去；不會自動回答、批准寫回或重送任何請求。", "This page only takes you there; it never answers, approves writeback or resends anything."))
            }.padding(StudioMetrics.page).frame(maxWidth: 900).frame(maxWidth: .infinity)
        }
    }
}
