import SwiftUI
import UniformTypeIdentifiers
import AIROOMCore

struct ChatView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let agent: AgentKind
    @State private var importing = false
    @State private var renaming = false
    @State private var renameText = ""
    @State private var dropTargeted = false

    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private var memberName: String { model.archive.members.first(where: { $0.agent == agent })?.name ?? agent.shortName }
    private var busy: Bool { model.busyAgents.contains(agent) }
    private var topic: Conversation? { model.activeTopic(for: agent) }
    private var draft: Binding<String> {
        Binding(get: { model.chatDrafts[agent] ?? "" },
                set: { model.chatDrafts[agent] = $0; model.draftChanged(agent) })
    }
    private var pendingAttachments: [ChatAttachment] { model.chatAttachments[agent] ?? [] }
    private var canSend: Bool {
        !busy && (!draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !pendingAttachments.isEmpty)
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            transcript
            Divider()
            composer
        }
        .onAppear { if agent == .codex { model.loadCodexModels() } }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.image, .pdf, .text, .sourceCode, .json, .data],
                      allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { model.addAttachments(urls, to: agent) }
        }
        .alert(t("重新命名話題", "Rename topic"), isPresented: $renaming) {
            TextField(t("話題名稱", "Topic name"), text: $renameText)
            Button(t("儲存", "Save")) { if let id = topic?.id { model.renameTopic(id, title: renameText) } }
            Button(t("取消", "Cancel"), role: .cancel) {}
        }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 12) {
            StudioAvatar(agent: agent, size: 40, running: busy)
            VStack(alignment: .leading, spacing: 3) {
                Text(memberName).font(.headline)
                Text(t("成員工位 · ", "Member Desk · ") + agent.rawValue)
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if busy { ProgressView().controlSize(.small) }
            topicMenu
            Button { model.openVendorDesktop(for: agent) } label: {
                Label(agent == .codex ? t("在 Codex App 開新工作階段", "New session in Codex App") : t("在 Claude App 開新工作階段", "New session in Claude App"), systemImage: "arrow.up.forward.app")
            }
            .help(t("僅開啟官方桌面 App；不傳送 AIROOM 對話、Token 或工作紀錄。若設定了工作目錄，將交由 App 自行確認存取。", "Opens the official desktop app only. Does not copy AIROOM conversations or credentials. An explicitly configured workspace may be offered to the app."))
            Button { model.newTopic(for: agent) } label: { Label(t("開新話題", "New Topic"), systemImage: "plus.bubble") }
                .disabled(busy)
        }
        .padding(.horizontal, 21).padding(.vertical, 13)
    }

    private var topicMenu: some View {
        Menu {
            Section(t("話題（舊話題已歸檔，可隨時翻回）", "Topics (archived topics can be reopened)")) {
                ForEach(model.topics(for: agent)) { item in
                    Button { model.openTopic(item.id) } label: {
                        Label("\(item.title) · \(item.updatedAt.formatted(date: .abbreviated, time: .shortened))",
                              systemImage: item.archived ? "archivebox" : "checkmark.circle.fill")
                    }
                    .disabled(!item.archived)
                }
            }
            Divider()
            Button { renameText = topic?.title ?? ""; renaming = true } label: {
                Label(t("重新命名目前話題…", "Rename current topic…"), systemImage: "pencil")
            }
        } label: {
            Label(topic?.title ?? TopicNaming.defaultTitle, systemImage: "text.bubble")
                .lineLimit(1).frame(maxWidth: 220)
        }
        .disabled(busy)
    }

    // MARK: Transcript

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 18) {
                    if model.messages(for: agent).isEmpty { emptyState }
                    ForEach(model.messages(for: agent)) { message in messageRow(message).id(message.id) }
                }
                .padding(.horizontal, 25).padding(.vertical, 22)
                .frame(maxWidth: 940)
                .frame(maxWidth: .infinity)
            }
            .onChange(of: model.messages(for: agent).count) { _, _ in
                if let last = model.messages(for: agent).last?.id {
                    withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(last, anchor: .bottom) }
                }
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            StudioAvatar(agent: agent, size: 66)
            Text(t("找 \(memberName) 聊聊吧", "Start a conversation with \(memberName)"))
                .font(.title2.bold())
            Text(t("可以附上圖片、PDF 或文字檔，也能直接把檔案拖進來。對話在本機加密保存。",
                   "Attach images, PDFs or text files, or drag them in. History is encrypted on this device."))
                .foregroundStyle(.secondary)
        }
        .padding(.top, 105)
        .frame(maxWidth: .infinity)
    }

    private func messageRow(_ message: ChatMessage) -> some View {
        HStack(alignment: .top, spacing: 12) {
            if message.role == .user { Spacer(minLength: 42) }
            if message.role == .assistant { StudioAvatar(agent: agent, size: 34, running: busy) }
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 8) {
                    Text(message.role == .user ? t("你", "You") : memberName)
                        .font(.caption.weight(.semibold))
                    if let used = message.modelUsed {
                        Text(used).font(.caption2.monospaced()).foregroundStyle(.secondary)
                            .padding(.horizontal, 6).padding(.vertical, 1)
                            .background(.secondary.opacity(0.12), in: Capsule())
                            .help(t("這一輪實際使用的模型", "Model actually used for this reply"))
                    }
                    Spacer()
                    Text(message.createdAt, style: .time)
                        .font(.caption2).foregroundStyle(.secondary)
                }
                if !message.attachments.isEmpty { attachmentStrip(message.attachments, removable: false) }
                if !message.body.isEmpty {
                    Text(message.body)
                        .textSelection(.enabled)
                        .font(.system(size: 14))
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(14)
            .frame(maxWidth: 660, alignment: .leading)
            .background(message.role == .user
                ? agent.studioColor.opacity(0.12) : StudioTheme.surface(scheme),
                in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14)
                .strokeBorder(StudioTheme.border(scheme).opacity(0.85)))
            if message.role == .assistant { Spacer(minLength: 42) }
        }
    }

    private func attachmentStrip(_ items: [ChatAttachment], removable: Bool) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(items) { item in
                    AttachmentChip(attachment: item, onRemove: {
                        model.removeDraftAttachment(item.id, from: agent)
                    }, removable: removable)
                }
            }
        }
    }

    // MARK: Composer

    private var composer: some View {
        VStack(spacing: 9) {
            if !pendingAttachments.isEmpty { attachmentStrip(pendingAttachments, removable: true) }
            TextEditor(text: draft)
                .font(.body)
                .frame(minHeight: 72, maxHeight: 130)
                .scrollContentBackground(.hidden)
                .padding(8)
                .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(dropTargeted ? agent.studioColor : StudioTheme.border(scheme), lineWidth: dropTargeted ? 2 : 1))
            HStack(spacing: 10) {
                Button { importing = true } label: { Image(systemName: "plus.circle") }
                    .buttonStyle(.borderless).disabled(busy)
                    .help(t("附上圖片、PDF 或文字檔", "Attach images, PDFs or text files"))
                modelPickers
                Spacer()
                Image(systemName: "lock.shield").foregroundStyle(.secondary)
                Text(t("唯讀 · 不自動重送", "Read-only · No auto-retry"))
                    .font(.caption).foregroundStyle(.secondary)
                if busy {
                    Button(t("停止", "Stop"), role: .destructive) { model.stop(agent: agent) }
                }
                Button { model.submit(to: agent) } label: {
                    Label(t("傳送", "Send"), systemImage: "arrow.up")
                }
                .keyboardShortcut(.return, modifiers: .command)
                .buttonStyle(.borderedProminent)
                .disabled(!canSend)
            }
        }
        .padding(.horizontal, 23).padding(.vertical, 15)
        .background(StudioTheme.secondary(scheme))
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(\.isFileURL)
            guard !files.isEmpty, !busy else { return false }
            model.addAttachments(files, to: agent)
            return true
        } isTargeted: { dropTargeted = $0 }
    }

    private var modelPickers: some View {
        let choices = model.modelChoices(for: agent)
        let current = topic?.model ?? ""
        let efforts = choices.first(where: { $0.id == current })?.efforts
            ?? (agent == .claude ? ModelCatalog.claudeEfforts : Array(Set(choices.flatMap(\.efforts))).sorted())
        return HStack(spacing: 6) {
            Picker(t("模型", "Model"), selection: Binding(get: { current },
                                                          set: { model.setTopicModel(agent, model: $0) })) {
                Text(t("預設模型", "Default model")).tag("")
                ForEach(choices) { choice in Text(choice.title).tag(choice.id) }
                if !current.isEmpty && !choices.contains(where: { $0.id == current }) { Text(current).tag(current) }
            }
            .labelsHidden().frame(width: 140)
            Picker(t("思考程度", "Effort"), selection: Binding(get: { topic?.effort ?? "" },
                                                            set: { model.setTopicEffort(agent, effort: $0) })) {
                Text(t("預設思考", "Default effort")).tag("")
                ForEach(efforts, id: \.self) { Text($0).tag($0) }
            }
            .labelsHidden().frame(width: 115)
        }
        .disabled(busy)
        .help(t("只套用在這個話題", "Applies to this topic only"))
    }
}

