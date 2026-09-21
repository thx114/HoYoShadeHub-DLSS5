# 多游戏 ReShade.ini 管理 —— 规格（从真实样本反推）

样本（用户提供）：

```
D:\APPS\miHoYo Launcher\games\ZenlessZoneZero Game\ReShade.ini
D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.ini
D:\APPS\Star Rail\games\Star Rail Game\ReShade.ini
D:\WeGameApps\rail_apps\蓝色星原：旅谣(2002738)\ReShade.ini
D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons\   ← 插件真身都在这一个目录
```

---

## 1. 真实模型（重要，和 Hub 的假设不同）

**`ReShade.ini` 在每个游戏 exe 旁边，一个游戏一份；但插件文件是全局一份。**

四份样本的公共点：

```ini
[ADDON]
AddonPath=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons\
DisabledAddons=...
LoadFromDllMain=...

[GENERAL]
EffectSearchPaths=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Shaders\**
TextureSearchPaths=D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Textures\**
PresetPath=D:\APPS\HoYoShadeHub\HoYoShade\Presets\Mod OFF.ini
```

所以：

| 东西 | 作用域 |
| --- | --- |
| 插件文件（`.addon64`、配套 `.dll`） | **全局一份**，在 `AddonPath` 指向的目录 |
| 启用/禁用、hook 点等开关 | **每个游戏一份**，在各游戏目录的 `ReShade.ini` 里 |

「开关某个游戏里的某个插件」= 改那个游戏的 `ReShade.ini`，插件文件本身不动。

---

## 2. 三个关键键

### `DisabledAddons`（逗号分隔）

格式：`<Addon 内部名>@<文件名>`

真实样本：

```ini
DisabledAddons=RenoDX DLSS_A@renodx-dlss5-super-anus(1.0.8.18).addon64,RenoDX DLSS@renodx-dlss(9.17.12).addon64
DisabledAddons=RenoDX DLSS@renodx-dlss(ShortFuse_9.11.6).addon64
DisabledAddons=
```

- 前半段是 addon 自己注册的名字（`AddonInit` 里的 `info.name`），**不是我们能随便编的** —— 它由 DLL 决定。
- 后半段是文件名，**这个我们能控制**。
- 结论：**禁用时前半段必须写对**，否则 ReShade 匹配不上。名字的来源只有两个：
  1. 从已有 `DisabledAddons` 里学到（用户手工禁过一次就有）；
  2. 直接读 DLL 的 addon 注册名（要解析 PE / 或者让 ReShade 自己吐出来）。
- 兜底方案：写文件名的同时把已知的名字带上；不知道名字时**退化成重命名**（社区里已经在这么做，见下）。

### `LoadFromDllMain`（逗号分隔，DLSS5 专用）

```ini
LoadFromDllMain=renodx-dlss(9.17.12).addon64,,renodx-dlss5-super-anus(1.0.8.18).addon64
```

- 只有**文件名**，没有 `Name@` 前缀。
- 样本里出现了**空元素**（`a,,b`）—— 解析/写回时必须原样保留位置，不能过滤空项。
- 用途：某些 NR 插件做帧生成时，必须从 `DllMain` 阶段就加载，不能等 ReShade 后期加载。

### `DirectNeuralRenderingHookPoint`

```ini
DirectNeuralRenderingHookPoint=4     # 出现在 ZZZ / 星铁 / 蓝色星原
（Genshin Impact 的样本里没有这个键）
```

- **0 = off**，UI 上直接把 0 显示成 `off`。
- 1/2/3/4 是不同取值，语义未知且可能变 —— **不要做别名**，原样显示数字。
- **前置条件**：只有 `super-anus` 或 `renodx-dlss(ShortFuse)` 在装的时候才允许改这个键。

---

## 3. 插件文件名的命名约定（版本号就藏在这里）

