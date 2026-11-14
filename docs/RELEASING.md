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

## Windows 發版步驟

前置：.NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`），不需要 Visual Studio。發版前先確認 `windows/AIROOMApp/AIROOMApp.csproj` 的 `<Version>` 已與 macOS 對齊（`0.2.<repo commit 數>`）。

```bash
# 測試（單元全量；live 管線另加 AIROOM_LIVE_CLI=1）
dotnet test windows/AIROOMCore.Tests/AIROOMCore.Tests.csproj -c Release

# publish：自含、免安裝。先殺掉在跑的舊版，否則輸出檔被鎖住
taskkill //IM AIROOM.exe //F
dotnet publish windows/AIROOMApp/AIROOMApp.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true
```

輸出在 `windows/AIROOMApp/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/`。csproj 已有 `WindowsPackageType=None`＋`WindowsAppSDKSelfContained=true`，所以整個 `publish/` 資料夾自含 .NET 與 Windows App SDK 執行階段，解壓後直接跑 `AIROOM.exe`，免安裝。

```bash
cd windows/AIROOMApp/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64
zip -r AI-Room-Native-<版本>-windows-x64.zip publish
gh release create windows-v<版本> AI-Room-Native-<版本>-windows-x64.zip \
  --target <載體 commit> --title "AI Room Native <版本> for Windows" \
  --notes-file <說明檔> --latest=false
```

注意：

- `--latest=false` 不可省（見上表）。
- `--target` 指到帶著這個版本的 commit。版本號修改的 PR 若還沒合併，就指到那個分支的末端，不必等 PR（0.2.111 實際指到 `windows-release-version` 的 `a7a63c3`，PR #29 之後才合併）。
- 發完用 `gh release view windows-v<版本> --json tagName,isDraft,assets` 驗證。瀏覽器和 Releases 頁面的 HTML 快取很重，頁面看不到不代表沒發出去，以 API 為準。
- 附件命名 `AI-Room-Native-<版本>-windows-x64.zip`；說明檔裡的安裝需求至少要有 Windows 10 19041（x64）。
