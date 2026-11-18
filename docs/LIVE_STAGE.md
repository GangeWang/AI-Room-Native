# 直播舞台：TTS + VTube Studio（Live2D）

左側「直播舞台」設定；茶歇室的「雙 AI 同台直播」開始後自動套用。

## 流程

```text
AI 回覆（可含 [happy] 等情緒標籤）
  → LiveCueParser：只認 8 種白名單情緒，其他標籤丟棄；共用紀錄只存去標籤後的文字
  → LiveEvent（speaker / utterance / emotion / turnID / sequence）
  → LiveStageController：觸發該情緒對應的 VTS 熱鍵 → macOS TTS 念出 → 依音量 30 Hz 注入 MouthOpen
  → 念完才進入下一輪（兩位 AI 不會同時說話）
```

念的同時，下一位 AI 已經開始寫自己的台詞（只等上一句定稿，不等念完），念完再等「間隔」秒數（0–180，預設 3）就接上。
代價：某人說話時插入的觀眾留言或主持人插話，要再晚一輪才會被接到；每 12 輪的摘要那一輪不會提前準備。

## 第一次設定（兩位各一個原生版 VTube Studio）

用的是原生版 `/Applications/VTubeStudio.app`（Apple Silicon，bundle id `com.Denchi.VTube-Studio`，不需要 Steam）。

1. AI Room →「直播舞台」→ 勾選「連接 VTube Studio」→ 按 **開啟 VTube Studio**。
   AI Room 依連接埠由小到大一個一個開（`open -n /Applications/VTubeStudio.app --args --nosteam`），等前一個的 API 起來才開下一個，開好就自動連線。
   手動開也可以：同一行指令執行兩次，**等第一個起來再開第二個**。
2. 第一次使用時，在 VTS 設定開啟 **Start API**。兩個實例共用同一份設定，開一次兩個都會生效；開好後再按一次「開啟 VTube Studio」或各自按「連線」。
3. 兩個 VTS 各跳一次授權視窗，外掛名分別是 `AIROOM Native (codex)`、`AIROOM Native (claude)`，都按 **Allow**。Token 存在本機加密資料，之後重開 AI Room 不用再按。
4. 在每位成員卡的「模型」選該成員的 Live2D 模型。兩個實例共用「上次的模型」，開起來會是同一個；連線後 AI Room 會自動換成這裡選的。
5. 把各情緒對應到該模型的熱鍵，勾選「用語音念出發言」，選聲音 → 按「試演」。
6. OBS：加兩個 NDI 來源，`Live2D Camera NDI5`（先開的，8001，預設是 GPT 娘）和 `Live2D Camera NDI5 2`（後開的，8002，預設是 Claude）。聲音從系統預設輸出播放，以「桌面音訊」擷取。

### 連接埠和 NDI 名稱依啟動順序決定

VTS 每開一個實例就佔用 8001 起的下一個空連接埠，NDI 名稱也照順序編號，不會綁定某位成員。所以永遠先開 GPT 娘、再開 Claude；用「開啟 VTube Studio」按鈕就會照這個順序。

### 授權為什麼是每位成員一個外掛名

兩個實例共用 `StreamingAssets/Config/Plugins` 裡的授權檔，而 VTS 每個外掛名只留一份授權。以前兩位都叫 `AIROOM Native`，第二位按 Allow 時，第一位的 token 會立刻失效。現在每位成員各用一個外掛名，各自授權，互不影響。從舊版升級後，兩邊各要再按一次 Allow。

### 斷線

VTS 關掉或斷線時，AI Room 會在 1、2、5 秒後重試，之後每 10 秒一次，約 5 分鐘後停止。自動重連只用存好的 token，不會跳授權視窗；token 失效時會停下來，請你按「連線」重新授權。

## AI 接管骨架

成員卡勾「讓 AI 接管骨架」後，AI 可以在台詞裡寫姿勢標籤，念到那句時生效：

```text
[pose head=-15,5,10 gaze=left eyes=0.9 brows=0.6 smile=0.8 arms=0.8,0 elbows=0.8,0 in=0.5] 你看那邊！
```

- `head` 轉頭／點頭／歪頭（角度）、`gaze` 視線、`eyes` 眼睛、`brows` 眉毛、`smile` 笑容：送 VTS 內建的追蹤參數。
- `arms=左,右`（上臂）、`elbows=左,右`（手肘），各 -1～1：送成員自己的 VTS 自訂參數 `AIROOM<成員>ArmL/ArmR/ElbowL/ElbowR`。連線時 AI Room 會自動建立這些參數，但要在模型的參數設定裡把它們接到手臂參數，例如 claude 模型的 `ParamArmLA/RA`（上臂）、`ParamArmLB/RB`（手肘），範圍 -1～1 對應 -10～10。
- AI Room 以每秒 30 次平滑補間送出；沒說話時自動眨眼、呼吸、輕微晃動。數值都會限制範圍，無法辨識的標籤直接丟掉、不會念出來。
- 模型要有做該部位的形變才會動。直播舞台頁的「骨架串流」那行會顯示已送出的幀數、頭部角度和嘴型數值，方便對照。

## 限制（尚未完成）

- TTS 使用 macOS 內建聲音；更自然的聲音需在系統設定下載「加強版／高品質」語音。外部 TTS 服務尚未串接。
- 直播平台留言（YouTube／Bilibili）仍需手動插入；OBS + 直播姬雙端推流之後處理。
- 原生版 VTS 本身的 API、熱鍵、嘴型、雙實例與 NDI 已在 vtube-native 專案實測；AI Room 這邊的雙實例授權、模型自動載入與自動重連，以模擬 VTS 的單元測試覆蓋，實機驗收待做（兩邊按 Allow → 重開 AI Room → 兩邊都能自動認證）。
- `URLSessionWebSocketTask` 應會自動合併 VTS 拆成多段的長訊息（推測，尚未實機確認）。
