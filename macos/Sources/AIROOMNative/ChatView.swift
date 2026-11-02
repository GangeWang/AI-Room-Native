import SwiftUI
import AIROOMCore

struct ChatView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    let agent: AgentKind

    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private var memberName: String { model.archive.members.first(where: { $0.agent == agent })?.name ?? agent.shortName }
    private var draft: Binding<String> {
        Binding(get: { model.chatDrafts[agent] ?? "" }, set: { model.chatDrafts[agent] = $0 })
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                StudioAvatar(agent: agent, size: 40, running: model.busyAgents.contains(agent))
                VStack(alignment: .leading, spacing: 3) {
                    Text(memberName).font(.headline)
                    Text(t("成員工位 · ", "Member Desk · ") + agent.rawValue)
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if model.busyAgents.contains(agent) { ProgressView().controlSize(.small) }
                Button { model.openVendorDesktop(for: agent) } label: {
                    Label(agent == .codex ? t("在 Codex App 開新工作階段", "New session in Codex App") : t("在 Claude App 開新工作階段", "New session in Claude App"), systemImage: "arrow.up.forward.app")
                }
                .help(t("僅開啟官方桌面 App；不傳送 AIROOM 對話、Token 或工作紀錄。若設定了工作目錄，將交由 App 自行確認存取。", "Opens the official desktop app only. Does not copy AIROOM conversations or credentials. An explicitly configured workspace may be offered to the app."))
                Button {
                    model.resetConversation(for: agent)
                } label: { Label(t("開新話題", "New Topic"), systemImage: "plus.bubble") }
                    .disabled(model.busyAgents.contains(agent))
            }
            .padding(.horizontal, 21).padding(.vertical, 13)
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 18) {
                        if model.messages(for: agent).isEmpty {
                            VStack(spacing: 16) {
                                StudioAvatar(agent: agent, size: 66)
                                Text(t("找 \(memberName) 聊聊吧", "Start a conversation with \(memberName)"))
                                    .font(.title2.bold())
                                Text(t("這是一個獨立的成員工位，對話在本機加密保存。", "A private member desk. History is encrypted on this device."))
                                    .foregroundStyle(.secondary)
                                StudioNotice(message: t("目前可使用唯讀 CLI 或轉到官方桌面 App 開新工作階段；兩邊的對話尚未自動同步。", "Use the read-only CLI here or open a new session in the official desktop app. Conversations do not sync automatically."))
                                    .frame(maxWidth: 530)
                            }
                            .padding(.top, 105)
                            .frame(maxWidth: .infinity)
                        }
                        ForEach(model.messages(for: agent)) { message in
                            HStack(alignment: .top, spacing: 12) {
                                if message.role == .user { Spacer(minLength: 42) }
                                if message.role == .assistant {
                                    StudioAvatar(agent: agent, size: 34, running: model.busyAgents.contains(agent))
                                }
                                VStack(alignment: .leading, spacing: 7) {
                                    HStack {
                                        Text(message.role == .user ? t("你", "You") : memberName)
                                            .font(.caption.weight(.semibold))
                                        Spacer()
                                        Text(message.createdAt, style: .time)
                                            .font(.caption2).foregroundStyle(.secondary)
                                    }
                                    Text(message.body)
                                        .textSelection(.enabled)
                                        .font(.system(size: 14))
                                        .frame(maxWidth: .infinity, alignment: .leading)
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
                            .id(message.id)
                        }
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
            Divider()
            VStack(spacing: 9) {
                TextEditor(text: draft)
                    .font(.body)
                    .frame(minHeight: 72, maxHeight: 130)
                    .scrollContentBackground(.hidden)
                    .padding(8)
                    .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 12))
                    .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(StudioTheme.border(scheme)))
                HStack(spacing: 10) {
                    Image(systemName: "lock.shield")
                    Text(t("唯讀執行 · 不會自動重送不確定的請求", "Read-only CLI · Uncertain requests are not retried"))
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    if model.busyAgents.contains(agent) {
                        Button(t("停止", "Stop"), role: .destructive) { model.stop(agent: agent) }
                    }
                    Button { model.submit(to: agent) } label: {
                        Label(t("傳送", "Send"), systemImage: "arrow.up")
                    }
                    .keyboardShortcut(.return, modifiers: .command)
                    .buttonStyle(.borderedProminent)
                    .disabled(model.busyAgents.contains(agent) || draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
            .padding(.horizontal, 23).padding(.vertical, 15)
            .background(StudioTheme.secondary(scheme))
        }
    }
}
