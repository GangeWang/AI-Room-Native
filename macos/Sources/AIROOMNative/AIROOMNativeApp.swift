import SwiftUI
import AIROOMCore

@main
struct AIROOMNativeApp: App {
    @StateObject private var workbench = WorkbenchModel()

    var body: some Scene {
        WindowGroup("AIROOM Native") {
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
