import SwiftUI
import AIROOMCore

struct MembersView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @State private var selected: AgentKind = .claude
    @State private var editingCrop = false
    @State private var importing = false
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private func memberBinding(_ member: MemberCard) -> Binding<MemberCard> {
        Binding(
            get: { model.archive.members.first(where: { $0.id == member.id }) ?? member },
            set: { model.updateMember($0) }
        )
    }
    private var current: MemberCard? { model.archive.members.first(where: { $0.agent == selected }) }
    private func takeFocus() {
        if let agent = model.focusedMember { selected = agent; model.focusedMember = nil }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 21) {
                StudioPageHeading(eyebrow: "MEMBERS",
                    title: t("團隊與人物卡", "Team & Member Cards"),
                    subtitle: t("人物形象與個性在這裡設定；登入資料由官方 CLI 管理。", "Customize each member's appearance and persona. Authentication remains with the official CLI."))
                HStack(alignment: .top, spacing: 16) {
                    ForEach(AgentKind.allCases) { agent in
                        let name = model.archive.members.first(where: { $0.agent == agent })?.name ?? agent.shortName
                        Button { selected = agent } label: {
                            VStack(alignment: .leading, spacing: 8) {
                                StudioPortrait(agent: agent)
                                    .frame(height: 190).clipped()
                                Text(name).font(.headline)
                                Text(agent.rawValue).font(.caption).foregroundStyle(.secondary)
                            }
                            .padding(12)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 15))
                            .overlay(RoundedRectangle(cornerRadius: 15)
                                .strokeBorder(selected == agent ? agent.studioColor : StudioTheme.border(scheme), lineWidth: selected == agent ? 2 : 1))
                        }
                        .buttonStyle(.plain)
                    }
                }
                if let member = current {
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 16) {
                            HStack {
                                StudioAvatar(agent: selected, size: 42)
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(t("編輯人物卡", "Edit Member Card")).font(.headline)
                                    Text(selected.rawValue).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Button {
                                    editingCrop = true
                                } label: {
                                    Label(t("調整頭像", "Adjust Avatar"), systemImage: "crop.rotate")
                                }
                                .disabled(!model.storageAvailable)
                                Toggle(t("啟用成員", "Enable Member"), isOn: memberBinding(member).enabled)
                                    .toggleStyle(.switch)
                            }
                            Divider()
                            VStack(alignment: .leading, spacing: 6) {
                                Text(t("顯示名稱", "Display Name")).font(.subheadline.weight(.semibold))
                                TextField(t("顯示名稱", "Display Name"), text: memberBinding(member).name)
                                    .textFieldStyle(.roundedBorder)
                            }
                            VStack(alignment: .leading, spacing: 6) {
                                Text(t("人物性格與分工提示", "Persona & Role Instructions")).font(.subheadline.weight(.semibold))
                                TextEditor(text: memberBinding(member).persona)
                                    .frame(minHeight: 135)
                                    .font(.body).scrollContentBackground(.hidden)
                                    .padding(9)
                                    .background(StudioTheme.secondary(scheme), in: RoundedRectangle(cornerRadius: 10))
                            }
                            VStack(alignment: .leading, spacing: 6) {
                                Text(t("聊天時的發言傾向", "Speaking preference in chat")).font(.subheadline.weight(.semibold))
                                Picker("", selection: Binding(get: { member.talkativeness },
                                                              set: { value in model.updateLife(member.agent) { $0.talkativeness = value } })) {
                                    ForEach(Talkativeness.allCases) { Text($0.title).tag($0) }
                                }
                                .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 280)
                            }
                            StudioNotice(message: t("目前人物卡使用原版立繪作為顯示資源，但不會修改官方 CLI 帳戶。", "Portraits are presentation assets only; member cards do not modify official CLI accounts."))
                        }
                    }
                    StudioPanel { MemoryEditor(member: member, importing: $importing) }
                    StudioPanel { LifeEditor(member: member) }
                }
            }
            .padding(26)
            .frame(maxWidth: 980)
            .frame(maxWidth: .infinity)
        }
        .onAppear { takeFocus() }
        .onChange(of: model.focusedMember) { _, _ in takeFocus() }
        .sheet(isPresented: $importing) {
            MemoryImportSheet(agent: selected).environmentObject(model)
        }
        .sheet(isPresented: $editingCrop) {
            if let member = current {
                AvatarCropEditor(agent: member.agent, initialAnchor: member.avatarCrop) { newCrop in
                    var updated = member
                    updated.avatarCrop = newCrop
                    model.updateMember(updated)
                }
                .environmentObject(model)
            }
        }
    }
}

