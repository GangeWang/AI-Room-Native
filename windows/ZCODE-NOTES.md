# PR #2 交接筆記：ZCode＋Antigravity 執行端＋UI 補齊

狀態：PR #1（windows-native-mvp）已轉 ready for review，commit 到 `852a929`。
PR #2 分支名：`windows-zcode-antigravity-ui`（從 windows-native-mvp 開，PR base 設為 windows-native-mvp）。

## 使用者需求（原話）
「把你自己還有antigravity也做進去然後將UI補齊」→ ① ZCode 執行端（我，GLM）② Antigravity（agy）執行端 ③ UI 補齊。

## ZCode CLI 調查結果（2026-11-08 實測，本機）

- 桌面 App：`%LOCALAPPDATA%\Programs\ZCode\ZCode.exe`（Electron），**CLI 在** `resources\glm\zcode.cjs`，用系統 node 跑（機器有 node 26.7.0 在 `C:\Program Files\nodejs\node.exe`）。
- `node zcode.cjs --version` → 0.17.1 ✓
- **`-p` headless 不可用**：任何 cwd 都拋 "Select a model before sending a message"（model 綁 session，headless 不會帶；無 `--model` 旗標；`/model` slash command 在 headless 也被擋）。
- **正確通道：`node zcode.cjs app-server`**（stdio，自訂「ZCode Protocol」，非 JSON-RPC）。

### ZCode Protocol（已探明）
- 訊息格式：請求 `{"id":N,"method":"...","params":{…}}`、回應 `{"id":N,"result":{…}}` / `{"id":N,"error":{code,message,data}}`、通知 `{"method":"...","params":{…}}`（**無 jsonrpc 鍵**，帶 jsonrpc 會被拒）。
- 伺服器會反問 client，**必須回答**，否則 15 秒逾時（-32022）：
  - `session/requestRuntimePreferences`（params 含 sessionId、scope:"runtime-materialization"）→ 回 `{"id":N,"result":{"nativeSearchEnhancementsEnabled":false}}`
  - `interaction/requestOfficialMcpAuthHeaders` → 回 `{}` 即可（測過）
- **session id 來源**：`session/create` 的 result 裡 sessionId 是 "unknown"，**要從 `session/requestRuntimePreferences` 的 params.sessionId 拿**（notify 的 session.created 沒帶）。
- 流程（**重大更新**）：`session/create` → **不需要 setModel**（帳號預設模型即可；setModel registry 鍵待解但不擋路）→ `session/send` **`{"sessionId","content"}`**（是 content 不是 text）→ 回 `{"accepted":true,...}` → 等 `turn.completed` → `session/messages` 取最後 assistant 文字（`m.info.role==='assistant'`，比對 turnId 最穩）。
  - **尚未通（最新）**：`session/lifecycle/ensureReady` 回 `{completed:true}` 後 send 仍 accepted 但 turn 不啟動（status idle、turnCount 0）。**主假設**：session 沒有模型 → turn 靜默失敗 → **setModel 的 registry 鍵是必解項**。registry（class `ont`）建構 `E0r(0, providers)`；查 `new ont(` 的呼叫點看 providers 從哪來、glm 的 modelId 實際字串（可能與 model_usage 的 'GLM-5.3-Flash' 不同，例如小寫 slug）。另一線索：`session/requestRuntimePreferences` scope "runtime-materialization" 或許該在答案中帶模型偏好（zod 只收 nativeSearchEnhancementsEnabled——再確認 schema 是否 union）。
  - 對照：TUI 裡 `/model` 顯示的 model id 就是該填的值——下次開 ZCode TUI（`node zcode.cjs`）用 `/model` 看清單字串。
  - **建議的務實路線**：ZCode 短期以「待 CLI 支援」佔位（executor 枚舉、偵測、UI 全就位；提交時回友善錯誤「ZCode headless 模型選擇待 CLI 支援」），本 PR 先交付 Antigravity＋UI 補齊；registry 之謎留給新 session 專攻（關鍵字：app-server bootstrap 的 providers 來源、`E0r`、`resolveProviderOrder`、interaction/requestOfficialMcpAuthHeaders 的正確回答）。
  - **已驗證的真實 modelSelection**（從 db.sqlite 的 session_entry type='runtime/model_selection' 取出）：`{"modelSelection":{"providerId":"account:zai-start-plan","modelId":"GLM-5.3-Flash","options":{"reasoningLevel":"max"}}}`——setModel 用這組仍報 Provider Registry 錯誤 → registry 在 app-server 環境裡**根本沒有註冊 glm 帳號 provider**（可能要憑證物化完成後才註冊；我對 interaction/requestOfficialMcpAuthHeaders 回 {} 可能中斷了物化）。下一步：回答該請求時研究其 params 預期（也許要真的提供 auth headers）、或查 `new ont(` registry 建構的 providers 來源鏈、或試 TUI 同時開著時 app-server 是否共用 registry。
  - setModel registry 鍵（次要，不擋路）：ProviderRegistry.validateSelection 先查 providerId（"glm" 有過）再 getModel(providerId, modelId)——modelId 鍵不對；registry 建構在 E0r/resolveProviderOrder，GLM provider 的 models 設定來源待查。
