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

## 第一次設定

1. VTube Studio：設定 → 開啟 **Start API**（預設連接埠 8001），載入 GPT 做好的 Live2D 模型。
2. AIROOM →「直播舞台」→ 勾選「連接 VTube Studio」→ 按「連線」→ 在 VTS 跳出的 *AIROOM Native* 授權視窗按 **Allow**。授權 Token 存在本機加密資料，下次不會再問。
3. 在負責這個模型的成員卡勾選「由這位驅動 VTube Studio」，把各情緒對應到模型的熱鍵（表情 Toggle 或動作）。
4. 勾選「用語音念出發言」，為每位成員選擇聲音、語速、音高 → 按「試演」確認表情與嘴型。
5. 聲音從系統預設輸出播放；OBS 以「桌面音訊」擷取即可。

## 限制（尚未完成）

- VTube Studio 一次只載入一個主模型：目前只有勾選「驅動」的成員會動嘴與換表情，另一位只有聲音。第二個形象（第二個 VTS、或 VTS 內的 Live2D 道具）之後再實測決定。
- TTS 使用 macOS 內建聲音；更自然的聲音需在系統設定下載「加強版／高品質」語音。外部 TTS 服務尚未串接。
- 直播平台留言（YouTube／Bilibili）仍需手動插入；OBS + 直播姬雙端推流之後處理。
- VTS 實機連線、熱鍵與嘴型同步尚待你在裝好 VTube Studio 後驗收；協定層以模擬 VTS 的單元測試覆蓋。
