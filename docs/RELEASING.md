# 發版規則

同一個 repo 放 macOS 和 Windows 兩個 App，Releases 也共用，所以 tag 要分平台。

| 平台 | tag | 標題 | 附件 | Latest |
|---|---|---|---|---|
| macOS | `macos-v<版本>`（例：`macos-v0.2.108`） | `AI Room Native <版本> for macOS` | `AI-Room-Native-<版本>.dmg` | 可以 |
| Windows | `windows-v<版本>` | `AI Room Native <版本> for Windows` | Windows 安裝檔 | **不可以**：`gh release create … --latest=false` |

macOS 0.2.107 以前用的是 `v<版本>`，0.2.101 起的 App 兩種 tag 都認得。0.2.64 以前的 App 只認得 `v<版本>`，但那些版本已改成草稿、不再公開。

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
gh release create macos-v<版本> dist/AI-Room-Native-<版本>.dmg --target main --title "AI Room Native <版本> for macOS" --notes-file <說明檔>
```
