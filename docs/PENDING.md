# 待處理事項

已知但還沒處理的事。做完就從這裡刪掉，在對應的 PR 或 commit 裡註明。

最後整理：2026-11-14（Windows 0.2.111 發布後）。

## 資料安全

- **ZCode 寫入邊界的修正還沒實測**（macOS）
  - 寫入任務從 `edit` 模式改成 `build` 模式，由 AIROOM 只放行工作資料夾內的寫檔。規則有單元測試，但 Z.AI 方案額度在 2026-11-09 到期，所以還沒對真實的 ZCode 跑過（`AIROOM_LIVE_CLI=1 swift test --filter ZCodeTests`）。
  - 不確定的是 ZCode 收不收「放行」的回應格式。就算不收，結果是寫不進去，不會變成漏洞。
  - 2026-11-11 更新：Windows 版已對真實的 ZCode 實測過同一套政策（`build` 模式＋放行），「放行」的回應格式 ZCode 收下，寫入工作資料夾成功、終端機指令被拒（Windows PR #20 的 live 測試）。macOS 端本身仍未實測。
- **ZCode 唯讀模式能讀任何檔案，也能連網**（macOS、Windows）：`plan` 模式會放行 Read、Glob、Grep、WebFetch、WebSearch，而且讀檔不限工作區。方向：`session/send` 帶 `toolDisallowlist` 停用 WebFetch 和 WebSearch。這是從 ZCode 的程式碼讀出來的，沒有實測。
- **ZCode 權杖沒有檢查對象**：回答 `interaction/requestProviderRuntimeHeaders` 時沒有檢查 `providerId`。方向：只在對象是 `account:zai-start-plan` 時交出。
- **Windows 版的匯入**：✅ 已完成（2026-11-14，PR #27）。密碼匯入實作時就清掉程式路徑：`ImportPortable` 呼叫 `ClearMachineSettings`，並在匯入前列出原檔引用的資料夾、先備份再覆蓋（`windows/AIROOMCore/LocalState.cs`）。
- **ZCode plan 模式的 ExitPlanMode**（macOS）：模型在 plan 模式把整份方案放進 `ExitPlanMode` 工具呼叫，assistant 文字是空的；macOS 的 `turnResult` 只認文字，會讀不到回覆或被中間旁白提前完成。Windows 已修（`ZCodeTurn`），macOS 待跟進。
  - 動哪裡：`macos/Sources/AIROOMCore/ZCode.swift` 的 `turnResult(_:)` 與 `textParts(_:)`（約 206 行起）。
  - 照什麼修：Windows `windows/AIROOMCore/ZCode.cs` 的 `ZCodeTurn`（`Result`／`FinalText`／`ToolPlan`），測試案例在 `windows/AIROOMCore.Tests/ZCodeTests.cs`，逐條移植成 Swift 測試。
  - 修法要點：(1) 掃完整份訊息清單，記住這輪 user 訊息之後的**最後一則** assistant 訊息，`done` 只看它有沒有 `time.completed`，中間旁白不得提前完成；(2) 每則 assistant 訊息除 `type == "text"` 的 parts 外，也要檢查工具 parts（`type` 以 `tool-` 開頭，或 part 帶 `toolName == "ExitPlanMode"`），從 `input.plan` 抽方案字串，沒有 `input` 就看 part 根層的 `plan`；(3) 完成時的回覆：有方案就交方案（方案優先於它之後的旁白文字），沒有方案才交文字；(4) 輪詢放棄等待時的 `FinalText` 同規則：最後一則 assistant 的文字，否則是抽出的方案。
  - Swift 端注意：`[String: Any]` 解析下 plan 是 String；`textParts` 維持原樣，另寫一個 `toolPlan(_:)` helper，不要動 `decrypt`／`encrypt`。
- **AGY 任務執行不用終端機指令**（macOS）：AGY headless 對 `command` 工具自動拒絕，而且被拒後整個回合沒有回覆（agy 1.5.1 實測）。Windows 已加，macOS 待跟進。
  - 動哪裡：`macos/Sources/AIROOMCore/ProjectFlow.swift` 的 `taskPrompt(_:flow:round:names:ownerName:profile:)`（約 346 行），在「絕對不要動其他路徑」那一段的同一句後面加。
  - 加什麼（照 Windows `windows/AIROOMCore/ProjectFlow.cs` 約 656 行的原句）：「只用檔案工具（讀取、搜尋、編輯、寫檔）完成工作；不要用終端機指令（shell），寫入執行端不會放行，指令會被拒絕而且整個回合不會有回覆。」
  - 只加在任務提示詞，審核／裁定提示詞不動（與 Windows 一致）。
- **GLM API 串流偶發停滯**（兩平台）：實測部分輪次 `turn.started` 後完全沒有 model request 事件，掛住 5 分鐘以上；重試（新工作階段）即可恢復。Windows 的 live 測試對每次呼叫做了重試，App 端仍靠使用者按「接著做」。
- **直播的 TTS／VTube Studio 表演層**（Windows）：macOS 端有 AVSpeechSynthesizer＋VTS websocket（含口型包絡）；Windows 的直播以純文字運行（情緒標籤 parser 已移植、提示詞不帶 cue），表演層待實作。留言匯入、OBS 雙端推流兩端都等 Live2D 模型。
- **匯出檔跨平台互通**：Windows 的密碼匯出已對齊 macOS 的檔案格式（PBKDF2＋AES-GCM＋Apple 日期編碼），macOS 讀 Windows 匯出檔理應可直接運作；尚未實測，建議各做一次往返。

- **降版會悄悄改寫成員用的程式**（macOS）
  - 現象：舊版本讀到不認得的程式名稱（例如還沒有 ZCode 的版本讀到 `ZCode`），會退回 Claude Code（`MemberCard` 解碼：`?? agent.builtInExecutor ?? .claude`），下次存檔就寫回去。
  - 影響：從新版退回舊版後，該成員實際改由 Claude 回答，畫面上沒有任何提示。
  - 已發生：2026-11-09 在開發機上輪流安裝不同分支的版本時，GLM 成員被改成 Claude Code（之後已由使用者刪除）。
  - 方向：保留不認得的程式名稱，暫停該成員並提醒，不要換成別的程式。
  - 狀態：**決定先不處理，之後再做。**

## 跨平台要對齊的

## 功能缺口

- **Gemini（Antigravity）**
  - 不能傳圖片和 PDF。
  - 工位空白畫面寫「可以附上圖片、PDF」，對 Gemini 和 ZCode 都不正確。
  - 茶歇室、圓桌、專案會議室都還沒實測。
  - 人物卡的人設是空的。
- **ZCode（GLM）**
  - 額度用完時只顯示「模型請求失敗（prompt_failed）」，看不出原因；Z.AI 其實有回 `exceed quota limit`。
  - 思考程度寫死成 low／medium／high／max，但各模型支援的不同（例如 GLM-5-Turbo 只有 disabled／enabled），選錯會直接失敗。應該從內建設定讀每個模型的選項。
  - 只收文字。
  - 回覆靠輪詢取得，不是逐字串流。
  - 方案寫死為 `account:zai-start-plan`。
  - 用的是內部協議，ZCode 更新後可能失效。
- **Kimi 執行端**還沒接。
- **直播的延伸功能**：留言匯入、OBS 雙端推流，等 Live2D 模型。

## 專案與發行

- repo 還沒有 LICENSE。
- DMG 沒有經過 Apple 公證（這是決定好的，見 FEATURE_PARITY）。
