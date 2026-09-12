# Wv.plugin.DeskPet 发布说明

桌宠交互增强版：基于官方桌宠交互 4.2.2 的独立增强分支（可与官方桌宠共存安装）。

作者 GitHub 用户名：**Yutolan1**

## 发布产物（均在 `publish/` 目录）

| 文件 | 说明 |
| --- | --- |
| `Wv.plugin.DeskPet-4.3.0.zip` | 插件市场安装包（源码格式，客户端安装后自动热编译） |
| `Wv.plugin.DeskPet.json` | 插件市场注册文件（提交到 Alife.PluginMarket 的唯一文件） |
| `发布说明-DeskPet.md` | 本文件 |

zip 内容（无顶层文件夹，与安装后的 `Storage/Plugins/Wv.plugin.DeskPet` 目录一致，共 33 个文件）：

- `manifest.json` — 版本 4.3.0，依赖 Alife.Function.FunctionCaller、Alife.Function.MessageFilter
- `DeskPetService.cs` / `DeskPetServiceConfig.cs` / `DeskPetServiceUI_razor.g.cs` / `IDeskPet.cs`
- `Live2DDeskPet/` — 主体与各模块（含新增 RegionInteractionModule / GestureInteractionModule / PetInteractionHub）
- `Resources/Live2DDeskPet/` — 渲染页与 Live2D/Pixi 运行时

SHA256: `ea2b388db55c1c8440bd138b706fc28e33746ecb059b290c665491ad7d1a3f52`

## 本次更新（4.3.0）

基于官方桌宠交互 4.2.2 的增强分支（独立插件 id，可与官方桌宠共存安装）。

feat:互动区域可配置——每个区域可单独开关双击/长按/划动/滚轮，并各自配置触发话术；新建区域默认只开双击
feat:新增「交互开关」页，集中管理原版 8 种交互（启动反馈/点头部/点身体/连续触摸/旋转/大幅晃动/快速移动/鼠标转圈）
feat:新增「模型交互」页，可视化编辑模型 Interaction 交互池（气泡文本+表情+动作），写回 model3.json（自动 .bak 备份）并立即生效
feat:复活原版命中区交互（点头部/点身体/连续触摸）
feat:功能管理界面改为菜单栏（模型/区域/交互开关/模型交互），缓解页面过长
feat:支持模型原地切换（不销毁窗口），切换时告知角色并注入当前模型名与可用表情/动作；角色可主动切换模型
feat:表情/动作支持指定持续时长与长时间保持
fix:移除易与拖拽混淆的自研画圈/甩动识别，避免拖拽被误判为互动
fix:修复区域设置面板点击穿透、编辑中面板自动退出等问题


## 依赖

- `Alife.Function.FunctionCaller`（任意版本）
- `Alife.Function.MessageFilter`（任意版本）

## 与官方桌宠的关系

本插件使用独立 id `Wv.plugin.DeskPet` 与独立命名空间，模块 id 为 `Wv.plugin.DeskPet.DeskPetService`、`Wv.plugin.DeskPet.Live2DDeskPet`，
因此可与官方 `Alife.Function.DeskPet` 同时安装而不冲突（模块系统按类型全名注册，重名会报错）。

## 提交插件市场

把 `publish/Wv.plugin.DeskPet.json` 复制为 <https://github.com/BDFFZI/Alife.PluginMarket> 仓库根目录的 `Wv.plugin.DeskPet.json`，提 PR 即可。
`releases.4.3.0.file` 已指向本仓库 Release 资产，无需再改。