// MARK: Long-term memory

private struct MemoryEditor: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let member: MemberCard
    @Binding var importing: Bool
    @State private var draft = ""
    @State private var editing: UUID?
    @State private var editText = ""
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private var activeCount: Int { MemberLife.activeText(member.memories).count }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                Text(t("長期記憶", "Long-term memory")).font(.headline)
                Text(t("\(member.memories.filter { !$0.off }.count) 條啟用 · \(activeCount)/\(MemberLife.activeLimit) 字",
                       "\(member.memories.filter { !$0.off }.count) on · \(activeCount)/\(MemberLife.activeLimit) chars"))
                    .font(.caption).foregroundStyle(activeCount > MemberLife.activeLimit * 9 / 10 ? .orange : .secondary)
                Spacer()
                Button { importing = true } label: {
                    Label(t("從 \(member.agent.shortName) 匯入", "Import from \(member.agent.shortName)"), systemImage: "square.and.arrow.down")
                }
            }
            Text(t("啟用的記憶會隨人物設定一起送給這位成員（書桌、茶歇室、圓桌、專案）。關掉的條目保留但不送出。",
                   "Entries that are on are sent with the persona in every room. Entries that are off are kept but not sent."))
                .font(.caption).foregroundStyle(.secondary)
            if !member.memoryProposals.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text(t("\(member.name) 的提議（確認後才會加入）", "Suggested by \(member.name) (added only when kept)"))
                        .font(.subheadline.weight(.semibold))
                    ForEach(member.memoryProposals) { proposal in
                        HStack(alignment: .top, spacing: 10) {
                            VStack(alignment: .leading, spacing: 3) {
                                Text(proposal.text).textSelection(.enabled)
                                if !proposal.origin.isEmpty { Text(proposal.origin).font(.caption2).foregroundStyle(.secondary) }
                            }
                            Spacer()
                            Button(t("保留", "Keep")) { model.resolveProposal(member.agent, id: proposal.id, keep: true) }
                            Button(t("不要", "Drop")) { model.resolveProposal(member.agent, id: proposal.id, keep: false) }
                        }
                    }
                }
                .padding(12)
                .background(member.agent.studioColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
            }
            if member.memories.isEmpty {
                Text(t("還沒有記憶。可以手寫、從官方 CLI 匯入，或在書桌話題選單請成員提議。",
                       "No memories yet. Write one, import from the official CLI, or ask the member to suggest some from a desk topic."))
                    .font(.callout).foregroundStyle(.secondary)
            }
            ForEach(member.memories) { entry in
                HStack(alignment: .top, spacing: 10) {
                    Toggle("", isOn: Binding(get: { !entry.off }, set: { on in
                        model.editMemories(member.agent) { list in
                            if let i = list.firstIndex(where: { $0.id == entry.id }) { list[i].off = !on }
                        }
                    })).toggleStyle(.switch).labelsHidden().controlSize(.small)
                    VStack(alignment: .leading, spacing: 4) {
                        if editing == entry.id {
                            TextField("", text: $editText, axis: .vertical).textFieldStyle(.roundedBorder).lineLimit(2...8)
                            HStack {
                                Button(t("儲存", "Save")) {
                                    if model.updateMemory(member.agent, id: entry.id, text: editText) { editing = nil }
                                }.keyboardShortcut(.defaultAction)
                                Button(t("取消", "Cancel")) { editing = nil }
                            }
                        } else {
                            Text(entry.text).foregroundStyle(entry.off ? .secondary : .primary).textSelection(.enabled)
                        }
                        Text([entry.source.title, entry.origin].filter { !$0.isEmpty }.joined(separator: " · "))
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if editing != entry.id {
                        Button { editText = entry.text; editing = entry.id } label: { Image(systemName: "pencil") }
                            .buttonStyle(.borderless).help(t("編輯", "Edit"))
                        Button { model.editMemories(member.agent) { $0.removeAll { $0.id == entry.id } } } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless).help(t("刪除", "Delete"))
                    }
                }
                Divider()
            }
            HStack(alignment: .top) {
                TextField(t("寫一條要記住的事，例如：喜歡先看結論再看細節", "Something to remember, e.g. prefers the conclusion first"),
                          text: $draft, axis: .vertical)
                    .textFieldStyle(.roundedBorder).lineLimit(1...5)
                Button(t("加入", "Add")) { if model.addMemory(member.agent, text: draft) { draft = "" } }
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .onChange(of: member.agent) { _, _ in editing = nil; draft = "" }
    }
}

