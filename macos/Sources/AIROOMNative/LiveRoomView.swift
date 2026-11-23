import SwiftUI
import AIROOMCore

/// The 直播間: the AI live show in its own room, with the stage settings (voice, VTube Studio)
/// on a second tab. The lounge's topics never mix with the show's transcript.
struct LiveRoomView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var tab: Tab = .show
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    private enum Tab: Hashable { case show, stage }

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $tab) {
                Text(t("直播", "Show")).tag(Tab.show)
                Text(t("舞台設定", "Stage")).tag(Tab.stage)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 240)
            .padding(.vertical, 10)
            Divider()
            switch tab {
            case .show: show
            case .stage: LiveStageView(stage: model.stage)
            }
        }
        .onAppear { model.ensureLiveRoom() }
    }

    // MARK: Show

    private var show: some View {
        VStack(spacing: 0) {
            controls
            Divider()
            transcript
            Divider()
            composer
        }
    }

    private var settings: LiveShowSettings { model.archive.liveShow }
    private var hosts: [MemberID] { model.liveRoom?.participants ?? [] }

    private var controls: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                onAirBadge
                TextField(t("直播主題", "Show topic"), text: Binding(
                    get: { settings.topic },
                    set: { value in model.updateLiveConfig { $0.topic = String(value.prefix(2_000)) } }))
                    .textFieldStyle(.roundedBorder)
                    .disabled(model.liveActive)
                if model.liveActive {
                    Button(t("停止直播", "Stop Show"), role: .destructive) { model.stopLiveShow() }
                        .buttonStyle(.studioPrimary)
                } else {
                    Button { model.startLiveShow() } label: {
                        Label(t("開始直播", "Go Live"), systemImage: "play.fill")
                    }
                    .buttonStyle(.studioPrimary)
                    .disabled(model.roomRunning || settings.topic.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                              || !hosts.contains(where: { agent in model.card(agent)?.enabled == true }))
                    .help(model.roomRunning ? t("茶歇室正在進行，結束後才能開播", "Wait for the lounge to finish") : "")
                }
            }
            HStack(spacing: 14) {
                Text(t("主持", "Hosts")).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                ForEach(model.enabledMemberIDs) { agent in
                    Toggle(isOn: Binding(get: { hosts.contains(agent) },
                                         set: { model.setLiveParticipant(agent, included: $0) })) {
                        HStack(spacing: 5) {
                            StudioAvatar(agent: agent, size: 20, running: model.liveActive && model.stage.speaking == agent)
                            Text(model.card(agent)?.name ?? agent.shortName).font(.caption)
                        }
                    }
                    .toggleStyle(.checkbox)
                    .disabled(model.liveActive)
                }
                Spacer(minLength: 8)
                Group {
                    Stepper(t("間隔 \(settings.turnDelaySeconds) 秒", "Delay \(settings.turnDelaySeconds)s"),
                            value: Binding(get: { settings.turnDelaySeconds },
                                           set: { value in model.updateLiveConfig { $0.turnDelaySeconds = value } }),
                            in: 0...180, step: 1)
                    Stepper(t("最多 \(settings.maxTurns) 輪", "Up to \(settings.maxTurns) turns"),
                            value: Binding(get: { settings.maxTurns },
                                           set: { value in model.updateLiveConfig { $0.maxTurns = value } }),
                            in: 2...300, step: 2)
                    Stepper(t("最長 \(settings.maxDurationMinutes) 分鐘", "Up to \(settings.maxDurationMinutes) min"),
                            value: Binding(get: { settings.maxDurationMinutes },
                                           set: { value in model.updateLiveConfig { $0.maxDurationMinutes = value } }),
                            in: 10...240, step: 10)
                }
                .font(.caption).monospacedDigit()
                .disabled(model.liveActive)
            }
            Text(model.archive.liveStage.performs
                 ? t("舞台已開啟：每句話會念出並驅動 VTube Studio，念完才輪到下一位。聲音與表情在「舞台設定」。",
                     "Stage is on: lines are spoken and drive VTube Studio; the next host waits for the voice. Voices and expressions are under Stage.")
                 : t("目前只輸出文字。到「舞台設定」開啟語音與 VTube Studio。AI 的台詞會傳送給各自的供應商。",
                     "Text only. Turn on voice and VTube Studio under Stage. Lines go to each host's vendor."))
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 20).padding(.vertical, 14)
        .background(.ultraThinMaterial)
    }

    private var onAirBadge: some View {
        let live = model.liveActive
        return HStack(spacing: 6) {
            Circle().fill(live ? Color.white : Color.secondary).frame(width: 7, height: 7)
            Text(live ? t("直播中 · 第 \(model.liveTurns) 輪", "LIVE · turn \(model.liveTurns)") : "OFF AIR")
                .font(.caption.weight(.semibold)).monospacedDigit()
                .contentTransition(.numericText(value: Double(model.liveTurns)))
        }
        .foregroundStyle(live ? Color.white : Color.secondary)
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(live ? Color.red : StudioTheme.subtleFill(scheme), in: Capsule())
        .animation(StudioMotion.settle, value: live)
        .animation(StudioMotion.settle, value: model.liveTurns)
        .fixedSize()
    }

    private var entries: [RoomEntry] { model.liveRoom?.entries ?? [] }

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    if entries.isEmpty {
                        StudioEmptyState(icon: "dot.radiowaves.left.and.right",
                                         title: t("還沒開播", "Not live yet"),
                                         detail: t("設定主題、勾選主持的成員，然後開始直播。", "Set a topic, pick the hosts, then go live."))
                            .padding(.top, 60)
                    }
                    ForEach(entries) { entry in
                        row(entry).id(entry.id)
                            .transition(StudioMotion.arrival(reduceMotion))
                    }
                }
                .animation(reduceMotion ? StudioMotion.ease() : StudioMotion.settle, value: entries.count)
                .padding(24)
                .frame(maxWidth: 810)
                .frame(maxWidth: .infinity)
            }
            .background(StudioRoomBackdrop())
            .onAppear { if let last = entries.last?.id { proxy.scrollTo(last, anchor: .bottom) } }
            .onChange(of: entries.count) { _, _ in
                if let last = entries.last?.id {
                    withAnimation(reduceMotion ? nil : StudioMotion.easeOut(0.25)) { proxy.scrollTo(last, anchor: .bottom) }
                }
            }
        }
    }

    @ViewBuilder private func row(_ entry: RoomEntry) -> some View {
        if entry.speaker == .system {
            Text(entry.body).font(.caption).foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 12).padding(.vertical, 5)
                .background(StudioTheme.surface(scheme).opacity(0.85), in: Capsule())
                .frame(maxWidth: .infinity)
        } else {
            HStack(alignment: .top, spacing: 10) {
                if let agent = entry.agent {
                    StudioAvatar(agent: agent, size: 30)
                } else {
                    Image(systemName: "person.crop.circle.fill").font(.system(size: 28)).foregroundStyle(StudioTheme.warning)
                }
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(entry.displayName).font(.subheadline.weight(.semibold))
                        if entry.delivery == .inProgress {
                            ProgressView().controlSize(.mini)
                        } else if entry.delivery == .failed || entry.delivery == .interrupted {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                                .help(t("這句沒有完成，不會進入其他主持人的上下文。", "Unfinished; left out of the other hosts' context."))
                        }
                        Spacer()
                        Text(entry.createdAt, style: .time).font(.caption2).foregroundStyle(.secondary)
                    }
                    RichText(entry.body).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(12)
                .studioCard(radius: 12)
                .overlay(alignment: .leading) {
                    // The speaker's colour down the edge, as in the lounge.
                    Rectangle().fill(entry.agent?.studioColor ?? StudioTheme.warning).frame(width: 3)
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                }
            }
        }
    }

    // MARK: Host and audience

    private var composer: some View {
        VStack(alignment: .leading, spacing: 10) {
            if model.liveActive {
                if !model.liveSubtitle.isEmpty {
                    Text(model.liveSubtitle).font(.callout).lineLimit(2)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                HStack(spacing: 8) {
                    TextField(t("以主持人身分插話，下一位發言的 AI 會先照做", "Cut in as the host; the next speaker follows it"),
                              text: $model.liveHostDraft)
                        .onSubmit { model.interjectLive() }
                    Button { model.interjectLive() } label: { Label(t("插話", "Cut In"), systemImage: "megaphone") }
                        .buttonStyle(.studioPrimary)
                        .keyboardShortcut(.return, modifiers: .command)
                        .disabled(model.liveHostDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                HStack(spacing: 8) {
                    TextField(t("觀眾暱稱", "Viewer name"), text: $model.liveAudienceName).frame(width: 120)
                    TextField(t("觀眾留言，下一輪 AI 會看到", "Viewer comment for the next turn"), text: $model.liveAudienceDraft)
                        .onSubmit { model.enqueueLiveAudienceMessage() }
                    Button(t("加入留言", "Post Comment")) { model.enqueueLiveAudienceMessage() }
                        .disabled(model.liveAudienceDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            } else {
                Text(t("開播後可以在這裡插話和加入觀眾留言。一位 AI 可以獨自開台，多位會輪流接話；只在 App 開著時進行，每 12 輪產生一次摘要。",
                       "Once live, cut in and add viewer comments here. One host can run the show alone, or several take turns. Runs only while the app is open; a summary every 12 turns."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            if !settings.summary.isEmpty {
                DisclosureGroup(t("直播摘要（之後每一輪都會帶著它）", "Show summary (sent with every later turn)")) {
                    ScrollView {
                        Text(settings.summary).font(.caption).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 160)
                }
                .font(.caption)
            }
        }
        .textFieldStyle(.roundedBorder)
        .padding(.horizontal, 20).padding(.vertical, 13)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
    }
}
