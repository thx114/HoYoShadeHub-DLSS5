# DLSS5 Preset Switcher

这是一个独立的 ReShade add-on，用来在游戏内切换别人提供的 DLSS5 / RenoDX 画面预设。

## 安装

将 `dlss5-preset-switcher.addon64` 放入：

```text
<HoYoShade>\reshade-shaders\Addons\
```

推荐把预设放入：

```text
<HoYoShade>\reshade-shaders\Addons\DLSS5-Presets\
```

也可以直接把 `.ini` 放在 `.addon64` 旁边。`DLSS5-Presets` 会递归扫描子目录，方便按游戏或作者整理。插件不会删除文件；在资源管理器中添加或删除文件后，在游戏内点击 **Refresh files** 即可更新列表。

## 预设格式

预设必须是 UTF-8 编码的 INI 文件。插件支持两类内容：

1. **ReShade effect preset**：包含 `[xxx.fx]`，或 `[GENERAL]` 中的 `Techniques`、`TechniqueSorting`、`PreprocessorDefinitions`。这类文件会通过 ReShade API 切换当前 effect preset。
2. **RenoDX / DLSS5 配置**：例如 `[RENODX-DLSS]` 下的 `DirectNeuralRenderingHookPoint` / `DirectNeuralRenderingHookStage`，以及 `[RenoDX.DLSS5]` 下的 `NRHookPoint`、`DX11Source`、`EnableHooks`。这些键会通过 ReShade 的公开配置 API 写入当前全局配置，让 `renodx-dlss.addon64` 与 `renodx-dlss5.addon64` 在其支持动态读取或配置变更通知时重新读取。兼容旧预设时也接受 `[RENODX-DLSS5]` 这类包含 `RENODX` 的段名。

一个文件可以同时包含两类内容。插件不直接编辑 `ReShade.ini`，也不会自动启用被用户禁用的 RenoDX add-on。

## 游戏内操作

打开 ReShade overlay 后进入 **DLSS5 Presets**：

- 点击预设名称或 **Apply**：应用该文件；
- **Refresh files**：重新扫描新增/删除的 `.ini`；
- **Auto-reload active file**：当前文件的修改时间变化后自动重新应用；
- **Clear / restore original**：恢复第一次应用前的 RenoDX/DLSS5 配置和 ReShade 当前 preset。

首次应用后，插件会保留本次会话的原始配置作为恢复基线。重新启动游戏会建立新的基线。

## 注意事项

- 示例文件中的 RenoDX key 只用于说明格式，不保证适用于所有 RenoDX 版本；以实际 `renodx-dlss.addon64` / `renodx-dlss5.addon64` 版本支持的 section 和 key 为准。
- 对于 ReShade 的 `[GENERAL]`、`[INPUT]`、`[ADDON]` 等 host-level 配置，插件不通过逐键写入方式覆盖它们；ReShade effect preset 应使用 `set_current_preset_path` 由 ReShade 自己加载。这避免共享预设意外关闭 add-on 或修改快捷键。
- 配置 API 没有公开的“删除键”操作，因此原本不存在的 RenoDX key 在清除时会恢复为空字符串；如果目标 add-on 将空字符串视为默认值，这就是预期行为，否则建议在预设中写出明确的默认值。
- 插件只负责配置对接，不实现 RenoDX 内部 hook，也不替代 `renodx-dlss.addon64` 或 `renodx-dlss5.addon64`。如果某个 RenoDX 版本只在进程启动时读取配置，那么切换后需要按该版本要求重启游戏；插件无法通过公开 ReShade API 强制刷新 RenoDX 的私有运行时状态。

## 构建

在安装了 Visual Studio 2022 C++ 工具的开发者命令行中执行：

```bat
build.cmd
```

也可以提前设置 `VCVARS` 指向 `vcvars64.bat`。输出为 `dlss5-preset-switcher.addon64`。

本目录随附 ReShade 6.8 SDK 头文件和 Dear ImGui 1.92.5 头文件；其授权文件分别来自 `reshade/LICENSE.txt` 与 `imgui/LICENSE.txt`。