private struct MemoryImportSheet: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.dismiss) private var dismiss
    let agent: AgentKind
    @State private var sources: [MemoryImportSource] = []
    @State private var source: String?
    @State private var candidates: [MemoryCandidate] = []
    @State private var picked: Set<Int> = []
    @State private var error: String?
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(t("匯入 \(agent.shortName) 的記憶", "Import \(agent.shortName) memories")).font(.title3.weight(.semibold))
            Text(agent == .codex
                 ? t("只讀取 ~/.codex/memories/memory_summary.md 中關於你的段落（使用者畫像、偏好）。",
                     "Only the sections about you in ~/.codex/memories/memory_summary.md are read.")
                 : t("只讀取 ~/.claude/projects/*/memory/ 裡的記憶檔，不讀登入資料或對話紀錄。",
                     "Only memory files in ~/.claude/projects/*/memory/ are read — never credentials or transcripts."))
                .font(.caption).foregroundStyle(.secondary)
            if sources.isEmpty {
                Text(t("這台 Mac 上沒有找到可以匯入的記憶。", "No importable memory was found on this Mac.")).foregroundStyle(.secondary)
            } else {
                Text(t("來源", "Source")).font(.subheadline.weight(.semibold))
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(sources) { item in
                            Button { source = item.id } label: {
                                HStack {
                                    Image(systemName: source == item.id ? "largecircle.fill.circle" : "circle")
                                    Text(item.label).lineLimit(1).truncationMode(.middle)
                                    Spacer()
                                    Text(t("\(item.count) 條", "\(item.count)")).font(.caption).foregroundStyle(.secondary)
                                }
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .padding(.vertical, 3)
                        }
                    }
                }
                .frame(maxHeight: 150)
            }
            if let error { Text(error).font(.callout).foregroundStyle(.red) }
            if !candidates.isEmpty {
                HStack {
                    Text(t("選擇要加入的條目", "Pick entries to add")).font(.subheadline.weight(.semibold))
                    Spacer()
                    Button(picked.count == candidates.count ? t("全不選", "None") : t("全選", "All")) {
                        picked = picked.count == candidates.count ? [] : Set(candidates.indices)
                    }.buttonStyle(.borderless)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(Array(candidates.enumerated()), id: \.offset) { index, item in
                            Toggle(isOn: Binding(get: { picked.contains(index) },
                                                 set: { if $0 { picked.insert(index) } else { picked.remove(index) } })) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(item.text).lineLimit(6)
                                    Text(item.origin).font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                            .toggleStyle(.checkbox)
                        }
                    }
                }
                .frame(minHeight: 160, maxHeight: 340)
            }
            HStack {
                Spacer()
                Button(t("取消", "Cancel")) { dismiss() }
                Button(t("匯入 \(picked.count) 條", "Import \(picked.count)")) {
                    model.importMemories(agent, picked.sorted().map { candidates[$0] })
                    dismiss()
                }
                .keyboardShortcut(.defaultAction).disabled(picked.isEmpty)
            }
        }
        .padding(22)
        .frame(width: 560)
        .onAppear { sources = MemoryImport.sources(for: agent) }
        .onChange(of: source) { _, id in
            candidates = []; picked = []; error = nil
            guard let id else { return }
            do {
                candidates = try MemoryImport.candidates(for: agent, source: id)
                if candidates.isEmpty { error = t("這個來源沒有可匯入的條目。", "Nothing to import from this source.") }
            } catch { self.error = error.localizedDescription }
        }
    }
}

