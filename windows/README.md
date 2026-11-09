# AIROOM Native（Windows 原生版）

Windows 原生重寫：**WinUI 3（Windows App SDK 2.6）＋ .NET 10**，不用 Electron。

- 產品介紹與原出處：見[根目錄的 README](../README.md)。
- 要做出的功能：以 [docs/FEATURE_PARITY.md](../docs/FEATURE_PARITY.md) 為準，行為和 macOS 版一致。
- 送給模型的提示詞：[docs/PROMPTS.md](../docs/PROMPTS.md)。兩個平台送出相同的內容。
- 發版：tag 用 `windows-v<版本>`，不能標成 Latest，原因見 [docs/RELEASING.md](../docs/RELEASING.md)。

## 目前範圍（第一階段核心 MVP）

- **成員工位**：多話題、草稿、附件（圖片/PDF/文字檔）、逐話題模型與思考程度、串流回覆、
  個人資料夾直接修改、其他資料夾在副本修改（應用／撤銷／丟棄，衝突會拒絕寫回）。
- **人物卡**：名字、稱呼、一句話介紹、代表色、預設模型與思考程度、長期記憶、
  立繪（模板圖或上傳）、隱藏與停用、新增（六種形象模板）與刪除。
- **執行端**：Claude Code 與 Codex，優先使用 Claude App／ChatGPT(Codex) App 自帶的 CLI，
  不必另外安裝；登入一律走官方 CLI 自己的終端機流程。
- **待你處理**、呼叫次數、全部中止、系統匣、Windows 通知、更新提醒（讀 `windows-v…` 發版）。
- **加密**：狀態與附件以 AES-256-GCM 加密，金鑰由 DPAPI 保護，只存在這台電腦。

茶歇室、圓桌、專案會議室、排程、額度查詢、直播舞台屬於第二階段以後；
狀態檔已為這些區塊保留空間（原樣帶過，不會弄壞未來的資料）。

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

程式結構：

- `AIROOMCore/`：不依賴介面的核心邏輯（流程、提示詞、加密存檔、CLI 執行），對應 macOS 版的
  `macos/Sources/AIROOMCore`，有完整單元測試（68 個，移植自 macOS 的測試套件）。
- `AIROOMApp/`：WinUI 3 介面與應用狀態。

## 和 macOS 版的差別

- **加密方式**：macOS 用鑰匙圈，Windows 用 DPAPI（都綁定本機使用者）；兩邊的狀態檔互不相通。
- **Codex 走 `codex exec --json`**：macOS 版走 app-server 協定，這是文件明載的 Windows headless
  路徑，續聊用 `codex exec resume`；之後再升級成與 macOS 相同的 app-server 對等。
- **DeepSeek Harness 與 Antigravity（agy）尚未支援此平台**：成員卡保留選項，接上時不需要改狀態格式。
- 頭像裁切用與 macOS 相同的臉部錨點數學（`PortraitCropLayout`）；上傳頭像的裁切編輯器在後續階段補上。

## 隱私與安全

- AIROOM 不讀取、也不保存任何官方 CLI 的登入憑證；登入一律在終端機使用官方流程。
- 對話、人物卡與設定只存在這台電腦：`%APPDATA%\AIROOMNative`，以 DPAPI 保護的金鑰加密。
- 送給成員程式的內容會送到對應的服務商（Anthropic、OpenAI）。
- CLI 程序的環境變數採白名單制：你的 API 金鑰、cookie、雲端憑證不會被帶進成員的程序。
- 修改檔案的工作在副本進行，由你確認後才寫回；副本是檢查機制，不是作業系統沙箱。
