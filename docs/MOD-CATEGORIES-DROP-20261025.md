# Mod 分类与界面维护（2026-10-25）

## 当前行为

- XXMI/Rocket 按现有角色文件夹分组，不迁移现有 Mod。分类筛选保留全部角色与未分类入口。
- Rocket 预览图按约定文件名进行有界嵌套查找；跳过目录链接，不将材质图片当预览图。
- 缩略图在独立容器内以容器中心做 Composition 缩放。
- 角色栏宽度从 260 改为 160 DIP，外间距 8、内边距 4、行最小高度 32、字号 13。移除标题、关闭按钮与说明；保留顶部筛选按钮。

## 验证边界

- 分类目录策略回归：60 项通过；预览查找：7 项通过，记录见 artifacts/mod-category-tests.log 及相关测试程序。
- 11:53 部署：Release 构建 308 警告、0 错误，980 文件哈希匹配，4 文件替换。主 DLL SHA256：EB878C5C0EBBF945DC1CD3EC34A101A314B92B0BCF2675C42A86145B50A61F79。
- 备份：D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-nr-20261025-115301。
- 紧凑分类实际 UI 测试脚本：artifacts/Test-ModCompactUi-20261025.ps1；截图与结果：artifacts/mod-compact-ui-20261025。
- Windows 文件拖放及全页面覆盖包路由代码已编写；跨进程合成 WM_DROPFILES 测试收到 0 条路径，尚不能宣称 Explorer 实际拖放端到端验证通过。不得将该测试失败直接归因于应用导入逻辑。
- 未进行真实游戏测试。
## 12:00 后续修正

- 分类栏开合改为同步设置 Visibility；先停止宽度和透明度动画，避免旧动画的最终值覆盖重新打开状态。顶部开关同步状态。
- Mod 页选择使用独立的 mod_browser_use_rocket 配置键，按游戏持久保存。首次没有该键时沿用启动设置作为默认值；在页面选择后端不改变游戏启动开关。
- Rocket 模式将 ComboBox_Importer 设为 Collapsed，XXMI 模式恢复显示。
- Release 构建 308 警告、0 错误；部署 980 文件匹配，2 文件替换。主 DLL SHA256：457236CCEEB9DD2DF97AB19B4FA77A4D9A41C28973CB28EF426B80E232FB673D。
- 备份目录：D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-nr-20261025-120050。

## 用户后续约定

- 用户明确要求“不用你测试启动器”：从此停止自主启动及 UI 操作，仅执行构建、静态检查和部署文件校验；此直接要求优先于 AGENTS.md 中的默认自主 UI 验证流程。
- Mod 开关补充独立的圆角遮罩 Composition 阴影（模糊 8、向下 2、透明度 0.7），与图片缩放容器分离，且阴影层不接收点击。
- 此前后端持久保存键已在便携版数据库中确认写入；重启检查结果见 artifacts/mod-backend-restart-20261025/receipt.json。

- 阴影版本 Release 构建通过（308 警告、0 错误），SHA256：748BEC02773F4CA09601CBDFA9CFF88E4AE964ECB02C1C4D8194362CF09A4936。test 仍在运行，本次阴影版本未部署；部署脚本 artifacts/Deploy-ModShadowTest-20261025.ps1 已准备。未继续启动或操作 UI。
