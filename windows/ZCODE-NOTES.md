# ZCode 執行端：協議配方（已全打通 ✅）與 PR #3 狀態

狀態：ZCode executor 已端到端打通——C# 管線（AIROOMCore/ZCode.cs）實測：
新對話「收到」✓、跨程序 resume 記憶（新程序問上一題答對）✓、Core 測試 80 全綠 ✓。

## 執行通道（唯一可行路線）

- 桌面 App：`%LOCALAPPDATA%\Programs\ZCode\ZCode.exe`（Electron），CLI 在 `resources\glm\zcode.cjs`，用系統 node 跑。
- `-p` headless **不可用**（"Select a model before sending a message"，model 綁 session）。
- 正確通道：`node zcode.cjs app-server`（stdio 行協定，**非 JSON-RPC**，帶 `jsonrpc` 鍵會被拒）。

## 必帶環境變數（缺一 WAF/註冊表就出事）

| 變數 | 值 |
|---|---|
| `ZCODE_BUILTIN_PROVIDER_CONFIG_FILE` | 最新的 `~/.zcode/v2/runtime/provider/windows-x86_64/<ver>/endpoint-*/zcode-builtin.json` |
| `ZCODE_PERSONAL_PROVIDER_CONFIG_FILE` | `~/.zcode/v2/provider_config.json` |
| `ZCODE_APP_VERSION` | `3.17.2`（桌面版版本字串；直接 HTTP 會被 WAF 3012 擋，app-server 自己的請求帶這個就過） |
| `ZCODE_TELEMETRY_DEVICE_MID` | `~/.zcode/v2/telemetry-state.json` 的 `deviceMid` |

## 認證（重點：zcodejwttoken，不是 project PAT）

- `~/.zcode/v2/credentials.json`，值格式 `enc:v1:<iv>.<tag>.<data>`（base64url）＝AES-256-GCM；
  金鑰＝`sha256("zcode-credential-fallback:win32:<home>:<username>")`。
- 模型請求的 auth 是 **`zcodejwttoken`**（start-plan 的 JWT），透過
  `interaction/requestProviderRuntimeHeaders` 回給伺服器：
  `{headersApplied:true, requestAuth:{apiKey:<jwt>, headers:{Authorization:"Bearer <jwt>"}}}`。
  （`project-key-location:v1:*` 的 PAT 與模型授權無關。）

## 註冊表（registry）——卡最久的關鍵

App-server 的快照**必須**由 client 先推帳號設定，否則 `session/setModel` 報
"Provider Registry 中不存在 Model"：

```
provider/updateAccountConfig {
  revision: "airoom:<epochms>",              // 任意新 revision
  basedOnZCodeBuiltinRevision: "zcode-builtin:43:<sha256(resolved builtin 檔完整路徑) 的小寫 hex>",
                                             // 43 = 該檔內的 revision 欄位；快照比對不符就整份丟棄
  providers: { "account:zai-start-plan": {
                 builtinModelIds: [/* 從 builtin 檔讀出的 GLM 模型清單 */],
                 access: { type: "zhipu-account", entitled: true } } },
  states:    { "account:zai-start-plan": { availability:"available", entitled:true, current:true } }
}
```

順序：`session/create`（workspace Path+Key）→ `provider/updateAccountConfig` →
`session/setModel {model:{providerId, modelId, options:{reasoningLevel}}}` →
`session/subscribe`（**必須在 send 之前同步送出**）→ `session/send {sessionId, content}`。

## 伺服器反問（15 秒逾時 -32022，必答，result 鍵不可漏）

- `session/requestRuntimePreferences` → `{"result":{"nativeSearchEnhancementsEnabled":false}}`；
  **sessionId 從它的 params.sessionId 拿**（session/create 的 result 沒有）。
- `interaction/requestProviderRuntimeHeaders` → 見上（帶 zcodejwt）。
- 其他（`interaction/requestOfficialMcpAuthHeaders` 等）→ `{"result":{}}` 即可。
- 回應格式：`{"id":<原 id 原樣>, "result":{…}}`——漏掉 `result` 鍵＝-32600 invalid_union。

## 回合完成偵測（此版本伺服器「從不發 turn.completed」）

