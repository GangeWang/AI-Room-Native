import SwiftUI
import AIROOMCore

@main
struct AIROOMNativeApp: App {
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
        }
        .defaultSize(width: 1180, height: 780)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button(LocalizedUI.text("開始新對話", language: workbench.archive.settings.language)) { workbench.resetConversation(for: .claude) }
                    .keyboardShortcut("n", modifiers: .command)
            }
        }
    }
}
