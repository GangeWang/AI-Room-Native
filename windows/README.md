# AI Room Native（Windows 原生版）

Windows 原生重寫：**WinUI 3（Windows App SDK 2.6）＋ .NET 10**，不用 Electron。

- 產品介紹與原出處：見[根目錄的 README](../README.md)。
- 要做出的功能：以 [docs/FEATURE_PARITY.md](../docs/FEATURE_PARITY.md) 為準，行為和 macOS 版一致。
- 送給模型的提示詞：[docs/PROMPTS.md](../docs/PROMPTS.md)。兩個平台送出相同的內容。
- 發版：tag 用 `windows-v<版本>`，不能標成 Latest，原因見 [docs/RELEASING.md](../docs/RELEASING.md)。

## 目前範圍

兩個平台的行為已對齊（平台差別見下節）；首次公開 release 為 `windows-v0.2.111`。

- **成員工位**：多話題、草稿、附件（圖片/PDF/文字檔）、逐話題模型與思考程度、串流回覆、session 續接、
  個人資料夾直接修改、其他資料夾在副本修改（應用／撤銷／丟棄，衝突會拒絕寫回）、話題封存。
- **茶歇室**：共用紀錄、依序發言、@ 點名、舉手、會議模式；AI 直播（觀眾留言、主持人插話下一句就回應）。
- **圓桌**：各自準備 → 公開 → 互評 → 審議 → 結論 → 帶回專案。
- **專案會議室**：討論 → 選負責人 → 方案 → 執行（副本＋衝突檢查合併）→ 驗收寫回 → 完成；
  三種複核程度、並行任務、複核異議由你裁定、額度用完時等待或換人接手。
- **人物卡**：名字、稱呼、一句話介紹、代表色、預設模型與思考程度、長期記憶、作息與小習慣、成員關係、
  立繪與頭像裁切、六種形象模板、隱藏與停用、新增與刪除。
- **排程與閒聊**：每天／一次／固定間隔的提醒與成員發言（錯過不補發）、每日開場、成員低頻閒聊、全部暫停。
- **首頁儀表板**、**待你處理**、用量與額度（Codex app-server／Claude `/usage`）、呼叫次數、
  全部中止、系統匣、Windows 通知、更新提醒（讀 `windows-v…` 發版）、開始選單開關。
- **直播舞台**：Windows 內建 TTS（音量口型）＋ Steam 版 VTube Studio（逐成員授權、自動載入模型、
  斷線重連、AI 用 `[pose …]` 接管骨架、單一成員也能開台）。
- **密碼匯出／匯入**：與 macOS 相同的檔案格式，可跨平台搬資料（登入憑證不包含在內）。
- **加密**：狀態與附件以 AES-256-GCM 加密，金鑰由 DPAPI 保護，只存在這台電腦。

## 從原始碼建置

需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）與 git。

```powershell
cd windows
powershell -File Scripts\fetch-assets.ps1   # 下載並驗證原版立繪素材（可略，缺圖時顯示名字首字）
powershell -File Scripts\build.ps1          # 建置＋單元測試
powershell -File Scripts\build.ps1 -Publish # 另外發布 win-x64 到 dist\AIROOM\
```

手動等效命令：

```powershell
dotnet build AIROOMNative.slnx
dotnet test AIROOMCore.Tests/AIROOMCore.Tests.csproj
dotnet run --project AIROOMApp
```

設定 `AIROOM_LIVE_CLI=1` 再跑 `dotnet test`，會另外測試已登入的官方 CLI 與實機管線（直播、專案流程等，不會改工作資料夾外的檔案）。

程式結構：

- `AIROOMCore/`：不依賴介面的核心邏輯（流程、提示詞、加密存檔、CLI 執行、直播舞台、VTS client），
  對應 macOS 版的 `macos/Sources/AIROOMCore`，有完整單元測試（221 個，案例與 macOS 測試套件對譯）。
- `AIROOMApp/`：WinUI 3 介面與應用狀態。

## 和 macOS 版的差別

- **加密方式**：macOS 用鑰匙圈，Windows 用 DPAPI（都綁定本機使用者）；兩邊的狀態檔互不相通，跨機搬家用密碼匯出檔。
- **Codex 走 `codex exec --json`**：macOS 版走 app-server 協定，這是文件明載的 Windows headless
  路徑，續聊用 `codex exec resume`；額度查詢已走 app-server。對話對等之後再與 macOS 升級到同一層。
- **DeepSeek Harness 尚未支援此平台**：成員卡保留選項，接上時不需要改狀態格式。Antigravity（agy）已支援。
- **直播舞台的聲音與 VTS**：Windows 的 TTS 是整句合成（首句延遲約 1–2 秒；macOS 以 buffer 串流，約 0.5 秒）；
  VTube Studio 是 Steam 版（啟動器掃 Steam 資料夾，路徑可在「設定 → 工作台」覆寫）；
  成員的模型選單放在直播舞台頁（macOS 放在成員卡）。

## 隱私與安全

- AIROOM 不讀取、也不保存任何官方 CLI 的登入憑證；登入一律在終端機使用官方流程。
- 對話、人物卡與設定只存在這台電腦：`%APPDATA%\AIROOMNative`，以 DPAPI 保護的金鑰加密。
- 送給成員程式的內容會送到對應的服務商（Anthropic、OpenAI、Z.AI）。
- CLI 程序的環境變數採白名單制：你的 API 金鑰、cookie、雲端憑證不會被帶進成員的程序。
- ZCode 寫入任務走 build 模式、只放行工作資料夾內的寫檔；唯讀模式跑在消毒副本。
- 修改檔案的工作在副本進行，由你確認後才寫回；副本是檢查機制，不是作業系統沙箱。
