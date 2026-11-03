# AIROOM Native — 開發說明

這裡是 AIROOM Native 的 Swift 套件。產品介紹、下載與原出處見[根目錄的 README](../README.md)；和原版 [tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice) 的逐項對照見 [FEATURE_PARITY.md](../FEATURE_PARITY.md)。

- 純 SwiftUI／AppKit + Foundation：沒有 Electron、WebView 或 Node.js，也沒有第三方 Swift 套件。
- 只支援 Apple Silicon（arm64）、macOS 14 以上。
- 介面可在「設定 → 介面語言」切換繁體中文和英文。

## 建置與安裝

需要 Apple Silicon Mac、Xcode 與 Swift 工具鏈。

```zsh
cd macos
swift test                        # 單元測試（不呼叫真正的 AI，不花額度）
./Scripts/package-app.sh          # 建置 dist/AIROOMNative.app
./Scripts/install-app.sh --open   # 安裝到 /Applications 並開啟
./Scripts/make-dmg.sh             # 產生 dist/AIROOMNative-<版本>.dmg
./Scripts/audit-arm64.sh          # 確認 App 只含 arm64
```

| 腳本 | 作用 |
| --- | --- |
| `package-app.sh` | 下載並驗證原版圖片素材、以 release 模式建置、組出 `.app`、簽章。版本號是 `0.2.<commit 數>`。 |
| `fetch-original-assets.sh` | 從原版鎖定的 commit 下載立繪、背景與圖示，並比對 Git blob hash；已存在且正確的檔案不會重新下載。 |
| `install-app.sh` | 安裝到 /Applications；舊版移到垃圾桶（可還原），不碰使用者資料與鑰匙圈金鑰。 |
| `make-dmg.sh` | 把 `.app` 包成可拖曳安裝的 DMG，用同一個身分簽章，但不經公證。 |
| `audit-arm64.sh` | 用 `lipo` 確認執行檔只有 arm64。 |

**簽章**：`package-app.sh` 依序找鑰匙圈裡的「Apple Development」憑證、名為「AIROOM Local Signing」的自簽憑證，都沒有才改用臨時（ad hoc）簽章。也可以用 `AIROOM_SIGN_IDENTITY` 指定。臨時簽章每次建置的身分都不同，鑰匙圈會在每次更新後再問一次是否允許存取加密金鑰；用固定憑證就不會。

**真機 CLI 測試**：`AIROOM_LIVE_CLI=1 swift test` 會另外測試這台 Mac 上已登入的 Claude Code 和 Codex（只讀模型清單，不送出對話）。

## 程式結構

```text
macos/
├── Package.swift
├── Scripts/                  # 建置、安裝、DMG、素材下載
├── Sources/AIROOMCore/       # 只依賴 Foundation 的核心邏輯，全部有單元測試
├── Sources/AIROOMNative/     # SwiftUI 介面與 App 狀態（WorkbenchModel）
│   └── Resources/            # 原版圖片素材與 ARTWORK_NOTICE.txt
└── Tests/AIROOMCoreTests/
```

`AIROOMCore` 的主要檔案：

| 檔案 | 內容 |
| --- | --- |
| `CLIProcessRunner.swift`、`CodexAppServer.swift`、`CLIEvents.swift` | 執行 Claude Code（stream-json、`--resume` 延續對話）與 Codex App Server（JSON-RPC、`thread/resume`） |
| `DesktopApps.swift` | 找 Claude Desktop 和 ChatGPT App 附帶的 CLI，找不到才找 PATH |
| `MemberDesk.swift`、`DeskWorkspace.swift`、`WorkspaceStaging.swift` | 成員工位、話題、工作資料夾、副本與寫回 |
| `Lounge.swift` | 茶歇室接話、點名、舉手、會議與直播 |
| `Roundtable.swift` | 圓桌：各自準備、互評、問題清單、審議、結論 |
| `ProjectFlow.swift`、`ProjectFlowExtras.swift` | 專案會議室：方案、分輪執行、複核、異議裁定、額度等待、驗收 |
| `Collaboration.swift`、`MemberLife.swift`、`PortraitFraming.swift` | 人物卡、長期記憶、作息、立繪與頭像取景 |
| `Workstation.swift`、`WorkflowFeatures.swift` | 待你處理、排程 |
| `AccountQuota.swift` | 向官方 CLI 查詢 5 小時與每週額度 |
| `LocalState.swift`、`KeychainStateProtector.swift` | 加密存檔、備份與還原 |
| `PrivacyGuard.swift` | 子程序環境變數白名單、副本中的機密檔案過濾、輸出遮蔽 |
| `LiveStage.swift`、`VTubeStudio.swift` | 直播舞台：情緒標籤、TTS 事件、VTube Studio API（見 [LIVE_STAGE.md](../LIVE_STAGE.md)） |

## 資料

- 存放位置：`~/Library/Application Support/AIROOMNative`。
- 加密方式：對話與設定存在 `state.aesgcm`，以 AES-256-GCM 加密；金鑰是隨機產生的 256-bit，存在登入鑰匙圈。
- 金鑰取不到時：程式停止儲存，不會退回明文。
- 備份：綁定這台 Mac，不能直接拿到另一台還原。
- 不保存官方 CLI 的帳號憑證；登入一律交給官方 CLI 自己處理。

安全邊界與已知風險見 [SECURITY.md](SECURITY.md)。重點是：工作副本只是讓你檢查改動的機制，不是作業系統沙箱；CLI 仍以你的使用者權限執行，也能連網。

## 圖片素材

`Sources/AIROOMNative/Resources/` 裡的立繪、背景與圖示來自原版 AIROOM，作者是 Bilibili 的 ZipZipPipe，依 CC BY-NC-SA 4.0 授權，詳見 [ARTWORK_NOTICE.txt](Sources/AIROOMNative/Resources/ARTWORK_NOTICE.txt)。這個授權只適用於圖片，不適用於程式碼。
