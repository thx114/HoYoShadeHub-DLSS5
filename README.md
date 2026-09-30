# HoYoShade Hub · DLSS5 Fork

HoYoShade Hub 的非官方 DLSS5 分支，用于管理米哈游/自定义游戏的 ReShade、插件和 OptiScaler。

## 功能

- 游戏发现与多游戏 `ReShade.ini` 管理。
- 插件安装、卸载、版本切换和按游戏启用/禁用。
- RenoDX DLSS / DLSS5 配置：HookPoint、`DX11Source`、`EnableHooks`、`LoadFromDllMain`。
- DLSS5 / DLSS-NR 运行时检查与组件版本识别。
- OptiScaler 构建下载、选择和启动注入。
- 覆盖包（`filelist.json`）安装，支持 HoYoShade、OptiScaler、插件和运行时 DLL。

## 运行要求

- Windows 10 1809（17763）或更高。
- WebView2 Runtime。
- 使用 DLSS5 / DLSS-NR 时，按插件要求准备游戏目录中的 `nvngx_dlssnr.dll`。

## 下载与更新

从 [Releases](https://github.com/thx114/HoYoShadeHub-DLSS5/releases) 下载
`HoYoShadeHub_Portable_<版本>_x64.zip`，解压到便携版目录并覆盖旧文件。

包不包含 `config.ini`，不会覆盖已有配置、游戏列表和用户数据。当前目录结构为：

```text
HoYoShadeHub.exe
version.ini
app-<版本>/
```

## 构建

需要 .NET 10 SDK。指定 SDK 路径后执行：

```powershell
# 普通便携包
./package.ps1 -Version 1.3.9.7 -Architecture x64 -Configuration Release `
  -DotNet C:\Users\<用户>\.dotnet10\dotnet.exe

# 真便携包：缓存、临时文件和数据库备份也放在包内
./package.ps1 -Version 1.3.9.7 -PortableLocal

# 完整包：额外包含 HoYoShade 框架和精简资源
./package-full.ps1 -Version 1.3.9.7 `
  -ShadeSource 'D:\APPS\HoYoShadeHub\HoYoShade'
```

输出：

```text
build/deploy-<版本>/
build/release/HoYoShadeHub_Portable_<版本>_<架构>.zip
```

普通包不携带 HoYoShade 框架；完整包只携带框架和必要资源，插件目录保持为空，由启动器按需安装。

## 覆盖包

在插件页选择“本地安装”，或将 zip 拖入窗口。带顶层 `filelist.json` 的包按清单安装：

```json
{
  "hysxOverlay": 1,
  "name": "示例覆盖包",
  "version": "1.0",
  "targets": [
    { "from": "HoYoShade", "to": "shade" },
    { "from": "OptiScaler", "to": "optiscaler" }
  ]
}
```

支持的目标：`shade`、`optiscaler`、`modules`、`skip`。安装只覆盖包内文件，不删除用户文件、缓存或账本；没有清单的旧包也兼容按目录结构识别。

## 开发说明

- 主程序：`src/HoYoShadeHub/`
- 扩展服务：`src/HoYoShadeHub.Extensions/`
- 测试：`src/HoYoShadeHub.Extensions.Tests/`
- 构建脚本：`compile.ps1`、`package.ps1`、`package-full.ps1`
- 测试命令：

```powershell
& C:\Users\<用户>\.dotnet10\dotnet.exe run `
  --project src/HoYoShadeHub.Extensions.Tests/HoYoShadeHub.Extensions.Tests.csproj `
  -c Release
```

## 许可

本项目沿用 HoYoShade Hub 的 MIT 许可。HoYoShade 框架、ReShade、字体及其他第三方组件遵循各自许可证。
