# LYJ —— Siemens NX 二次开发工具箱

一套给 Siemens NX 用的 C# 插件（.NET Framework 4.7.2），提供三类能力：

- **图档导入 / 导出**：STL、STEP、IGES、Parasolid(X_T) 一键进出
- **加工助手**：无模式悬浮窗，实时显示当前选中工序的刀具 / 转速 / 进给，并可直接改
- **加工模板同步**：扫描加工模板 prt → 生成工序清单（`cam_strategies.txt`）与工序图标 → 模板变更时自动重建，清单供加工助手插入工序

> **本项目与 Siemens 公司无关，非官方产品。** "NX"、"Siemens" 为其各自权利人的商标，此处仅用于说明适用软件。  
> 使用与分发本代码不代表获得 NX 的任何授权；运行本插件需要你自己持有合法有效的 NX 许可。  
> 仓库不包含任何 Siemens 的二进制文件、官方数据库内容或加工模板部件。

---

## 一、环境要求

| 项    | 要求                                                 |
| ---- | -------------------------------------------------- |
| NX   | **仅适配 Designcenter 2606**（唯一验证过的版本）；其它版本未适配，NX Open 接口存在差异 |
| 运行时  | .NET Framework 4.7.2                               |
| 构建   | .NET SDK（`dotnet build`）或 Visual Studio 2019+      |
| 开发前提 | 本机需安装 NX，且有 `NXBIN\managed` 下的 `NXOpen.dll` 等托管程序集 |

> 为什么必须装 NX：`NXOpen / NXOpenUI / NXOpen.Utilities / NXOpen.UF` 由 NX 安装包提供，不随本仓库分发（csproj 中 `Private=False`，编译产物不会拷贝这些 DLL）。

## 二、安装（普通用户）

不需要编译，直接用打好的安装包：

1. 到 [Releases](../../releases) 下载 `LYJ安装程序_V*.exe`
2. 右键 → **以管理员身份运行**（安装程序要写 `custom_dirs.dat`、注册卸载信息，需要提权）
3. 在列表里勾选要启用插件的 NX（**仅 NX 2606 适配**，其它版本后面会标 `[未适配]`）
4. 安装完重启 NX：菜单栏出现 **LYJ** 菜单，功能区出现 **LYJ** 选项卡

静默安装、指定安装目录、卸载方式见 [文档/使用说明.txt](文档/使用说明.txt)。

## 三、构建

程序集路径不写死在工程里，二选一：

```powershell
# 方式 A：环境变量指向 NX 安装根目录
$env:NX_BASE_DIR = "<你的 NX 安装根目录>"
dotnet build 源码\LYJ\LYJ.csproj -c Release
```

```
# 方式 B：在 源码\LYJ\ 下建一份 NXBaseDir.local.props（已在 .gitignore 中，不会被提交）
<Project>
  <PropertyGroup>
    <NXBaseDir>&lt;你的 NX 安装根目录&gt;</NXBaseDir>
  </PropertyGroup>
</Project>
```

找不到 NX 托管程序集时，构建会直接报出「请设置 NX_BASE_DIR」而不是一串引用错误。

## 四、部署

产物是单个 `LYJ.dll`，按 NX 的约定放到部署目录下：

```
<LYJ_HOME>\
├── startup\        lyj.men / rbn_lyj*.rtb+grb / LYJ.dll
├── application\    功能区按钮图标 lyj_*.bmp（工序图标 cam_op_*.bmp 首次启动补齐）
└── config\         加工模板与运行时配置（首次启动自动补齐）
```

把该目录登记进 NX 的 `custom_dirs.dat`（`%UGII_BASE_DIR%\UGII\menus\custom_dirs.dat`，一行一个目录），重启 NX 即可。

部署根目录不写死：优先环境变量 `LYJ_HOME`，否则按 `LYJ.dll` 所在目录推断（`<根>\startup\` 的上一级）。

## 五、加载机制

1. NX 启动 → 读 `custom_dirs.dat` → 扫描 `startup\` → 解析 `lyj.men`
2. `APPLICATION_BUTTON LYJ_APP` + `LIBRARIES LYJ` → 加载 `LYJ.dll`
3. NX 调用 `public static int Startup()`
4. `Startup` 用 `MenuBarManager.AddMenuAction("动作代号", 回调)` 绑定行为，并调用 `EnsureRuntimeFiles()` 补齐配置、校正功能区文件
5. 点按钮 → 菜单项的 `ACTIONS 代号` → 触发对应回调

> **`.men` / `.rtb` / `.grb` 必须是 GBK(936) 无 BOM。** 写成 UTF-8 会让中文标签乱码，甚至导致 NX 完全读不出该文件。

## 六、功能区结构

只有一个选项卡 **LYJ**（`rbn_lyj.rtb`），由三个组拼成：

| 组文件                  | 组名   | 内容                    |
| -------------------- | ---- | --------------------- |
| `rbn_lyj_import.grb` | 图档导入 | STL / STP / IGS / X_T |
| `rbn_lyj_export.grb` | 图档导出 | STL / STP / IGS / X_T |
| `rbn_lyj_cam.grb`    | 加工   | 加工助手（悬浮窗）             |

「加工助手」是加工功能唯一的入口：开关一个无模式悬浮窗，实时显示并修改当前选中工序的刀具、转速、进给；工序的插入在悬浮窗的「模板」页里双击完成，不在功能区占位。

> 原来的「NX-编程」选项卡已并入 LYJ。`rbn_nxprogramming.rtb` 与 `gen_cam_strategies.men` 不再生成，DLL 每次启动会删掉遗留文件，避免出现空页签/空菜单。

新增按钮的完整链路：`.men` 加 `BUTTON` 块（ID 形如 `LYJ_APP__xxx`）→ 功能区文件里写 `BUTTON <已注册的动作 ID>` → `Startup()` 里 `AddMenuAction` 注册 → 重新编译部署。

## 七、加工模板

模板部件是**使用者自己的资产**，本仓库不提供。请把自己的加工模板放到 `config\测试.prt`，或用 `config\cam_template.txt` 指到任意路径（一行一个，`#` 为注释）。

