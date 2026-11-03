# AIROOM Native · 我們的 AI 工作室（macOS 原生版）

> **原出處：[tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice)（AIROOM · 我们的 AI 工作室）**
>
> 本專案是 AIROOM 的 macOS 原生重新實作。功能設計、四個房間的工作方式、介面風格、人物與工作流程都來自原版，感謝原作者 [@tonyAmadeus](https://github.com/tonyAmadeus)。程式碼以 Swift 重新撰寫，沒有使用原版的 TypeScript／Electron 程式碼；圖片素材來自 ZipZipPipe（CC BY-NC-SA 4.0），詳見下方〈素材與授權〉。

把 Claude 和 Codex 請進同一個視窗，給每位成員名字、頭像和個性，讓它們像同事一樣：單獨幫你做事、一起聊天開會、各寫一稿互相比較、分工完成專案，最後由你驗收。每位成員背後都是真實的官方程式（Claude Code、Codex CLI），用你自己的帳號登入；AIROOM 負責把它們組織起來，並把說了什麼、做了什麼、改了哪些檔案都攤在你面前。

## 下載

到 [Releases](https://github.com/GangeWang/AIROOMNative/releases/latest) 下載 `AIROOMNative-<版本>.dmg`，打開後把 AIROOM Native 拖到「應用程式」。

- **需求**：Apple Silicon（M 系列）Mac、macOS 14 以上。
- **成員程式**：至少登入一個官方 CLI。Claude 會優先使用 Claude Desktop「Code」分頁附帶的 Claude Code；Codex 會優先使用 ChatGPT App 附帶的 Codex。都找不到時才找系統 PATH。
- **未經 Apple 公證**：App 有簽章但沒有公證，第一次開啟會被 macOS 擋下，請到「系統設定 → 隱私權與安全性」按「強制打開」。

## 能做什麼

| 房間 | 適合的事 | 怎麼進行 |
| --- | --- | --- |
| 成員工位 | 一對一問答、起草、整理資料、小任務 | 多個話題、草稿、附件、逐話題選模型；個人資料夾直接修改，其他資料夾在副本修改，可套用或撤銷 |
| 茶歇室 | 閒聊、開會、請 Codex 畫圖 | 成員依內容接話、@ 點名、舉手、會議紀要；可「雙 AI 同台直播」並插入觀眾留言 |
| 圓桌 | 評審、比稿、方案比較 | 各自獨立準備（同時進行）→ 公開 → 互評 → 問題清單 → 審議 → 採用並確認結論，可帶回專案 |
| 專案會議室 | 多任務分工、分輪交付 | 討論 → 選負責人 → 方案 → 執行與複核 → 驗收寫回；三種複核程度、並行任務、複核異議由你裁定、額度用完時等待或換人接手 |

其他：

- **人物卡**：名字、稱呼、一句話介紹、代表色、預設模型與思考程度、自訂立繪與頭像、長期記憶、作息與小習慣、成員關係。
- **待你處理**：成員的問題、待你決定的事、待驗收的成果集中在一處，可逐則忽略。
- **排程**：每天、一次或固定間隔的提醒與成員發言；全部暫停、最近觸發紀錄。
- **用量與額度**：向官方查詢 5 小時與每週剩餘額度，並統計 AIROOM 自己的呼叫次數。
- **資料與備份**：本機資料以 Keychain 金鑰加密；備份可先預覽內容再恢復。
- **直播舞台（實驗中）**：macOS 語音朗讀與 VTube Studio 表情、嘴型同步，設定方式見 [LIVE_STAGE.md](LIVE_STAGE.md)。

逐項和原版的對照見 [FEATURE_PARITY.md](FEATURE_PARITY.md)。

## 和原版的差別

- **macOS 原生**：SwiftUI／AppKit，沒有 Electron 或 Node.js；只支援 Apple Silicon。原版是 Windows 的 Electron 程式。
- **成員只有 Claude 和 Codex**：原版還支援 DeepSeek、Qwen、Kimi、GLM 等執行端，這裡暫未支援。
- **沒有自動更新、未經公證**：更新請到 Releases 下載新版 DMG。
- **加密方式不同**：資料以這台 Mac 鑰匙圈裡的金鑰加密，備份無法直接拿到另一台 Mac 還原。

## 從原始碼建置

需要 Xcode 與 Swift 工具鏈。

```zsh
cd macos
swift test                     # 單元測試
./Scripts/package-app.sh       # 產生 dist/AIROOMNative.app（會先下載並驗證原版圖片素材）
./Scripts/install-app.sh --open  # 安裝到「應用程式」並開啟（舊版會移到垃圾桶）
./Scripts/make-dmg.sh          # 另外產生可拖曳安裝的 DMG
```

`package-app.sh` 會自動找鑰匙圈裡的 Apple Development 憑證來簽章；找不到時改用臨時簽章，此時每次更新後鑰匙圈都會再問一次是否允許存取。設定 `AIROOM_LIVE_CLI=1` 再跑 `swift test`，會另外測試已登入的官方 CLI（只讀取模型清單，不花額度）。

程式結構：

- `macos/Sources/AIROOMCore`：不依賴介面的核心邏輯（流程、提示詞、加密存檔、CLI 執行），有完整單元測試。
- `macos/Sources/AIROOMNative`：SwiftUI 介面與 App 狀態。

腳本、簽章與各檔案的用途見 [macos/README.md](macos/README.md)。

## 隱私與安全

- AIROOM 不讀取、也不保存任何官方 CLI 的登入憑證；登入一律在終端機使用官方流程。
- 對話、人物卡與設定只存在這台 Mac：`~/Library/Application Support/AIROOMNative`，以鑰匙圈金鑰加密。
- 你傳給成員的內容會送到對應的服務商（Anthropic、OpenAI）。茶歇室與專案中已完成的發言，會作為共同上下文送給其他成員所屬的服務商。
- 修改檔案的工作在副本進行，由你確認後才寫回；副本是檢查機制，不是作業系統沙箱，CLI 仍以你的使用者權限執行。詳見 [macos/SECURITY.md](macos/SECURITY.md)。

## 素材與授權

- **圖片素材**：人物立繪（Claudia、GPTia 等）、茶歇室背景與程式圖示都沿用原版 AIROOM，作者是 Bilibili 的 ZipZipPipe，依 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/deed.zh-hant)（姓名標示－非商業性－相同方式分享）授權。其中鯨魚娘（`ds.webp`）是 ZipZipPipe 以 @上善无形 的原創角色所做的二次創作。
  - 本專案未修改這些圖片，只用於免費、非商業的用途。
  - 任何人使用或再散布這些圖片，都必須標示作者、不得用於商業用途；以它們改作的作品也必須以 CC BY-NC-SA 4.0 公開分享。
  - 這個授權只適用於圖片，不適用於本專案的程式碼。
- **程式碼**：原版 [tonyAmadeus/offAice](https://github.com/tonyAmadeus/offAice) 沒有指定授權條款；本專案的 Swift 程式碼為獨立重寫，目前尚未指定授權條款。

## 致謝

感謝 [@tonyAmadeus](https://github.com/tonyAmadeus) 設計並公開 AIROOM，也感謝 ZipZipPipe 繪製人物與場景圖片。本專案的產品構想、工作流程與介面設計都來自原版，這裡只是用 macOS 原生技術重新實作。
