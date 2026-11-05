import SwiftUI
import AppKit
import AIROOMCore

/// The menu bar icon keeps AIROOM running after its window is closed, and a click on
/// the Dock icon then opened nothing by itself, so the app looked stuck.
final class AppDelegate: NSObject, NSApplicationDelegate {
    var openWorkbench: (() -> Void)?

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        // Not the flag: the menu bar icon's own window can count as visible.
        let showing = sender.windows.contains { $0.canBecomeMain && $0.isVisible && !$0.isMiniaturized }
        guard !showing else { return true }
        openWorkbench?()
        sender.activate(ignoringOtherApps: true)
        return false
    }
}

/// The menu bar icon. It exists from launch whether or not a window ever opened, so it
/// is what hands the open-window action to the delegate, which has no SwiftUI environment.
private struct MenuBarIcon: View {
    let delegate: AppDelegate
    let busy: Bool
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Image(systemName: busy ? "circle.hexagongrid.fill" : "circle.hexagongrid")
            .onAppear { delegate.openWorkbench = { openWindow(id: "main") } }
    }
}

@main
struct AIROOMNativeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var workbench = WorkbenchModel()

    init() {
        // Every rebuild is signed ad hoc with a new identity, so restoring the
        // previous build's window state fails and SwiftUI then opened no
        // window at all. The single workbench window is simply recreated.
        UserDefaults.standard.register(defaults: ["NSQuitAlwaysKeepsWindows": false])
    }

    var body: some Scene {
        // One workbench window: a single scene can always be reopened from the
        // Window menu or the Dock, and never splits state across two windows.
        Window("AIROOM Native", id: "main") {
            ContentView()
                .environmentObject(workbench)
                .environment(\.locale, Locale(identifier: workbench.archive.settings.language.rawValue))
                .frame(minWidth: 840, minHeight: 560)
                // Daily opening: the first activation of each day (once the state is unlocked).
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
                    workbench.windowOpened()
                }
        }
        .defaultSize(width: 1180, height: 780)
        // Like upstream's tray icon: closing the window keeps AIROOM available here.
        MenuBarExtra {
            StudioMenuBar().environmentObject(workbench)
        } label: {
            MenuBarIcon(delegate: delegate, busy: workbench.anythingRunning)
        }
        .commands {
            CommandGroup(after: .appInfo) {
                Button(StudioI18n.t("檢查更新…", "Check for Updates…", workbench.archive.settings.language)) {
                    workbench.checkForUpdates(manual: true, announce: true)
                }
                .disabled(workbench.updateChecking)
            }
            CommandGroup(replacing: .newItem) {
                Button(LocalizedUI.text("開始新對話", language: workbench.archive.settings.language)) { workbench.resetConversation(for: .claude) }
                    .keyboardShortcut("n", modifiers: .command)
            }
            // The same 全部中止 as the menu bar icon, reachable while the window is in front.
            CommandMenu(StudioI18n.t("工作", "Work", workbench.archive.settings.language)) {
                Button(StudioI18n.t("全部中止", "Stop All", workbench.archive.settings.language)) { workbench.stopAll() }
                    .keyboardShortcut(".", modifiers: [.command, .shift])
                    .disabled(!workbench.anythingRunning)
            }
        }
    }
}

struct StudioMenuBar: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Environment(\.openWindow) private var openWindow
    private func t(_ zh: String, _ en: String) -> String { StudioI18n.t(zh, en, model.archive.settings.language) }

    var body: some View {
        ForEach(model.memberIDs) { agent in
            Text("\(model.memberName(agent))：" + (model.busyAgents.contains(agent) ? t("工作中", "Working") : t("空閒", "Idle")))
        }
        if model.roomRunning { Text(t("茶歇室討論中", "Lounge is talking")) }
        if !model.attentionItems.isEmpty {
            Text(t("\(model.attentionItems.count) 項待你處理", "\(model.attentionItems.count) items need you"))
        }
        if let release = model.availableUpdate {
            Button(t("有新版本 \(release.version)，前往下載", "Download version \(release.version)")) { model.openUpdatePage() }
        }
        Divider()
        Button(t("打開 AIROOM 工作室", "Open AIROOM")) {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button(t("全部中止", "Stop All")) { model.stopAll() }
            .disabled(!model.anythingRunning)
        Divider()
        Button(t("結束 AIROOM", "Quit AIROOM")) { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