真实目录 `D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons\`：

```
renodx-dlss(9.17.12).addon64                    2.6 MB
renodx-dlss(ShortFuse_9.11.6).addon64x          2.7 MB   ← 注意 .addon64x，这就是「禁用」的另一种做法
renodx-dlss5-super-anus(1.0.8.18).addon64       3.7 MB
renodx-neural-interposer-nvngx.dll.addon64      1.6 MB
dlss5-bridge.addon64                            0.5 MB
```

模式：`<slug>(<版本>).addon64`，版本可能带分支前缀（`ShortFuse_9.11.6`）。

**这就是「现有插件版本怎么获取」的答案** —— 从文件名括号里解析，不需要读 PE 或问 GitHub。

扩展名约定：

| 扩展名 | 含义 |
| --- | --- |
| `.addon64` | 启用的 64 位 addon |
| `.addon64x` | **被重命名禁用的** addon |
| `.addon32` / `.addon32x` | 32 位同上 |
| `.dll` | 配套运行时依赖（`nvngx_*`、`sl.*`），不是 addon |

目录里还有大量非插件垃圾：`.txt`、`.zip`、以及**用 0 字节空文件当备注**（`该插件效果会差一些，仅作为备选`）。扫描时必须过滤。

---

## 4. DLL 管理

清单源（用户指定）：<https://github.com/RankFTW/RHI/blob/main/dlss_manifest.json>
→ raw: `https://raw.githubusercontent.com/RankFTW/RHI/main/dlss_manifest.json`（6.3 KB）

结构：

```json
{
  "dlss":   [ { "version": "310.9.1", "url": "https://github.com/RankFTW/rhi-repo/releases/download/dlss-310.9.1/nvngx_dlss_310.9.1.zip" } ],
  "dlssd":  [ ... ],   // nvngx_dlssd  (Ray Reconstruction)
  "dlssg":  [ ... ],   // nvngx_dlssg  (Frame Generation)
  "dlssnr": [ ... ],   // nvngx_dlssnr (Neural Rendering)
  "streamline": [ ... ] // sl.*.dll 一整套
}
```

每个条目是一个 zip（`nvngx_dlss_<version>.zip`），解压出来的 dll **直接放进插件目录**
（样本里 `nvngx_dlss.dll` / `sl.*.dll` 和 `.addon64` 混在一起）。

当前已装版本怎么读：实数样本里这些 dll 没有版本后缀，只能读 **PE 版本资源**
（`FileVersionInfo.GetVersionInfo(path).FileVersion`）—— 这条待验证。

---

## 5. 需要做到的

1. **发现各游戏的 ReShade.ini**
   - 自动：Hub 已知的游戏安装路径（`GameLauncherService`，按 GameBiz）+ `<游戏目录>\ReShade.ini` 存在性
   - 自动：扫常见根目录（`D:\APPS\miHoYo Launcher\games\*`、`D:\APPS\Star Rail\games\*`、`D:\WeGameApps\rail_apps\*`）——用户这三个样本都在这种"启动器 + games 子目录"结构里
   - 手动：添加任意目录
2. **每个游戏独立开关每个插件**
   - 两种手段：写 `DisabledAddons`（正规）/ 重命名 `.addon64 ↔ .addon64x`（全局禁用，影响所有游戏）
   - UI 要能区分这两种
3. **编辑 `LoadFromDllMain`**（保留空槽位）
4. **编辑 `DirectNeuralRenderingHookPoint`**（0 显示 `off`；只有 super-anus / renodx-dlss(ShortFuse) 在装时可改）
5. **插件显示名用发布名**，不是文件名
6. **版本管理**：文件名括号 = 已装版本；GitHub release tag = 最新版本；显示「有更新」
7. **DLL 管理**：从 `dlss_manifest.json` 选版本、下载 zip、解压到插件目录

---

## 6. addon 内部名的获取（已解决）

### 6.1 ReShade 源码给的权威答案

`crosire/reshade` → `source/addon_manager.cpp`，**禁用判定分支**：

