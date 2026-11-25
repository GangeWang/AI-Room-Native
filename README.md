# AI Room Native · 我們的 AI 工作室（原生版）

> **原出處：[tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice)（AIROOM · 我们的 AI 工作室）**
>
> 本專案是 AIROOM 的原生重新實作，有 macOS 版和 Windows 版（`windows/`，WinUI 3 + .NET）。功能設計、四個房間的工作方式、介面風格、人物與工作流程都來自原版，感謝原作者 [@tonyAmadeus](https://github.com/tonyAmadeus)。macOS 版以 Swift、Windows 版以 C# 重新撰寫，沒有使用原版的 TypeScript／Electron 程式碼；圖片素材來自 ZipZipPipe（CC BY-NC-SA 4.0），詳見下方〈素材與授權〉。

把 Claude 和 Codex 請進同一個視窗，給每位成員名字、頭像和個性，讓它們像同事一樣：單獨幫你做事、一起聊天開會、各寫一稿互相比較、分工完成專案，最後由你驗收。每位成員背後都是真實的官方程式（Claude Code、Codex CLI），用你自己的帳號登入；AIROOM 負責把它們組織起來，並把說了什麼、做了什麼、改了哪些檔案都攤在你面前。

## 下載

兩個平台都在 [Releases](https://github.com/GangeWang/AI-Room-Native/releases)，版本號一致（`0.2.<commit 數>`），tag 分別是 `macos-v…` 和 `windows-v…`。Windows 版不會佔用「Latest」位置，以免舊版 macOS App 的更新檢查被干擾——到 Releases 頁自己挑平台。

**macOS**：下載最新的 `AI-Room-Native-<版本>.dmg`，打開後把 AI Room Native 拖到「應用程式」。

- **需求**：Apple Silicon（M 系列）Mac、macOS 14 以上。
- **成員程式**：至少登入一個官方 CLI（Claude Code、Codex、Antigravity 的 `agy`），在 DeepSeek Harness 裡存好 API 金鑰，或在 ZCode 桌面版登入 Z.AI。`agy` 要另外安裝（見 [Antigravity CLI 文件](https://antigravity.google/docs/getting-started?tab=cli)），和 Antigravity App 共用登入。Claude 會優先使用 Claude Desktop「Code」分頁附帶的 Claude Code；Codex 會優先使用 ChatGPT App 附帶的 Codex。都找不到時才找系統 PATH。
- **未經 Apple 公證**：App 有簽章但沒有公證，第一次開啟會被 macOS 擋下，請到「系統設定 → 隱私權與安全性」按「強制打開」。

**Windows**：下載最新的 `AI-Room-Native-<版本>-windows-x64.zip`，解壓後直接執行 `AIROOM.exe`，**免安裝**、不寫登錄檔；想被開始選單搜尋找到（等同 Spotlight／啟動台），在「設定 → 啟動」打開開關，之後按 Win 鍵輸入「AI Room」即可。

- **需求**：Windows 10 19041 以上（x64）。.NET 與 Windows App SDK 執行階段已整包打進 zip，不需另外安裝。
- **成員程式**：與 macOS 相同的原則——官方 CLI 由官方桌面 App 安裝與登入，AIROOM 只偵測路徑。Claude 優先使用 Claude App 自帶的 Claude Code（`%APPDATA%\Claude\claude-code\`，含 Microsoft Store 版）；Codex 優先使用 ChatGPT(Codex) App 自帶的執行檔（`%LOCALAPPDATA%\OpenAI\Codex\`）；ZCode 桌面版登入 Z.AI；Antigravity 的 `agy` 另外安裝（DeepSeek Harness 此平台未支援）。都找不到時才找 PATH。
- **exe 未簽署**：第一次執行時 Windows SmartScreen 可能提示「Windows 已保護您的電腦」，按「更多資訊 → 仍要執行」。
- **搬到另一台電腦**：密碼匯出／匯入的加密檔格式兩個平台相同，可以在 Mac 與 PC 之間搬資料（登入憑證不包含在內，到新電腦要重新登入）。

## 能做什麼

| 房間 | 適合的事 | 怎麼進行 |
| --- | --- | --- |
| 成員工位 | 一對一問答、起草、整理資料、小任務 | 多個話題、草稿、附件、逐話題選模型；個人資料夾直接修改，其他資料夾在副本修改，可套用或撤銷 |
| 茶歇室 | 閒聊、開會、請 Codex 畫圖 | 成員依內容接話、@ 點名、舉手、會議紀要；可「AI 直播」並插入觀眾留言或主持人插話 |
| 圓桌 | 評審、比稿、方案比較 | 各自獨立準備（同時進行）→ 公開 → 互評 → 問題清單 → 審議 → 採用並確認結論，可帶回專案 |
| 專案會議室 | 多任務分工、分輪交付 | 討論 → 選負責人 → 方案 → 執行與複核 → 驗收寫回；三種複核程度、並行任務、複核異議由你裁定、額度用完時等待或換人接手 |

其他：

- **人物卡**：名字、稱呼、一句話介紹、代表色、預設模型與思考程度、自訂立繪與頭像、長期記憶、作息與小習慣、成員關係。
- **新增成員**：可從 Claudia、GPTia、DS、Gemini、Kimi、GLM 等形象模板新增成員；好幾位成員可以共用同一個程式，各有各的人設與記憶。
- **待你處理**：成員的問題、待你決定的事、待驗收的成果集中在一處，可逐則忽略。
- **首頁儀表板**：成員卡、開工前檢查、專案進度。
- **排程**：每天、一次或固定間隔的提醒與成員發言；每日開場、成員低頻閒聊；全部暫停、最近觸發紀錄。
- **用量與額度**：向官方查詢 5 小時與每週剩餘額度，並統計 AIROOM 自己的呼叫次數。
- **資料與備份**：本機資料加密（macOS 用鑰匙圈金鑰、Windows 用 DPAPI 金鑰）；備份可先預覽內容再恢復。要搬電腦，用密碼匯出成一個加密檔（含附件與頭像），在新電腦輸入密碼匯入；格式兩個平台相同。
- **AI 直播與直播舞台（實驗中）**：把成員的發言念出來並驅動 Live2D 形象——情緒標籤、嘴型同步、AI 用 `[pose …]` 接管骨架、單一成員也能獨自開台、主持人插話下一句就回應。macOS 用內建 TTS＋原生版 VTube Studio；Windows 用 Windows 內建 TTS＋Steam 版 VTube Studio。設定方式見 [docs/LIVE_STAGE.md](docs/LIVE_STAGE.md)。

逐項和原版的對照見 [docs/FEATURE_PARITY.md](docs/FEATURE_PARITY.md)。

## 和原版的差別

- **兩個平台都是原生**：macOS 用 SwiftUI／AppKit、Windows 用 WinUI 3＋.NET，都沒有 Electron 或 Node.js，各是單一程序。原版是 Windows 的 Electron 程式——Windows 版等於回到它的主場平台把它重寫成原生。
- **執行端有 Claude Code、Codex、Antigravity、ZCode**：原版另外支援的 Qwen、Kimi 等執行端暫未支援；Kimi 形象目前由上述程式代跑。DeepSeek Harness 僅 macOS 版支援。
- **不會自動更新、未簽署／未公證**：有新版本時 App 會提醒（macOS 讀 `macos-v…`、Windows 讀 `windows-v…`），請到 Releases 下載新版。macOS 拖進「應用程式」取代；Windows 解壓蓋掉舊資料夾即可，資料都會保留（資料存在使用者資料夾，不在程式資料夾）。
- **加密方式不同**：資料以本機金鑰（macOS 鑰匙圈／Windows DPAPI）加密，所以備份無法直接拿到另一台還原；跨機搬家用的是另一種密碼加密的匯出檔。

## 資源用量比較

### macOS

2026-11-04 在同一台 Apple Silicon Mac 上實測。原版（[tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice) commit `c6e2b59`，Electron 44.5.1）從原始碼以開發模式啟動；AI Room Native 為 0.2.58。記憶體是 macOS `footprint` 量到的實際佔用（也就是「活動監視器」顯示的記憶體），加總 App 的所有程序。

| 狀況 | 原版（Electron） | AI Room Native |
| --- | --- | --- |
| 剛啟動、閒置 30 秒 | 250 MB | 134 MB |
| 開過茶歇室、圓桌、用量、設定之後 | 約 350 MB | 115 MB |
| 長時間使用後（多輪對話） | 未量測 | 約 180 MB（高峰 280 MB） |
| 程序數 | 5 個（主程序、GPU、畫面、網路服務、執行服務） | 1 個 |
| 程式本體大小 | Electron 執行環境 288 MB＋程式 10 MB | 16 MB |
| 閒置時 CPU | 約 0.1% | 約 0% |

- 原版的記憶體大多在 GPU 程序：開過幾個畫面後，從 132 MB 漲到 223 MB。
- 量測時原版是全新安裝，沒有成員和對話；AI Room Native 已載入三位成員、立繪和茶歇室紀錄。即使如此，原版的用量仍約為 2–3 倍。
- 兩邊都沒有算進 Claude Code、Codex、DeepSeek Harness 這些成員程式本身：它們工作時用多少記憶體，換哪種介面架構都一樣。

### Windows

2026-11-23 在 Windows 11 專業版（Ryzen 7 7700X）上實測 AI Room Native 0.2.111——就是 Releases 上那個自含式 zip 解壓後的產物。原版是 Windows 的 Electron 程式，但這次沒有把原版拿出來跑（macOS 表裡的 Electron 數字是在 Mac 上以開發模式跑的；Electron 多程序＋Chromium 的架構在兩個平台相同，那些數字可當量級參考，不是同機對照）。記憶體是「工作管理員 → 詳細資料」顯示的數字，也就是程序的私人記憶體（Private Memory），量法與 macOS 的 `footprint` 接近但不是同一個量。

| 狀況 | AI Room Native（Windows 原生） |
| --- | --- |
| 剛啟動、閒置 30 秒 | 145 MB |
| 開過全部 12 個頁面（首頁、工位、茶歇室、額度、圓桌、專案、排程、直播、待你處理、成員、設定）之後 | 203 MB |
| 長時間使用後 | 未量測 |
| 程序數 | 1 個 |
| 程式本體大小 | zip 92 MB；解壓 235 MB（533 個檔案） |
| 閒置時 CPU | 約 0%（15 秒取樣累計 0 秒） |

- 解壓 235 MB 明顯比 macOS 的 16 MB 大，是刻意取捨：Windows 版把 .NET 10 與 Windows App SDK 執行階段整包打進 zip，所以免安裝、也不要求使用者先裝任何執行階段；macOS 的 Swift 二進位直接用系統內建框架。zip 壓縮後 92 MB。
- Windows 版沒有 GPU／網路等輔助程序：全程 1 個程序，和 macOS 版相同。
- 成員程式（Claude Code、Codex 等）同樣不算在內。
- 這台機器是桌機（8 核心／95 GB），數字代表這台機器上的實測；記憶體用量主要由 WinUI 3 的組合快取與 XAML 決定，硬體差異影響不大，但不同 Windows 版本與顯示卡驅動可能讓數字略有出入。

## 從原始碼建置

**macOS**：需要 Xcode 與 Swift 工具鏈。

```zsh
cd macos
swift test                     # 單元測試
./Scripts/package-app.sh       # 產生 dist/AI Room Native.app（會先下載並驗證原版圖片素材）
./Scripts/install-app.sh --open  # 安裝到「應用程式」並開啟（舊版會移到垃圾桶）
./Scripts/make-dmg.sh          # 另外產生可拖曳安裝的 DMG
```

`package-app.sh` 會自動找鑰匙圈裡的 Apple Development 憑證來簽章；找不到時改用臨時簽章，此時每次更新後鑰匙圈都會再問一次是否允許存取。設定 `AIROOM_LIVE_CLI=1` 再跑 `swift test`，會另外測試已登入的官方 CLI（只讀取模型清單，不花額度）。

**Windows**：需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`），不需要 Visual Studio。

```powershell
cd windows
powershell -File Scripts\build.ps1          # 建置＋單元測試
powershell -File Scripts\build.ps1 -Publish # 另外發布 win-x64 自含版到 dist\AIROOM\
```

設定 `AIROOM_LIVE_CLI=1` 再跑 `dotnet test`，同樣會另外測試已登入的官方 CLI。發 release 的步驟（tag 規則、Windows 版為什麼不能標 Latest）見 [docs/RELEASING.md](docs/RELEASING.md)。

目錄結構：

- `macos/`：macOS 原生版（Swift 套件）。
  - `macos/Sources/AIROOMCore`：不依賴介面的核心邏輯（流程、提示詞、加密存檔、CLI 執行），有完整單元測試。
  - `macos/Sources/AIROOMNative`：SwiftUI 介面與 App 狀態。
- `windows/`：Windows 原生版（WinUI 3 + .NET），說明見 [windows/README.md](windows/README.md)。
  - `windows/AIROOMCore`：與 macOS `AIROOMCore` 逐檔對譯的核心邏輯（C#），同一套單元測試案例。
  - `windows/AIROOMApp`：WinUI 3 介面與 App 狀態。
- `docs/`：兩個平台共用的文件：和原版的功能對照、送給模型的提示詞、直播舞台設定、發版規則、[待處理事項](docs/PENDING.md)。

腳本、簽章與各檔案的用途見 [macos/README.md](macos/README.md)。

## 隱私與安全

- AIROOM 不讀取、也不保存任何官方 CLI 的登入憑證；登入一律在終端機使用官方流程。
- 對話、人物卡與設定只存在本機：macOS 在 `~/Library/Application Support/AIROOMNative`（鑰匙圈金鑰加密）、Windows 在 `%APPDATA%\AIROOMNative`（DPAPI 金鑰加密）。兩平台都不會把你的資料送去任何第三方。
- 你傳給成員的內容會送到對應的服務商（Anthropic、OpenAI、Z.AI）。茶歇室與專案中已完成的發言，會作為共同上下文送給其他成員所屬的服務商。
- 更新提醒每天最多向 GitHub 讀取一次公開的版本清單（macOS 挑 `macos-v…`／Windows 挑 `windows-v…`），不傳送你的任何資料，也不會自動下載或安裝；可在「設定 → 工作台」關閉。
- 修改檔案的工作在副本進行，由你確認後才寫回；副本是檢查機制，不是作業系統沙箱，CLI 仍以你的使用者權限執行。ZCode 寫入任務另有 build 模式與寫入放行清單。詳見 [macos/SECURITY.md](macos/SECURITY.md)（兩平台設計相同）。

## 素材與授權

- **圖片素材**：人物立繪（Claudia、GPTia 等）、茶歇室背景與程式圖示都沿用原版 AIROOM，作者是 Bilibili 的 ZipZipPipe，依 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hant)（姓名標示－非商業性－相同方式分享）授權。其中鯨魚娘（DS，`ds.webp`）是 ZipZipPipe 以 @上善无形 的原創角色所做的二次創作。
  - 本專案未修改這些圖片，只用於免費、非商業的用途。
  - 任何人使用或再散布這些圖片，都必須標示作者、不得用於商業用途；以它們改作的作品也必須以 CC BY-NC-SA 4.0 公開分享。
  - 這個授權只適用於圖片，不適用於本專案的程式碼。
- **程式碼**：原版 [tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice) 沒有指定授權條款；本專案的 Swift／C# 程式碼為獨立重寫，目前尚未指定授權條款。

## 致謝

感謝 [@tonyAmadeus](https://github.com/tonyAmadeus) 設計並公開 AIROOM，也感謝 ZipZipPipe 繪製人物與場景圖片。本專案的產品構想、工作流程與介面設計都來自原版，這裡只是用 macOS／Windows 原生技術把它重新實作。
