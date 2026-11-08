# 發版規則

同一個 repo 放 macOS 和 Windows 兩個 App，Releases 也共用，所以 tag 要分平台。

| 平台 | tag | 附件 | Latest |
|---|---|---|---|
| macOS | `v<版本>`（例：`v0.2.64`） | `AIROOMNative-<版本>.dmg` | 可以 |
| Windows | `windows-v<版本>` | Windows 安裝檔 | **不可以**：`gh release create … --latest=false` |

## 為什麼 Windows 版不能標成 Latest

0.2.64 以前的 macOS App 只讀 GitHub 的「Latest」那一版來提醒更新。Windows 版若成了 Latest：

- 舊的 macOS App 讀不懂 `windows-v…`，於是不再提醒任何 macOS 更新；
- tag 若沒有平台前綴，舊 App 還可能把 Windows 的版本號當成 macOS 的新版。

0.2.65 起的 macOS App 改讀整份發版清單，只挑 `v…` 或 `macos-v…`、而且附有 DMG 的版本，不再受 Latest 影響。但只要還有人用舊版，就要遵守上面的規則。

## 版本號

macOS 的版本是 `0.2.<commit 數>`，commit 數算的是整個 repo。所以 Windows 的 commit 也會讓 macOS 的版本號往前跳：號碼不連續，但一定只增不減，更新比對不受影響。

## macOS 發版步驟

```bash
cd macos
swift test
./Scripts/package-app.sh && ./Scripts/make-dmg.sh
gh release create v<版本> dist/AIROOMNative-<版本>.dmg --target main --title "AIROOM Native <版本>" --notes-file <說明檔>
```
