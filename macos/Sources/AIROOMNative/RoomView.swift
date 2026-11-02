import SwiftUI
import AIROOMCore

struct RoomView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private var context: SharedContextSnapshot {
        RoomPromptBuilder.snapshot(room: model.activeRoom ?? TeamRoom(), language: lang)
    }

    var body: some View {
        ZStack {
            StudioRoomBackdrop()
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 11) {
                    HStack(spacing: 14) {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(t("共用上下文對話室", "Shared AI Conversation"))
                                .font(.title2.bold())
                            Text(t("同一段歷史供所有成員閱讀；完成的回覆會接續傳給下一位。",
                                   "All agents see the same transcript; each completed reply is available to the next."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Menu {
                            ForEach(model.archive.rooms) { room in
                                Button {
                                    model.selectRoom(room.id)
                                } label: {
                                    Label(room.title, systemImage: room.id == model.activeRoom?.id ? "checkmark.circle.fill" : "text.bubble")
                                }
                            }
                            Divider()
                            Button {
                                model.createRoom()
                            } label: { Label(t("新對話室", "New room"), systemImage: "plus") }
                        } label: {
                            Label(model.activeRoom?.title ?? t("討論室", "Discussion"), systemImage: "rectangle.stack")
                        }
                        .disabled(model.roomRunning || model.liveActive)
                        Button {
                            model.createRoom()
                        } label: {
                            Label(t("新話題", "New topic"), systemImage: "plus.bubble")
                        }
                        .disabled(model.roomRunning || model.liveActive)
                    }
                    HStack(spacing: 12) {
                        Text(t("在場", "Present")).font(.caption.weight(.semibold))
                        ForEach(AgentKind.allCases) { agent in
                            Toggle(isOn: Binding(
                                get: { model.activeRoom?.participants.contains(agent) ?? false },
                                set: { model.setRoomParticipant(agent, included: $0) }
                            )) {
                                HStack(spacing: 6) {
                                    StudioAvatar(agent: agent, size: 26)
                                    Text(agent.shortName).font(.caption)
                                }
                            }
                            .toggleStyle(.checkbox)
                            .disabled(model.roomRunning || model.liveActive)
                        }
                        Spacer()
                        Label("\(context.includedCount) / \(model.roomEntries().count) " + t("則共用紀錄", "shared entries"), systemImage: "text.book.closed")
                            .font(.caption).foregroundStyle(.secondary)
                        if context.omittedEntries > 0 {
                            Label(t("較早 \(context.omittedEntries) 則未傳送", "\(context.omittedEntries) older omitted"), systemImage: "exclamationmark.circle")
                                .font(.caption).foregroundStyle(.orange)
                        }
                    }
                }
                .padding(18)
                .background(StudioTheme.surface(scheme).opacity(0.91))
                // LIVE is a bounded, explicitly started cooperative conversation,
                // not the occasional idle auto-chat rule from the original app.
                DisclosureGroup {
                    VStack(alignment: .leading, spacing: 10) {
                        TextField(t("直播主題", "Show topic"), text: Binding(
                            get: { model.archive.liveShow.topic },
                            set: { value in model.updateLiveConfig { $0.topic = String(value.prefix(2_000)) } }))
                            .textFieldStyle(.roundedBorder).disabled(model.liveActive)
                        HStack(spacing: 16) {
                            Stepper(t("間隔 \(model.archive.liveShow.turnDelaySeconds) 秒", "Delay \(model.archive.liveShow.turnDelaySeconds)s"),
                                value: Binding(get: { model.archive.liveShow.turnDelaySeconds },
                                               set: { value in model.updateLiveConfig { $0.turnDelaySeconds = value } }),
                                in: 15...180, step: 15).disabled(model.liveActive)
                            Stepper(t("最多 \(model.archive.liveShow.maxTurns) 輪", "Up to \(model.archive.liveShow.maxTurns) turns"),
                                value: Binding(get: { model.archive.liveShow.maxTurns },
                                               set: { value in model.updateLiveConfig { $0.maxTurns = value } }),
                                in: 2...300, step: 2).disabled(model.liveActive)
                            Stepper(t("最長 \(model.archive.liveShow.maxDurationMinutes) 分鐘", "Up to \(model.archive.liveShow.maxDurationMinutes) min"),
                                value: Binding(get: { model.archive.liveShow.maxDurationMinutes },
                                               set: { value in model.updateLiveConfig { $0.maxDurationMinutes = value } }),
                                in: 10...240, step: 10).disabled(model.liveActive)
                        }
                        .font(.caption)
                        HStack(spacing: 12) {
                            if model.liveActive {
                                Label(t("直播中 · 第 \(model.liveTurns) 輪", "LIVE · \(model.liveTurns) turns"), systemImage: "dot.radiowaves.left.and.right")
                                    .foregroundStyle(.red)
                                Button(t("停止直播", "Stop live show"), role: .destructive) { model.stopLiveShow() }
                            } else {
                                Button { model.startLiveShow() } label: {
                                    Label(t("開始雙 AI 同台直播", "Start multi-AI show"), systemImage: "play.fill")
                                }.buttonStyle(.borderedProminent).disabled(model.roomRunning)
                            }
                            Spacer()
                            Text(t("需要兩位 AI；僅本機執行期間有效；每 12 輪產生一次摘要。", "Requires 2 agents. Runs only while app is open; summary every 12 turns."))
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                        if model.liveActive {
                            HStack(spacing: 8) {
                                TextField(t("觀眾暱稱", "Viewer name"), text: $model.liveAudienceName).frame(width: 125)
                                TextField(t("插入觀眾留言，下一輪 AI 會看到", "Insert viewer comment for next AI turn"),
                                          text: $model.liveAudienceDraft)
                                    .onSubmit { model.enqueueLiveAudienceMessage() }
                                Button(t("加入聊天室", "Post comment")) { model.enqueueLiveAudienceMessage() }
                                    .disabled(model.liveAudienceDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            }
                            .textFieldStyle(.roundedBorder)
                            if !model.liveSubtitle.isEmpty {
                                Text(model.liveSubtitle).font(.caption).lineLimit(3)
                                    .textSelection(.enabled).foregroundStyle(.secondary)
                            }
                        }
                        Text(t("目前只輸出文字事件，尚未串接 TTS、Live2D、OBS 或直播平台聊天室。AI 回覆會傳送給所選供應商。",
                               "Text events only. TTS, Live2D, OBS and streaming chat ingestion are not connected yet. Messages go to the selected vendors."))
                            .font(.caption2).foregroundStyle(.secondary)
                    }.padding(.vertical, 8)
                } label: {
                    Label(t("雙 AI 同台直播模式", "Multi-AI live co-host mode"), systemImage: "dot.radiowaves.left.and.right")
                        .font(.subheadline.weight(.semibold))
                }
                .padding(.horizontal, 20).padding(.vertical, 9)
                .background(StudioTheme.surface(scheme).opacity(0.93))
                Divider()
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 14) {
                            if model.roomEntries().isEmpty {
                                StudioEmptyState(icon: "cup.and.saucer.fill",
                                    title: t("歡迎來到茶歇室", "Welcome to the Lounge"),
                                    detail: t("選擇在場成員，再提出第一個話題。", "Invite teammates and start the first conversation."))
                                    .padding(.top, 65)
                            }
                            ForEach(model.roomEntries()) { entry in
                                HStack(alignment: .top, spacing: 10) {
                                    if let agent = entry.agent {
                                        StudioAvatar(agent: agent, size: 33)
                                    } else {
                                        Image(systemName: "person.crop.circle.fill")
                                            .font(.system(size: 31)).foregroundStyle(StudioTheme.warning)
                                    }
                                    VStack(alignment: .leading, spacing: 7) {
                                        HStack {
                                            Text(entry.displayName).font(.subheadline.weight(.semibold))
                                            if entry.delivery == .inProgress {
                                                ProgressView().controlSize(.mini)
                                            } else if entry.delivery == .failed || entry.delivery == .interrupted {
                                                Image(systemName: "exclamationmark.triangle.fill")
                                                    .foregroundStyle(.orange)
                                                    .help(t("本次回覆未完成，不會進入其他成員的上下文。", "Unconfirmed response; excluded from other agents' context."))
                                            }
                                            Spacer()
                                            Text(entry.createdAt, style: .time)
                                                .font(.caption2).foregroundStyle(.secondary)
                                        }
                                        Text(entry.body).font(.system(size: 14))
                                            .textSelection(.enabled)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                    .padding(14)
                                    .background(StudioTheme.surface(scheme).opacity(0.96),
                                                in: RoundedRectangle(cornerRadius: 13))
                                    .overlay(RoundedRectangle(cornerRadius: 13)
                                        .strokeBorder(StudioTheme.border(scheme)))
                                }
                                .id(entry.id)
                            }
                        }
                        .padding(24)
                        .frame(maxWidth: 810)
                        .frame(maxWidth: .infinity)
                    }
                    .onChange(of: model.roomEntries().count) { _, _ in
                        if let last = model.roomEntries().last?.id {
                            withAnimation { proxy.scrollTo(last, anchor: .bottom) }
                        }
                    }
                }
                Divider()
                VStack(alignment: .leading, spacing: 10) {
                    TextEditor(text: $model.roomDraft)
                        .frame(height: 78).font(.body).scrollContentBackground(.hidden)
                        .padding(8)
                        .background(StudioTheme.surface(scheme), in: RoundedRectangle(cornerRadius: 12))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(StudioTheme.border(scheme)))
                    HStack(spacing: 10) {
                        Picker(t("回覆成員", "Responders"), selection: $model.roomTarget) {
                            Text(t("所有在場成員", "All present agents")).tag(nil as AgentKind?)
                            ForEach(AgentKind.allCases.filter { model.activeRoom?.participants.contains($0) ?? false }) { agent in
                                Text(agent.shortName).tag(Optional(agent))
                            }
                        }
                        .pickerStyle(.menu)
                        .frame(maxWidth: 245)
                        .disabled(model.roomRunning || model.liveActive)
                        Text(t("會將共用紀錄傳給選定 AI 供應商；僅成功回覆會繼續共享。", "Sends shared history to selected AI vendors; only successful replies propagate."))
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        if model.roomRunning {
                            ProgressView().controlSize(.small)
                            Button(t("停止剩餘發言", "Stop remaining turns"), role: .destructive) { model.stopRoom() }
                        }
                        Button { model.sendToRoom() } label: {
                            Label(t("送出給成員", "Send to team"), systemImage: "arrow.up")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(model.roomRunning || model.liveActive || model.roomDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
                .padding(.horizontal, 21).padding(.vertical, 13)
                .background(StudioTheme.secondary(scheme).opacity(0.95))
            }
        }
    }
}
