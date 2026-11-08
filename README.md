# LYJ

**Siemens NX 图档导入导出 + 加工助手插件** — C# / .NET Framework 4.7.2 / Windows

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![NX](https://img.shields.io/badge/NX-2606%20only-orange)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-512bd4)
[![Release](https://img.shields.io/badge/release-v1.0-brightgreen.svg)](../../releases)

在 NX 里加一个 **LYJ** 菜单和一个 **LYJ** 功能区选项卡，做三件事：

| 能力 | 一句话说明 |
| --- | --- |
| **图档导入 / 导出** | STL、STEP、IGES、Parasolid(X_T) 各四个按钮，一键进出，不用再翻菜单 |
| **加工助手** | 常驻悬浮窗：实时读出选中工序的 35 项参数，其中 20 项可直接改 |
| **加工模板同步** | 扫描模板 prt 生成工序清单与按钮图标，模板一改自动重建 |

<p align="center">
  <img src="文档/截图/01-overview.png" width="840" alt="LYJ 功能区与加工助手悬浮窗">
  <br>
  <sub>功能区「LYJ」选项卡 · 加工助手悬浮窗（参数页，35 项实时回读）</sub>
</p>

> [!WARNING]
> **仅适配 NX / Designcenter 2606。** 其它版本未做适配，NX Open 接口存在差异，装上也不会正常工作。
> 安装程序会把检测到的非 2606 版本标为 `[未适配]`，但不会拦你安装。

> 本项目非 Siemens 官方产品，与 Siemens 公司无关。"NX"、"Siemens" 为其各自权利人的商标，此处仅用于说明适用软件。
> 使用本代码不代表获得 NX 的任何授权，运行插件需要你持有合法有效的 NX 许可。
> 仓库不包含任何 Siemens 的二进制文件、官方数据库内容或加工模板部件。

---

## 一、环境要求

| 项 | 要求 |
| --- | --- |
| NX | **仅 Designcenter / NX 2606**（唯一验证过的版本） |
| 操作系统 | Windows（x64） |
| 运行时 | .NET Framework 4.7.2（Windows 10 及以上自带） |
| 构建 | .NET SDK（`dotnet build`）或 Visual Studio 2019+ |
| 开发前提 | 本机需安装 NX，且有 `NXBIN\managed` 下的 `NXOpen.dll` 等托管程序集 |

> 为什么必须装 NX：`NXOpen / NXOpenUI / NXOpen.Utilities / NXOpen.UF` 由 NX 安装包提供，不随本仓库分发
> （csproj 中 `Private=False`，编译产物不会拷贝这些 DLL）。

## 二、安装

不用编译，直接用打好的安装包：

1. 到 [Releases](../../releases) 下载 `LYJ-Setup-V*.exe`（本地打包时文件名是 `LYJ安装程序_V*.exe`，同一个文件）
2. 右键 → **以管理员身份运行**（要写 `custom_dirs.dat`、注册卸载信息，需要提权）
3. 在列表里勾选要启用插件的 NX（**只有 2606 会正常显示，其它版本带 `[未适配]`**）
4. 重启 NX

装完应该看到：

- **菜单栏** → `LYJ` → 导入 / 导出 / 加工助手
- **功能区** → `LYJ` 选项卡 → 图档导入 / 图档导出 / 加工 三个组

卸载走「控制面板 → 程序和功能 → LYJ」。静默安装、指定安装目录、手动登记等高级用法见
[文档/使用说明.txt](文档/使用说明.txt)。

## 三、功能说明

### 3.1 图档导入 / 导出

菜单里各四个按钮，对应四种格式：

| 格式 | 扩展名 | 导入 | 导出 |
| --- | --- | :---: | :---: |
| STL（网格 / 收敛体） | `.stl` | ✅ | ✅ |
| STEP | `.stp` | ✅ | ✅ |
| IGES | `.igs` | ✅ | ✅ |
| Parasolid | `.x_t` | ✅ | ✅ |

### 3.2 加工助手

点「加工助手」开一个**无模式悬浮窗**，可以一直挂在图形区旁边，不抢焦点、不挡操作。

**实时回读** — 在工序导航器里点中任一工序，窗口立即显示它的参数（200 ms 轮询）。

**35 项参数，20 项可改**（其余只读，用于核对）：

| 类别 | 只读（核对用） | 可改 |
| --- | --- | --- |
| 工序 | 刀路类型、真实刀名 | 程序组、刀路名称、刀路坐标(MCS) |
| 刀具 | 直径、圆角、类型、刀头、长度、刃长、锥角、尖角、夹持库号、刀头偏置、最短刀长、描述 | 刀具名称（换刀）、刀具编号 |
| 切削 | 加工时间 | 切削模式、切削方向、刀路方向、层到层、侧底一致、行距、下刀量 |
| 余量与公差 | — | 侧面余量、底面余量、内公差、外公差 |
| 进给 | — | 转速、进给、刀尖深度、刀尖步距 |

**改法**：单击数值直接改，回车生效，Esc 取消。下拉项（刀具、程序组、MCS、切削模式等）单击弹出候选列表。

**写回方式**：`SetUndoMark` + `CreateFeedsBuilder`（转速 / 进给）+ `MoveObjects(View.MachineTool, …)`（换刀），
写之前会建撤销标记，改坏了直接 Ctrl+Z 回退。

**两个页签** — 「参数」页是上面那张表；「模板」页是树形列出的模板工序，**双击即插入**当前程序组。

<p align="center">
  <img src="文档/截图/02-template-tree.png" width="340" alt="加工助手 - 模板页">
  <br>
  <sub>「模板」页：按模板的程序树列出工序，双击插入</sub>
</p>

**齿轮配置** — 字体、字号、行的显示 / 隐藏、行的上下排序，都在这颗齿轮里，配置存本地。

| 模板页配置 | 参数页配置 |
| :---: | :---: |
| <img src="文档/截图/03-assistant-config.png" width="430" alt="模板配置"> | <img src="文档/截图/04-param-config.png" width="430" alt="参数配置"> |
| 多模板路径管理（`cam_template.txt` 的可视化编辑） | 字体 / 字号 / 显示哪些行 / 行顺序 / 颜色 |

### 3.3 加工模板同步

启动时比对模板 prt 的时间戳，有变化就自动重建：

```
枚举模板程序树 → 生成工序清单 cam_strategies.txt + 工序图标 cam_op_*.bmp → 记时间戳
```

清单供「加工助手 → 模板」页使用。同步逻辑只跑在模板变更时，正常启动不占时间。

## 四、常见问题

| 现象 | 原因 | 解决 |
| --- | --- | --- |
| 菜单栏 / 功能区看不到 | `custom_dirs.dat` 没登记，或路径写错 | 检查 `%UGII_BASE_DIR%\UGII\menus\custom_dirs.dat` 里的登记行，重启 NX |
| 中文显示成乱码 | 菜单文件不是 GBK | 另存为 ANSI / GBK，且**不加 BOM** |
| 按钮不显示，但菜单能点 | 功能区 `BUTTON` 写了菜单项 ID | 改成已注册的**动作 ID**（如 `LYJ_APP__cam_assistant`） |
| 功能区多出一个空组框 | 组的 `TITLE` 为空 | 每个组必须有非空名字 |
| 点工序提示「未进入加工」 | 当前部件不在 CAM 环境 | 先进入加工模块再选工序 |
| 编译报 DLL 无法覆盖 | NX 正占用（`GetUnloadOption` = AtTermination） | 关闭 NX 再编译 |
| 插入工序失败 | 模板未注册或组选错 | 看 `config\insert_last.log` |

## 五、从源码构建

程序集路径不写死，二选一：

```powershell
# 方式 A：环境变量指向 NX 安装根目录
$env:NX_BASE_DIR = "<你的 NX 安装根目录>"
dotnet build 源码\LYJ\LYJ.csproj -c Release
```

```xml
<!-- 方式 B：源码\LYJ\NXBaseDir.local.props（已在 .gitignore 中，不会提交） -->
<Project>
  <PropertyGroup>
    <NXBaseDir>&lt;你的 NX 安装根目录&gt;</NXBaseDir>
  </PropertyGroup>
</Project>
```

找不到 NX 托管程序集时，构建会直接报「请设置 NX_BASE_DIR」，而不是抛一串引用错误。

## 六、部署与加载机制

产物是单个 `LYJ.dll`，按 NX 的约定放置：

```
<LYJ_HOME>\
├── startup\        lyj.men / rbn_lyj*.rtb+grb / LYJ.dll
├── application\    功能区按钮图标 lyj_*.bmp（工序图标 cam_op_*.bmp 首次启动补齐）
└── config\         加工模板与运行时配置（首次启动自动补齐）
```

把这个目录写进 `custom_dirs.dat`（一行一个目录），重启 NX。部署根目录不写死：优先环境变量 `LYJ_HOME`，
否则按 `LYJ.dll` 所在位置推断（`<根>\startup\` 的上一级）。

加载流程：

1. NX 启动 → 读 `custom_dirs.dat` → 扫描 `startup\` → 解析 `lyj.men`
2. `APPLICATION_BUTTON LYJ_APP` + `LIBRARIES LYJ` → 加载 `LYJ.dll`
3. NX 调用 `public static int Startup()`
4. `Startup` 用 `MenuBarManager.AddMenuAction("动作代号", 回调)` 绑定行为，并调 `EnsureRuntimeFiles()` 补齐配置、校正功能区
5. 点按钮 → 菜单项的 `ACTIONS 代号` → 触发对应回调

> [!IMPORTANT]
> **`.men` / `.rtb` / `.grb` 必须 GBK(936) 无 BOM。** 写成 UTF-8 会让中文标签乱码，甚至导致 NX 完全读不出该文件。
> 在 GitHub 上打开这些文件显示乱码是**正常的**——不要"修"它。

## 七、功能区结构与二次开发

只有一个选项卡 **LYJ**（`rbn_lyj.rtb`），由三个组文件拼成：

| 组文件 | 组名 | 内容 |
| --- | --- | --- |
| `rbn_lyj_import.grb` | 图档导入 | STL / STP / IGS / X_T |
| `rbn_lyj_export.grb` | 图档导出 | STL / STP / IGS / X_T |
| `rbn_lyj_cam.grb` | 加工 | 加工助手（悬浮窗） |

「加工助手」是加工功能的唯一入口：**功能区不占按钮位置**，工序插入在悬浮窗的「模板」页双击完成。

新增一个按钮的完整链路：

1. `lyj.men` 里加 `BUTTON` 块，ID 形如 `LYJ_APP__xxx`
2. 功能区组文件里写 `BUTTON <已注册的动作 ID>`（写菜单项 ID 按钮不显示）
3. `Startup()` 里 `AddMenuAction` 注册回调
4. 重新编译部署

> 原「NX-编程」选项卡已并入 LYJ。`rbn_nxprogramming.rtb` 与 `gen_cam_strategies.men` 不再生成，
> `LYJ.dll` 每次启动会清掉这些遗留文件，避免出现点不动的空页签 / 空菜单。

## 八、加工模板

模板部件是**使用者自己的资产**，仓库里不提供。两种方式指向模板：

- 放到 `config\测试.prt`（默认位置）
- 或用 `config\cam_template.txt` 指到任意路径（一行一个，`#` 开头为注释）

要点：

- 模板注册名 = prt 文件名（去扩展名），换模板后名字自动跟着变
- 启动时比对时间戳，变化即自动同步（不再生成菜单 / 选项卡文件）
- 插入工序用 `CAMOperationCollection.CreateWithUserName(...)`，失败写 `config\insert_last.log`

> Release 里的安装包**含一份模板骨架**（无几何、无刀轨），只为了让加工助手首次启动能跑起来。
> 实际加工请换成你自己的模板。

## 九、打包安装程序

```powershell
# 编译 → 收集 → payload.zip → 编译安装器 → 输出到 打包输出\dist
.\打包输出\build_release.ps1                     # 素材全在 资源\，不依赖任何已部署目录
.\打包输出\build_release.ps1 -Major 1 -Minor 0    # 自定义版本号前缀
```

打包素材全部来自仓库内，和本机部署状态无关：

| 素材 | 位置 |
| --- | --- |
| 菜单 / 功能区 / 组文件 | `资源\菜单功能区\lyj.men`、`rbn_lyj*.rtb/grb` |
| 功能区按钮图标（9 张） | `资源\按钮图标\lyj_*.bmp`（60×60 BMP） |
| 加工模板 | `资源\加工模板\测试.prt`（自备，不入库） |
| 主程序 | 打包时现编译的 `源码\LYJ\bin\Release\LYJ.dll` |

产生物命名：`LYJ安装程序_V<主>.<次>.<yyMMdd-HHmm>.exe`。
**上传 GitHub Release 前请改成英文名**（如 `LYJ-Setup-V1.0.261107-2005.exe`），GitHub 会把附件名里的中文替换成点号。

安装器自测（纯逻辑，不需要 NX）：

```powershell
dotnet build 源码\安装器\自测\SetupTest.csproj -c Release
.\源码\安装器\自测\bin\Release\SetupTest.exe
```

## 十、开发者笔记：NX Open 实测差异

NX Open 的部分成员与官方文档描述不一致，以下是本机在 **2606** 上实测踩过的：

- `Selection` 没有 `GetSelectedObjects()`，只有 `GetNumSelectedObjects()` + `GetSelectedObject(i)`
- `Session.MarkId` 类型不存在，用 `var mark = session.SetUndoMark(...)`
- `NXMessageBox` 没有 `DialogResponse` 子类型，需要 Yes/No 时改用 WinForms `MessageBox`
- `NXOpen.CAM.Tool`（文档标 NX2306+）、`CAMObject.GetNameOfType`（标 NX1899+）在 2606 上均不可用
- 判定程序组要用 `CAMSetup.GetRoot(View.X)` 比对父链 Tag，**不能靠组名**
- `Operation.GetToolpathTime()` 按刀轨实时计算，很慢 → 用 `BeginInvoke` 延后 + 按 Tag 缓存
- WinForms 控件取屏幕坐标必须 `ctl.PointToScreen(Point.Empty)`，传 `(ctl.Left, ctl.Bottom)` 会把手里的父容器偏移重复加一遍
- 悬浮窗里做下拉框要用**独立无边框窗体** + `ShowWithoutActivation` + `WS_EX_NOACTIVATE`，
  子控件会被悬浮窗客户区裁掉

欢迎 PR 补充。

## 十一、贡献

Issue 与 PR 都欢迎。提交前请确保：

1. `dotnet build 源码\LYJ\LYJ.csproj -c Release` 零错误
2. 不带 NX 的 DLL、官方 `.dat` 数据、个人加工模板、机器绝对路径
3. `.men` / `.rtb` / `.grb` 保持 GBK 无 BOM

## 十二、来源说明

本项目部分代码由 AI 辅助生成，全部经人工在 NX 2606 上实机验证。API 用法以实测为准，不以文档描述为准。

## 十三、许可证

**Apache License 2.0** — 全文见 [LICENSE](LICENSE)，归属声明见 [NOTICE](NOTICE)。

| | |
| --- | --- |
| ✅ | 允许商用、修改、分发，也允许把衍生版本闭源发布 |
| ✅ | 保留版权声明与 `NOTICE` 中的归属信息即可（这就是"注明来源"） |
| ⚠️ | 修改过的文件需要注明"已被修改" |
| ⚠️ | **不授予商标许可**（第 6 条）——"LYJ"这个名称不能用作你衍生产品的名字 |
| ❌ | 不提供任何担保，作者不承担使用后果 |

> 如果希望**衍生作品也必须开源**（禁止别人做闭源商业版），那应该用 GPL-3.0。
> 本项目选 Apache-2.0，是明确允许闭源商用的。

## 附录：仓库目录结构

根目录只放仓库必备文件，其余按用途分到中文文件夹：

| 目录 | 内容 |
| --- | --- |
| `源码\LYJ\` | 主程序 C# 工程。目录名与程序集名一致，保持英文以免影响 `dotnet build` 与 VS 识别 |
| `源码\安装器\` | 安装程序工程；`自测\` 是纯逻辑测试（不依赖 NX） |
| `资源\菜单功能区\` | `lyj.men`、`rbn_lyj.rtb` 与三个 `.grb` 组文件（GBK 无 BOM） |
| `资源\按钮图标\` | 功能区 9 张 60×60 BMP |
| `资源\加工模板\` | 加工模板 prt（使用者自备，不入库） |
| `工具脚本\` | `gen_icon.py` 生成安装包图标、`deploy_ribbon.py` 重排功能区并覆盖 DLL、`图标清单.txt` |
| `文档\` | 安装包依带的 `使用说明.txt` |
| `打包输出\` | `build_release.ps1`；运行时生成 `build\`（中间件）与 `dist\`（安装包） |
| `内部归档\` | **不入库**：工序参数数据表、参数分析脚本、第三方图标生成小工具 |