// MARK: Daily life

private struct LifeEditor: View {
    @EnvironmentObject private var model: WorkbenchModel
    let member: MemberCard
    @State private var quirkDraft = ""
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                Text(t("日常生活", "Daily life")).font(.headline)
                Spacer()
                let period = RoutinePeriod.at(.now)
                Text(t("現在（\(period.title)）：", "Now (\(period.title)): ") + (MemberLife.status(member) ?? t("沒有設定", "not set")))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(t("作息、小習慣和關係是你為角色寫的設定：會顯示成成員狀態、用在閒聊和每日開場；成員會被告知這些不是真實發生的事。",
                   "Routine, habits and relations are character settings: shown as status and used for small talk and the daily greeting. Members are told they are not real events."))
                .font(.caption).foregroundStyle(.secondary)
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                ForEach(RoutinePeriod.allCases) { period in
                    GridRow {
                        Text("\(period.title) \(period.hours.from):00–\(period.hours.to):00").font(.callout).foregroundStyle(.secondary)
                        TextField(period.example, text: Binding(
                            get: { member.routine[period.rawValue] ?? "" },
                            set: { value in model.updateLife(member.agent) { $0.routine[period.rawValue] = value } }))
                            .textFieldStyle(.roundedBorder)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                Text(t("小習慣（最多 \(MemberLife.quirksMax) 個）", "Habits (up to \(MemberLife.quirksMax))")).font(.subheadline.weight(.semibold))
                if !member.quirks.isEmpty {
                    HStack(spacing: 6) {
                        ForEach(member.quirks, id: \.self) { quirk in
                            HStack(spacing: 4) {
                                Text(quirk)
                                Button { model.updateLife(member.agent) { $0.quirks.removeAll { $0 == quirk } } } label: {
                                    Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
                                }.buttonStyle(.borderless)
                            }
                            .padding(.horizontal, 9).padding(.vertical, 4)
                            .background(member.agent.studioColor.opacity(0.14), in: Capsule())
                        }
                    }
                }
                HStack {
                    TextField(t("例如：轉筆", "e.g. spins a pen"), text: $quirkDraft).textFieldStyle(.roundedBorder).frame(maxWidth: 260)
                        .onSubmit(addQuirk)
                    Button(t("加入", "Add"), action: addQuirk)
                        .disabled(quirkDraft.trimmingCharacters(in: .whitespaces).isEmpty || member.quirks.count >= MemberLife.quirksMax)
                }
            }
            let others = model.archive.members.filter { $0.agent != member.agent }
            if !others.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Text(t("和其他成員的關係", "Relations to teammates")).font(.subheadline.weight(.semibold))
                    ForEach(others) { other in
                        HStack {
                            Text(other.name).frame(width: 110, alignment: .leading)
                            TextField(t("例如：老搭檔，常互相吐槽", "e.g. old partners who tease each other"), text: Binding(
                                get: { member.relations.first { $0.agent == other.agent }?.text ?? "" },
                                set: { value in model.updateLife(member.agent) { card in
                                    card.relations.removeAll { $0.agent == other.agent }
                                    card.relations.append(MemberRelation(agent: other.agent, text: value))
                                } }))
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                }
            }
        }
        .onChange(of: member.agent) { _, _ in quirkDraft = "" }
    }

    private func addQuirk() {
        let quirk = quirkDraft.trimmingCharacters(in: .whitespaces)
        guard !quirk.isEmpty, member.quirks.count < MemberLife.quirksMax else { return }
        model.updateLife(member.agent) { $0.quirks.append(quirk) }
        quirkDraft = ""
    }
}