/// Attachment pill; images show a thumbnail decrypted on demand.
private struct AttachmentChip: View {
    @EnvironmentObject private var model: WorkbenchModel
    let attachment: ChatAttachment
    let onRemove: () -> Void
    let removable: Bool
    @State private var thumbnail: NSImage?

    init(attachment: ChatAttachment, onRemove: @escaping () -> Void, removable: Bool) {
        self.attachment = attachment; self.onRemove = onRemove; self.removable = removable
    }

    var body: some View {
        HStack(spacing: 6) {
            if let thumbnail {
                Image(nsImage: thumbnail).resizable().scaledToFill()
                    .frame(width: 34, height: 34).clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Image(systemName: attachment.kind == .pdf ? "doc.richtext" : attachment.kind == .text ? "doc.text" : "photo")
                    .frame(width: 22)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(attachment.fileName).font(.caption).lineLimit(1)
                Text(ByteCountFormatter.string(fromByteCount: Int64(attachment.byteCount), countStyle: .file))
                    .font(.caption2).foregroundStyle(.secondary)
            }
            if removable {
                Button(action: onRemove) { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.borderless).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .frame(maxWidth: 220, alignment: .leading)
        .background(.secondary.opacity(0.1), in: RoundedRectangle(cornerRadius: 9))
        .task(id: attachment.id) {
            guard attachment.kind == .image, thumbnail == nil, let data = model.attachmentData(attachment.id) else { return }
            thumbnail = NSImage(data: data)
        }
    }
}
