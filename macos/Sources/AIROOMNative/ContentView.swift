import SwiftUI
import AIROOMCore

private enum StudioRoute: Hashable {
    case home, chat(MemberID), room, roundtable, projects, members, attention, quota, automation, live, settings
}

private enum SidebarItem: Hashable {
    case route(StudioRoute), project(UUID)
}

struct ContentView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage("airoom.ui.appearance") private var appearance = "system"
    @State private var route: StudioRoute = .home
    @State private var selectedProjectID: UUID?
    @State private var memberSheet: MemberID?
    @State private var searchVisible = false
    @State private var searchText = ""
    @FocusState private var searchFocused: Bool
    @State private var sidebarSelection: SidebarItem? = .route(.home)
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
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 220, ideal: 250, max: 340)
        } detail: {
            // Laid out at exactly the column's size. Without this, expanding a DisclosureGroup
            // in a page made NavigationSplitView grow to the page's full height, pushing the
            // whole window's content past its edges (reproduced; an HStack shell did not do it).
            GeometryReader { column in
            VStack(spacing: 0) {
                if let release = model.availableUpdate {
                    UpdateBanner(release: release)
                    Divider()
                }
                ZStack {
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
                    case .live: LiveRoomView()
                    case .attention: StudioAttentionView(onOpen: openAttention)
                    case .settings: SettingsView()
                    }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .buttonStyle(.studioSecondary)
                    // A new page fades up into place; the old one just fades (exit faster than entry).
                    .id(route)
                    .transition(.asymmetric(insertion: reduceMotion ? .opacity : .opacity.combined(with: .offset(y: 8)),
                                            removal: .opacity))
                }
                .animation(StudioMotion.easeOut(0.22), value: route)
                .clipped()
            }
            .frame(width: column.size.width, height: column.size.height)
            }
            .background(StudioTheme.background(scheme))
            // The studio's warm accent lives in the content; the sidebar keeps the accent
            // colour people chose in System Settings (HIG: Sidebars, Color › App accent colors).
            .tint(StudioTheme.orange)
            // The window's title names the place, never the app (HIG: Toolbars › Titles).
            .navigationTitle(routeTitle)
            .toolbar { toolbarItems }
        }
        .preferredColorScheme(appearance == "light" ? .light : appearance == "dark" ? .dark : nil)
        .onChange(of: route) { _, new in
            // Keep the sidebar in step when a page is opened from elsewhere (home, search, attention).
            if case .project = sidebarSelection, new == .projects { return }
            sidebarSelection = .route(new)
        }
        .onChange(of: model.settingsRequest) { _, _ in route = .settings }
        .sheet(item: $memberSheet) { member in
            StudioMemberSheet(agent: member, onDesk: { route = .chat(member); memberSheet = nil },
                              onManage: { model.focusedMember = member; route = .members; memberSheet = nil })
                .environmentObject(model)
                .tint(StudioTheme.orange)
                .buttonStyle(.studioSecondary)
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

    /// A standard source list: system material, accent-coloured symbols, native selection and badges.
    private var sidebar: some View {
        List(selection: sidebarBinding) {
            Label(t("首頁", "Home"), systemImage: "house").tag(SidebarItem.route(.home))
            Section {
                ForEach(model.visibleAgents) { agent in
                    memberRow(agent).tag(SidebarItem.route(.chat(agent)))
                }
            } header: {
                sectionHeader(t("團隊", "Team"), action: { route = .members },
                              icon: "person.crop.rectangle.stack", help: t("管理人物卡", "Manage members"))
            }
            Section {
                Label(t("茶歇室", "Lounge"), systemImage: "cup.and.saucer").tag(SidebarItem.route(.room))
                Label(t("圓桌", "Roundtable"), systemImage: "bubble.left.and.bubble.right").tag(SidebarItem.route(.roundtable))
                Label(t("待你處理", "Needs Attention"), systemImage: "checklist")
                    .badge(attentionCount)
                    .tag(SidebarItem.route(.attention))
                Label(t("用量與額度", "Usage & Quotas"), systemImage: "chart.bar").tag(SidebarItem.route(.quota))
                Label(t("直播間", "Live Room"), systemImage: "dot.radiowaves.left.and.right")
                    .badge(model.liveActive ? Text("LIVE") : nil)
                    .tag(SidebarItem.route(.live))
                Label(t("排程與自動對話", "Schedules & Auto Chat"), systemImage: "calendar.badge.clock").tag(SidebarItem.route(.automation))
            }
            Section {
                Label(t("專案工作區", "Project Workspace"), systemImage: "folder").tag(SidebarItem.route(.projects))
                ForEach(model.archive.projectFlows.prefix(8)) { project in
                    HStack(spacing: 6) {
                        Label(project.title, systemImage: "doc.text").lineLimit(1)
                        Spacer(minLength: 0)
                        // The project is waiting for you (plan, next round or acceptance).
                        if model.attentionItems.contains(where: { $0.target == .project(project.id) }) {
                            Circle().fill(.tint).frame(width: 6, height: 6)
                                .accessibilityLabel(t("待你處理", "Needs you"))
                        }
                    }
                    .tag(SidebarItem.project(project.id))
                }
            } header: {
                sectionHeader(t("專案", "Projects"), action: { route = .projects },
                              icon: "plus", help: t("新增或開啟專案", "New or open a project"))
            }
            Section {
                Label(t("管理人物卡", "Manage Members"), systemImage: "person.crop.rectangle.stack").tag(SidebarItem.route(.members))
                Label(t("設定與連接", "Settings & Connections"), systemImage: "gearshape").tag(SidebarItem.route(.settings))
            }
        }
        .listStyle(.sidebar)
    }

    private func memberRow(_ agent: MemberID) -> some View {
        let member = model.archive.members.first(where: { $0.agent == agent })
        let busy = model.busyAgents.contains(agent)
        return HStack(spacing: 8) {
            StudioAvatar(agent: agent, size: 28, running: busy, pulses: true)
            VStack(alignment: .leading, spacing: 1) {
                Text(member?.name ?? agent.shortName).lineLimit(1)
                // Idle members show their routine for this part of the day (a setting, not real work).
                TimelineView(.periodic(from: .now, by: 300)) { context in
                    Text(busy ? t("正在工作", "Working")
                         : (member?.enabled == false ? t("已停用", "Disabled")
                            : member.flatMap { MemberLife.status($0, at: context.date) } ?? t("空閒中", "Available")))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 0)
            StudioQuotaCompact(quota: model.quota(of: agent))
            Button { memberSheet = agent } label: {
                Image(systemName: "person.text.rectangle")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .help(t("人物卡", "Member card"))
            .accessibilityLabel(t("\(agent.shortName) 的人物卡", "\(agent.shortName)'s card"))
        }
        .contextMenu {
            Button(t("前往成員工位", "Open Member Desk")) { route = .chat(agent) }
            Button(t("人物卡…", "Member Card…")) { memberSheet = agent }
        }
    }

    private func sectionHeader(_ title: String, action: @escaping () -> Void, icon: String, help: String) -> some View {
        HStack {
            Text(title)
            Spacer()
            Button(action: action) { Image(systemName: icon) }
                .buttonStyle(.borderless)
                .help(help)
                .accessibilityLabel(help)
        }
    }

    private var attentionCount: Int { model.attentionItems.count }

    /// Sidebar rows are either a place or one project.
    private var sidebarBinding: Binding<SidebarItem?> {
        Binding(get: { sidebarSelection }, set: { item in
            guard let item else { return }
            sidebarSelection = item
            switch item {
            case .route(let destination): route = destination
            case .project(let id):
                model.selectedFlowID = id; selectedProjectID = id; route = .projects
            }
        })
    }

    private func openAttention(_ target: AttentionItem.Target) {
        switch target {
        case .room(let id):
            if model.liveRoom?.id == id { route = .live } else { model.selectRoom(id); route = .room }
        case .roundtable(let id): model.selectedRoundtableID = id; route = .roundtable
        case .project(let id):
            if model.archive.projectFlows.contains(where: { $0.id == id }) { model.selectedFlowID = id }
            selectedProjectID = id; route = .projects
        case .settings: route = .settings
        case .member(let agent): route = .chat(agent)
        case .memberCard(let agent): model.focusedMember = agent; route = .members
        }
    }

    /// Plain system toolbar items: borderless symbols with tooltips (HIG: Toolbars).
    @ToolbarContentBuilder private var toolbarItems: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            if searchVisible {
                TextField(t("前往…", "Go to…"), text: $searchText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 180)
                    .focused($searchFocused)
                    .onSubmit { jumpToSearch() }
                    .onExitCommand { closeSearch() }
                    .onAppear { searchFocused = true }
                Button { closeSearch() } label: { Label(t("關閉", "Close"), systemImage: "xmark.circle.fill") }
                    .help(t("關閉快速導覽", "Close quick navigation"))
            } else {
                Button { searchVisible = true } label: {
                    Label(t("快速導覽", "Quick Navigation"), systemImage: "magnifyingglass")
                }
                .keyboardShortcut("k", modifiers: .command)
                .help(t("快速導覽 (⌘K)", "Quick navigation (⌘K)"))
            }
        }
        ToolbarItem(placement: .primaryAction) {
            Image(systemName: model.storageAvailable ? "lock.shield" : "exclamationmark.lock")
                .foregroundStyle(model.storageAvailable ? AnyShapeStyle(.secondary) : AnyShapeStyle(Color.red))
                .help(model.storageAvailable
                      ? t("本機資料已加密；機密檔案受保護", "Local data is encrypted; sensitive files are protected")
                      : t("儲存功能受限：資料不會寫入", "Storage unavailable: nothing is being saved"))
                .accessibilityLabel(model.storageAvailable ? t("本機資料已加密", "Local data encrypted") : t("儲存功能受限", "Storage unavailable"))
        }
    }

    private func closeSearch() { searchVisible = false; searchText = "" }

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
        case .automation: return t("排程與自動對話", "Schedules")
        case .live: return t("直播間", "Live Room")
        case .settings: return t("設定與連接", "Settings")
        }
    }
    private func navigate(to text: String) {
        let key = text.lowercased()
        if key.contains("claude") { route = .chat(.claude) }
        else if key.contains("codex") { route = .chat(.codex) }
        else if key.contains("專案") || key.contains("project") { route = .projects }
        else if key.contains("茶") || key.contains("room") || key.contains("lounge") { route = .room }
        else if key.contains("圓") || key.contains("round") { route = .roundtable }
        else if key.contains("直播") || key.contains("舞台") || key.contains("vtube") || key.contains("stage")
                    || key.contains("tts") || key.contains("live") { route = .live }
        else if key.contains("排程") || key.contains("schedule") { route = .automation }
        else if key.contains("設定") || key.contains("setting") { route = .settings }
        else if key.contains("成員") || key.contains("member") { route = .members }
        else if key.contains("待") || key.contains("attention") { route = .attention }
        else { route = .home }
        closeSearch()
    }

    /// Keyboard navigation is instant: it repeats too often to wait on motion.
    private func jumpToSearch() {
        var instant = Transaction()
        instant.disablesAnimations = true
        withTransaction(instant) { navigate(to: searchText) }
    }
}