實測（probe7/8）訂閱後只會收到 `state.updated`／`session/lifecycle/changed`／telemetry；
`turn.*` 事件完全不出現。可用訊號：

- **完成**：poll `session/messages`（900ms），最新 assistant 訊息
  `info.time.completed` 非 null ＝回合結束（串流中是 undefined）。
- **界線**：只認「created 晚於本回合 user 訊息」的 assistant 訊息——session 問候氣泡
  （timeline part、無文字、created 落在 send 處理當下）與舊回合的回覆都不算。
- **失敗**：`state.updated` 通知 `params.reason == "prompt_failed"`（失敗回合永不產生
  assistant 訊息，poll 等不到完成）。
- 失敗範例：setModel 給不存在的 modelId → setModel 本身先報
  `ModelProtocolError: Provider Registry 中不存在 Model`。

## 其他實測事實

- `session/resume {sessionId}` 跨程序續聊 ✓（CLI 自己的 db.sqlite 持久化）。
- 模型清單寫死在 builtin 檔的 `config.providerConfigRules.providerRules[].config.builtinModelIds`；
  C# 動態讀最新 endpoint 檔（桌面更新後自動跟著變）。
- 使用者方案：zai-start-plan / GLM-5.3-Flash（ZCODE WEEKEND BUILD entitlement）。
- 探測器：`tmp-zcode-probe/probe6..8.cjs`、`tmp-zcode-probe/harness/`（C# harness， repo 外）。

## ZCode Executor（C#）地圖

- `AIROOMCore/ZCode.cs`：`ZCode`（常數＋LoadAuth：解密 credentials、deviceMid、builtin 檔、
  basedOn 計算、模型清單）＋`ZCodeAppServerClient.RunAsync`（一回合一行程：create/resume →
  updateAccountConfig → setModel → subscribe → send → poll → final fetch）。
- `CliProcessRunner`：ZCode 分支（LoadAuth→FindNodeOnPath→RunAsync→`Resumed` 旗標）。
- `Workstation.OfficialCLIProbe`：zcode.cjs 存在＝可偵測；LoadAuth 非 null＝已登入。
- 檔案附件：ZCode 文字-only（`AcceptsFiles` 排除）。
- 測試：`ZCodeTests`（解密配方/basedOn/LoadAuth 合成 home）＋`ZCodeAppServerLiveTests`
  （有桌面+node 才跑，3 分鐘逾時）。

## Antigravity（agy）— **已由使用者完整驗收 ✅（2026-11-08）**
- 工位對話實測成功：偵測（winget 路徑）、登入判定、plan 模式唯讀對話、串流回覆、session 記錄全通。
- 最終修正鏈：① `CommandPlan.Arguments` 要路由到 Antigravity.Arguments ② `--disable-slash-commands` 會使 `--mode` 失效→不能加 ③ run settings 必須 allowlist 唯讀工具。
- winget 套件 `Google.AntigravityCLI` 1.5.3，位置 `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Google.AntigravityCLI_...\agy.exe`。
- `agy models` 清單非空＝已登入；`--effort low|medium|high` 直接對應思考程度。

## 剩餘 UI 補齊清單
1. **頭像裁切編輯器**：上傳立繪後可拖曳/縮放設 avatarCrop（PortraitFaceAnchor.Translated 已移植好）。
2. **備份 UI**：Core 已有 backup/preview/restore/delete（LocalStateStore），Settings 頁加區塊。
3. **狀態橫幅**：WorkbenchModel.StatusMessage 目前只清不顯示 → 殼層 InfoBar。

## 流程備忘
- 所有改動走 PR。PR #3（windows-zcode-executor）base 設 `windows-antigravity-ui`。
- 建置：逐專案 `dotnet build windows/AIROOMApp/AIROOMApp.csproj -c Release -p:Platform=x64`
  （slnx 帶 `-p:Platform` 會靜默跳過專案）；測試 `dotnet test windows/AIROOMCore.Tests/...`（80 全綠）。
- app.exe 執行中會鎖檔 → build 前先 `taskkill //IM AIROOM.exe //F`。
- 啟動測試：`AIROOMApp/bin/x64/Release/net10.0-windows10.0.19041.0/AIROOM.exe`；log 在 `%LOCALAPPDATA%\AIROOMNative\startup.log`。
