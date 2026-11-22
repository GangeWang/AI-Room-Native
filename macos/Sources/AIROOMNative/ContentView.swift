import SwiftUI
import AIROOMCore

private enum StudioRoute: Hashable {
    case home, chat(MemberID), room, roundtable, projects, members, attention, quota, automation, stage, settings

    /// Each place keeps one hue: its sidebar tile, selection line and breadcrumb.
    var hue: Color {
        switch self {
        case .home: return StudioTheme.orange
        case .chat(let agent): return agent.studioColor
        case .room: return StudioTheme.amber
        case .roundtable: return StudioTheme.violet
        case .projects: return StudioTheme.sky
        case .members: return StudioTheme.plum
        case .attention: return StudioTheme.orange
        case .quota: return StudioTheme.moss
        case .automation: return StudioTheme.teal
        case .stage: return StudioTheme.rose
        case .settings: return StudioTheme.slate
        }
    }

    var symbol: String {
        switch self {
        case .home: return "house.fill"
        case .chat: return "bubble.left.fill"
        case .room: return "cup.and.saucer.fill"
        case .roundtable: return "person.3.fill"
        case .projects: return "folder.fill"
        case .members: return "person.crop.rectangle.stack.fill"
        case .attention: return "checklist"
        case .quota: return "chart.bar.fill"
        case .automation: return "calendar.badge.clock"
        case .stage: return "person.wave.2.fill"
        case .settings: return "gearshape.fill"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @AppStorage("airoom.ui.appearance") private var appearance = "system"
    @State private var route: StudioRoute = .home
    @State private var selectedProjectID: UUID?
    @State private var memberSheet: MemberID?
    @State private var searchVisible = false
    @State private var searchText = ""
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        if model.stateLoaded { workbench } else { unlocking }
    }

    /// Shown while the encrypted state is being decrypted (possibly waiting for Keychain).
    private var unlocking: some View {
        VStack(spacing: 14) {
            ProgressView()
            Text(t("正在解鎖本機加密資料……", "Unlocking local encrypted data…")).font(.headline)
            Text(t("如果 macOS 詢問 AIROOMNative 是否可以使用鑰匙圈中的資料，請選「永遠允許」。資料解鎖前不會寫入任何東西。",
                   "If macOS asks whether AIROOMNative may use data in your keychain, choose \"Always Allow\". Nothing is written until it unlocks."))
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 420)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(StudioTheme.sidebar(scheme))
    }

    private var workbench: some View {
        HStack(spacing: 0) {
            sidebar.frame(width: 263)
            Rectangle().fill(StudioTheme.border(scheme)).frame(width: 1)
            VStack(spacing: 0) {
                topBar
                Divider().overlay(StudioTheme.border(scheme))
                if let release = model.availableUpdate {
                    UpdateBanner(release: release)
                    Divider().overlay(StudioTheme.border(scheme))
                }
                Group {
                    switch route {
                    case .home:
                        StudioDashboard(onChat: { route = .chat($0) },
                                        onProject: { route = .projects },
                                        onRoom: { route = .room },
                                        onMembers: { route = .members },
                                        onMemberCard: { memberSheet = $0 },
                                        onSettings: { route = .settings })
                    case .chat(let agent): ChatView(agent: agent)
                    case .room: RoomView()
                    case .roundtable: RoundtableView()
                    case .projects: ProjectRoomView(initialProjectID: selectedProjectID)
                    case .members: MembersView()
                    case .quota: StudioQuotaView()
                    case .automation: AutomationView()
                    case .stage: LiveStageView(stage: model.stage)
                    case .attention: StudioAttentionView(onOpen: openAttention)
                    case .settings: SettingsView()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .background(StudioTheme.background(scheme))
        }
        .background(StudioTheme.background(scheme))
        .tint(StudioTheme.orange)
        .buttonStyle(.studioSecondary)
        .preferredColorScheme(appearance == "light" ? .light : appearance == "dark" ? .dark : nil)
        .sheet(item: $memberSheet) { member in
            StudioMemberSheet(agent: member, onDesk: { route = .chat(member); memberSheet = nil },
                              onManage: { route = .members; memberSheet = nil })
                .environmentObject(model)
                .frame(minWidth: 420, idealWidth: 520, minHeight: 380)
        }
        .alert(t("工作台提醒", "Workbench Notice"), isPresented: Binding(
            get: { model.statusMessage != nil },
            set: { if !$0 { model.statusMessage = nil } }
        )) {
            Button(t("確定", "OK")) { model.statusMessage = nil }
        } message: {
            Text(model.statusMessage ?? "")
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button { route = .home } label: {
                HStack(spacing: 11) {
                    StudioBrandMark(size: 36)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("AIROOM").font(.system(size: 17, weight: .heavy, design: .rounded)).tracking(1.6)
                            .foregroundStyle(StudioTheme.ink(scheme))
                        Text(t("我們的 AI 工作室", "Our AI Studio")).font(.caption2).foregroundStyle(.secondary)
                    }
                    Spacer()
                }.padding(.horizontal, 16).padding(.top, 16).padding(.bottom, 14)
                    .studioSidebarHitTarget()
            }.buttonStyle(.plain)
            Rectangle().fill(StudioTheme.hairline(scheme)).frame(height: 1).padding(.horizontal, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    groupHeading(t("團隊", "TEAM"), action: { route = .members }, icon: "person.crop.rectangle.stack")
                    ForEach(model.visibleAgents) { agent in
                        let member = model.archive.members.first(where: { $0.agent == agent })
                        HStack(spacing: 0) {
                            Button { memberSheet = agent } label: {
                                HStack(spacing: 9) {
                                    StudioAvatar(agent: agent, size: 33, running: model.busyAgents.contains(agent))
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(member?.name ?? agent.shortName).font(.subheadline.weight(.semibold))
                                            .lineLimit(1)
                                        // Idle members show their routine for this part of the day (a setting, not real work).
                                        TimelineView(.periodic(from: .now, by: 300)) { context in
                                            Text(model.busyAgents.contains(agent)
                                                 ? t("正在工作", "Working")
                                                 : (member?.enabled == false ? t("已停用", "Disabled")
                                                    : member.flatMap { MemberLife.status($0, at: context.date) } ?? t("空閒中", "Available")))
                                                .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                        }
                                    }
                                    Spacer(minLength: 0)
                                    StudioQuotaCompact(quota: model.quota(of: agent))
                                }
                                .studioSidebarHitTarget()
                            }.buttonStyle(.plain)
                            Button { route = .chat(agent) } label: {
                                Image(systemName: "bubble.left").font(.system(size: 13))
                                    .frame(width: 27, height: 30)
                                    // A plain button only takes clicks on drawn pixels: without this,
                                    // a click inside the bubble's empty middle did nothing.
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .help(t("前往成員工位", "Open member desk"))
                            .accessibilityLabel(t("開啟", "Open") + " \(agent.shortName)")
                        }
                        // Room between the selection line at the left edge and the avatar.
                        .padding(.leading, 9)
                        .studioSelected(route == .chat(agent), accent: agent.studioColor, scheme: scheme)
                    }
                    sidebarDivider
                    navRow(.room, title: t("茶歇室", "Lounge"), subtitle: t("大家一起聊聊", "Team conversation"), icon: "cup.and.saucer.fill")
                    navRow(.roundtable, title: t("圓桌", "Roundtable"), subtitle: t("獨立比稿與比較", "Independent proposals"), icon: "person.3.fill")
                    navRow(.quota, title: t("用量與額度", "Usage & Quotas"), subtitle: t("官方訂閱配額", "Official subscription limits"), icon: "chart.bar.fill")
                    navRow(.automation, title: t("排程與直播", "Automation & Live"), subtitle: t("定時任務與連續對話", "Schedules and co-hosting"), icon: "calendar.badge.clock")
                    navRow(.stage, title: t("直播舞台", "Live Stage"), subtitle: t("語音與 VTube Studio", "Voice & VTube Studio"), icon: "person.wave.2.fill")
                    navRow(.attention, title: t("待你處理", "Needs Attention"), subtitle: attentionCount > 0 ? "\(attentionCount) \(t("項待確認", "items"))" : t("目前沒有待辦", "All clear"), icon: "checklist", badge: attentionCount)
                    sidebarDivider
                    groupHeading(t("專案", "PROJECTS"), action: { route = .projects }, icon: "plus")
                    navRow(.projects, title: t("專案工作區", "Project Workspace"), subtitle: t("查看全部專案", "All projects"), icon: "folder.fill")
                    ForEach(model.archive.projectFlows.prefix(8)) { project in
                        Button { model.selectedFlowID = project.id; selectedProjectID = project.id; route = .projects } label: {
                            HStack(spacing: 8) {
                                Image(systemName: "folder.fill").foregroundStyle(StudioTheme.amber).font(.caption)
                                Text(project.title).font(.subheadline).lineLimit(1)
                                Spacer()
                                // Orange dot: the project is waiting for you (plan, next round or acceptance).
                                Circle().fill(model.attentionItems.contains { $0.target == .project(project.id) } ? StudioTheme.orange : .clear)
                                    .frame(width: 6, height: 6)
                            }.padding(.horizontal, 12).padding(.vertical, 7)
                                .studioSidebarHitTarget()
                        }.buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 11).padding(.vertical, 10)
            }
            Rectangle().fill(StudioTheme.hairline(scheme)).frame(height: 1)
            VStack(spacing: 3) {
                navRow(.members, title: t("管理人物卡", "Manage Members"), subtitle: "", icon: "person.crop.rectangle.stack.fill")
                navRow(.settings, title: t("設定與連接", "Settings & Connections"), subtitle: "", icon: "gearshape.fill")
            }.padding(.horizontal, 11).padding(.vertical, 8)
            HStack(spacing: 7) {
                Circle().fill(model.storageAvailable ? StudioTheme.moss : .red).frame(width: 7, height: 7)
                    .shadow(color: (model.storageAvailable ? StudioTheme.moss : .red).opacity(0.6), radius: 3)
                Text(model.storageAvailable ? t("本機資料已加密", "Local data encrypted") : t("儲存功能受限", "Storage unavailable"))
                Spacer()
                Text("ARM64").font(.system(size: 9, weight: .bold, design: .monospaced))
                    .padding(.horizontal, 5).padding(.vertical, 2)
                    .background(StudioTheme.subtleFill(scheme), in: Capsule())
            }
            .font(.caption2).foregroundStyle(.secondary)
            .padding(.horizontal, 16).padding(.top, 2).padding(.bottom, 12)
        }
        .background {
            // Lamplight pooling at the top of the sidebar.
            ZStack(alignment: .top) {
                StudioTheme.sidebar(scheme)
                RadialGradient(colors: [StudioTheme.amber.opacity(scheme == .dark ? 0.10 : 0.14), .clear],
                               center: .topLeading, startRadius: 10, endRadius: 320)
            }
        }
    }

    private var attentionCount: Int { model.attentionItems.count }

    private var sidebarDivider: some View {
        Rectangle().fill(StudioTheme.hairline(scheme)).frame(height: 1)
            .padding(.vertical, 9).padding(.horizontal, 9)
    }

    private func openAttention(_ target: AttentionItem.Target) {
        switch target {
        case .room(let id): model.selectRoom(id); route = .room
        case .roundtable(let id): model.selectedRoundtableID = id; route = .roundtable
        case .project(let id):
            if model.archive.projectFlows.contains(where: { $0.id == id }) { model.selectedFlowID = id }
            selectedProjectID = id; route = .projects
        case .settings: route = .settings
        case .member(let agent): route = .chat(agent)
        case .memberCard(let agent): model.focusedMember = agent; route = .members
        }
    }

    private func groupHeading(_ title: String, action: @escaping () -> Void, icon: String) -> some View {
        HStack {
            Text(title).font(.system(size: 10.5, weight: .bold, design: .rounded)).tracking(1.6).foregroundStyle(.secondary)
            Spacer()
            Button(action: action) {
                Image(systemName: icon).font(.system(size: 11))
                    .frame(width: 22, height: 22).contentShape(Rectangle())
            }
                .buttonStyle(.plain).foregroundStyle(.secondary)
        }.padding(.horizontal, 9).padding(.top, 12).padding(.bottom, 5)
    }

    private func navRow(_ destination: StudioRoute, title: String, subtitle: String, icon: String, badge: Int = 0) -> some View {
        Button { route = destination } label: {
            HStack(spacing: 10) {
                StudioIconTile(symbol: icon, tint: destination.hue, size: 26)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.subheadline.weight(route == destination ? .semibold : .medium))
                        .foregroundStyle(StudioTheme.ink(scheme))
                    if !subtitle.isEmpty { Text(subtitle).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
                }
                Spacer(minLength: 0)
                if badge > 0 {
                    Text("\(badge)")
                        .font(.system(size: 10, weight: .bold, design: .rounded)).monospacedDigit()
                        .foregroundStyle(.white)
                        .padding(.horizontal, 6).frame(minWidth: 18, minHeight: 18)
                        .background(StudioTheme.accentGradient, in: Capsule())
                }
            }.padding(.horizontal, 8).padding(.vertical, subtitle.isEmpty ? 5 : 6)
                .studioSidebarHitTarget()
        }
        .buttonStyle(.plain)
        .studioSelected(route == destination, accent: destination.hue, scheme: scheme)
    }

    private var topBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "house").foregroundStyle(.secondary)
            Text(t("AI 工作室", "AI Studio")).foregroundStyle(.secondary)
            Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).foregroundStyle(.tertiary)
            StudioIconTile(symbol: route.symbol, tint: route.hue, size: 20)
            Text(routeTitle).font(.system(.subheadline, design: .rounded).weight(.semibold))
                .foregroundStyle(StudioTheme.ink(scheme))
            Spacer()
            if searchVisible {
                TextField(t("搜尋導覽", "Find a view"), text: $searchText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 165)
                    .onSubmit { jumpToSearch() }
                Button { searchVisible = false; searchText = "" } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
            } else {
                Button { searchVisible = true } label: {
                    Image(systemName: "magnifyingglass")
                        .frame(width: 28, height: 28)
                        .background(StudioTheme.subtleFill(scheme), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                        .contentShape(Rectangle())
                }
                    .buttonStyle(.plain)
                    .keyboardShortcut("k", modifiers: .command)
                    .help(t("快速導覽 (⌘K)", "Quick navigation (⌘K)"))
            }
            Divider().frame(height: 17)
            Image(systemName: "lock.shield.fill").foregroundStyle(StudioTheme.moss)
                .help(t("加密儲存與機密檔案保護", "Encrypted storage and sensitive file protection"))
        }
        .font(.subheadline)
        .padding(.horizontal, 22).frame(height: 52)
        .background(StudioTheme.surface(scheme).opacity(scheme == .dark ? 0.7 : 0.85))
    }

    private var routeTitle: String {
        switch route {
        case .home: return t("首頁", "Home")
        case .chat(let agent): return agent.shortName
        case .room: return t("茶歇室", "Lounge")
        case .roundtable: return t("圓桌", "Roundtable")
        case .projects: return t("專案工作區", "Projects")
        case .members: return t("成員管理", "Members")
        case .quota: return t("用量與額度", "Usage & Quotas")
        case .attention: return t("待你處理", "Needs Attention")
        case .automation: return t("排程與直播", "Automation & Live")
        case .stage: return t("直播舞台", "Live Stage")
        case .settings: return t("設定與連接", "Settings")
        }
    }
    private func jumpToSearch() {
        let key = searchText.lowercased()
        if key.contains("claude") { route = .chat(.claude) }
        else if key.contains("codex") { route = .chat(.codex) }
        else if key.contains("專案") || key.contains("project") { route = .projects }
        else if key.contains("茶") || key.contains("room") || key.contains("lounge") { route = .room }
        else if key.contains("圓") || key.contains("round") { route = .roundtable }
        else if key.contains("舞台") || key.contains("vtube") || key.contains("stage") || key.contains("tts") { route = .stage }
        else if key.contains("排程") || key.contains("schedule") || key.contains("live") { route = .automation }
        else if key.contains("設定") || key.contains("setting") { route = .settings }
        else if key.contains("成員") || key.contains("member") { route = .members }
        else if key.contains("待") || key.contains("attention") { route = .attention }
        else { route = .home }
        searchVisible = false
        searchText = ""
    }
}

private struct StudioSelectionModifier: ViewModifier {
    var selected: Bool
    var accent: Color
    var scheme: ColorScheme
    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        content
            .background {
                if selected {
                    shape.fill(StudioTheme.surface(scheme))
                        .shadow(color: StudioTheme.shadow(scheme), radius: 4, y: 1.5)
                        .overlay(shape.strokeBorder(StudioTheme.border(scheme).opacity(0.8), lineWidth: 1))
                }
            }
            .overlay(alignment: .leading) {
                if selected { Capsule().fill(accent).frame(width: 3, height: 20).offset(x: -1).allowsHitTesting(false) }
            }
    }
}

private extension View {
    /// In a plain-style Button, transparent spacers do not receive clicks by default.
    /// Expand the label to the sidebar width and make its entire rectangle hittable.
    func studioSidebarHitTarget() -> some View {
        frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
    }

    func studioSelected(_ selected: Bool, accent: Color, scheme: ColorScheme) -> some View {
        modifier(StudioSelectionModifier(selected: selected, accent: accent, scheme: scheme))
    }
}