- **setModel 卡關（最新狀態）**：zod schema `{sessionId, model:{providerId, modelId, options?}}`。handler（zcode.cjs 行 14913 col 17686）：`typeof u==="string" ? ldi(registry,u,…,{allowMissingReasoning:true}) : ule(registry,u)`；失敗訊息 "Provider Registry 中不存在 Model: [object Object]"＝validateSelection 回 model-not-found。已試 providerId `glm`（確認常數 IG="glm"）＋modelId `GLM-5.3-Flash`（Sgr 清單有，大小寫經 RQt 正規化）＋`options:{reasoningLevel:'high'}`＋create 後等 3 秒——全部同錯。
  - validateSelection（Yhe）：`n=t.options?.reasoningLevel`，undefined → "reasoning-level-missing"；不在 o.values → not-found 同型錯誤。reasoningLevel values 是 per-provider config（optionSpecs），GLM 的實際 values 未找到。
  - ldi 的字串解析：`"providerId:modelId"` 切開驗證（但 session/setModel 的 zod 只收 object）。
  - **下一個可疑點**：registry 的 providerId 可能不是 "glm"——`account:zai-start-plan` 也試過失敗。注意 `session/requestRuntimePreferences` scope:"runtime-materialization"：registry 可能在 runtime 物化後才填，且**答案內容會影響物化**（我只答了 nativeSearchEnhancementsEnabled:false）。試：① dump `session/lifecycle/changed` 通知 params（找 registry/provider 資訊）② 呼叫 `session/lifecycle/ensureReady` 再 setModel ③ 看 requestRuntimePreferences 完整 schema（zcode.cjs 搜 nativeSearchEnhancementsEnabled 附近）④ registry 建構 `resolveProviderOrder`/`effectiveProviders`（T0r 附近）看 providerId 實際值。
  - 現實後備：若 setModel 短期解不開，headless 路線改用 **`zcode app-server` 之外**——觀察 TUI 如何帶 model（TUI 走 desktop surface？`--surface desktop`？）；或第二階段先讓 ZCode 成員以「尚待 CLI 支援 headless 模型選擇」呈現，UI/偵測/架構全部就位。
- 探測器：`tmp-zcode-probe/probe.cjs`（已含：回答 runtime preferences、session id 從 requestRuntimePreferences params 拿、setModel 最新嘗試。改了直接 `ZC_CJS="$LOCALAPPDATA/Programs/ZCode/resources/glm/zcode.cjs" node probe.cjs` 再跑，免費）。
- 模型清單來源：`~/.zcode/cli/db/db.sqlite` 表 `model_usage` 有 `provider_id, model_id`（唯讀查詢；App 不該碰此檔——App 端 model 清單先寫死或留空，見下）。
- 其他可用方法：`session/stop`、`session/resume`、`session/list`、`session/compact`。通知：`turn.started/completed/failed`、`model.streaming`、`permission.requested`。
- 權限模式：`--mode` 在 -p 有；app-server 裡 `session/create` 後 projection.mode 顯示 "build"——模式怎麼設待查（可能 setModel 類似方法或 create params）。

### ZCode Executor 設計（C#）
- Executor 列舉加 `ZCode`（raw "ZCode"、ShortName "ZCode"、ExecutableName "zcode"）。
- 偵測 `BundledZCodePaths()`：`%LOCALAPPDATA%\Programs\ZCode\resources\glm\zcode.cjs`（存在 ✓）＋ configured path。執行需要 node：先 PATH 找 `node.exe`，再查 `%LOCALAPPDATA%\Programs\ZCode\...` 內有無（無）。找不到 node → 錯誤訊息要說需要 Node.js。
- `ZCodeAppServerClient`（新檔，仿 CodexAppServer.cs 模式）：常駐 app-server 程序＋行協定＋pending request map＋伺服器請求應答器（上面兩條）。每次 run：確保程序活著 → session/create（cwd=工作目錄）→ setModel → send → 等 turn.completed → session/messages 取最後 assistant 文字 → 回 CLIRunResult(sessionID=sess_…)；resume 用 `session/resume {sessionId}`。
- MemberTemplates 的 GLM 模板 plannedProgram 已是 "ZCode"——把 GLM 模板 suggestedExecutor 改成 ZCode？不行——Swift 版沒有 ZCode executor；Windows 版加上後，GLM 模板 `plannedProgram: "ZCode"` 正好對上（可留 plannedProgram 字串不動，executor 由使用者選）。

