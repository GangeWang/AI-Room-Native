import SwiftUI
import AIROOMCore

struct MembersView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.colorScheme) private var scheme
    @State private var selected: AgentKind = .claude
    @State private var editingCrop = false
    private var lang: InterfaceLanguage { model.archive.settings.language }
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, lang) }
    private func memberBinding(_ member: MemberCard) -> Binding<MemberCard> {
        Binding(
            get: { model.archive.members.first(where: { $0.id == member.id }) ?? member },
            set: { model.updateMember($0) }
        )
    }
    private var current: MemberCard? { model.archive.members.first(where: { $0.agent == selected }) }

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
                            StudioNotice(message: t("目前人物卡使用原版立繪作為顯示資源，但不會修改官方 CLI 帳戶。", "Portraits are presentation assets only; member cards do not modify official CLI accounts."))
                        }
                    }
                }
            }
            .padding(26)
            .frame(maxWidth: 980)
            .frame(maxWidth: .infinity)
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