- 模板注册名 = prt 文件名（去扩展名），换模板后名字自动跟着变；
- 启动时比对模板时间戳，变化即自动同步：枚举程序树 → 生成工序图标、`cam_strategies.txt`、时间戳（不再生成菜单/选项卡文件）；
- 插入工序用 `CAMOperationCollection.CreateWithUserName(...)`，失败会写 `config\insert_last.log` 供排查。

## 八、打包安装程序

```powershell
# 编译 → 收集 → payload.zip → 编译安装器 → 输出到 打包输出\dist
.\打包输出\build_release.ps1            # 标准件全在 资源\，不需要任何已部署目录
.\打包输出\build_release.ps1 -Major 1 -Minor 0   # 自定义版本号前缀
```

打包素材全部来自仓库内，和本机部署状态无关：

| 素材            | 位置                                     |
| ------------- | -------------------------------------- |
| 菜单 / 功能区 / 组文件 | `资源\菜单功能区\lyj.men`、`rbn_lyj*.rtb/grb` |
| 功能区按钮图标（9 张）  | `资源\按钮图标\lyj_*.bmp`（60×60 BMP） |
| 加工模板          | `资源\加工模板\测试.prt`（自备，不入库） |
| 主程序           | 打包时现编译的 `源码\LYJ\bin\Release\LYJ.dll` |

`-SourceDeploy` 现在是可选的：只在想让「模板」从某个已部署目录兜底时才用，留空不影响打包。

安装器会按注册表与常见目录探测 NX 安装位置，可写 `custom_dirs.dat` 完成登记，并支持静默安装（见 `文档\使用说明.txt`）。

安装器自测：`dotnet build 源码\安装器\自测\SetupTest.csproj -c Release` 后运行 `bin\SetupTest.exe`（纯逻辑测试，不需要 NX）。

## 九、常见问题

| 现象           | 原因                                       | 解决                   |
| ------------ | ---------------------------------------- | -------------------- |
| 菜单栏 / 功能区看不到 | `custom_dirs.dat` 没登记或路径错                | 检查注册行，重启 NX          |
| 中文乱码         | 文件不是 GBK                                 | 另存为 ANSI/GBK，且不加 BOM |
| 按钮不显示但菜单能点   | 功能区 `BUTTON` 写了菜单项 ID                    | 改成已注册的动作 ID          |
| 功能区多出空组框     | 组 `TITLE` 为空                             | 组必须有非空名字             |
| 点工序提示未进入加工   | 当前部件不在 CAM 环境                            | 先进入加工模块              |
| DLL 无法覆盖     | NX 占用（`GetUnloadOption` = AtTermination） | 关闭 NX 再编译            |

## 十、已知接口坑（欢迎补充）

NX Open 的部分成员与文档描述不一致，实测踩过的（NX 2606）：

- `Selection` 没有 `GetSelectedObjects()`，只有 `GetNumSelectedObjects()` + `GetSelectedObject(i)`
- `Session.MarkId` 类型不存在，用 `var mark = session.SetUndoMark(...)`
- `NXMessageBox` 没有 `DialogResponse` 子类型，需要 Yes/No 时改用 WinForms `MessageBox`
- CAM 侧的 `NXOpen.CAM.Tool`（NX2306+）、`CAMObject.GetNameOfType`（NX1899+）在 2606 上不可用
- 按组视图判定程序组要用 `CAMSetup.GetRoot(View.X)` 比对父链 Tag，不能靠组名

## 十一、来源说明

本项目部分代码由 AI 辅助生成，全部经人工在 NX 2606 上实机验证。API 用法以实测为准，不以文档描述为准。

## 十二、贡献

欢迎 Issue 与 PR。提交前请确保：

1. `dotnet build 源码\LYJ\LYJ.csproj -c Release` 零错误；
2. 不把 NX 的 DLL、官方 `.dat` 数据、个人加工模板或机器绝对路径带进提交；
3. `.men` / `.rtb` / `.grb` 保持 GBK 无 BOM。

## 附：仓库目录结构

根目录只放仓库必备文件（`.gitignore`、`LICENSE`、`NOTICE`、`README.md`），其余按用途分到中文文件夹：

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

## 许可证

本项目采用 **Apache License 2.0**，全文见 [LICENSE](LICENSE)，归属声明见 [NOTICE](NOTICE)。

用一句话概括：

- ✅ 允许商用、修改、分发，也允许把衍生版本闭源发布
- ✅ 保留版权声明与 `NOTICE` 中的归属信息即可（这就是"注明来源"）
- ⚠️ 修改过的文件需要注明"已被修改"
- ⚠️ **不授予商标许可**（许可证第 6 条）——"LYJ"这个名称不能用作你衍生产品的名字
- ❌ 不提供任何担保，作者不承担使用后果

> 如果你希望**衍生作品也必须开源**（禁止别人拿去做闭源商业版），那要用 GPL-3.0 而不是 Apache-2.0。本项目选择 Apache-2.0，是明确允许闭源商用的。
