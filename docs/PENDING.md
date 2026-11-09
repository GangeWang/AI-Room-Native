# 待處理事項

已知但還沒處理的事。做完就從這裡刪掉，在對應的 PR 或 commit 裡註明。

最後整理：2026-11-09（macOS 0.2.101 發布後）。

## 資料安全

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
  - 只收文字。
  - 回覆靠輪詢取得，不是逐字串流。
  - 方案寫死為 `account:zai-start-plan`。
  - 用的是內部協議，ZCode 更新後可能失效。
- **Kimi 執行端**還沒接。
- **Windows 版的 Kimi、GLM 立繪**：macOS 已加入（原版根目錄的 `KIMI.webp`、`GLM.jpg`），`windows/Scripts/fetch-assets.ps1` 還沒有。
- **直播的延伸功能**：留言匯入、OBS 雙端推流，等 Live2D 模型。

## 專案與發行

- repo 還沒有 LICENSE。
- DMG 沒有經過 Apple 公證（這是決定好的，見 FEATURE_PARITY）。
