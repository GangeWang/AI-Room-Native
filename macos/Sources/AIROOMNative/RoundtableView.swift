import SwiftUI
import AIROOMCore

struct RoundtableView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                StudioPageHeading(eyebrow: "ROUNDTABLE",
                    title: t("獨立圓桌", "Roundtable"),
                    subtitle: t("同一題目交給兩位成員獨立作答；公開前彼此看不到答案。", "Two independent answers to the same task, without sharing each other's responses."))
                StudioPanel {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack(spacing: 9) {
                            Image(systemName: "text.bubble")
                                .foregroundStyle(StudioTheme.orange)
                            Text(t("議題與材料", "Topic and Material")).font(.headline)
                            Spacer()
                            Text(t("獨立比較", "Independent comparison")).font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        TextEditor(text: $model.roundtablePrompt)
                            .frame(height: 115)
                            .font(.body).scrollContentBackground(.hidden)
                            .padding(8)
                            .background(StudioTheme.secondary(scheme), in: RoundedRectangle(cornerRadius: 11))
                        HStack(spacing: 12) {
                            Button { model.startRoundtable() } label: {
                                Label(t("開始獨立比較", "Start Comparison"), systemImage: "person.2")
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(model.roundtableRunning || model.roundtablePrompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            if model.roundtableRunning {
                                ProgressView().controlSize(.small)
                                Button(t("取消剩餘比較", "Cancel remaining")) { model.stopRoundtable() }
                            }
                            Spacer()
                            HStack(spacing: 5) {
                                StudioAvatar(agent: .claude, size: 26)
                                StudioAvatar(agent: .codex, size: 26)
                            }
                        }
                    }
                }
                HStack(spacing: 10) {
                    Image(systemName: "lock.shield").foregroundStyle(StudioTheme.moss)
                    Text(t("第一階段答案獨立；公開後可互相審查與建議投票。候選採用權始終由你決定。", "Independent proposals, then peer review and advisory voting. You choose the final candidate."))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Text(t("獨立回答", "Independent Responses"))
                    .font(.headline)
                HStack(alignment: .top, spacing: 16) {
                    ForEach(AgentKind.allCases) { agent in
                        let result = model.roundtableResults.first(where: { $0.agent == agent })
                        StudioPanel {
                            VStack(alignment: .leading, spacing: 15) {
                                HStack(spacing: 11) {
                                    StudioAvatar(agent: agent, size: 42)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(agent.shortName).font(.headline)
                                        Text(t("獨立成員", "Independent agent"))
                                            .font(.caption).foregroundStyle(.secondary)
                                    }
                                    Spacer()
                                    if result == nil {
                                        Image(systemName: "hourglass")
                                            .foregroundStyle(.tertiary)
                                    } else if result?.error != nil {
                                        Image(systemName: "exclamationmark.triangle")
                                            .foregroundStyle(.red)
                                    } else {
                                        Image(systemName: "checkmark.circle")
                                            .foregroundStyle(StudioTheme.moss)
                                    }
                                }
                                Divider()
                                Text(result?.text ?? t("尚未執行比較。", "No result yet."))
                                    .font(.system(size: 14))
                                    .textSelection(.enabled)
                                    .frame(maxWidth: .infinity, minHeight: 150, alignment: .topLeading)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .topLeading)
                    }
                }
                if !model.archive.roundtables.isEmpty {
                    Text(t("歷史議題", "Past Topics")).font(.headline)
                    ForEach(model.archive.roundtables.prefix(20)) { old in
                        StudioPanel {
                            DisclosureGroup(String(old.question.prefix(90))) {
                                VStack(alignment: .leading, spacing: 12) {
                                    ForEach(old.results) { result in
                                        HStack(spacing: 9) {
                                            StudioAvatar(agent: result.agent, size: 29)
                                            Text(result.agent.shortName).font(.subheadline.weight(.semibold))
                                        }
                                        Text(result.text).textSelection(.enabled)
                                            .font(.subheadline)
                                        Divider()
                                    }
                                    if old.reviews.count < 2 || old.reviews.contains(where: { $0.error != nil }) {
                                        Button {
                                            model.reviewRoundtable(id: old.id)
                                        } label: { Label(t("啟動／重試交叉互評與建議投票", "Start / retry peer review"), systemImage: "checkmark.bubble") }
                                            .disabled(model.roundtableRunning || model.roundtableReviewRunning || old.results.contains(where: { $0.error != nil }))
                                    }
                                    if !old.reviews.isEmpty {
                                        Text(t("互評結果", "Peer reviews")).font(.headline)
                                        ForEach(old.reviews) { review in
                                            VStack(alignment: .leading, spacing: 4) {
                                                Text("\(review.reviewer.shortName) → \(review.candidate.shortName) · " +
                                                    (review.vote.map { t("投給 \($0.shortName)", "Vote: \($0.shortName)") } ?? t("未提供有效投票", "No valid vote")))
                                                    .font(.caption.weight(.semibold))
                                                Text(review.error ?? review.notes).font(.subheadline).textSelection(.enabled)
                                            }
                                        }
                                    }
                                    HStack(spacing: 12) {
                                        ForEach(old.results.filter { $0.error == nil }) { item in
                                            Button {
                                                model.adoptRoundtable(id: old.id, candidate: item.agent)
                                            } label: {
                                                Label(old.adopted == item.agent
                                                      ? t("已採用 \(item.agent.shortName)", "Adopted \(item.agent.shortName)")
                                                      : t("採用 \(item.agent.shortName)", "Adopt \(item.agent.shortName)"),
                                                      systemImage: old.adopted == item.agent ? "checkmark.circle.fill" : "circle")
                                            }.disabled(model.roundtableReviewRunning)
                                        }
                                        Spacer()
                                    }
                                    if old.adopted != nil && old.handoffProjectID == nil {
                                        Menu {
                                            ForEach(model.archive.projects.filter { [.draft, .rejected, .failed].contains($0.phase) }) { project in
                                                Button(project.title) { model.handoffRoundtable(id: old.id, to: project.id) }
                                            }
                                        } label: { Label(t("帶回專案", "Send to project"), systemImage: "folder.badge.plus") }
                                    } else if old.handoffProjectID != nil {
                                        Label(t("已送入專案", "Sent to project"), systemImage: "checkmark.circle")
                                            .foregroundStyle(.secondary)
                                    }
                                }.padding(.top, 12)
                            }
                        }
                    }
                }
            }
            .padding(26)
            .frame(maxWidth: 1120)
            .frame(maxWidth: .infinity)
        }
    }
}
