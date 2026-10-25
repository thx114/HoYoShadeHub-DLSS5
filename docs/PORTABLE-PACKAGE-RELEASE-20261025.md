# 普通便携包与 GitHub 发布（2026-10-25）

本轮用户明确要求提交 Git，并向 `thx114/HoYoShadeHub-DLSS5` 发布普通包（非 Full）。版本为 `1.4.6.14`，标签为 `v1.4.6.14`。

## 构建

```powershell
$env:HYSHADE_DOTNET = 'C:\Users\thx11\.dotnet10\dotnet.exe'
.\package.ps1 -Version '1.4.6.14' -Architecture x64 -Configuration Release `
    -Output 'build/portable-1.4.6.14/HoYoShadeHub'
```

产物为 `build/release/HoYoShadeHub_Portable_1.4.6.14_x64.zip`。只包含外层启动器、`version.ini` 和版本化应用，不带 `HoYoShade/` 框架、用户数据库、缓存或日志。沿用普通包原有默认的数据路径行为，没有额外启用 `-PortableLocal`。

普通入口现也调用 `tools/package-storage.ps1` 清理之前的便携 ZIP（含上一轮 Full）、旧暂存，并在完成后删除本轮暂存；保留校验 JSON 等小型记录。删除失败按 `AGENTS.md` 约定转移到指定 cleanup。上一轮 Full 的构建结果作为历史记录保留在 `FULL-PACKAGE-BUILD.md`，不代表其 ZIP 会长期占用空间。

## 提交与发布边界

- 提交当前启动器、扩展、测试、云端种子及打包维护源码，包含编译依赖的新文件；不提交 SDK、第三方研究 checkout、二进制包、日志及本机数据库。
- 保留远端已发布的目录更新提交；不强推、不覆盖已有标签。Git smart HTTP 不可用时，通过 GitHub Git Data API 校验和传输完全相同的 Git 对象，更新分支前检查远端基线。
- GitHub Release 只上传普通 ZIP 和 SHA256 文件；发布正文见 `release-notes/1.4.6.14.md`。发布后回读标签、提交和资产 digest，核对实际文件名、大小与哈希。
- 不启动启动器或游戏，构建和离线回归不等同于真实交互/游戏测试。
