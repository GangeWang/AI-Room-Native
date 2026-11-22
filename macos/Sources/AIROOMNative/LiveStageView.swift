import SwiftUI
import AVFoundation
import AIROOMCore

/// Voice + VTube Studio setup for the live co-host show.
struct LiveStageView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @ObservedObject var stage: LiveStageController
    @State private var voices: [AVSpeechSynthesisVoice] = []
    /// Optional rehearsal line per member, e.g. to try pose values.
    @State private var scripts: [MemberID: String] = [:]

    private var settings: LiveStageSettings { model.archive.liveStage }
    private var english: Bool { model.archive.settings.language == .english }
    private func t(_ zh: String, _ en: String) -> String { english ? en : zh }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                StudioPageHeading(eyebrow: "LIVE STAGE", title: t("直播舞台", "Live Stage"),
                    subtitle: t("把 AI 直播的每一句話念出來，並用 VTube Studio 驅動 Live2D 表情與嘴型。一位說完才輪到下一位。",
                                "Speak every co-host line and drive Live2D expressions and lip sync through VTube Studio. One speaker at a time."))
                onAirStrip
                speechPanel
                vtsPanel
                ForEach(model.enabledMemberIDs) { agent in characterPanel(agent) }
                StudioNotice(message: t("AI 只能從固定的 8 種情緒中選擇；App 只觸發你在下方對應、且目前模型確實存在的熱鍵。VTube Studio 授權 Token 只存在本機加密資料中。聲音從系統預設輸出播放，OBS 擷取桌面音訊即可。",
                                        "Agents can only pick one of 8 fixed emotions; the app only fires hotkeys you mapped below that exist on the loaded model. The VTube Studio token stays in the local encrypted state. Audio plays on the default output for OBS desktop capture."))
            }
            .padding(StudioMetrics.page)
            .frame(maxWidth: 980, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .onAppear { if voices.isEmpty { voices = SpeechPlayer.voices() } }
    }

    private var speechPanel: some View {
        StudioPanel {
            VStack(alignment: .leading, spacing: 10) {
                Toggle(isOn: Binding(get: { settings.speechEnabled },
                                     set: { value in model.updateLiveStage { $0.speechEnabled = value } })) {
                    HStack(spacing: 10) {
                        StudioIconTile(symbol: "waveform", tint: StudioTheme.sky, size: 28)
                        Text(t("直播時用語音念出發言（macOS 內建 TTS）", "Speak lines aloud (built-in macOS TTS)"))
                            .font(StudioType.sectionTitle)
                    }
                }
                Text(t("想要更自然的中文聲音：系統設定 → 輔助使用 → 朗讀內容 → 系統聲音 → 管理聲音，下載「美佳（加強版）」等高品質語音後，回到這裡選擇。",
                       "For better voices: System Settings → Accessibility → Spoken Content → System Voice → Manage Voices, download an Enhanced/Premium voice, then pick it below."))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var vtsPanel: some View {
        StudioPanel {
            VStack(alignment: .leading, spacing: 10) {
                Toggle(isOn: Binding(get: { settings.vtsEnabled },
                                     set: { value in model.updateLiveStage { $0.vtsEnabled = value } })) {
                    HStack(spacing: 10) {
                        StudioIconTile(symbol: "person.crop.square.badge.camera", tint: StudioTheme.rose, size: 28)
                        Text(t("連接 VTube Studio（Live2D）", "Connect VTube Studio (Live2D)"))
                            .font(StudioType.sectionTitle)
                    }
                }
                Text(t("每位成員各用一個原生版 VTube Studio（不需要 Steam）。按「開啟 VTube Studio」會依連接埠由小到大一個一個開，開好就自動連線並換上各自的模型。連接埠和 NDI 名稱都依開啟順序決定：先開的是 8001／Live2D Camera NDI5，後開的是 8002／Live2D Camera NDI5 2。第一次要在 VTS 設定開啟「Start API」，兩個實例共用設定，開一次即可。",
                       "Each co-host uses its own native VTube Studio (no Steam needed). \"Open VTube Studio\" starts them one by one, lowest port first, then connects each and puts on its model. Ports and NDI names follow start order: first is 8001 / Live2D Camera NDI5, second is 8002 / Live2D Camera NDI5 2. Turn on \"Start API\" in VTS once; both instances share settings."))
                    .font(.caption).foregroundStyle(.secondary)
                HStack(spacing: 10) {
                    Button { model.launchVTubeStudio() } label: {
                        Label(t("開啟 VTube Studio", "Open VTube Studio"), systemImage: "play.rectangle")
                    }
                    .disabled(!settings.vtsEnabled || stage.launchStatus != nil || model.avatarMembers.isEmpty)
                    if let status = stage.launchStatus {
                        ProgressView().controlSize(.small)
                        Text(status).font(.caption).foregroundStyle(.secondary)
                    }
                }
                if let warning = stage.lastWarning {
                    Label(warning, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange)
                }
            }
        }
    }

    private func vtsControls(_ agent: MemberID, _ character: LiveCharacterStage) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Text(t("VTS 連接埠", "VTS port"))
                TextField("8001", value: Binding(get: { character.vtsPort },
                                                 set: { value in model.updateStageCharacter(agent) { $0.vtsPort = value } }),
                          format: .number.grouping(.never))
                    .frame(width: 80).textFieldStyle(.roundedBorder)
                Button(stage.isConnected(agent) ? t("重新連線", "Reconnect") : t("連線", "Connect")) { model.connectVTubeStudio(agent) }
                    .buttonStyle(.studioPrimary)
                    .disabled(!settings.vtsEnabled || stage.isBusy(agent))
                if stage.isConnected(agent) || stage.isBusy(agent) {
                    Button(t("重新讀取熱鍵", "Reload hotkeys")) { Task { await stage.refreshHotkeys(agent) } }
                    Button(t("中斷", "Disconnect")) { stage.disconnect(agent) }
                }
                Spacer()
                if !character.vtsToken.isEmpty {
                    Button(t("撤銷授權", "Forget token"), role: .destructive) { model.forgetVTubeStudioToken(agent) }
                        .font(.caption)
                }
            }
            connectionStatus(agent)
            modelPicker(agent, character)
        }
    }

    private func modelPicker(_ agent: MemberID, _ character: LiveCharacterStage) -> some View {
        let available = stage.models(agent)
        return HStack(spacing: 10) {
            Picker(t("模型", "Model"), selection: Binding(
                get: { character.vtsModelID },
                set: { id in model.chooseStageModel(agent, available.first(where: { $0.id == id })) })) {
                Text(t("沿用 VTS 目前的模型", "Keep the model VTS has open")).tag("")
                if !character.vtsModelID.isEmpty, !available.contains(where: { $0.id == character.vtsModelID }) {
                    Text((character.vtsModelName.isEmpty ? String(character.vtsModelID.prefix(8)) : character.vtsModelName)
                         + t("（離線）", " (offline)")).tag(character.vtsModelID)
                }
                ForEach(available) { item in Text(item.name).tag(item.id) }
            }
            .frame(maxWidth: 360)
            Text(t("兩個 VTS 共用設定，開啟時會是同一個模型；連線後自動換成這裡選的。",
                   "Both instances share settings and open the same model; this one is put on after connecting."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func connectionStatus(_ agent: MemberID) -> some View {
        switch stage.connection(agent) {
        case .off:
            Label(t("未連線", "Not connected"), systemImage: "circle").font(.caption).foregroundStyle(.secondary)
        case .connecting:
            Label(t("連線中…若 VTube Studio 跳出「\(VTSProtocol.pluginName(for: agent))」授權視窗，請按 Allow",
                    "Connecting… click Allow on the \"\(VTSProtocol.pluginName(for: agent))\" prompt in VTube Studio"),
                  systemImage: "hourglass").font(.caption)
        case .reconnecting(let attempt):
            Label(t("VTube Studio 斷線，自動重連中（第 \(attempt) 次）…", "VTube Studio dropped; reconnecting (try \(attempt))…"),
                  systemImage: "arrow.triangle.2.circlepath").font(.caption).foregroundStyle(.orange)
        case .connected(let name):
            Label(t("已連線 · 模型：", "Connected · model: ") + (name.isEmpty ? t("（未載入）", "(none loaded)") : name)
                  + " · \(stage.hotkeys(agent).count) " + t("個熱鍵", "hotkeys"),
                  systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green)
        case .failed(let message):
            Label(message, systemImage: "xmark.octagon").font(.caption).foregroundStyle(.red)
        }
    }

    private func characterPanel(_ agent: MemberID) -> some View {
        let character = settings.character(agent)
        let name = model.archive.members.first(where: { $0.agent == agent })?.name ?? agent.shortName
        let shape = RoundedRectangle(cornerRadius: StudioMetrics.radiusPanel, style: .continuous)
        return VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 10) {
                    StudioAvatar(agent: agent, size: 34)
                    Text(name).font(StudioType.sectionTitle)
                    Spacer()
                    if stage.speaking == agent {
                        Label(t("發言中", "Speaking"), systemImage: "speaker.wave.2.fill").font(.caption).foregroundStyle(agent.studioColor)
                    }
                    Button { model.rehearseStage(agent, script: scripts[agent] ?? "") } label: { Label(t("試演", "Rehearse"), systemImage: "play.circle") }
                        .disabled(model.liveActive || !settings.performs)
                }
                TextField(t("自訂試演台詞（可留空；可用 [happy]、[pose head=10 arms=1,0] 等標籤）",
                            "Custom rehearsal line (optional; tags like [happy] or [pose head=10 arms=1,0] work)"),
                          text: Binding(get: { scripts[agent] ?? "" }, set: { scripts[agent] = $0 }))
                    .textFieldStyle(.roundedBorder).font(.caption)
                Picker(t("聲音", "Voice"), selection: Binding(get: { character.voiceIdentifier },
                        set: { value in model.updateStageCharacter(agent) { $0.voiceIdentifier = value } })) {
                    Text(t("系統預設", "System default")).tag("")
                    ForEach(voices, id: \.identifier) { voice in
                        Text("\(voice.name) · \(voice.language)\(voice.quality == .default ? "" : " ★")").tag(voice.identifier)
                    }
                }
                HStack(spacing: 18) {
                    slider(t("語速", "Rate"), value: character.speechRate, range: 0.3...0.65) { value in
                        model.updateStageCharacter(agent, persist: false) { $0.speechRate = value }
                    }
                    slider(t("音高", "Pitch"), value: character.pitch, range: 0.5...2.0) { value in
                        model.updateStageCharacter(agent, persist: false) { $0.pitch = value }
                    }
                }
                Divider()
                Toggle(t("這位有自己的 VTube Studio 形象", "This co-host has its own VTube Studio avatar"),
                       isOn: Binding(get: { character.drivesAvatar },
                                     set: { value in model.updateStageCharacter(agent) { $0.drivesAvatar = value } }))
                if character.drivesAvatar {
                    vtsControls(agent, character)
                    if character.aiRig, let status = stage.rigStatus[agent] {
                        Text(status).font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                    Toggle(isOn: Binding(get: { character.aiRig }, set: { model.setStageRig(agent, $0) })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(t("讓 AI 接管骨架", "Let the agent drive the rig"))
                            Text(t("AI 用 [pose …] 標籤直接控制頭部角度、視線、眼睛、眉毛和笑容，跟台詞同步；沒說話時自動眨眼、呼吸、輕微晃動。以 VTS 追蹤參數送出，會取代鏡頭追蹤。",
                                   "The agent poses head, gaze, eyes, brows and smile with [pose …] tags, synced to its words; idles with blinks and breathing between lines. Sent as VTS tracking inputs, replacing camera tracking."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    HStack {
                        Text(t("嘴型參數", "Mouth parameter"))
                        TextField("MouthOpen", text: Binding(get: { character.mouthParameter },
                                  set: { value in model.updateStageCharacter(agent) { $0.mouthParameter = value } }))
                            .frame(width: 160).textFieldStyle(.roundedBorder)
                        Text(t("VTS 預設輸入參數為 MouthOpen", "VTS default input is MouthOpen")).font(.caption).foregroundStyle(.secondary)
                    }
                    emotionGrid(agent, character)
                }
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        // The member's color washes down from the top, like a gel on a stage light.
        .background(alignment: .top) {
            LinearGradient(colors: [agent.studioColor.opacity(stage.speaking == agent ? 0.3 : 0.17), .clear],
                           startPoint: .top, endPoint: .bottom)
                .frame(height: 96)
                .animation(.easeInOut(duration: 0.3), value: stage.speaking == agent)
        }
        .clipShape(shape)
        .studioCard(radius: StudioMetrics.radiusPanel)
        .overlay(shape.strokeBorder(stage.speaking == agent ? agent.studioColor.opacity(0.7) : .clear, lineWidth: 1.5))
    }

    /// Broadcast status at a glance: on air or not, who has the mic, which outputs are armed.
    private var onAirStrip: some View {
        let live = model.liveActive
        let connected = model.enabledMemberIDs.filter { stage.isConnected($0) }.count
        let avatars = model.enabledMemberIDs.filter { settings.character($0).drivesAvatar }.count
        return HStack(spacing: 14) {
            HStack(spacing: 7) {
                Circle().fill(live ? Color.red : Color.white.opacity(0.35)).frame(width: 9, height: 9)
                    .shadow(color: live ? .red : .clear, radius: 5)
                Text(live ? "ON AIR" : "OFF AIR")
                    .font(.system(size: 12, weight: .heavy, design: .rounded)).tracking(1.6)
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 11).padding(.vertical, 6)
            .background(live ? Color.red.opacity(0.75) : Color.white.opacity(0.12), in: Capsule())
            if let speaker = stage.speaking {
                HStack(spacing: 7) {
                    StudioAvatar(agent: speaker, size: 24)
                    Text(t("\(speaker.shortName) 發言中", "\(speaker.shortName) speaking"))
                        .font(.callout.weight(.semibold)).foregroundStyle(.white)
                    Image(systemName: "waveform").symbolEffect(.variableColor.iterative, options: .repeating)
                        .foregroundStyle(speaker.studioColor)
                }
            } else {
                Text(live ? t("等待下一句", "Waiting for the next line") : t("直播尚未開始，到茶歇室開一場 AI 直播", "Not live. Start an AI live show in the Lounge"))
                    .font(.callout).foregroundStyle(.white.opacity(0.75)).lineLimit(1)
            }
            Spacer(minLength: 8)
            stripBadge(icon: "waveform", text: t("語音", "Voice"), on: settings.speechEnabled)
            stripBadge(icon: "person.crop.square.badge.camera", text: "VTS \(connected)/\(avatars)", on: settings.vtsEnabled && connected > 0)
        }
        .padding(.horizontal, 16).padding(.vertical, 13)
        .background {
            RoundedRectangle(cornerRadius: StudioMetrics.radiusPanel, style: .continuous)
                .fill(LinearGradient(colors: [Color(red: 0.16, green: 0.11, blue: 0.14), Color(red: 0.10, green: 0.08, blue: 0.11)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(alignment: .leading) {
                    RadialGradient(colors: [StudioTheme.rose.opacity(live ? 0.45 : 0.22), .clear],
                                   center: .leading, startRadius: 0, endRadius: 260)
                        .clipShape(RoundedRectangle(cornerRadius: StudioMetrics.radiusPanel, style: .continuous))
                }
                .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
        }
        .overlay(RoundedRectangle(cornerRadius: StudioMetrics.radiusPanel, style: .continuous)
            .strokeBorder(Color.white.opacity(0.08), lineWidth: 1))
        .environment(\.colorScheme, .dark)
    }

    private func stripBadge(icon: String, text: String, on: Bool) -> some View {
        HStack(spacing: 5) {
            Image(systemName: icon).font(.system(size: 11, weight: .semibold))
            Text(text).font(.caption.weight(.semibold)).monospacedDigit()
        }
        .foregroundStyle(on ? Color.white : Color.white.opacity(0.45))
        .padding(.horizontal, 9).padding(.vertical, 5)
        .background(on ? StudioTheme.moss.opacity(0.55) : Color.white.opacity(0.08), in: Capsule())
    }

    private func emotionGrid(_ agent: MemberID, _ character: LiveCharacterStage) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Text(t("情緒 → 熱鍵", "Emotion → hotkey")).font(.subheadline.weight(.semibold))
                Button(t("依名稱自動對應", "Match by name")) { model.autoMapStageEmotions(agent) }
                    .disabled(stage.hotkeys(agent).isEmpty)
                    .help(t("熱鍵名稱含有 happy／開心 這類字就自動對應", "Maps hotkeys whose names contain happy, sad…"))
                Spacer()
            }
            emotionPickers(agent, character)
        }
    }

    private func emotionPickers(_ agent: MemberID, _ character: LiveCharacterStage) -> some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 210), spacing: 12)], alignment: .leading, spacing: 8) {
            ForEach(LiveEmotion.allCases.filter { $0 != .neutral }) { emotion in
                Picker(english ? emotion.rawValue : emotion.zhTitle, selection: Binding(
                    get: { character.emotionHotkeys[emotion.rawValue] ?? "" },
                    set: { value in model.updateStageCharacter(agent) { $0.emotionHotkeys[emotion.rawValue] = value.isEmpty ? nil : value } })) {
                    Text(t("不觸發", "None")).tag("")
                    // Keep a mapped ID selectable even while VTS is offline.
                    if let mapped = character.emotionHotkeys[emotion.rawValue],
                       !stage.hotkeys(agent).contains(where: { $0.id == mapped }) {
                        Text(t("（離線）", "(offline) ") + mapped.prefix(8)).tag(mapped)
                    }
                    ForEach(stage.hotkeys(agent)) { hotkey in Text("\(hotkey.name) · \(hotkey.type)").tag(hotkey.id) }
                }
            }
        }
        .font(.caption)
    }

    private func slider(_ title: String, value: Double, range: ClosedRange<Double>,
                        set: @escaping (Double) -> Void) -> some View {
        HStack {
            Text(title).frame(width: 40, alignment: .leading)
            Slider(value: Binding(get: { value }, set: set), in: range,
                   onEditingChanged: { editing in if !editing { model.saveLiveStage() } })
            Text(String(format: "%.2f", value)).monospacedDigit().font(.caption).frame(width: 36)
        }
    }
}
