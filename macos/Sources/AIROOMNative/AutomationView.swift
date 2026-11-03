import SwiftUI
import AIROOMCore

struct AutomationView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @State private var title = ""
    @State private var text = ""
    @State private var kind: ScheduleKind = .daily
    @State private var hour = 12
    @State private var minute = 0
    @State private var onceAt = Date.now.addingTimeInterval(3_600)
    @State private var interval = 60
    @State private var action: ScheduleAction = .reminder
    @State private var target: AgentKind?
    /// The schedule being edited; nil adds a new one.
    @State private var editing: UUID?

    private var english: Bool { model.archive.settings.language == .english }
    private func t(_ zh: String, _ en: String) -> String { english ? en : zh }

    private func describe(_ item: ScheduledActivity) -> String {
        switch item.kind {
        case .daily: return t("每天 ", "Daily ") + String(format: "%02d:%02d", item.hour, item.minute)
        case .once: return t("一次 · ", "Once · ") + (item.onceAt?.formatted(date: .abbreviated, time: .shortened) ?? "—")
        case .interval: return t("每 \(item.intervalMinutes) 分鐘", "Every \(item.intervalMinutes) min")
        }
    }

    private func edit(_ item: ScheduledActivity) {
        editing = item.id
        title = item.name; text = item.text; kind = item.kind; hour = item.hour; minute = item.minute
        onceAt = item.onceAt ?? Date.now.addingTimeInterval(3_600); interval = item.intervalMinutes
        action = item.action; target = item.target
    }

    private func resetForm() {
        editing = nil; title = ""; text = ""; kind = .daily
        onceAt = Date.now.addingTimeInterval(3_600)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                StudioPageHeading(eyebrow: "AUTOMATION", title: t("排程與自動對話", "Schedules & Auto Chat"),
                    subtitle: t("只有 App 執行時才檢查（關掉視窗後在選單列仍會執行）；錯過的觸發不補發。", "Checked only while the app runs (the menu bar keeps it running); missed triggers are never replayed."))
                StudioPanel {
                    VStack(alignment: .leading, spacing: 13) {
                        Text(editing == nil ? t("建立排程", "New schedule") : t("編輯排程", "Edit schedule")).font(.headline)
                        TextField(t("排程名稱", "Task name"), text: $title)
                        Picker(t("類型", "Action"), selection: $action) {
                            Text(t("提醒", "Reminder")).tag(ScheduleAction.reminder)
                            Text(t("讓 AI 在聊天室回應", "AI room message")).tag(ScheduleAction.roomMessage)
                        }
                        .pickerStyle(.segmented)
                        TextField(t("內容或提示詞", "Message or prompt"), text: $text)
                        Picker(t("觸發方式", "Trigger"), selection: $kind) {
                            Text(t("每天", "Daily")).tag(ScheduleKind.daily)
                            Text(t("一次", "Once")).tag(ScheduleKind.once)
                            Text(t("固定間隔", "Interval")).tag(ScheduleKind.interval)
                        }
                        .pickerStyle(.segmented)
                        HStack {
                            switch kind {
                            case .daily:
                                Picker(t("小時", "Hour"), selection: $hour) {
                                    ForEach(0..<24, id: \.self) { value in Text(String(format: "%02d", value)).tag(value) }
                                }.frame(width: 125)
                                Picker(t("分鐘", "Minute"), selection: $minute) {
                                    ForEach(0..<60, id: \.self) { value in Text(String(format: "%02d", value)).tag(value) }
                                }.frame(width: 125)
                            case .once:
                                DatePicker(t("時間", "At"), selection: $onceAt, in: Date.now..., displayedComponents: [.date, .hourAndMinute])
                                    .frame(maxWidth: 300)
                            case .interval:
                                Stepper(t("每 \(interval) 分鐘", "Every \(interval) min"), value: $interval,
                                        in: ScheduledActivity.intervalRange, step: 5)
                                    .frame(maxWidth: 220)
                            }
                            if action == .roomMessage {
                                Picker(t("回覆成員", "Responder"), selection: $target) {
                                    Text(t("所有在場成員", "All present")).tag(nil as AgentKind?)
                                    ForEach(AgentKind.allCases) { agent in
                                        Text(agent.shortName).tag(Optional(agent))
                                    }
                                }.frame(maxWidth: 200)
                            }
                            Spacer()
                            if editing != nil {
                                Button(t("取消編輯", "Cancel")) { resetForm() }
                            }
                            Button(editing == nil ? t("建立排程", "Add schedule") : t("儲存修改", "Save")) {
                                model.saveSchedule(id: editing, name: title, text: text, kind: kind, hour: hour, minute: minute,
                                                   onceAt: onceAt, intervalMinutes: interval, action: action, target: target)
                                resetForm()
                            }.buttonStyle(.borderedProminent).disabled(title.isEmpty || text.isEmpty)
                        }
                        if action == .roomMessage {
                            Text(t("「讓 AI 在聊天室回應」會送到建立時所在的茶歇室話題，並使用成員額度。", "AI room messages go to the lounge topic open when the schedule is made, and use member quota."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                StudioPanel {
                    VStack(alignment: .leading, spacing: 13) {
                        HStack {
                            Text(t("現有任務", "Schedules")).font(.headline)
                            Spacer()
                            Toggle(t("全部暫停", "Pause all"), isOn: Binding(get: { model.archive.schedulesPaused },
                                                                          set: { model.setSchedulesPaused($0) }))
                                .toggleStyle(.switch)
                                .help(t("暫停期間到點的任務會跳過，恢復後也不補發。", "Triggers that come due while paused are skipped and not replayed."))
                        }
                        if model.archive.schedules.isEmpty {
                            Text(t("尚未建立排程", "No schedules yet")).foregroundStyle(.secondary)
                        }
                        ForEach(model.archive.schedules) { item in
                            HStack(spacing: 10) {
                                Toggle(isOn: Binding(
                                    get: { model.archive.schedules.first(where: { $0.id == item.id })?.enabled ?? false },
                                    set: { model.setSchedule(id: item.id, enabled: $0) })) {
                                    VStack(alignment: .leading) {
                                        Text(item.name).font(.subheadline.weight(.semibold))
                                        Text(describe(item) + " · " + item.text)
                                            .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                                        if let next = LocalAutomation.next(item, now: .now) {
                                            Text(t("下次：", "Next: ") + next.formatted(date: .abbreviated, time: .shortened))
                                                .font(.caption2).foregroundStyle(.secondary)
                                        }
                                    }
                                }.toggleStyle(.checkbox)
                                Spacer()
                                Button(t("編輯", "Edit")) { edit(item) }.buttonStyle(.borderless)
                                Button(role: .destructive) {
                                    if editing == item.id { resetForm() }
                                    model.deleteSchedule(id: item.id)
                                } label: { Image(systemName: "trash") }.buttonStyle(.borderless)
                            }
                            Divider()
                        }
                    }
                }
                StudioPanel {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(t("最近觸發", "Recent triggers")).font(.headline)
                        if model.archive.scheduleRuns.isEmpty {
                            Text(t("暫無觸發記錄。", "No triggers yet.")).foregroundStyle(.secondary)
                        }
                        ForEach(model.archive.scheduleRuns.reversed().prefix(20)) { run in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                Text(run.at.formatted(date: .abbreviated, time: .shortened)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                Text(run.outcome == .done ? t("已完成", "Done") : run.outcome == .skipped ? t("已跳過", "Skipped") : t("失敗", "Failed"))
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(run.outcome == .done ? .green : run.outcome == .skipped ? .secondary : .orange)
                                Text(run.name).font(.caption)
                                Text(run.reason).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            }
                        }
                    }
                }
                StudioPanel {
                    VStack(alignment: .leading, spacing: 12) {
                        Text(t("每日開場", "Daily greeting")).font(.headline)
                        Toggle(t("每天第一次打開 AIROOM 時，由成員輪流在茶歇室問候你", "The first time you open AIROOM each day, a member greets you in the lounge"),
                               isOn: Binding(get: { model.archive.dailyOpening.enabled },
                                             set: { model.configureDailyOpening(enabled: $0) }))
                        Text(t("從下一天開始生效；會提到昨天房間裡確實說過的事，不會編造。每次一則、一次呼叫。",
                               "Starts the next day. It may mention what was actually said in the room yesterday. One line, one call."))
                            .font(.caption).foregroundStyle(.secondary)
                        Divider()
                        Text(t("成員閒聊", "Member small talk")).font(.headline)
                        Toggle(t("兩位成員依作息和關係設定，在茶歇室輕鬆聊兩句", "Two members exchange two lines in the lounge, based on their routines and relations"), isOn: Binding(
                            get: { model.archive.autoChat.enabled },
                            set: { enabled in model.configureIdleChat(enabled: enabled,
                                intervalMinutes: model.archive.autoChat.intervalMinutes,
                                dailyLimit: model.archive.autoChat.dailyLimit) }))
                        HStack(spacing: 18) {
                            Stepper(t("每 \(model.archive.autoChat.intervalMinutes) 分鐘", "Every \(model.archive.autoChat.intervalMinutes) min"),
                                value: Binding(get: { model.archive.autoChat.intervalMinutes },
                                               set: { model.configureIdleChat(enabled: model.archive.autoChat.enabled,
                                                     intervalMinutes: $0, dailyLimit: model.archive.autoChat.dailyLimit) }),
                                in: 60...1440, step: 60)
                            Stepper(t("每天上限 \(model.archive.autoChat.dailyLimit) 次", "Daily cap \(model.archive.autoChat.dailyLimit)"),
                                value: Binding(get: { model.archive.autoChat.dailyLimit },
                                               set: { model.configureIdleChat(enabled: model.archive.autoChat.enabled,
                                                     intervalMinutes: model.archive.autoChat.intervalMinutes, dailyLimit: $0) }),
                                in: 1...6)
                        }
                        Text(t("若想讓 Claude 和 Codex 長時間互相接話、回應觀眾，請到茶歇室使用「雙 AI 同台直播」。",
                               "For continuous multi-agent co-hosting and viewer comments, start a Live Show in the Lounge."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .padding(24)
            .frame(maxWidth: 1000)
            .frame(maxWidth: .infinity)
        }
    }
}