```cpp
std::vector<std::string> disabled_addons;
config.get("ADDON", "DisabledAddons", disabled_addons);
...
// Avoid loading library altogether when it is found in the disabled add-on list
if (addon_info info;
        std::find_if(disabled_addons.cbegin(), disabled_addons.cend(),
                [file_name = path.filename().u8string(), &info](const std::string_view addon_name) {
                        const size_t at_pos = addon_name.find('@');
                        if (at_pos == std::string_view::npos)
                                return false;                       // ← 没有 @ 永远匹配不上
                        info.name = addon_name.substr(0, at_pos);   // ← @ 前：只用于显示
                        info.file = addon_name.substr(at_pos + 1);
                        return file_name == info.file;              // ← 只比文件名
                }) != disabled_addons.cend())
{
        info.handle = nullptr;
        info.external = false;
        addon_loaded_info.push_back(std::move(info));
        continue;                                                   // ← 根本不 LoadLibrary
}
```

三条结论：

1. **`@` 必须存在**。没有 `@` 的条目 `return false`，永远命中不了。
2. **`@` 前面的名字只影响显示**。匹配只用 `@` 后面那段文件名，所以名字猜错不会导致禁用失效。
3. **禁用时不 LoadLibrary** —— addon 根本没被加载，所以也不存在「加载它才能问名字」的循环依赖。

### 6.2 名字从哪来

同一个函数里，**启用**分支：

```cpp
const HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | ...);
const auto init_func = ... GetProcAddress(module, "AddonInit");
if (init_func != nullptr && !init_func(module, g_module_handle)) { ... }

if (addon_info *const registered_info = find_addon(module))
        registered_info->external = false;
else
        // "No add-on was registered by '%s'. Unloading again ..."
```

名字是 addon 在 `DllMain` / `AddonInit` 里**自己调 `ReShadeRegisterAddon(name, ...)` 注册**的。
**不在 PE 导出表，也不一定在版本资源里** —— 所以不真正加载 DLL 拿不到权威值。

### 6.3 实测（用户机器上的真 DLL）

| 文件 | PE `FileDescription` | ini 里 ReShade 写的 | 结论 |
| --- | --- | --- | --- |
| `renodx-dlss5-super-anus(1.0.8.18).addon64` | `RenoDX DLSS_A` | `RenoDX DLSS_A` | 版本资源可用 ✅ |
| `renodx-dlss(9.17.12).addon64` | *（空）* | `RenoDX DLSS` | 版本资源不可用 ❌ |
| `renodx-dlss(ShortFuse_9.11.6).addon64x` | *（空）* | `RenoDX DLSS` | 同上 ❌ |
| `dlss5-bridge.addon64` | `DLSS 5 Bridge` | — | 可用 ✅ |

对 `renodx-dlss` 在二进制里做候选名包含匹配 → **命中 `RenoDX DLSS`**。

### 6.4 最终策略：分层解析，猜不准也不影响功能

`AddonNameResolver.Resolve()` 的优先级：

1. **学到的名字** —— 用户在任何游戏里用 ReShade 叠加层手动开关过一次，ReShade 就会把真名写进那个游戏的 `DisabledAddons`，我们读到就学下来（存 `AddonNameCache`，文件名 → 名字）。这是最可信的来源，而且会**自愈**：用一个游戏学到，所有游戏都受益。
2. **已知映射** —— catalog 里维护的 slug → 内部名。
3. **PE 版本资源 `FileDescription`** —— 有就用。
4. **二进制候选名匹配** —— 拿 catalog 给的发布名去 DLL 字节里找（UTF-16 / UTF-8 都试）。
5. **兜底** —— 用文件名里的 slug，保证 `@` 前面永远有东西。

因为第 6.1 节第 2 条，**第 3~5 步猜错都只是显示难看，不影响禁用是否生效**。

## 7. 剩下要做的

- 游戏发现（用户样本在 `D:\APPS\miHoYo Launcher\games\*`、`D:\APPS\Star Rail\games\*`、`D:\WeGameApps\rail_apps\*` 这类「启动器 + games 子目录」结构里）
- 多游戏 ReShade.ini 的管理界面
- DLL 管理（按 `dlss_manifest.json` 选版本、下载 zip、解压进插件目录）
- 「有新版本」提示 + slug → 发布名的映射表
- DLL 已装版本识别（实测 dll 文件名不带版本，得读 PE 版本资源，这条还没验证）
