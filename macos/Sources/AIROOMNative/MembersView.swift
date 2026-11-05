import SwiftUI
import AIROOMCore

struct MembersView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @State private var selected: MemberID = .claude
    @State private var editingCrop = false
    @State private var importing = false
    @State private var personaFile: PersonaFile?
    @State private var confirmingReset = false
    @State private var confirmingRemove = false
    @State private var adding = false
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
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 170, maximum: 260), spacing: 16, alignment: .top)],
                          alignment: .leading, spacing: 16) {
                    ForEach(model.memberIDs) { agent in
                        let name = model.archive.members.first(where: { $0.agent == agent })?.name ?? agent.shortName
                        Button { selected = agent } label: {
                            VStack(alignment: .leading, spacing: 8) {
                                StudioPortrait(agent: agent)
                                    .frame(height: 190).clipped()
                                Text(name).font(.headline)
                                let hidden = model.card(agent)?.cardHidden == true
                                Text(agent.executor.rawValue + (hidden ? t(" · 人物卡已隱藏", " · Card hidden") : ""))
                                    .font(.caption).foregroundStyle(.secondary)
                                if let motto = model.card(agent)?.motto, !motto.isEmpty {
                                    Text("「\(motto)」").font(.caption).foregroundStyle(.secondary).lineLimit(2)
                                }
                            }
                            .padding(12)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 15))
                            .overlay(RoundedRectangle(cornerRadius: 15)
                                .strokeBorder(selected == agent ? agent.studioColor : StudioTheme.border(scheme), lineWidth: selected == agent ? 2 : 1))
                        }
                        .buttonStyle(.plain)
                    }
                    Button { adding = true } label: {
                        VStack(spacing: 10) {
                            Image(systemName: "person.crop.circle.badge.plus").font(.system(size: 34, weight: .light))
                            Text(t("新增成員", "Add member")).font(.headline)
                            Text(t("可套用 DS、Gemini、Kimi、GLM 等形象", "Start from DS, Gemini, Kimi, GLM and more"))
                                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                        }
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 250)
                        .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 15))
                        .overlay(RoundedRectangle(cornerRadius: 15).strokeBorder(StudioTheme.border(scheme), style: StrokeStyle(lineWidth: 1, dash: [5, 4])))
                    }
                    .buttonStyle(.plain)
                    .disabled(!model.storageAvailable)
                }
                if let member = current {
                    StudioPanel {
                        VStack(alignment: .leading, spacing: 16) {
                            HStack {
                                StudioAvatar(agent: selected, size: 42)
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(t("編輯人物卡", "Edit Member Card")).font(.headline)
                                    Text(member.executor.rawValue).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Button {
                                    editingCrop = true
                                } label: {
                                    Label(t("調整頭像", "Adjust Avatar"), systemImage: "crop.rotate")
                                }
                                .disabled(!model.storageAvailable)
                                Toggle(t("隱藏人物卡", "Hide card"), isOn: memberBinding(member).cardHidden)
                                    .toggleStyle(.switch)
                                    .help(t("只是不顯示在首頁和側欄；成員照常參與聊天和工作。", "Only leaves the card off the home page and sidebar; the member still takes part."))
                                Toggle(t("啟用成員", "Enable Member"), isOn: memberBinding(member).enabled)
                                    .toggleStyle(.switch)
                            }
                            Divider()
                            HStack(alignment: .top, spacing: 14) {
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(t("顯示名稱", "Display Name")).font(.subheadline.weight(.semibold))
                                    TextField(t("顯示名稱", "Display Name"), text: memberBinding(member).name)
                                        .textFieldStyle(.roundedBorder)
                                }
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(t("怎麼稱呼你", "What they call you")).font(.subheadline.weight(.semibold))
                                    TextField(t("留空就叫你「\(model.archive.settings.ownerName)」", "Empty: \(model.archive.settings.ownerName)"),
                                              text: memberBinding(member).userTitle)
                                        .textFieldStyle(.roundedBorder)
                                }
                            }
                            VStack(alignment: .leading, spacing: 6) {
                                Text(t("一句話介紹", "One-line intro")).font(.subheadline.weight(.semibold))
                                TextField(t("會顯示在團隊卡片上", "Shown on the team cards"), text: memberBinding(member).motto)
                                    .textFieldStyle(.roundedBorder)
                            }
                            MemberColorPicker(member: member, color: memberBinding(member).color)
                            MemberExecutorPicker(member: member)
                            VStack(alignment: .leading, spacing: 6) {
                                HStack {
                                    Text(t("人物性格與分工提示", "Persona & Role Instructions")).font(.subheadline.weight(.semibold))
                                    Spacer()
                                    Button {
                                        if let file = PersonaFile.read(for: member.executor) { personaFile = file }
                                        else { model.statusMessage = t("沒有找到 \(PersonaFile.location(for: member.executor).path)，或檔案是空的。",
                                                                       "\(PersonaFile.location(for: member.executor).path) was not found or is empty.") }
                                    } label: {
                                        Label(t("從 \(PersonaFile.location(for: member.executor).lastPathComponent) 匯入…", "Import from \(PersonaFile.location(for: member.executor).lastPathComponent)…"),
                                              systemImage: "square.and.arrow.down")
                                    }
                                    .buttonStyle(.borderless).font(.caption)
                                }
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
                            MemberDefaultsPicker(member: member)
                            Divider()
                            HStack {
                                Button(role: .destructive) { confirmingReset = true } label: {
                                    Label(t("恢復預設資料", "Restore defaults"), systemImage: "arrow.counterclockwise")
                                }
                                .disabled(!model.storageAvailable)
                                if !member.agent.isBuiltIn {
                                    Button(role: .destructive) { confirmingRemove = true } label: {
                                        Label(t("刪除成員", "Remove member"), systemImage: "trash")
                                    }
                                    .disabled(!model.storageAvailable || model.busyAgents.contains(member.agent))
                                }
                                Spacer()
                                Text(t("修改會直接儲存，下一次呼叫生效。", "Changes save as you type and apply from the next call."))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                    StudioPanel { MemberArtworkEditor(member: member, editingCrop: $editingCrop) }
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
        .sheet(item: $personaFile) { file in
            PersonaImportSheet(file: file) {
                guard var updated = current else { return }
                updated.persona = file.text
                model.updateMember(updated)
                model.statusMessage = t("已匯入 \(file.path)。", "Imported \(file.path).")
            }
            .environmentObject(model)
        }
        .confirmationDialog(t("把 \(current?.name ?? "") 恢復成預設資料？", "Restore \(current?.name ?? "") to defaults?"),
                            isPresented: $confirmingReset) {
            Button(t("恢復預設資料", "Restore defaults"), role: .destructive) { model.resetMember(selected) }
            Button(t("取消", "Cancel"), role: .cancel) {}
        } message: {
            Text(t("人設、稱呼、介紹、代表色、預設模型、記憶、日常生活和上傳的圖片都會清除；啟用狀態保留。",
                   "Persona, address, intro, color, default model, memories, daily life and uploaded pictures are cleared; enabled stays."))
        }
        .confirmationDialog(t("刪除 \(current?.name ?? "")？", "Remove \(current?.name ?? "")?"), isPresented: $confirmingRemove) {
            Button(t("刪除成員", "Remove member"), role: .destructive) {
                let agent = selected
                model.removeMember(agent)
                if model.card(agent) == nil { selected = .claude }
            }
            Button(t("取消", "Cancel"), role: .cancel) {}
        } message: {
            Text(t("人物卡和 TA 的工位話題會一起刪除，無法復原。茶歇室、圓桌和專案裡 TA 說過的話會保留。",
                   "The card and their desk topics are deleted for good. What they said in rooms, roundtables and projects stays."))
        }
        .sheet(isPresented: $adding) {
            AddMemberSheet { agent in selected = agent }.environmentObject(model)
        }
        .sheet(isPresented: $editingCrop) {
            if let member = current {
                if let source = model.avatarSource(member.agent) {
                    AvatarCropEditor(agent: member.agent, source: source) { newCrop in
                        guard var updated = model.card(member.agent) else { return }
                        if source.isUploadedAvatar { updated.avatarImageCrop = newCrop } else { updated.avatarCrop = newCrop }
                        model.updateMember(updated)
                    }
                    .environmentObject(model)
                }
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
                    Label(t("從 \(member.executor.shortName) 匯入", "Import from \(member.executor.shortName)"), systemImage: "square.and.arrow.down")
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
    let agent: MemberID
    @State private var sources: [MemoryImportSource] = []
    @State private var source: String?
    @State private var candidates: [MemoryCandidate] = []
    @State private var picked: Set<Int> = []
    @State private var error: String?
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(t("匯入 \(agent.executor.shortName) 的記憶", "Import \(agent.executor.shortName) memories")).font(.title3.weight(.semibold))
            Text(agent.executor == .codex
                 ? t("只讀取 ~/.codex/memories/memory_summary.md 中關於你的段落（使用者畫像、偏好）。",
                     "Only the sections about you in ~/.codex/memories/memory_summary.md are read.")
                 : agent.executor == .deepseek
                 ? t("DeepSeek Harness 沒有可以匯入的記憶檔。", "DeepSeek Harness has no memory files to import.")
                 : agent.executor == .antigravity
                 ? t("Antigravity 沒有可以匯入的記憶檔。", "Antigravity has no memory files to import.")
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
                .frame(height: min(168, CGFloat(sources.count) * 28))
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
        .onAppear { sources = MemoryImport.sources(for: agent.executor) }
        .onChange(of: source) { _, id in
            candidates = []; picked = []; error = nil
            guard let id else { return }
            do {
                candidates = try MemoryImport.candidates(for: agent.executor, source: id)
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

// MARK: Card details

/// The member's color: a few swatches, any color, or back to the built-in one.
private struct MemberColorPicker: View {
    @EnvironmentObject private var model: WorkbenchModel
    let member: MemberCard
    @Binding var color: String
    private static let swatches = ["#d9692f", "#7b6fd6", "#3c66d9", "#2b8a98", "#a35bdb", "#c2456b", "#3f8a5c", "#b8862b", "#5e6b7a"]
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(t("代表色", "Color")).font(.subheadline.weight(.semibold))
            HStack(spacing: 8) {
                ForEach(Self.swatches, id: \.self) { hex in
                    Button { color = hex } label: {
                        Circle().fill(Color(hex: hex) ?? .gray).frame(width: 22, height: 22)
                            .overlay(Circle().strokeBorder(.primary.opacity(color.lowercased() == hex ? 0.8 : 0), lineWidth: 2).padding(-3))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(t("代表色 \(hex)", "Color \(hex)"))
                }
                ColorPicker("", selection: Binding(get: { member.agent.studioColor },
                                                   set: { if let hex = $0.hexString { color = hex } }), supportsOpacity: false)
                    .labelsHidden()
                    .help(t("自訂顏色", "Custom color"))
                if !color.isEmpty {
                    Button(t("用回原本顏色", "Use the original color")) { color = "" }.buttonStyle(.borderless).font(.caption)
                }
            }
        }
    }
}

/// Which model and effort this member uses when a topic or room doesn't choose.
private struct MemberDefaultsPicker: View {
    @EnvironmentObject private var model: WorkbenchModel
    let member: MemberCard
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    private func save(_ change: (inout MemberCard) -> Void) {
        guard var card = model.card(member.agent) else { return }
        change(&card)
        model.updateMember(card)
    }

    var body: some View {
        let choices = model.modelChoices(for: member.agent)
        let current = member.defaultModel
        let chosen = current.isEmpty ? choices.first(where: \.isDefault) : choices.first(where: { $0.id == current })
        let efforts = chosen?.efforts ?? (member.executor == .claude ? ModelCatalog.claudeEfforts : [])
        VStack(alignment: .leading, spacing: 6) {
            Text(t("這位成員預設的模型與思考程度", "This member's default model and effort")).font(.subheadline.weight(.semibold))
            HStack(spacing: 8) {
                Picker(t("模型", "Model"), selection: Binding(get: { current }, set: { value in
                    save { card in
                        card.defaultModel = value
                        let allowed = (value.isEmpty ? choices.first(where: \.isDefault) : choices.first(where: { $0.id == value }))?.efforts ?? []
                        if !card.defaultEffort.isEmpty, !allowed.isEmpty, !allowed.contains(card.defaultEffort) { card.defaultEffort = "" }
                    }
                })) {
                    Text(t("跟隨 CLI 設定", "Follow the CLI") + (choices.first(where: \.isDefault).map { "（\($0.title)）" } ?? "")).tag("")
                    ForEach(choices) { choice in Text(choice.title).tag(choice.id) }
                    if !current.isEmpty && !choices.contains(where: { $0.id == current }) { Text(current).tag(current) }
                }
                .labelsHidden().frame(width: 230)
                Button { model.refreshModelCatalogs([member.executor]) } label: {
                    if model.modelCatalogLoading.contains(member.executor) { ProgressView().controlSize(.mini) }
                    else { Image(systemName: "arrow.clockwise") }
                }
                .buttonStyle(.borderless)
                .help(t("重新向官方 CLI 讀取模型清單", "Reload the model list from the official CLI"))
                Picker(t("思考程度", "Effort"), selection: Binding(get: { member.defaultEffort }, set: { value in save { $0.defaultEffort = value } })) {
                    Text(t("跟隨 CLI 設定", "Follow the CLI")).tag("")
                    ForEach(efforts, id: \.self) { Text($0).tag($0) }
                    if !member.defaultEffort.isEmpty && !efforts.contains(member.defaultEffort) { Text(member.defaultEffort).tag(member.defaultEffort) }
                }
                .labelsHidden().frame(width: 140)
                .disabled(efforts.isEmpty && member.defaultEffort.isEmpty)
            }
            Text(t("新話題、茶歇室、圓桌和專案會議室都用這個；話題裡另外選的模型優先。", "Used by new topics, the lounge, roundtables and the project room; a model picked in a topic wins."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// Uploaded portrait and avatar, the portrait's framing, and the avatar crop.
private struct MemberArtworkEditor: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let member: MemberCard
    @Binding var editingCrop: Bool
    @State private var picking = false
    @State private var pickingAvatar = false
    /// The framing being dragged; saved when the slider is released.
    @State private var framing: (scale: Double, x: Double, y: Double)?
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    private var saved: (scale: Double, x: Double, y: Double) {
        (member.portraitScale ?? 1, member.portraitOffset?.x ?? 0, member.portraitOffset?.y ?? 0)
    }

    private func save(_ change: (inout MemberCard) -> Void) {
        guard var card = model.card(member.agent) else { return }
        change(&card)
        model.updateMember(card)
    }

    private func slider(_ title: String, _ key: WritableKeyPath<(scale: Double, x: Double, y: Double), Double>,
                        range: ClosedRange<Double>, format: (Double) -> String) -> some View {
        let value = (framing ?? saved)[keyPath: key]
        return VStack(alignment: .leading, spacing: 3) {
            HStack { Text(title).font(.caption); Spacer(); Text(format(value)).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
            Slider(value: Binding(get: { value }, set: { new in var f = framing ?? saved; f[keyPath: key] = new; framing = f }),
                   in: range) { editing in
                guard !editing, let f = framing else { return }
                save { card in
                    card.portraitScale = abs(f.scale - 1) < 0.005 ? nil : f.scale
                    card.portraitOffset = abs(f.x) < 0.002 && abs(f.y) < 0.002 ? nil : PortraitOffset(x: f.x, y: f.y)
                }
                framing = nil
            }
            .tint(member.agent.studioColor)
        }
    }

    var body: some View {
        let shown = framing ?? saved
        VStack(alignment: .leading, spacing: 12) {
            Text(t("立繪與頭像", "Portrait & avatar")).font(.headline)
            HStack(alignment: .top, spacing: 18) {
                StudioPortrait(agent: member.agent, preview: (shown.scale, PortraitOffset(x: shown.x, y: shown.y)))
                    .frame(width: 150, height: 220)
                    .background(member.agent.studioColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 8) {
                        Button { pickingAvatar = false; picking = true } label: {
                            Label(member.portrait == nil ? t("上傳立繪…", "Upload portrait…") : t("更換立繪…", "Replace portrait…"), systemImage: "photo")
                        }
                        if member.portrait != nil {
                            Button(t("用回原版立繪", "Use the original portrait")) {
                                save { $0.portrait = nil; $0.portraitScale = nil; $0.portraitOffset = nil; $0.avatarCrop = nil }
                            }
                        }
                    }
                    HStack(spacing: 8) {
                        StudioAvatar(agent: member.agent, size: 44)
                        Button { pickingAvatar = true; picking = true } label: {
                            Label(member.avatar == nil ? t("上傳單獨頭像…", "Upload separate avatar…") : t("更換頭像…", "Replace avatar…"), systemImage: "person.crop.circle")
                        }
                        if member.avatar != nil {
                            Button(t("移除頭像", "Remove avatar")) { save { $0.avatar = nil; $0.avatarImageCrop = nil } }
                        }
                        Button { editingCrop = true } label: { Label(t("調整頭像…", "Adjust avatar…"), systemImage: "crop.rotate") }
                            .disabled(model.avatarSource(member.agent) == nil)
                    }
                    slider(t("立繪大小", "Portrait size"), \.scale, range: MemberCard.portraitScaleRange) { "\(Int(($0 * 100).rounded()))%" }
                    slider(t("左右位置", "Horizontal"), \.x, range: -0.5...0.5) { String(format: "%+.0f%%", $0 * 100) }
                    slider(t("上下位置", "Vertical"), \.y, range: -0.5...0.5) { String(format: "%+.0f%%", $0 * 100) }
                    if member.portraitScale != nil || member.portraitOffset != nil {
                        Button(t("立繪位置恢復預設", "Reset portrait framing")) { save { $0.portraitScale = nil; $0.portraitOffset = nil } }
                            .buttonStyle(.borderless).font(.caption)
                    }
                }
            }
            Text(t("圖片會加密複製到 AIROOM 的資料夾，原檔不會被改動。支援 PNG、JPG、WebP，8 MB 以內。沒有單獨頭像時，頭像從立繪裁切。",
                   "Pictures are copied, encrypted, into AIROOM's folder; the originals stay untouched. PNG, JPG or WebP up to 8 MB. Without a separate avatar, the avatar is cut from the portrait."))
                .font(.caption).foregroundStyle(.secondary)
        }
        .disabled(!model.storageAvailable)
        .fileImporter(isPresented: $picking, allowedContentTypes: [.png, .jpeg, .webP]) { result in
            guard case .success(let url) = result, let stored = model.storeMemberImage(url) else { return }
            if pickingAvatar { save { $0.avatar = stored; $0.avatarImageCrop = nil } }
            else { save { $0.portrait = stored; $0.portraitScale = nil; $0.portraitOffset = nil; $0.avatarCrop = nil } }
        }
    }
}

/// A CLI's instruction file is not always a persona: show it whole before it replaces anything.
private struct PersonaImportSheet: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.dismiss) private var dismiss
    let file: PersonaFile
    let onImport: () -> Void
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(t("匯入前先看看", "Check before importing")).font(.title3.weight(.semibold))
            Text(file.path).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
            Text(t("確認這是一段人設（身分、語氣、稱呼）。如果是工作規則或工具說明就不要匯入，否則成員可能照著去做。匯入會取代目前的人設。",
                   "Make sure this is a persona (identity, voice, how to address you). Don't import work rules or tool instructions, or the member may follow them. Importing replaces the current persona.")
                 + (file.truncated ? t("檔案較長，只保留前 \(MemberCard.personaLimit) 字。", " The file is long; only the first \(MemberCard.personaLimit) characters are kept.") : ""))
                .font(.callout)
            ScrollView {
                Text(file.text).font(.callout.monospaced()).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(10)
            }
            .frame(height: 300)
            .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
            HStack {
                Spacer()
                Button(t("不匯入", "Don't import")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(t("用這段作為人設", "Use as persona")) { onImport(); dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 600)
    }
}

/// Which program a member runs on. Changing it starts their CLI conversations afresh.
struct MemberExecutorPicker: View {
    @EnvironmentObject private var model: WorkbenchModel
    let member: MemberCard
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(t("使用的程式", "Runs on")).font(.subheadline.weight(.semibold))
            Picker("", selection: Binding(get: { member.executor }, set: { model.setExecutor($0, for: member.agent) })) {
                ForEach(Executor.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 440)
            .disabled(!model.storageAvailable || model.busyAgents.contains(member.agent))
            Text(note).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var note: String {
        if let planned = member.look?.plannedProgram {
            return t("這個形象原本是給 \(planned) 的；AIROOM 接上 \(planned) 之前，先由上面選的程式代跑。換程式後，TA 的對話會從頭開始（會帶上完整的前情）。",
                     "This look is meant for \(planned); until AIROOM supports it, the program above runs it. Switching programs starts their conversations afresh (with full context).")
        }
        return t("同一個程式可以給好幾位成員用，額度也一起算。換程式後，TA 的對話會從頭開始（會帶上完整的前情）。",
                 "Several members can share a program and its quota. Switching programs starts their conversations afresh (with full context).")
    }
}

/// A new member: pick a look, a program and a name.
struct AddMemberSheet: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme
    let added: (MemberID) -> Void
    @State private var template: MemberTemplate? = MemberTemplate.named("deepseek")
    @State private var executor: Executor = MemberTemplate.named("deepseek")?.suggestedExecutor ?? .claude
    @State private var name = "DS"
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(t("新增成員", "Add member")).font(.title3.weight(.semibold))
            Text(t("先選一個形象（之後都能改），再選 TA 用哪個程式。", "Pick a look (all editable later), then the program they run on."))
                .font(.caption).foregroundStyle(.secondary)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 96), spacing: 10)], spacing: 10) {
                ForEach(MemberTemplate.all) { option in tile(option) }
                tile(nil)
            }
            HStack(spacing: 14) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(t("名字", "Name")).font(.subheadline.weight(.semibold))
                    TextField(t("名字", "Name"), text: $name).textFieldStyle(.roundedBorder).frame(width: 200)
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text(t("使用的程式", "Runs on")).font(.subheadline.weight(.semibold))
                    Picker("", selection: $executor) { ForEach(Executor.allCases) { Text($0.rawValue).tag($0) } }
                        .pickerStyle(.segmented).labelsHidden().frame(width: 380)
                }
            }
            if let planned = template?.plannedProgram {
                StudioNotice(message: t("\(template?.name ?? "") 原本是給 \(planned) 的形象。AIROOM 還沒接上 \(planned)，先由 \(executor.rawValue) 代跑；接上之後可以在人物卡改過去。",
                                        "\(template?.name ?? "") is meant for \(planned). AIROOM doesn't run \(planned) yet, so \(executor.rawValue) stands in; switch on the card once it does."))
            }
            HStack {
                Spacer()
                Button(t("取消", "Cancel")) { dismiss() }
                Button(t("新增", "Add")) {
                    if let agent = model.addMember(template: template, executor: executor, name: name) { added(agent) }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(22)
        .frame(width: 620)
    }

    private func tile(_ option: MemberTemplate?) -> some View {
        let chosen = option?.id == template?.id
        let color = option.flatMap { Color(hex: $0.color) } ?? .secondary
        return Button {
            template = option
            name = option?.name ?? ""
            executor = option?.suggestedExecutor ?? .claude
        } label: {
            VStack(spacing: 6) {
                ZStack {
                    Circle().fill(color.opacity(0.15))
                    if let art = option?.art, let image = StudioAsset.image(art),
                       let crop = PortraitCropLayout.make(diameter: 56, sourceWidth: Double(image.size.width),
                                                          sourceHeight: Double(image.size.height), anchor: option!.face) {
                        Image(nsImage: image).resizable().interpolation(.high)
                            .frame(width: CGFloat(crop.imageWidth), height: CGFloat(crop.imageHeight))
                            .position(x: CGFloat(crop.centerX), y: CGFloat(crop.centerY))
                    } else {
                        Text(option.map { String($0.name.prefix(1)) } ?? "+")
                            .font(.system(size: 24, weight: .bold, design: .rounded)).foregroundStyle(color)
                    }
                }
                .frame(width: 56, height: 56).clipShape(Circle())
                Text(option?.name ?? t("空白", "Blank")).font(.caption.weight(.semibold))
                Text(option?.plannedProgram ?? (option == nil ? t("自己設定", "Your own") : option!.suggestedExecutor.rawValue))
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(8)
            .frame(maxWidth: .infinity)
            .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(chosen ? color : StudioTheme.border(scheme), lineWidth: chosen ? 2 : 1))
        }
        .buttonStyle(.plain)
    }
}