## Antigravity（agy）— **已由使用者完整驗收 ✅（2026-11-08）**
- 工位對話實測成功：偵測（winget 路徑）、登入判定、plan 模式唯讀對話、串流回覆、session 記錄全通。
- 最終修正鏈：① `CommandPlan.Arguments` 要路由到 Antigravity.Arguments（漏了會拋 not-available）② `--disable-slash-commands` 會使 `--mode` 失效→不能加 ③ run settings 必須 allowlist 唯讀工具（list_dir/view_file/grep_search/find_by_name/read_url_content/read_resource），否則被自動拒絕的工具會讓回合無聲結束、完全沒有回覆。
- 教訓：**slnx 方案建置帶 `-p:Platform=x64` 會靜默跳過專案**（MSB4126 或 1 秒 no-op）——一律逐專案建置；且 App 執行中會鎖 bin 檔，build 前先 taskkill。
- **winget 套件 `Google.AntigravityCLI` 1.5.3 已裝**，實際位置 `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Google.AntigravityCLI_Microsoft.Winget.Source_8wekyb3d8bbwe\agy.exe`（不是文件說的 `%LOCALAPPDATA%\agy\bin`）；winget 有加 PATH alias（新殼生效）。
- `agy --version` → 1.5.3 ✓；`agy models` 已回清單（gemini-3.8-flash-high/medium/low、claude-sonnet-4-6、claude-opus-4-6-thinking…）＝**已登入**（憑證與桌面 App 共用），macOS 式「models 清單非空=authenticated」判定可用。
- `agy models` 輸出格式 `slug\tTitle` 與 macOS parseModels 完全一致 ✓。
- Headless（docs/cli/headless）與 macOS 移植一致：`-p`、`--input-format/--output-format stream-json`、stdin 一行 `{"event":"user","message":{"content":…}}`（content 可為字串或 text blocks）、事件 `init → step_update → result`、`--conversation <id>` 續聊、`--sandbox`、`--model <slug>`、**`--effort low|medium|high`（直接對應 AIROOM 思考程度！）**、`agy models`（清單=登入狀態判定 ✓）、`agy agents`。未登入時 exit 帶 authentication error（不會 hang）。
- **與 macOS 版差異**：`--mode plan/accept-edits`、`--settings {toolPermission…}`、`--disable-slash-commands` 在 Windows headless **不存在**。權限走 `~/.gemini/antigravity-cli/settings.json` 的 `permissions.allow`（如 "command(git)"、"write_file(src/)"）；headless 中被拒的工具權限是**軟拒**（繼續執行、stderr 記事、exit 0）。
- 引數設計（C#）：ReadOnly → `--sandbox`（軟拒寫入＝近似唯讀）；StagedWrite → 同旗組，因 --dangerously-skip-permissions 會放行所有工具（含 Bash/網路，違反 AIROOM 檔案工具-only 原則）不可用——PR#2 先讓 StagedWrite 走同組旗標並在 UI 註明 agy 寫入需在它的 settings.json 配置 permissions.allow；`--effort` 帶成員卡的 effort；`--model` 帶話題 model。
- 偵測路徑：configured ＋ `%LOCALAPPDATA%\agy\bin\agy.exe` ＋ `%USERPROFILE%\.local\bin\agy.exe` ＋ PATH。狀態探測：跑 `agy models`，有清單=authenticated、空=notLoggedIn（macOS 同款邏輯）。

## UI 補齊清單
1. **Markdown 渲染**（macOS RichText：標題/**粗**/*斜*/`code`/清單，連結保持純文字）→ chat bubble 用。
2. **頭像裁切編輯器**：上傳立繪後可拖曳/縮放設 avatarCrop（PortraitFaceAnchor.Translated 已移植好）。
3. **備份 UI**：Core 已有 backup/preview/restore/delete（LocalStateStore），Settings 頁加區塊。
4. **狀態橫幅**：WorkbenchModel.StatusMessage 目前只清不顯示 → 殼層 InfoBar。
5. 執行端下拉的「尚未支援」標籤更新（ZCode 支援、Antigravity 偵測、DeepSeek 仍不支援）。

## 流程備忘
- 所有改動走 PR。PR #2 base 設 `windows-native-mvp`。
- 建置：`dotnet build AIROOMNative.slnx`、測試 `dotnet test AIROOMCore.Tests/...`（現 68 全綠）。
- app.exe 執行中會鎖檔 → build 前先 `taskkill //IM AIROOM.exe //F`。
- 啟動測試：`AIROOMApp/bin/x64/Release/net10.0-windows10.0.19041.0/AIROOM.exe`（無 RID 路徑）；log 在 `%LOCALAPPDATA%\AIROOMNative\startup.log`。
- Start-Process 之後 Get-Process 判斷存活；UI 在使用者桌面可見。
