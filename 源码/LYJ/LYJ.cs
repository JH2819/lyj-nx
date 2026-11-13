// Copyright 2026 The LYJ Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NXOpen;
using NXOpen.Facet;
using NXOpen.MenuBar;
using NXOpen.UF;

namespace LYJ
{
    /// <summary>
    /// NX 外挂入口。
    ///
    /// 加载机制：
    ///   1. NX 启动时扫描 custom_dirs.dat 中登记的目录，读取 startup\lyj.men；
    ///   2. 菜单文件里 APPLICATION_BUTTON 的 LIBRARIES LYJ 让 NX 加载本程序集；
    ///   3. NX 调用本类的 Startup()，在 Startup 中用 AddMenuAction 把
    ///      菜单 ACTIONS 代号与 C# 回调方法绑定。
    /// </summary>
    public class LYJApp
    {
        private static Session theSession;
        private static UI theUI;

        /// <summary>
        /// NX 加载程序集后的入口方法（方法名必须为 Startup，返回 int）。
        /// </summary>
        public static int Startup()
        {
            int retValue = 0;
            try
            {
                theSession = Session.GetSession();
                theUI = UI.GetUI();

                // 首次运行自愈：安装包只带 DLL/菜单/功能区/模板，其余文件（目录、模板路径、
                // 工序清单、图标记录、全部图标、生成的菜单）都在第一次启动 NX 时补齐。
                EnsureRuntimeFiles();
                EnsureFixedToolIcons();   // 固定按钮图标补画（独立于 CAM 模板流程，见函数注释）

                MenuBarManager mbm = theUI.MenuBarManager;

                // 菜单 ACTIONS 代号 -> 回调方法（与 lyj.men 中一致）
                // 统一走 AddSafeAction：老版本 NX 缺少新 API 时给中文提示，而不是抛 .NET 异常。
                AddSafeAction(mbm, "LYJ_APP__import_auto", ImportAutoCallback, "导入图档");
                AddSafeAction(mbm, "LYJ_APP__export_stl", ExportStlCallback, "导出STL");
                AddSafeAction(mbm, "LYJ_APP__export_stp", ExportStpCallback, "导出STP/STEP");
                AddSafeAction(mbm, "LYJ_APP__export_igs", ExportIgsCallback, "导出IGS");
                AddSafeAction(mbm, "LYJ_APP__export_xt", ExportXtCallback, "导出X_T");

                // 部件显示：透明化（原生 Block Styler 对话框）+ 可配置的颜色按钮（默认五个）
                AddSafeAction(mbm, "LYJ_APP__transparency", TransparencyCallback, "透明化");
                for (int i = 0; i < ColorSlots.Max; i++)
                {
                    int slot = i;                                   // 闭包要抓当前值
                    AddSafeAction(mbm, ColorActionId(slot),
                        delegate (MenuButtonEvent ev) { return ColorSlotCallback(slot); },
                        "部件颜色 " + (slot + 1));
                }
                AddSafeAction(mbm, "LYJ_APP__color_config", ColorConfigCallback, "配置颜色按钮");

                // 加工：模板同步（内部调用）+ 工序策略回调（自动生成）+ 加工助手悬浮窗
                AddSafeAction(mbm, "LYJ_APP__cam_refresh", CamRefreshCallback, "刷新编程模板");
                AddSafeAction(mbm, "LYJ_APP__cam_assistant", CamAssistantCallback, "加工助手");
                AutoRefreshCamStrategies();          // 启动时自动同步模板（生成/更新按钮文件，重启生效）
                RegisterCamStrategyActions(mbm);     // 注册模板策略回调（按现有清单）
            }
            catch (NXException)
            {
                // Startup 阶段 UI 尚未就绪时不弹窗，静默返回，避免影响 NX 启动
            }
            return retValue;
        }


        /// <summary>
        /// “导入图档” 唯一入口：选一个文件，按扩展名自动挑对应的导入器。
        /// 覆盖 NX 原生「文件 → 导入」里的全部图档格式：
        ///   NX 部件 / STEP(203/214/242) / IGES / Parasolid / CATIA V4、V5、3DXML /
        ///   Creo(Pro-E) / DXF-DWG / ACIS / Rhino / Inventor / STL / 3MF / OBJ /
        ///   VRML / IFC / Revit / G3D / I-deas / ASC / Geospatial / CGM。
        /// </summary>
        private static MenuBarManager.CallbackStatus ImportAutoCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                string file = PickFile(BuildImportFilter(),
                    "选择要导入的图档（按扩展名自动匹配导入器）");
                if (file == null)
                    return MenuBarManager.CallbackStatus.Cancel;        // 用户取消

                // 按扩展名分派（统一小写；Creo 的 .prt.1 版本号后缀一并归一）
                string ext = NormalizeImportExt(file);
                switch (ext)
                {
                    case ".stl":
                        return ImportStlFrom(file);
                    case ".stp":
                    case ".step":
                        return ImportStepFrom(file);
                    case ".igs":
                    case ".iges":
                        return ImportIgesFrom(file);
                    case ".x_t":
                    case ".x_b":
                    case ".xmt_txt":
                    case ".xmt_bin":
                        return ImportParasolidFrom(file);
                    case ".prt":
                    case ".asm":
                        // .prt 既可能是 NX 部件，也可能是 Creo 部件：按文件头判别
                        return IsNxPartFile(file) ? ImportNxPartFrom(file) : ImportCreoFrom(file);
                    case ".xpr":
                    case ".xas":
                    case ".neu":
                        return ImportCreoFrom(file);
                    case ".catpart":
                    case ".catproduct":
                        return ImportCatiaV5From(file);
                    case ".model":
                    case ".exp":
                    case ".session":
                    case ".dlv":
                        return ImportCatiaV4From(file);
                    case ".3dxml":
                        return Import3DXmlFrom(file);
                    case ".dxf":
                    case ".dwg":
                        return ImportDxfDwgFrom(file);
                    case ".sat":
                    case ".sab":
                    case ".asat":
                        return ImportAcisFrom(file);
                    case ".3dm":
                        return ImportRhinoFrom(file);
                    case ".ipt":
                    case ".iam":
                        return ImportInventorFrom(file);
                    case ".3mf":
                        return Import3MfFrom(file);
                    case ".obj":
                        return ImportObjFrom(file);
                    case ".wrl":
                    case ".vrml":
                        return ImportVrmlFrom(file);
                    case ".ifc":
                        return ImportIfcFrom(file);
                    case ".rvt":
                        return ImportRevitFrom(file);
                    case ".g3d":
                        return ImportG3dFrom(file);
                    case ".mf1":
                    case ".arc":
                    case ".unv":
                    case ".pkg":
                        return ImportIdeasFrom(file);
                    case ".asc":
                        return ImportAscFrom(file);
                    case ".shp":
                        return ImportGeospatialFrom(file);
                    case ".cgm":
                        return ImportCgmFrom(file);
                    default:
                        theUI.NXMessageBox.Show("不支持的文件类型",
                            NXMessageBox.DialogType.Warning,
                            "无法识别的扩展名：" + (ext.Length == 0 ? "(无扩展名)" : ext) + "\r\n\r\n" +
                            "本入口覆盖 NX 原生 文件 → 导入 里的图档格式：" +
                            "STEP / IGES / Parasolid / CATIA / Creo / DXF-DWG / ACIS / Rhino / " +
                            "Inventor / STL / 3MF / OBJ / VRML / IFC / Revit / 3DXML / G3D / " +
                            "I-deas / ASC / Geospatial / CGM。\r\n\r\n" +
                            "块、符号、点云（POINTS）等特殊对象请用 NX 自带导入。");
                        return MenuBarManager.CallbackStatus.Continue;
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 导入 STL：先导入为传统小平面体，再批量转成收敛体，最后移到指定图层。
        /// 移植自 ImportSTL_Journal.vb（NX 2606 验证过的 API）。
        /// </summary>
        private static MenuBarManager.CallbackStatus ImportStlFrom(string stlFilePath)
        {
            try
            {
                // ==================== 参数区（按需修改） ====================
                int targetLayer = 1;                    // 导入后放入的图层号; 0 = 不移动图层
                var angularTolerance = STLImportBuilder.AngularToleranceTypes.Coarse;
                var stlUnits = STLImportBuilder.STLFileUnitsTypes.Millimeters;
                var originalBodyOption = ConvertFacetBodyBuilder.OriginalBodyOptions.Delete;
                // ==================== 以下为执行逻辑 ====================

                // 1. 文件存在性校验
                if (!File.Exists(stlFilePath))
                {
                    theUI.NXMessageBox.Show("错误", NXMessageBox.DialogType.Error,
                        "STL 文件不存在: " + stlFilePath);
                    return MenuBarManager.CallbackStatus.Continue;
                }

                // 2. 取得当前工作部件
                Part workPart = theSession.Parts.Work;
                if (workPart == null)
                {
                    theUI.NXMessageBox.Show("错误", NXMessageBox.DialogType.Error,
                        "请先打开或新建一个部件文件再执行导入。");
                    return MenuBarManager.CallbackStatus.Continue;
                }

                // 3. 导入前，记录当前已有小平面体（用于对比识别新导入的体）
                var beforeBodies = new List<FacetedBody>();
                foreach (FacetedBody body in workPart.FacetedBodies)
                    beforeBodies.Add(body);

                // 4. 第 1 步：导入 STL 为传统 Nx 小平面体
                STLImportBuilder builder = null;
                bool importOK = false;
                try
                {
                    builder = workPart.FacetedBodies.CreateSTLImportBuilder();
                    builder.File = stlFilePath;
                    builder.AngularTolerance = angularTolerance;
                    builder.FacetBodyType = STLImportBuilder.FacetBodyTypes.Nx;
                    builder.STLFileUnits = stlUnits;
                    builder.Commit();
                    importOK = true;
                }
                catch (Exception ex)
                {
                    theUI.NXMessageBox.Show("导入失败", NXMessageBox.DialogType.Error,
                        "错误信息: " + ex.Message);
                }
                finally
                {
                    if (builder != null) builder.Destroy();
                }
                if (!importOK)
                    return MenuBarManager.CallbackStatus.Continue;

                // 5. 导入后对比，找出本次新导入的小平面体
                var imported = new List<FacetedBody>();
                foreach (FacetedBody body in workPart.FacetedBodies)
                    if (!beforeBodies.Contains(body))
                        imported.Add(body);

                // 6. 第 2 步：将每个小平面体转换为收敛体
                var converted = new List<NXObject>();
                foreach (FacetedBody body in imported)
                {
                    ConvertFacetBodyBuilder convBuilder = null;
                    try
                    {
                        convBuilder = workPart.FacetedBodies.FacetModelingCollection
                            .CreateConvertFacetBodyBuilder();
                        convBuilder.FacetedBodiesToConvert.Add(body);
                        convBuilder.OutputType = ConvertFacetBodyBuilder.OutputTypes.ConvergentBody;
                        convBuilder.OriginalBodyOption = originalBodyOption;
                        convBuilder.Commit();
                        foreach (NXObject res in convBuilder.GetCommittedObjects())
                            converted.Add(res);
                    }
                    catch (Exception ex)
                    {
                        theUI.NXMessageBox.Show("转换失败", NXMessageBox.DialogType.Error,
                            "小平面体转换失败: " + ex.Message);
                    }
                    finally
                    {
                        if (convBuilder != null) convBuilder.Destroy();
                    }
                }

                // 7. 移动到指定图层
                if (targetLayer > 0 && converted.Count > 0)
                {
                    foreach (NXObject obj in converted)
                    {
                        if (obj is DisplayableObject)
                            workPart.Layers.MoveDisplayableObjects(targetLayer,
                                new DisplayableObject[] { (DisplayableObject)obj });
                    }
                }

                // 8. 完成提示：写入 NX 窗口底部状态栏（不弹窗）
                UFSession.GetUFSession().Ui.SetStatus(
                    "成功导入 " + imported.Count + " 个 STL，转换为 " + converted.Count + " 个收敛体。");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 导入 STEP：读文件头 FILE_SCHEMA 识别 AP203 / AP214 / AP242，挑对应导入器。
        /// </summary>
        private static MenuBarManager.CallbackStatus ImportStepFrom(string stpFile)
        {
            try
            {
                Part workPart = RequireWorkPart();
                if (workPart == null || !EnsureImportTargetReady(workPart))
                    return MenuBarManager.CallbackStatus.Continue;

                // 识别 STEP 应用协议，选择对应导入器
                string ap = DetectStepAp(stpFile);
                NXOpen.Builder importer = null;
                try
                {
                    switch (ap)
                    {
                        case "203":
                        {
                            var i = theSession.DexManager.CreateStep203Importer();
                            i.InputFile = stpFile;
                            i.ImportTo = Step203Importer.ImportToOption.WorkPart;
                            i.FlattenAssembly = true;
                            i.Optimize = true;
                            i.SewSurfaces = true;
                            i.FileOpenFlag = false;
                            i.OutputFile = TempImportPrt("step203");
                            var def = ResolveNxFile("STEP203UG", "step203ug.def");
                            if (def != null) i.SettingsFile = def;
                            EnableStandardObjectTypes(i.ObjectTypes);
                            importer = i;
                            break;
                        }
                        case "242":
                        {
                            var i = theSession.DexManager.CreateStep242Importer();
                            i.InputFile = stpFile;
                            i.ImportTo = Step242Importer.ImportToOption.WorkPart;
                            i.FlattenAssembly = true;
                            i.Optimize = true;
                            i.SewSurfaces = true;
                            i.FileOpenFlag = false;
                            i.OutputFile = TempImportPrt("step242");
                            var def = ResolveNxFile("TRANSLATORS", "step242", "step242ug.def");
                            if (def != null) i.SettingsFile = def;
                            EnableStandardObjectTypes(i.ObjectTypes);
                            importer = i;
                            break;
                        }
                        default: // AP214 及未识别的情况
                        {
                            var i = theSession.DexManager.CreateStep214Importer();
                            i.InputFile = stpFile;
                            i.ImportTo = Step214Importer.ImportToOption.WorkPart;
                            i.FlattenAssembly = true;
                            i.Optimize = true;
                            i.SewSurfaces = true;
                            i.FileOpenFlag = false;
                            i.OutputFile = TempImportPrt("step214");
                            var def = ResolveNxFile("STEP214UG", "step214ug.def");
                            if (def != null) i.SettingsFile = def;
                            EnableStandardObjectTypes(i.ObjectTypes);
                            importer = i;
                            break;
                        }
                    }
                    importer.Commit();
                }
                finally
                {
                    if (importer != null) importer.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus(
                    "STEP (AP" + ap + ") 导入完成: " + Path.GetFileName(stpFile));
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 导入 IGES 到当前工作部件。
        /// </summary>
        private static MenuBarManager.CallbackStatus ImportIgesFrom(string igsFile)
        {
            try
            {
                Part workPart = RequireWorkPart();
                if (workPart == null || !EnsureImportTargetReady(workPart))
                    return MenuBarManager.CallbackStatus.Continue;

                IgesImporter importer = null;
                try
                {
                    importer = theSession.DexManager.CreateIgesImporter();
                    importer.InputFile = igsFile;
                    importer.ImportTo = IgesImporter.ImportToEnum.WorkPart;
                    // 按原生导入对话框录制日志补全的属性（缺失会导致 Commit 报
                    // "试图使用不活动的对象"）
                    var def = ResolveNxFile("iges", "igesimport.def");
                    if (def != null) importer.SettingsFile = def;
                    importer.CopiousData = IgesImporter.CopiousDataEnum.LinearNURBSpline;
                    importer.LayerDefault = 0;
                    importer.LayerMask = "0-99999";
                    importer.GeomFixupTol = 0.0127;
                    importer.FlattenAssembly = true;
                    importer.Optimize = true;
                    importer.SewSurfaces = true;
                    importer.OutputFile = TempImportPrt("iges");
                    importer.FileOpenFlag = false;
                    EnableStandardObjectTypes(importer.ObjectTypes);
                    importer.Commit();
                }
                finally
                {
                    if (importer != null) importer.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus(
                    "IGES 导入完成: " + Path.GetFileName(igsFile));
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 打开导入器的常用对象类型开关。
        /// 关键：ObjectTypeSelector 默认全部为 false，不设置的话 Commit 会
        /// "成功"但一个对象都不导入（本项目实际踩过的坑）。
        /// </summary>
        private static void EnableStandardObjectTypes(ObjectTypeSelector ot)
        {
            ot.Curves = true;             // 曲线
            ot.Surfaces = true;           // 曲面
            ot.Solids = true;             // 实体
            ot.ConvergentBodies = true;   // 收敛体
            ot.FacetBodies = true;        // 小平面体
            ot.Structures = true;         // 装配结构
            ot.ProductData = true;        // 产品数据
            ot.Csys = true;               // 坐标系
            ot.Attributes = true;         // 属性
        }

        /// <summary>
        /// 导入前确保工作部件处于"活动"状态，否则 Dex 导入 Commit 会抛
        /// "试图使用不活动的对象"：
        ///   1. 工作部件必须同时是显示部件（工作部件=某个组件时最常见）；
        ///   2. 部件必须完全加载（部分加载的装配会触发该错误）。
        /// 返回 false 表示已弹窗提示，调用方应直接返回。
        /// </summary>
        private static bool EnsureImportTargetReady(Part workPart)
        {
            if (workPart != theSession.Parts.Display)
            {
                theUI.NXMessageBox.Show("LYJ - 提示", NXMessageBox.DialogType.Warning,
                    "当前工作部件不是显示部件，无法向其导入图档。\r\n\r\n" +
                    "请先在装配导航器中双击要导入的目标部件，" +
                    "使其成为显示部件后再执行导入。");
                return false;
            }
            if (!workPart.IsFullyLoaded)
                workPart.LoadThisPartFully();
            return true;
        }

        /// <summary>
        /// 导入 Parasolid（.x_t / .xmt_txt）到当前工作部件。
        /// </summary>
        private static MenuBarManager.CallbackStatus ImportParasolidFrom(string xtFile)
        {
            try
            {
                Part workPart = RequireWorkPart();
                if (workPart == null || !EnsureImportTargetReady(workPart))
                    return MenuBarManager.CallbackStatus.Continue;

                ParasolidImporter importer = null;
                try
                {
                    importer = theSession.DexManager.CreateParasolidImporter();
                    importer.InputFile = xtFile;
                    importer.FlattenAssembly = true;
                    importer.UseActiveLayer = true;
                    EnableStandardObjectTypes(importer.ObjectTypes);
                    importer.Commit();
                }
                finally
                {
                    if (importer != null) importer.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus(
                    "X_T 导入完成: " + Path.GetFileName(xtFile));
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        // ==================== 其余格式：一一对应 NX 原生「文件 → 导入」 ====================
        //
        // 约定（与 NX 原生导入对话框一致）：
        //   · 一律导入到【当前工作部件】（ImportTo = WorkPart），不新开部件；
        //   · 导入器凡是有「展平装配」选项的一律勾选（FlattenAssembly = true）；
        //   · 凡是有「缝合」选项的一律勾选（SewSurfaces / Sew = true）；
        //   · 其余参数保持导入器默认值。
        //
        // 创建入口分两类（NXOpen 的设计如此，不是笔误）：
        //   · theSession.DexManager.CreateXxxImporter()  —— 绝大多数格式
        //   · workPart.ImportManager.CreateXxxImporter() —— NX 部件 / STL / VRML / CGM 等

        /// <summary>所有支持的图档扩展名（用于文件对话框的「所有支持的图档」筛选器）。</summary>
        private const string ImportAllExt =
            "*.stl;*.stp;*.step;*.igs;*.iges;*.x_t;*.x_b;*.xmt_txt;*.xmt_bin;*.prt;*.asm;*.xpr;*.xas;" +
            "*.CATPart;*.CATProduct;*.model;*.exp;*.session;*.dlv;*.3dxml;*.dxf;*.dwg;" +
            "*.sat;*.sab;*.asat;*.3dm;*.ipt;*.iam;*.3mf;*.obj;*.wrl;*.vrml;*.ifc;*.rvt;" +
            "*.g3d;*.mf1;*.arc;*.unv;*.pkg;*.asc;*.shp;*.cgm";

        /// <summary>
        /// 组装「导入图档」的文件筛选器：先给一个全格式入口，再按类别分组，
        /// 最后是常用格式的单列条目——用户既可以直接选任意支持的文件，也能按格式过滤。
        /// </summary>
        private static string BuildImportFilter()
        {
            const string cad3d =
                "*.stp;*.step;*.igs;*.iges;*.x_t;*.x_b;*.xmt_txt;*.xmt_bin;*.prt;*.asm;*.xpr;*.xas;" +
                "*.CATPart;*.CATProduct;*.model;*.exp;*.session;*.dlv;*.3dxml;*.sat;*.sab;*.asat;" +
                "*.3dm;*.ipt;*.iam;*.g3d";
            const string mesh = "*.stl;*.3mf;*.obj;*.wrl;*.vrml";
            const string bim = "*.ifc;*.rvt";
            const string twod = "*.dxf;*.dwg;*.cgm";
            const string other = "*.mf1;*.arc;*.unv;*.pkg;*.asc;*.shp";

            return "所有支持的图档|" + ImportAllExt
                 + "|常用三维格式 (STEP/IGES/Parasolid/CATIA/Creo/NX/Inventor/Rhino/ACIS)|" + cad3d
                 + "|网格面片 (STL/3MF/OBJ/VRML)|" + mesh
                 + "|建筑模型 (IFC/Revit)|" + bim
                 + "|二维图纸 (DXF/DWG/CGM)|" + twod
                 + "|其他 (I-deas/ASC/Geospatial)|" + other
                 + "|STEP (*.stp;*.step)|*.stp;*.step"
                 + "|IGES (*.igs;*.iges)|*.igs;*.iges"
                 + "|Parasolid (*.x_t;*.x_b;*.xmt_txt;*.xmt_bin)|*.x_t;*.x_b;*.xmt_txt;*.xmt_bin"
                 + "|NX 部件 (*.prt)|*.prt"
                 + "|CATIA V5 (*.CATPart;*.CATProduct)|*.CATPart;*.CATProduct"
                 + "|CATIA V4 (*.model;*.exp;*.session;*.dlv)|*.model;*.exp;*.session;*.dlv"
                 + "|Creo / Pro-E (*.prt;*.asm;*.xpr;*.xas)|*.prt;*.asm;*.xpr;*.xas"
                 + "|DXF/DWG (*.dxf;*.dwg)|*.dxf;*.dwg"
                 + "|ACIS (*.sat;*.sab;*.asat)|*.sat;*.sab;*.asat"
                 + "|Rhino (*.3dm)|*.3dm"
                 + "|STL (*.stl)|*.stl"
                 + "|3MF (*.3mf)|*.3mf"
                 + "|Wavefront OBJ (*.obj)|*.obj"
                 + "|IFC (*.ifc)|*.ifc"
                 + "|Revit (*.rvt)|*.rvt"
                 + "|所有文件 (*.*)|*.*";
        }

        /// <summary>
        /// 归一化导入用扩展名：
        ///   · 统一小写（.STP / .Stp / .stp 视为同一格式）；
        ///   · Creo 的版本号后缀（xxx.prt.3 / xxx.asm.7）还原成 .prt / .asm。
        /// </summary>
        private static string NormalizeImportExt(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();

            // .1 / .3 这类纯数字后缀 = Creo 的版本号
            if (ext.Length > 1 && ext.Substring(1).All(char.IsDigit))
            {
                string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(file)).ToLowerInvariant();
                if (inner == ".prt" || inner == ".asm")
                    return inner;
            }
            return ext;
        }

        /// <summary>
        /// 判断 .prt 是 NX 部件还是 Creo 部件。
        /// NX 部件文件头是文本魔数 "SPLMSSTR"（Siemens PLM stream，实测 NX 2606 生成的 prt 如此），
        /// 少数场景下也可能是 OLE 复合文档；Creo 部件是纯文本（#UGC:2 PART ...）。
        /// 两种 NX 特征都要认，否则 NX 部件会被误判成 Creo。
        /// </summary>
        private static bool IsNxPartFile(string file)
        {
            try
            {
                using (FileStream fs = File.OpenRead(file))
                {
                    byte[] head = new byte[8];
                    if (fs.Read(head, 0, 8) < 8) return false;

                    // "SPLMSSTR"
                    if (head[0] == (byte)'S' && head[1] == (byte)'P' && head[2] == (byte)'L'
                        && head[3] == (byte)'M' && head[4] == (byte)'S' && head[5] == (byte)'S'
                        && head[6] == (byte)'T' && head[7] == (byte)'R')
                        return true;

                    // OLE 复合文档
                    return head[0] == 0xD0 && head[1] == 0xCF
                        && head[2] == 0x11 && head[3] == 0xE0;
                }
            }
            catch
            {
                return false;   // 读不了就按 Creo 走，失败时 NX 会给明确报错
            }
        }

        /// <summary>
        /// 导入器统一执行壳：取工作部件 → 建导入器 → 逐项配置 → Commit → 销毁。
        /// 所有格式共用，保证错误处理与状态栏提示一致。
        /// </summary>
        private static MenuBarManager.CallbackStatus RunImporter<T>(
            string file, string displayName, Func<Part, T> create, Action<T> configure)
            where T : Builder
        {
            try
            {
                Part workPart = RequireWorkPart();
                if (workPart == null || !EnsureImportTargetReady(workPart))
                    return MenuBarManager.CallbackStatus.Continue;

                T importer = create(workPart);
                configure(importer);
                CommitImporter(importer, displayName, file);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 提交导入器，并统一处理销毁与失败提示。
        /// 单个格式失败不影响其它格式——用户可据此改用 NX 原生导入。
        /// </summary>
        private static void CommitImporter(Builder importer, string displayName, string file)
        {
            if (importer == null) return;
            try
            {
                importer.Commit();
                UFSession.GetUFSession().Ui.SetStatus(
                    displayName + " 导入完成: " + Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                theUI.NXMessageBox.Show("LYJ - " + displayName + " 导入失败",
                    NXMessageBox.DialogType.Error,
                    "文件：" + file + "\r\n\r\n" + ex.Message + "\r\n\r\n" +
                    "如果反复失败，可改用 NX 自带的 文件 → 导入 → " + displayName + "。");
            }
            finally
            {
                try { importer.Destroy(); } catch { }
            }
        }

        /// <summary>NX 部件（.prt）：把另一个部件的内容导入当前工作部件。</summary>
        private static MenuBarManager.CallbackStatus ImportNxPartFrom(string prtFile)
        {
            return RunImporter(prtFile, "部件",
                wp => wp.ImportManager.CreatePartImporter(),
                imp =>
                {
                    imp.FileName = prtFile;
                    imp.LayerOption = PartImporter.LayerOptionType.Work;   // 放到工作图层
                    imp.Scale = 1.0;
                });
        }

        /// <summary>Creo / Pro-E（.prt .asm .xpr .xas）：导入到工作部件。</summary>
        private static MenuBarManager.CallbackStatus ImportCreoFrom(string file)
        {
            return RunImporter(file, "Creo",
                wp => theSession.DexManager.CreateCreoImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = CreoImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>CATIA V4（.model .exp .session .dlv）。</summary>
        private static MenuBarManager.CallbackStatus ImportCatiaV4From(string file)
        {
            return RunImporter(file, "CATIA V4",
                wp => theSession.DexManager.CreateCatiav4Importer(),
                imp =>
                {
                    imp.InputFile = file;
                });
        }

        /// <summary>CATIA V5（.CATPart .CATProduct）：导入到工作部件。</summary>
        private static MenuBarManager.CallbackStatus ImportCatiaV5From(string file)
        {
            return RunImporter(file, "CATIA V5",
                wp => theSession.DexManager.CreateCatiav5Importer(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = Catiav5Importer.ImportToOption.WorkPart;
                });
        }

        /// <summary>CATIA V6 / 3DXML（.3dxml）。</summary>
        private static MenuBarManager.CallbackStatus Import3DXmlFrom(string file)
        {
            return RunImporter(file, "3DXML",
                wp => theSession.DexManager.CreateThreedxmlImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = ThreeDXMLImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>DXF / DWG（.dxf .dwg）：二维图纸与三维模型都能导入工作部件。</summary>
        private static MenuBarManager.CallbackStatus ImportDxfDwgFrom(string file)
        {
            return RunImporter(file, "DXF/DWG",
                wp => theSession.DexManager.CreateDxfdwgImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = DxfdwgImporter.ImportToEnum.Work;
                });
        }

        /// <summary>ACIS（.sat .sab .asat）：缝合选项勾选。</summary>
        private static MenuBarManager.CallbackStatus ImportAcisFrom(string file)
        {
            return RunImporter(file, "ACIS",
                wp => theSession.DexManager.CreateAcisImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.Sew = true;                  // 勾选：自动缝合
                });
        }

        /// <summary>Rhino（.3dm）：曲线 / 曲面 / 实体 / 网格都导入。</summary>
        private static MenuBarManager.CallbackStatus ImportRhinoFrom(string file)
        {
            return RunImporter(file, "Rhino",
                wp => theSession.DexManager.CreateRhinoImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = RhinoImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>Autodesk Inventor（.ipt .iam）。</summary>
        private static MenuBarManager.CallbackStatus ImportInventorFrom(string file)
        {
            return RunImporter(file, "Inventor",
                wp => theSession.DexManager.CreateInventorImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = InventorImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>3MF（.3mf）：三维网格模型，其余参数按导入器默认。</summary>
        private static MenuBarManager.CallbackStatus Import3MfFrom(string file)
        {
            return RunImporter(file, "3MF",
                wp => theSession.DexManager.CreateImporter3mf(),
                imp =>
                {
                    imp.InputFile = file;
                });
        }

        /// <summary>Wavefront OBJ（.obj）：按收敛体导入。</summary>
        private static MenuBarManager.CallbackStatus ImportObjFrom(string file)
        {
            return RunImporter(file, "OBJ",
                wp => theSession.DexManager.CreateWavefrontObjImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = WavefrontObjImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>VRML（.wrl .vrml）：走部件级的导入器（FileName，不是 InputFile）。</summary>
        private static MenuBarManager.CallbackStatus ImportVrmlFrom(string file)
        {
            return RunImporter(file, "VRML",
                wp => wp.ImportManager.CreateVrmlImporter(),
                imp =>
                {
                    imp.FileName = file;
                });
        }

        /// <summary>IFC（.ifc）：展平装配选项勾选。</summary>
        private static MenuBarManager.CallbackStatus ImportIfcFrom(string file)
        {
            return RunImporter(file, "IFC",
                wp => theSession.DexManager.CreateIfcImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = IfcImporter.ImportToOption.WorkPart;
                    imp.FlattenAssembly = true;      // 勾选：展平装配
                });
        }

        /// <summary>Revit（.rvt）。</summary>
        private static MenuBarManager.CallbackStatus ImportRevitFrom(string file)
        {
            return RunImporter(file, "Revit",
                wp => theSession.DexManager.CreateRevitImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = RevitImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>G3D（.g3d）。</summary>
        private static MenuBarManager.CallbackStatus ImportG3dFrom(string file)
        {
            return RunImporter(file, "G3D",
                wp => theSession.DexManager.CreateG3dImporter(),
                imp =>
                {
                    imp.InputFile = file;
                    imp.ImportTo = G3DImporter.ImportToOption.WorkPart;
                });
        }

        /// <summary>I-deas 部件（.mf1 .arc .unv .pkg）。</summary>
        private static MenuBarManager.CallbackStatus ImportIdeasFrom(string file)
        {
            return RunImporter(file, "I-deas",
                wp => theSession.DexManager.CreateBdsImporter(),
                imp =>
                {
                    imp.InputFile = file;
                });
        }

        /// <summary>ASC（.asc）：点 / 几何数据。</summary>
        private static MenuBarManager.CallbackStatus ImportAscFrom(string file)
        {
            return RunImporter(file, "ASC",
                wp => theSession.DexManager.CreateAscImporter(),
                imp =>
                {
                    imp.InputFile = file;
                });
        }

        /// <summary>Geospatial（.shp 等地理数据）：走 FileName 属性。</summary>
        private static MenuBarManager.CallbackStatus ImportGeospatialFrom(string file)
        {
            return RunImporter(file, "Geospatial",
                wp => theSession.DexManager.CreateGeospatialDataImporter(),
                imp =>
                {
                    imp.FileName = file;
                });
        }

        /// <summary>CGM（.cgm）：二维图，走部件级导入器。</summary>
        private static MenuBarManager.CallbackStatus ImportCgmFrom(string file)
        {
            return RunImporter(file, "CGM",
                wp => wp.ImportManager.CreateCgmImporter(),
                imp =>
                {
                    imp.FileName = file;
                });
        }

        /// <summary>
        /// 弹出对象选择对话框，让用户框选要导出的体（可多选，可跨装配组件选）。
        /// 取消或未选返回 false。
        /// </summary>
        private static bool TrySelectExportObjects(string fmt, out NXObject[] selected)
        {
            selected = null;
            // type=70 (UF_solid_type) 时 Subtype 被忽略，由第三参 SolidBodySubtype 决定：
            //   0  = UF_UI_SEL_FEATURE_BODY              普通实体
            //   35 = UF_UI_SEL_FEATURE_SHEET_BODY        片体
            //   17 = UF_UI_SEL_FEATURE_CONVERGENT_SOLID_BODY 收敛实体
            //   18 = UF_UI_SEL_FEATURE_CONVERGENT_SHEET_BODY 收敛片体
            var masks = new Selection.MaskTriple[]
            {
                new Selection.MaskTriple(70, 0, 0),    // 实体
                new Selection.MaskTriple(70, 0, 35),   // 片体
                new Selection.MaskTriple(70, 0, 17),   // 收敛实体
                new Selection.MaskTriple(70, 0, 18)    // 收敛片体
            };
            NXObject[] objs;
            Selection.Response resp = theUI.SelectionManager.SelectObjects(
                "选择要导出的模型（可框选、可多选），确定后导出 " + fmt,
                "LYJ - 导出" + fmt,
                Selection.SelectionScope.AnyInAssembly,
                Selection.SelectionAction.ClearAndEnableSpecific,
                false, false, masks, out objs);
            if (resp == Selection.Response.Cancel || resp == Selection.Response.Back)
                return false;
            if (objs == null || objs.Length == 0)
                return false;
            selected = objs;
            return true;
        }

        /// <summary>
        /// “导出STL” 按钮：用户选择模型后导出为 STL（二进制）。
        /// </summary>
        private static MenuBarManager.CallbackStatus ExportStlCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                Part displayPart = RequireDisplayedPartForExport();
                if (displayPart == null)
                    return MenuBarManager.CallbackStatus.Continue;

                NXObject[] selObjs;
                if (!TrySelectExportObjects("STL", out selObjs))
                    return MenuBarManager.CallbackStatus.Continue;

                string outFile = PickSaveFile("STL 文件 (*.stl)|*.stl",
                    "导出 STL", DefaultExportName(".stl"));
                if (outFile == null)
                    return MenuBarManager.CallbackStatus.Cancel;

                STLCreator creator = null;
                try
                {
                    creator = theSession.DexManager.CreateStlCreator();
                    creator.OutputFile = outFile;
                    creator.OutputType = STLCreator.OutputTypeEnum.Binary;
                    creator.ChordalTol = 0.05;
                    creator.AngularTol = 5.0;
                    creator.ExportSelectionBlock.SetArray(selObjs);
                    creator.Commit();
                }
                finally
                {
                    if (creator != null) creator.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus("STL 已导出 " + selObjs.Length + " 个对象: " + outFile);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// “导出STP/STEP” 按钮：当前显示部件导出为 STEP AP214。
        /// </summary>
        private static MenuBarManager.CallbackStatus ExportStpCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                Part displayPart = RequireDisplayedPartForExport();
                if (displayPart == null)
                    return MenuBarManager.CallbackStatus.Continue;

                NXObject[] selObjs;
                if (!TrySelectExportObjects("STP/STEP", out selObjs))
                    return MenuBarManager.CallbackStatus.Continue;

                string outFile = PickSaveFile("STEP 文件 (*.stp;*.step)|*.stp;*.step",
                    "导出 STEP", DefaultExportName(".stp"));
                if (outFile == null)
                    return MenuBarManager.CallbackStatus.Cancel;

                StepCreator creator = null;
                try
                {
                    creator = theSession.DexManager.CreateStepCreator();
                    creator.ExportFrom = StepCreator.ExportFromOption.DisplayPart;
                    creator.ExportAs = StepCreator.ExportAsOption.Ap214;
                    creator.OutputFile = outFile;
                    var def = ResolveNxFile("STEP214UG", "step214ug.def");
                    if (def != null) creator.SettingsFile = def;
                    // 用户选择的模型 → 只导出这些对象
                    creator.ExportSelectionBlock.SelectionComp.SetArray(selObjs);
                    // 对象类型过滤器默认全关，不开会导致"没有有效导出对象"
                    EnableStandardObjectTypes(creator.ObjectTypes);
                    creator.Commit();
                }
                finally
                {
                    if (creator != null) creator.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus("STEP 已导出: " + outFile);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// “导出IGS” 按钮：当前显示部件导出为 IGES。
        /// </summary>
        private static MenuBarManager.CallbackStatus ExportIgsCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                Part displayPart = RequireDisplayedPartForExport();
                if (displayPart == null)
                    return MenuBarManager.CallbackStatus.Continue;

                NXObject[] selObjs;
                if (!TrySelectExportObjects("IGS", out selObjs))
                    return MenuBarManager.CallbackStatus.Continue;

                string outFile = PickSaveFile("IGES 文件 (*.igs;*.iges)|*.igs;*.iges",
                    "导出 IGES", DefaultExportName(".igs"));
                if (outFile == null)
                    return MenuBarManager.CallbackStatus.Cancel;

                IgesCreator creator = null;
                try
                {
                    creator = theSession.DexManager.CreateIgesCreator();
                    creator.ExportFrom = IgesCreator.ExportFromOption.DisplayPart;
                    creator.OutputFile = outFile;
                    creator.FlattenAssembly = true;
                    creator.LayerMask = "0-99999";
                    var def = ResolveNxFile("iges", "igesexport.def");
                    if (def != null) creator.SettingsFile = def;
                    // 用户选择的模型 → 只导出这些对象
                    creator.ExportSelectionBlock.SelectionComp.SetArray(selObjs);
                    // 对象类型过滤器默认全关，不开会导致"没有有效导出对象"
                    EnableStandardObjectTypes(creator.ObjectTypes);
                    creator.Commit();
                }
                finally
                {
                    if (creator != null) creator.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus("IGES 已导出: " + outFile);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// “导出X_T” 按钮：当前显示部件导出为 Parasolid (X_T)。
        /// </summary>
        private static MenuBarManager.CallbackStatus ExportXtCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                Part displayPart = RequireDisplayedPartForExport();
                if (displayPart == null)
                    return MenuBarManager.CallbackStatus.Continue;

                NXObject[] selObjs;
                if (!TrySelectExportObjects("X_T", out selObjs))
                    return MenuBarManager.CallbackStatus.Continue;

                string outFile = PickSaveFile("Parasolid 文件 (*.x_t)|*.x_t",
                    "导出 X_T", DefaultExportName(".x_t"));
                if (outFile == null)
                    return MenuBarManager.CallbackStatus.Cancel;

                ParasolidExporter exporter = null;
                try
                {
                    exporter = theSession.DexManager.CreateParasolidExporter();
                    exporter.ExportFrom = ParasolidExporter.ExportFromOption.DisplayedPart;
                    exporter.OutputFile = outFile;
                    exporter.ParasolidVersion = ParasolidExporter.ParasolidVersionOption.Current;
                    exporter.FlattenAssembly = true;
                    exporter.HeaderInformation = true;
                    // 对象类型过滤器默认全关，不开会导致"没有有效导出对象"
                    EnableStandardObjectTypes(exporter.ObjectTypes);
                    // 用户选择的模型 → 只导出这些对象
                    exporter.ExportSelectionBlock.SelectionComp.SetArray(selObjs);
                    exporter.Commit();
                }
                finally
                {
                    if (exporter != null) exporter.Destroy();
                }

                UFSession.GetUFSession().Ui.SetStatus("X_T 已导出: " + outFile);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        // ==================== 部件显示：透明化 + 固定颜色 ====================

        /// <summary>「透明化」：打开原生对话框（上面选部件，下面透明度滑条）。</summary>
        private static MenuBarManager.CallbackStatus TransparencyCallback(MenuButtonEvent buttonEvent)
        {
            DisplayTools.ShowTransparencyDialog();
            return MenuBarManager.CallbackStatus.Continue;
        }

        // 「显示」组的颜色按钮：固定 10 颗，颜色每次点击时从 config\color_slots.txt 现读，
        // 所以在配置窗口里改完颜色**不用重启 NX**，回到图形区直接点就能用新颜色。
        private static string ColorActionId(int slot)
        {
            // 两位编号：color01…color10 —— 一位数会有 color1/color10 的前缀歧义
            return "LYJ_APP__color" + (slot + 1).ToString("00");
        }

        private static MenuBarManager.CallbackStatus ColorSlotCallback(int slot)
        {
            try
            {
                DisplayGroupCfg cfg = DisplayGroupCfg.Load();
                ColorSlot s = (cfg.Slots != null && slot < cfg.Slots.Length) ? cfg.Slots[slot] : null;

                if (s == null || !s.Enabled)
                {
                    // 空位（图标是个「＋」）：直接带去配置窗口，比弹一句提示有用
                    ColorConfigDialog.Show();
                    return MenuBarManager.CallbackStatus.Continue;
                }

                string colorName = s.ColorName;
                if (string.IsNullOrEmpty(colorName)) colorName = ColorSlots.EnglishName(s.Name);
                DisplayTools.ApplyColorSlot(s.ColorId, colorName, new[] { s.R, s.G, s.B }, s.Name);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>「配置」按钮：打开 NX 原生对话框，用内置色盘配置最多 10 个颜色按钮。</summary>
        private static MenuBarManager.CallbackStatus ColorConfigCallback(MenuButtonEvent e)
        {
            ColorConfigDialog.Show();
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 导出前确保有显示部件。
        /// </summary>
        private static Part RequireDisplayedPartForExport()
        {
            Part displayPart = theSession.Parts.Display;
            if (displayPart == null)
            {
                theUI.NXMessageBox.Show("LYJ - 提示", NXMessageBox.DialogType.Warning,
                    "请先打开要导出的部件文件。");
            }
            return displayPart;
        }

        /// <summary>
        /// 弹出文件保存对话框，取消返回 null。
        /// </summary>
        private static string PickSaveFile(string filter, string title, string defaultName)
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = title;
                sfd.Filter = filter;
                sfd.FileName = defaultName;
                sfd.OverwritePrompt = true;
                sfd.RestoreDirectory = true;
                if (sfd.ShowDialog() != DialogResult.OK)
                    return null;
                return sfd.FileName;
            }
        }

        /// <summary>
        /// 默认导出文件名：当前显示部件名（无扩展名）+ 新扩展名。
        /// </summary>
        private static string DefaultExportName(string ext)
        {
            string name = "export";
            Part displayPart = theSession.Parts.Display;
            if (displayPart != null && !string.IsNullOrEmpty(displayPart.FullPath))
                name = Path.GetFileNameWithoutExtension(displayPart.FullPath);
            return name + ext;
        }

        /// <summary>
        /// 解析 NX 安装目录下的文件路径（相对 UGII_BASE_DIR），不存在返回 null。
        /// </summary>
        private static string ResolveNxFile(params string[] relativeParts)
        {
            string baseDir = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if (string.IsNullOrEmpty(baseDir)) return null;
            string p = baseDir;
            foreach (string part in relativeParts)
                p = Path.Combine(p, part);
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// 临时缓存 .prt 路径（Dex 导入器的 OutputFile，translator 中转用）。
        /// </summary>
        private static string TempImportPrt(string tag)
        {
            return Path.Combine(Path.GetTempPath(), "lyj_" + tag + "_out.prt");
        }

        /// <summary>
        /// 弹出文件选择框，取消返回 null。
        /// </summary>
        private static string PickFile(string filter, string title)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = title;
                ofd.Filter = filter;
                ofd.RestoreDirectory = true;
                if (ofd.ShowDialog() != DialogResult.OK)
                    return null;
                return ofd.FileName;
            }
        }

        /// <summary>
        /// 校验工作部件；无工作部件时弹窗提示并返回 null。
        /// </summary>
        private static Part RequireWorkPart()
        {
            Part workPart = theSession.Parts.Work;
            if (workPart == null)
            {
                theUI.NXMessageBox.Show("错误", NXMessageBox.DialogType.Error,
                    "请先打开或新建一个部件文件再执行导入。");
            }
            return workPart;
        }

        /// <summary>
        /// 读取 STEP 文件头 FILE_SCHEMA，识别应用协议："203" / "214" / "242"。
        /// 识别失败时默认 "214"。
        /// </summary>
        private static string DetectStepAp(string stepFile)
        {
            try
            {
                using (StreamReader sr = new StreamReader(stepFile, System.Text.Encoding.ASCII))
                {
                    for (int i = 0; i < 200; i++)
                    {
                        string line = sr.ReadLine();
                        if (line == null) break;

                        if (line.IndexOf("AP242", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf("242_MANAGED", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "242";
                        if (line.IndexOf("AUTOMOTIVE_DESIGN", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "214";
                        if (line.IndexOf("CONFIG_CONTROL_DESIGN", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "203";
                    }
                }
            }
            catch
            {
                // 读取失败按默认处理
            }
            return "214";
        }

        private static void ShowError(Exception ex)
        {
            try
            {
                // 跨版本兼容：老版本 NX 缺少新 API 时给出可读提示，而不是 .NET 原始异常
                string msg = ex.Message;
                if (IsApiMissing(ex))
                {
                    msg = "当前 NX 版本（" + NxVersionText() + "）不支持该功能。\r\n\r\n" +
                          "缺少的接口：" + ex.GetType().Name + " — " + ex.Message + "\r\n\r\n" +
                          "可以改用 NX 自带命令完成（文件 → 导入/导出）。";
                }
                theUI.NXMessageBox.Show("LYJ - 错误",
                    NXMessageBox.DialogType.Error, msg);
            }
            catch
            {
                // 忽略二次异常
            }
        }

        /// <summary>判断异常是否属于「NX 版本缺少 API」类型（类型/成员/文件找不到）。</summary>
        private static bool IsApiMissing(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is TypeLoadException || e is MissingMemberException || e is MissingMethodException
                    || e is FileNotFoundException || e is FileLoadException || e is EntryPointNotFoundException
                    || e is BadImageFormatException)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 注册菜单回调并包一层版本容错：
        /// 老版本 NX（如 NX12）缺少某个 API 时，点按钮只会弹出中文提示，不会让 NX 报错。
        /// 说明：.NET 是在调用方法时才 JIT 编译方法体，所以包在外层 try 里可以捕获 TypeLoadException。
        /// </summary>
        private static void AddSafeAction(MenuBarManager mbm, string actionId,
            Func<MenuButtonEvent, MenuBarManager.CallbackStatus> callback, string featureName)
        {
            mbm.AddMenuAction(actionId, delegate (MenuButtonEvent ev)
            {
                try
                {
                    return callback(ev);
                }
                catch (Exception ex)
                {
                    if (IsApiMissing(ex))
                    {
                        try
                        {
                            // 导入类按钮有明确替代路径，单独给提示
                            string extra = featureName.StartsWith("导入", StringComparison.Ordinal)
                                ? "\r\n\r\n可以改用：文件 → 导入 → 对应格式。"
                                : "\r\n\r\n可以改用 NX 自带命令完成。";
                            theUI.NXMessageBox.Show("LYJ - " + featureName,
                                NXMessageBox.DialogType.Warning,
                                "当前 NX 版本（" + NxVersionText() + "）不支持该功能。" + extra);
                        }
                        catch { }
                    }
                    else
                    {
                        ShowError(ex);
                    }
                    return MenuBarManager.CallbackStatus.Continue;
                }
            });
        }

        #region 加工：插入工序（加工模板策略）

        // ==================== 配置区 ====================
        // 部署根目录不再写死：优先环境变量 LYJ_HOME，其次根据本 DLL 位置推断
        // （DLL 放在 <根>\startup\ 下时取上一级），最后退回默认值。
        // 因此外挂可以安装到任意目录（C:\LYJ、D:\Software\LYJ 等），
        // 一个安装包即可用在任意电脑上。
        private static readonly string CamDeployRoot      = ResolveDeployRoot();
        private static readonly string CamStartupDir      = Path.Combine(CamDeployRoot, "startup");
        private static readonly string CamAppDir          = Path.Combine(CamDeployRoot, "application");
        private static readonly string CamCfgDir          = Path.Combine(CamDeployRoot, "config");   // 配置目录（模板+配置+图标）
        private static readonly string CamIconDir         = Path.Combine(CamCfgDir, "BMP");          // 工序图标目录（英文 BMP 命名）
        private static readonly string CamListFile        = Path.Combine(CamCfgDir, "cam_strategies.txt");    // 工序名清单（自动生成）
        private static readonly string CamStampFile       = Path.Combine(CamCfgDir, "cam_strategies.stamp");  // 模板时间戳标记
        private static readonly string CamIconMapFile     = Path.Combine(CamCfgDir, "icon_map.txt");          // 图标记录（只补缺）
        private const string ToolsGrbFile    = "rbn_nxprogramming_tools.grb";  // 旧：NX-编程 选项卡的"工具"组，启动时清掉
        private const string OldSysToolsGrb  = "rbn_lyj_systools.grb";     // 旧：LYJ 的"系统工具"组，启动时清掉
        private const string OldInsertGrb    = "rbn_nxprogramming_insert.grb"; // 旧：插入工序组，启动时清掉
        private static readonly string CamTemplateCfgFile = Path.Combine(CamCfgDir, "cam_template.txt");      // 模板 prt 路径配置
        private static readonly string CamDefaultTemplate = Path.Combine(CamCfgDir, "测试.prt");           // 默认加工模板（随安装包发布）

        /// <summary>供加工助手（MachAssistantForm）复用：部署根目录。</summary>
        internal static string DeployRoot { get { return CamDeployRoot; } }

        /// <summary>供「显示」组配置复用：startup / application / config 三个目录。</summary>
        internal static string StartupDir { get { return CamStartupDir; } }
        internal static string AppDir { get { return CamAppDir; } }
        internal static string CfgDir { get { return CamCfgDir; } }

        /// <summary>供加工助手复用：当前加工模板 prt 路径（cam_template.txt 里第一个有效路径）。</summary>
        internal static string TemplatePath { get { return GetTemplatePath(); } }

        /// <summary>供加工助手复用：配置里列出的全部模板 prt（一行一个，# 或 ; 开头为注释）。
        /// 加工助手→模板页按这个顺序为每个模板建一个根节点。</summary>
        internal static List<string> TemplatePaths { get { return GetTemplatePaths(); } }

        /// <summary>供加工助手复用：模板配置文件 cam_template.txt 的完整路径；
        /// 文件不存在时先按标准说明块生成一份，保证“打开配置文档”永远有东西可开。</summary>
        internal static string TemplateCfgPath
        {
            get
            {
                EnsureTemplateCfg();
                return CamTemplateCfgFile;
            }
        }

        /// <summary>
        /// 供加工助手复用：按模板里的工序名插入工序。
        /// 与加工助手「模板」页双击插入走完全相同的实现
        /// （InsertTemplateOperation → CAMOperationCollection.CreateWithUserName），
        /// 目标组同样优先取工序导航器里当前选中的程序/几何/方法/刀具组。
        /// templateType 为 null/空时用第一个模板。
        /// </summary>
        internal static void InsertOpByName(string opName)
        {
            InsertOpByName(opName, null);
        }

        /// <summary>指定模板插入工序（templateType = 模板 prt 文件名去扩展名）。</summary>
        internal static void InsertOpByName(string opName, string templateType)
        {
            if (string.IsNullOrEmpty(opName)) return;
            InsertTemplateOperation(opName.Trim(), templateType);
        }

        /// <summary>
        /// 加工模板注册名 = 模板 prt 的文件名（不带扩展名）。
        /// NX 的 CAMOperationCollection.CreateWithUserName 按这个名字去
        /// MACH\resource\template_part\metric 里找模板部件，所以换模板 prt 后名字自动跟着变。
        /// </summary>
        private static string CamTemplateTypeName
        {
            get { return TemplateTypeOf(GetTemplatePath()); }
        }

        /// <summary>解析部署根目录（支持任意安装路径）。</summary>
        private static string ResolveDeployRoot()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("LYJ_HOME");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                    return env.TrimEnd('\\');
            }
            catch { }
            try
            {
                string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        // 标准布局：<根>\startup\LYJ.dll → 根
                        if (string.Equals(Path.GetFileName(dir), "startup", StringComparison.OrdinalIgnoreCase))
                        {
                            string parent = Path.GetDirectoryName(dir);
                            if (!string.IsNullOrEmpty(parent))
                                return parent;
                        }
                        return dir;
                    }
                }
            }
            catch { }
            // 极端兜底：兼容旧的 D:\LYJ 部署
            return Directory.Exists(@"D:\LYJ") ? @"D:\LYJ" : @"C:\LYJ";
        }

        /// <summary>当前 NX 主版本号（NX12 → 12；NX1847 → 1847；NX2606 → 2606）。取不到返回 0。</summary>
        private static int NxMajorVersion()
        {
            try
            {
                Version v = typeof(Session).Assembly.GetName().Version;
                if (v != null && v.Major > 0)
                    return v.Major;
            }
            catch { }
            return 0;
        }

        /// <summary>NX 版本文本（用于提示/诊断）。</summary>
        private static string NxVersionText()
        {
            int m = NxMajorVersion();
            if (m >= 1000)
                return "NX " + m;          // NX1847 起的年份式编号
            if (m > 0)
                return "NX " + m + ".0";   // NX12、NX11、NX10…
            return "NX (未识别)";
        }

        /// <summary>
        /// 收敛体 STL 导入 API（NXOpen.Facet）是否可用。NX11 之前没有该命名空间，
        /// 直接调用会抛 TypeLoadException，这里先探测一次并缓存。
        /// </summary>
        private static bool? _facetApi;
        private static bool FacetApiAvailable
        {
            get
            {
                if (_facetApi.HasValue)
                    return _facetApi.Value;
                bool ok;
                try
                {
                    ok = Type.GetType("NXOpen.Facet.STLImportBuilder, NXOpen") != null;
                }
                catch { ok = false; }
                _facetApi = ok;
                return ok;
            }
        }

        /// <summary>
        /// 运行时自愈：把安装包里没有、由程序自己生成的配置全部补齐（缺什么补什么，已有不动）。
        /// 安装包只带 startup（菜单/功能区/DLL）+ 7 张按钮图标 + config\测试.prt，
        /// 其余靠这里在第一次启动 NX 时创建：
        ///   config\            配置目录
        ///   config\BMP\        自定义工序图标素材目录（可空，空则由内置生成器画图标）
        ///   config\cam_template.txt   当前使用的模板 prt 路径（用户可改）
        ///   config\cam_strategies.txt 工序清单（生成器写）
        ///   config\cam_strategies.stamp 模板时间戳（生成器写）
        ///   config\icon_map.txt       图标记录（生成器写）
        ///   startup\gen_cam_strategies.men / rbn_nxprogramming.rtb（生成器写）
        ///   application\lyj_*.bmp / cam_op_*.bmp / cam_grp_*.bmp（生成器画）
        /// </summary>
        private static void EnsureRuntimeFiles()
        {
            try
            {
                Directory.CreateDirectory(CamDeployRoot);
                Directory.CreateDirectory(CamStartupDir);
                Directory.CreateDirectory(CamAppDir);
                Directory.CreateDirectory(CamCfgDir);
                Directory.CreateDirectory(CamIconDir);
            }
            catch { }
            // 模板路径配置：带说明注释（旧版只有一行路径的文件会自动补说明并保留路径）
            EnsureTemplateCfg();
            // 多模板：逐个复制到本机 NX 加工模板目录，CreateWithUserName 按模板名才认得
            try
            {
                foreach (string tpl in GetTemplatePaths())
                    if (File.Exists(tpl)) TryRegisterTemplateToNx(tpl);
            }
            catch { }
            EnsureRibbonTabs();
        }

        /// <summary>
        /// 功能区选项卡骨架（每次启动幂等重写，保证从旧版本升级后按钮也落在新位置）：
        ///   LYJ 选项卡 = 图档导入 + 图档导出 + 加工（加工助手）
        /// 原来独立的「NX-编程」选项卡已并入 LYJ，启动时删除其 rtb/men，避免残留空页签。
        /// 选项卡按"组文件存在与否"拼装，缺哪个组就跳过，不会让 NX 报找不到组。
        /// </summary>
        private static void EnsureRibbonTabs()
        {
            try
            {
                Encoding gbk = Encoding.GetEncoding("GBK");

                // 1. "LYJ"选项卡：按存在的组文件拼
                var rtb = new StringBuilder();
                rtb.AppendLine("TITLE LYJ");
                rtb.AppendLine();
                rtb.AppendLine("VERSION 170");
                foreach (string g in new string[]
                    { "rbn_lyj_import.grb", "rbn_lyj_export.grb",
                      "rbn_lyj_color.grb", "rbn_lyj_cam.grb" })
                {
                    // 组文件缺失时补写：缺了组，rtb 会变成只有 TITLE 的空壳，
                    // NX 启动直接弹「在文件中构建工具条的数据不充分」。
                    EnsureGroupFile(g);
                    if (!File.Exists(Path.Combine(CamStartupDir, g)))
                        continue;
                    rtb.AppendLine();
                    rtb.AppendLine("GROUP " + g);
                }
                File.WriteAllText(Path.Combine(CamStartupDir, "rbn_lyj.rtb"), rtb.ToString(), gbk);
            }
            catch { }

            // 2. 清掉旧版本遗留的功能区/菜单文件（已被新布局取代）
            //    「NX-编程」选项卡并入 LYJ 后，rbn_nxprogramming.rtb 与 gen_cam_strategies.men
            //    都不再生成，这里每次启动兜底删除，防止旧文件被 NX 加载出空页签/空菜单。
            try
            {
                foreach (string old in new string[]
                    { OldSysToolsGrb, OldInsertGrb, "rbn_lyj_mach.grb", ToolsGrbFile,
                      "rbn_nxprogramming.rtb", "gen_cam_strategies.men",
                      "rbn_lyj_transparency.grb" })   // 透明化已并入「显示」组（rbn_lyj_color.grb）
                {
                    string p = Path.Combine(CamStartupDir, old);
                    if (File.Exists(p))
                        File.Delete(p);
                }
            }
            catch { }
        }

        private static int lastIconMade;        // 最近一次刷新新生成的图标数（仅用于提示）

        /// <summary>
        /// 组文件（.grb）不存在时补写一份标准内容。
        /// 这些组是定值资产（不随模板变化），但漏拷时会连累 rbn_lyj.rtb 变成空壳，
        /// NX 就报「在文件中构建工具条的数据不充分」。
        /// 「显示」组例外：它按 config\color_slots.txt 生成，每次启动都重写。
        /// </summary>
        private static void EnsureGroupFile(string fileName)
        {
            string path = Path.Combine(CamStartupDir, fileName);
            bool isDisplay = fileName.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isDisplay)
            {
                WriteDisplayGroupFile(path);
                return;
            }
            if (File.Exists(path))
                return;

            var sb = new StringBuilder();
            if (fileName.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 导入组：单个「导入图档」按钮，选文件后按扩展名自动分派
                sb.Append("TITLE 图档导入\n\nVERSION 170\n");
                sb.Append("\nBUTTON LYJ_APP__import_auto\n");
                sb.Append("LABEL 导入图档\n");
                sb.Append("BITMAP lyj_import_auto\n");
                sb.Append("RIBBON_STYLE ALWAYS_LARGE_IMAGE\n");
            }
            else if (fileName.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 导出组：一个「导出图档」下拉按钮（CASCADE），展开四项格式。
                // 内容与 资源\菜单功能区\rbn_lyj_export.grb 保持一致；内层 BUTTON 显式写
                // LABEL/BITMAP，与 lyj.men 里的同名菜单项同源，免得下拉项显示成动作 ID。
                sb.Append("TITLE 图档导出\n\nVERSION 170\n");
                sb.Append("\nBEGIN_CASCADE LYJ_RBN_EXPORT_CASCADE\n");
                sb.Append("RIBBON_STYLE ALWAYS_LARGE_IMAGE\n");
                sb.Append("LABEL 导出图档\n");
                sb.Append("BITMAP lyj_export_auto\n");
                foreach (string[] kv in new[]
                {
                    new[] { "export_stl", "导出STL" },
                    new[] { "export_stp", "导出STP/STEP" },
                    new[] { "export_igs", "导出IGS" },
                    new[] { "export_xt",  "导出X_T" },
                })
                {
                    sb.Append("\nBUTTON LYJ_APP__" + kv[0] + "\n");
                    sb.Append("LABEL " + kv[1] + "\n");
                    sb.Append("BITMAP lyj_" + kv[0] + "\n");
                }
                sb.Append("\nEND_CASCADE\n");
            }
            else if (fileName.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 走不到这里：显示组由 WriteDisplayGroupFile 按配置生成（见上面 isDisplay 分支）
                WriteDisplayGroupFile(path);
                return;
            }
            else
            {
                // 加工组：只有「加工助手」一个按钮（原「NX-编程」选项卡并入 LYJ）
                sb.Append("TITLE 加工\n\nVERSION 170\n");
                sb.Append("\nBUTTON LYJ_APP__cam_assistant\n");
                sb.Append("LABEL 加工助手\n");
                sb.Append("BITMAP lyj_cam_assistant\n");
                sb.Append("RIBBON_STYLE ALWAYS_LARGE_IMAGE\n");
            }
            File.WriteAllText(path, sb.ToString(), Encoding.GetEncoding("GBK"));
        }

        /// <summary>
        /// 「显示」组（rbn_lyj_color.grb）：**结构固定**写 12 个按钮
        /// （透明化 + 颜色 1~10 + 配置，全部 RIBBON_STYLE MEDIUM_IMAGE 仅图标、中号），
        /// 每次启动重写一次即可。
        ///
        /// 为什么固定而不按配置增删：NX 的功能区是启动时加载的，运行时改 .grb 不重载，
        /// 增删按钮就得重启 NX。固定 12 个后，用户在配置里改颜色**不需要重启** ——
        /// 颜色值是点按钮时现场从 config\color_slots.txt 读的；没配置的槽画成「＋」空位。
        /// </summary>
        private static void WriteDisplayGroupFile(string path)
        {
            DisplayGroupCfg cfg = DisplayGroupCfg.Load();

            var sb = new StringBuilder();
            sb.Append("TITLE 显示\n\nVERSION 170\n");

            // 槽图标的文件名带「配置指纹」：NX 按 BITMAP 名字缓存位图，
            // 名字不变时改了颜色也可能一直用旧图（配置页和按钮对不上就是这个原因）。
            string suffix = ColorSlots.IconSuffix(cfg);

            if (cfg.ShowTransparency)
                AppendIconButton(sb, "LYJ_APP__transparency", "透明化", "lyj_transparency");

            for (int i = 0; i < ColorSlots.Max; i++)
            {
                ColorSlot s = (cfg.Slots != null && i < cfg.Slots.Length) ? cfg.Slots[i] : null;
                bool ready = s != null && s.Enabled;
                string label = ready
                    ? (string.IsNullOrEmpty(s.Name) ? ("颜色 " + (i + 1)) : s.Name)
                    : ("颜色 " + (i + 1) + "（未配置）");
                AppendIconButton(sb, ColorActionId(i), label, ColorSlots.IconName(i, suffix));
            }

            AppendIconButton(sb, "LYJ_APP__color_config", "配置颜色按钮", "lyj_color_config");

            File.WriteAllText(path, sb.ToString(), Encoding.GetEncoding("GBK"));
        }

        /// <summary>显示组的一颗按钮：中号、仅图标（文字只在鼠标停留时当提示用）。</summary>
        private static void AppendIconButton(StringBuilder sb, string actionId, string label, string bitmap)
        {
            sb.Append("\nBUTTON " + actionId + "\n");
            sb.Append("LABEL " + label + "\n");
            sb.Append("BITMAP " + bitmap + "\n");
            sb.Append("RIBBON_STYLE MEDIUM_IMAGE\n");
        }

        /// <summary>配置对话框保存后调用：重写显示组文件（NX 下次启动生效）。</summary>
        internal static void RebuildDisplayGroup()
        {
            try { WriteDisplayGroupFile(Path.Combine(CamStartupDir, "rbn_lyj_color.grb")); }
            catch { }
        }

        private static string CamActionId(int index)
        {
            return "LYJ_CAM__OP" + (index + 1).ToString("00");
        }

        /// <summary>
        /// 主模板 prt 路径（= 配置里的第一个有效模板），供生成器/日志使用。
        /// 单模板的旧逻辑保持不变；多模板请用 TemplatePaths。
        /// </summary>
        private static string GetTemplatePath()
        {
            return GetTemplatePaths()[0];
        }

        /// <summary>
        /// 解析配置里列出的全部模板路径（保持书写顺序、去重；# 或 ; 开头是注释，行尾可写注释）。
        /// 配置里没有有效项时回退：
        ///   config\测试.prt（安装包自带）→ config 目录下最新的 *.prt → 默认路径（仅用于报错提示）。
        /// </summary>
        private static List<string> GetTemplatePaths()
        {
            var list = new List<string>();
            try
            {
                if (File.Exists(CamTemplateCfgFile))
                {
                    foreach (string raw in File.ReadAllLines(CamTemplateCfgFile, Encoding.UTF8))
                    {
                        string p = StripCfgComment(raw);
                        if (p.Length == 0) continue;
                        if (p.Length > 1 && p[0] == '"' && p[p.Length - 1] == '"')
                            p = p.Substring(1, p.Length - 2).Trim();   // 允许加引号
                        if (p.Length == 0 || !File.Exists(p)) continue;
                        bool dup = false;
                        foreach (string q in list)
                            if (string.Equals(q, p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (!dup) list.Add(p);
                    }
                }
            }
            catch { }
            if (list.Count == 0 && File.Exists(CamDefaultTemplate))
                list.Add(CamDefaultTemplate);
            if (list.Count == 0)
            {
                try
                {
                    var files = new List<FileInfo>(new DirectoryInfo(CamCfgDir).GetFiles("*.prt"));
                    if (files.Count > 0)
                    {
                        files.Sort(delegate (FileInfo a, FileInfo b)
                        {
                            return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);   // 最新的排前面
                        });
                        list.Add(files[0].FullName);
                    }
                }
                catch { }
            }
            if (list.Count == 0)
                list.Add(CamDefaultTemplate);
            return list;
        }

        /// <summary>
        /// 读取 cam_template.txt 里用户配置的原始路径（保持书写顺序、去重；
        /// 不过滤存在性、不做回退）——配置窗口的编辑数据源。
        /// </summary>
        internal static List<string> GetTemplatePathsRaw()
        {
            var list = new List<string>();
            try
            {
                if (File.Exists(CamTemplateCfgFile))
                {
                    foreach (string raw in File.ReadAllLines(CamTemplateCfgFile, Encoding.UTF8))
                    {
                        string p = StripCfgComment(raw);
                        if (p.Length > 1 && p[0] == '"' && p[p.Length - 1] == '"')
                            p = p.Substring(1, p.Length - 2).Trim();
                        if (p.Length == 0) continue;
                        bool dup = false;
                        foreach (string q in list)
                            if (string.Equals(q, p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (!dup) list.Add(p);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 把用户在配置窗口编辑的模板路径写回 cam_template.txt（保留说明块）。
        /// 编码 UTF-8 无 BOM、CRLF，与 EnsureTemplateCfg 保持一致，便于升级时备份迁移。
        /// </summary>
        internal static void SaveTemplatePaths(IList<string> paths)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(TemplateCfgHeader());
                if (paths != null)
                {
                    foreach (string p in paths)
                    {
                        string t = (p ?? "").Trim();
                        if (t.Length > 0) sb.AppendLine(t);
                    }
                }
                Directory.CreateDirectory(CamCfgDir);
                File.WriteAllText(CamTemplateCfgFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>cam_template.txt 头部说明块（初始化与配置窗口写回共用，保持文件格式统一）。</summary>
        private static string TemplateCfgHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("#  加工模板配置  cam_template.txt");
            sb.AppendLine("# ------------------------------------------------------------");
            sb.AppendLine("#  作用：给「加工助手 → 模板」页提供可双击插入的加工策略。");
            sb.AppendLine("#        每个模板 prt 显示为一个根节点（名字=文件名去扩展名），");
            sb.AppendLine("#        下面按模板里的程序树显示 工序文件夹 + 加工策略。");
            sb.AppendLine("#");
            sb.AppendLine("#  维护：模板路径在 加工助手 → 配置 → 模板配置 里新增/编辑/删除，");
            sb.AppendLine("#        保存后自动写回本文件；一般不需要手动编辑。");
            sb.AppendLine("#        一行一个模板文件的完整路径，按书写顺序显示，可写多个。");
            sb.AppendLine("#        以 # 或 ; 开头的行是注释，不会被读取；");
            sb.AppendLine("#        路径含空格不用加引号（加了也认）。");
            sb.AppendLine("#");
            sb.AppendLine("#  说明：模板 prt 会在 NX 启动时自动复制到本机加工模板目录");
            sb.AppendLine("#        <NX安装目录>\\MACH\\resource\\template_part\\metric，");
            sb.AppendLine("#        不需要手动拷贝；本文件用 UTF-8 保存即可，");
            sb.AppendLine("#        升级/迁移时把本文件连同模板 prt 一起备份即可。");
            sb.AppendLine("# ============================================================");
            return sb.ToString();
        }

        /// <summary>去掉配置行里的注释：整行以 # ; // 开头 → 空串；行内 " #" / " ;" 之后视为注释。</summary>
        private static string StripCfgComment(string line)
        {
            if (line == null) return "";
            string s = line.Trim();
            if (s.Length == 0) return "";
            if (s[0] == '#' || s[0] == ';') return "";
            if (s.StartsWith("//")) return "";
            int cut = -1;
            foreach (char c in new char[] { '#', ';' })
            {
                int i = s.IndexOf(c);
                if (i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t') && (cut < 0 || i < cut))
                    cut = i;
            }
            if (cut > 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        /// <summary>模板 prt 文件名（去扩展名）= CreateWithUserName 认的“模板名”。</summary>
        private static string TemplateTypeOf(string tplPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(tplPath))
                {
                    string n = Path.GetFileNameWithoutExtension(tplPath);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return "测试";
        }

        /// <summary>
        /// 配置文件 cam_template.txt：不存在则带着说明注释写一份；
        /// 已存在但没有说明块（旧版只写了一行路径）→ 补上说明并保留原有有效路径。
        /// 编码 UTF-8 无 BOM，换行 CRLF。
        /// </summary>
        private static void EnsureTemplateCfg()
        {
            try
            {
                const string mark = "# ====";
                string text = null;
                if (File.Exists(CamTemplateCfgFile))
                {
                    text = File.ReadAllText(CamTemplateCfgFile, Encoding.UTF8);
                    string head = (text ?? "").TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    if (head.StartsWith(mark))
                        return;                       // 已经有说明块，不动用户内容
                }

                // 收集原有有效路径（旧文件可能就一行）
                var keep = new List<string>();
                foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    string p = StripCfgComment(raw);
                    if (p.Length == 0 || p.StartsWith(mark)) continue;
                    if (p.Length > 1 && p[0] == '"' && p[p.Length - 1] == '"')
                        p = p.Substring(1, p.Length - 2).Trim();
                    if (p.Length > 0 && !keep.Contains(p)) keep.Add(p);
                }
                if (keep.Count == 0)
                {
                    string tpl = GetTemplatePath();
                    if (!string.IsNullOrEmpty(tpl)) keep.Add(tpl);
                }

                var sb = new StringBuilder();
                sb.Append(TemplateCfgHeader());
                foreach (string p in keep)
                    sb.AppendLine(p);

                Directory.CreateDirectory(CamCfgDir);
                File.WriteAllText(CamTemplateCfgFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 尽力把模板 prt 复制到当前 NX 的加工模板目录
        /// （&lt;UGII_BASE_DIR&gt;\MACH\resource\template_part\metric），
        /// 否则 CAMOperationCollection.CreateWithUserName 按模板名找不到模板部件。
        /// 没有写权限就静默跳过（安装程序安装时已经复制过一份）。
        /// </summary>
        /// <summary>配置窗口新增模板时也会调用：开放给 MachAssistantForm 使用。</summary>
        internal static void TryRegisterTemplateToNx(string tplPath)
        {
            try
            {
                string nxRoot = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
                if (string.IsNullOrEmpty(nxRoot)) return;
                string dir = Path.Combine(nxRoot, @"MACH\resource\template_part\metric");
                if (!Directory.Exists(dir)) return;
                string dest = Path.Combine(dir, Path.GetFileName(tplPath));
                if (File.Exists(dest))
                {
                    var a = new FileInfo(dest);
                    var b = new FileInfo(tplPath);
                    if (a.Length == b.Length && a.LastWriteTimeUtc >= b.LastWriteTimeUtc)
                        return;      // 已经是同一份
                }
                File.Copy(tplPath, dest, true);
            }
            catch { }
        }

        private static void SetStatus(string message)
        {
            try { UFSession.GetUFSession().Ui.SetStatus(message); } catch { }
        }

        /// <summary>同步模板工序清单（cam_strategies.txt）。功能区不再有对应按钮，
        /// 保留此动作只为兼容旧菜单文件；正常同步由启动时的 AutoRefreshCamStrategies 完成。</summary>
        private static MenuBarManager.CallbackStatus CamRefreshCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                int n = RegenerateCamStrategies(out string err);
                if (n < 0)
                {
                    theUI.NXMessageBox.Show("LYJ - 刷新失败",
                        NXMessageBox.DialogType.Error, err);
                }
                else
                {
                    theUI.NXMessageBox.Show("LYJ - 同步模板",
                        NXMessageBox.DialogType.Information,
                        "已从模板读出 " + n + " 个工序。\r\n\r\n模板: " + GetTemplatePath() +
                        "\r\n\r\n加工助手「模板」页按这份清单显示，双击工序即可插入。" +
                        (lastIconMade > 0 ? "\r\n\r\n本次自动补画了 " + lastIconMade + " 个图标。" : ""));
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 补齐功能区固定按钮图标（刷新/导入/导出/加工助手等）。
        /// 独立入口的理由：以前 EnsureToolIcons 只挂在"模板未变"的自动同步分支里，
        /// 一旦 cam_strategies.stamp 缺失或启动期模板重生成失败，固定图标永远补不上，
        /// 功能区按钮会因缺位图而不显示（2026-11-03 批量修改按钮踩坑）。        /// </summary>
        private static void EnsureFixedToolIcons()
        {
            try
            {
                Directory.CreateDirectory(CamAppDir);
                var map = LoadIconMap(CamIconMapFile);
                EnsureToolIcons(map);
                SaveIconMap(map);
            }
            catch { }

            // 「显示」组的图标按配置现画（48×48 纯色块 / 棋盘格 / 调色板），
            // 每次启动都重画：用户改完颜色配置重启 NX 就换成新颜色。
            try
            {
                // 首次运行把默认配置落盘，用户才能用记事本看到/改它
                if (!File.Exists(ColorSlots.CfgFile))
                    DisplayGroupCfg.Load().Save();

                ColorSlots.WriteAllIcons(DisplayGroupCfg.Load());

                // 清掉上一版写死的五个颜色图标（现在按槽位编号命名）
                foreach (string stale in new[]
                    { "lyj_color_blue", "lyj_color_green", "lyj_color_gray",
                      "lyj_color_white", "lyj_color_black" })
                {
                    string p = Path.Combine(CamAppDir, stale + ".bmp");
                    if (File.Exists(p)) File.Delete(p);
                }

                // 无指纹的旧槽图标（lyj_color_01.bmp…）不在这里删：
                // 本次启动 NX 读的可能是上一次写的 .grb（引用的就是这些名字），
                // 删早了会让按钮变空白。它们会在下次保存配置时由 WriteAllIcons(cleanup:true) 清掉。
            }
            catch { }
        }

        /// <summary>
        /// “加工助手” 按钮：开关悬浮窗（显示/修改当前选中工序的刀具、转速、进给）。
        /// </summary>
        private static MenuBarManager.CallbackStatus CamAssistantCallback(MenuButtonEvent buttonEvent)
        {
            try
            {
                MachAssistant.Toggle();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            return MenuBarManager.CallbackStatus.Continue;
        }

        /// <summary>
        /// 启动时自动同步：模板 prt 比上次生成新（或文件缺失）时重新生成，状态栏提示重启生效。
        /// </summary>
        private static void AutoRefreshCamStrategies()
        {
            try
            {
                string tpl = GetTemplatePath();
                string stamp = File.Exists(tpl)
                    ? File.GetLastWriteTimeUtc(tpl).Ticks.ToString()
                    : "";
                bool filesReady = File.Exists(CamListFile) && File.Exists(CamStampFile);
                if (filesReady && File.ReadAllText(CamStampFile).Trim() == stamp)
                {
                    // 模板没变：不重建菜单，但仍检查一遍图标——缺的由内置生成器补上，已有的不动
                    try
                    {
                        Directory.CreateDirectory(CamAppDir);
                        var map = LoadIconMap(CamIconMapFile);
                        lastIconMade = 0;
                        EnsureOpIconsFromList(map);   // 按上次生成的工序清单补工序图标（不打开模板）
                        EnsureToolIcons(map);         // 功能区固定按钮
                        SaveIconMap(map);
                    }
                    catch { }
                    return; // 模板未变，无需重建
                }

                int n = RegenerateCamStrategies(out string err);
                if (n >= 0)
                    SetStatus("LYJ: 已从模板生成 " + n + " 个工序按钮, 重启 NX 后生效");
                else
                    SetStatus("LYJ: 模板同步失败(不影响已装好的按钮): " + err);
            }
            catch
            {
                // 启动阶段静默，不影响 NX
            }
        }

        /// <summary>
        /// 按 Startup 时机注册模板策略回调（读取上次生成的清单，与 gen_cam_strategies.men 对应）。
        /// </summary>
        private static void RegisterCamStrategyActions(MenuBarManager mbm)
        {
            try
            {
                if (!File.Exists(CamListFile))
                    return;
                var ops = new List<string>();
                foreach (string line in File.ReadAllLines(CamListFile, Encoding.UTF8))
                {
                    string t = line.Trim();
                    if (t.Length > 0)
                        ops.Add(t);
                }
                for (int i = 0; i < ops.Count; i++)
                {
                    string opName = ops[i];
                    string actionId = CamActionId(i);
                    AddSafeAction(mbm, actionId, delegate (MenuButtonEvent ev)
                    {
                        InsertTemplateOperation(opName);
                        return MenuBarManager.CallbackStatus.Continue;
                    }, opName);
                }
            }
            catch
            {
                // 清单缺失/损坏不影响启动
            }
        }

        /// <summary>
        /// 核心生成器：枚举模板 prt 中的全部工序 → 生成图标、工序清单、
        /// 这两类文件已下线：「加工助手」并入 LYJ 选项卡，工序插入改由悬浮窗模板页完成。
        /// 返回工序数量，失败返回 -1 并带错误信息。
        /// </summary>
        private static int RegenerateCamStrategies(out string errorMessage)
        {
            errorMessage = null;
            try
            {
                EnsureRuntimeFiles();     // 裸安装（只有 DLL+模板）时先补齐目录与配置
                string tplPath = GetTemplatePath();
                if (!File.Exists(tplPath))
                {
                    errorMessage = "找不到加工模板:\r\n" + tplPath +
                        "\r\n\r\n请把加工模板 prt 放到：\r\n" + CamCfgDir +
                        "\r\n或在 " + CamTemplateCfgFile + " 里写上模板的完整路径（一行一个）。";
                    return -1;
                }

                // 模板必须同时存在于当前 NX 的加工模板目录，"插入工序"(CreateWithUserName) 才认。
                // 安装程序会复制；这里是换模板/换电脑后的兜底（没写权限就静默跳过）。
                TryRegisterTemplateToNx(tplPath);

                string tplErr;
                Part tpl = OpenTemplatePart(tplPath, out tplErr);
                if (tpl == null)
                {
                    errorMessage = "模板无法打开:\r\n" + tplPath +
                        (string.IsNullOrEmpty(tplErr) ? "" : "\r\n\r\n" + tplErr);
                    return -1;
                }
                NXOpen.CAM.CAMSetup tplSetup = tpl.CAMSetup;
                if (tplSetup == null)
                {
                    errorMessage = "模板中没有加工设置(CAMSetup):\r\n" + tplPath;
                    return -1;
                }

                // 1. 枚举模板工序——按程序树层级枚举（模板中的文件夹会生成
                //    多级下拉菜单）。树的先序展开顺序天然稳定（跟随模板结构）。
                CamNode treeRoot = BuildTemplateTree(tpl);
                var ops = new List<string>();
                FlattenTreeOps(treeRoot, ops);
                var dedup = new List<string>();
                foreach (string n0 in ops)
                {
                    string n = (n0 ?? "").Trim();
                    if (!string.IsNullOrEmpty(n) && !dedup.Contains(n))
                        dedup.Add(n);
                }
                ops = dedup;
                if (ops.Count == 0)
                {
                    // 兜底：层级枚举失败（懒加载等）时退回三路合并的平面枚举（无子菜单）
                    List<string> enumOps = ListTemplateOperations(tpl);
                    foreach (string n0 in enumOps)
                    {
                        string n = (n0 ?? "").Trim();
                        if (!string.IsNullOrEmpty(n) && !ops.Contains(n))
                            ops.Add(n);
                    }
                }
                if (ops.Count == 0)
                {
                    errorMessage = "模板中没有工序:\r\n" + tplPath;
                    return -1;
                }

                // 2. 图标（全部由内置生成器产出，不依赖外部脚本）：
                //    工序 → 优先取 配置目录\BMP\<工序名>.bmp 素材，无素材则由内置生成器画随机浅色底图标；
                //    已存在且内容未变的图标保留不重画（颜色稳定），只有缺图标/文字变了才补。
                string iconSrcDir = CamIconDir;
                Directory.CreateDirectory(CamAppDir);
                Directory.CreateDirectory(CamStartupDir);
                Directory.CreateDirectory(CamCfgDir);
                var iconMap = LoadIconMap(CamIconMapFile);
                lastIconMade = 0;
                for (int i = 0; i < ops.Count; i++)
                {
                    string iconName = "cam_op_" + (i + 1).ToString("00");
                    string dest = Path.Combine(CamAppDir, iconName + ".bmp");
                    string src = FindIconFile(iconSrcDir, ops[i]);
                    if (src != null)
                    {
                        try { File.Copy(src, dest, true); } catch { }
                        iconMap[iconName] = "src:" + ops[i];
                        continue;
                    }
                    EnsureIcon(iconName, "gen:" + ops[i], SplitIconLines(ops[i]), iconMap);
                }
                PruneCamIcons("cam_op_", 1, ops.Count);

                // 2.1 文件夹图标：下拉按钮也要有图标（同一个内置生成器，缺才补）
                int grpCounter = 0;
                if (treeRoot.Children.Count > 0)
                    AssignFolderIcons(treeRoot, CamAppDir, ref grpCounter, iconMap);
                PruneCamIcons("cam_grp_", 0, grpCounter - 1);

                // 2.2 功能区固定按钮（导入/导出/加工助手）图标同样由内置生成器补缺
                EnsureToolIcons(iconMap);

                SaveIconMap(iconMap);

                // 3. 工序清单（Startup 时按此注册回调）
                File.WriteAllLines(CamListFile, ops, Encoding.UTF8);

                // 4. 菜单脚本与「NX-编程」选项卡均已下线：
                //    「加工助手」并入 LYJ 选项卡（rbn_lyj_cam.grb），
                //    工序插入改由加工助手「模板」页双击完成。
                //    所以不再生成 gen_cam_strategies.men / rbn_nxprogramming.rtb，
                //    旧文件由 EnsureRibbonTabs() 兜底删除，避免 NX 加载出空菜单/空页签。

                // 5. 校正 LYJ 选项卡（并清掉旧版遗留的功能区/菜单文件）
                EnsureRibbonTabs();

                // 7. 时间戳标记（下次启动判断模板是否变化）
                string stamp = File.GetLastWriteTimeUtc(tplPath).Ticks.ToString();
                File.WriteAllText(CamStampFile, stamp);

                // 8. 关闭模板部件，保持会话干净（Parts.Open 会把模板设为工作部件，
                //    不关闭的话启动后工作部件是隐藏的模板，可能干扰用户后续操作）
                try
                {
                    if (tpl != theSession.Parts.Display && !tpl.IsModified)
                        tpl.Close(NXOpen.BasePart.CloseWholeTree.False,
                            NXOpen.BasePart.CloseModified.CloseModified, null);
                }
                catch { }

                return ops.Count;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return -1;
            }
        }

        /// <summary>在图标目录中查找工序对应图标：精确匹配 → 大小写/空白不敏感匹配。找不到返回 null。</summary>
        private static string FindIconFile(string iconDir, string opName)
        {
            try
            {
                string exact = Path.Combine(iconDir, opName + ".bmp");
                if (File.Exists(exact))
                    return exact;
                string want = (opName ?? "").Trim().Replace(" ", "");
                foreach (string f in Directory.GetFiles(iconDir, "*.bmp"))
                {
                    string n = Path.GetFileNameWithoutExtension(f)
                        .Trim().Replace(" ", "");
                    if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch { }
            return null;
        }

        /// <summary>文件夹图标上的文字：去掉“策略/加工策略”等后缀，便于小图标显示。</summary>
        private static string FolderIconText(string folderName)
        {
            string t = (folderName ?? "").Trim();
            while (t.EndsWith("策略", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 2).Trim();
            while (t.EndsWith("加工", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 2).Trim();
            if (t.Length == 0)
                t = (folderName ?? "组").Trim();
            return t;
        }

        /// <summary>为树中每个文件夹生成下拉按钮图标（cam_grp_NN.bmp）。图标缺失或文字变了才重画。</summary>
        private static void AssignFolderIcons(CamNode node, string appDir, ref int counter,
            Dictionary<string, string> iconMap)
        {
            foreach (CamNode c in node.Children)
            {
                if (!c.IsFolder)
                    continue;
                c.IconName = "cam_grp_" + counter.ToString("00");
                counter++;
                try
                {
                    string text = FolderIconText(c.Name);
                    EnsureIcon(c.IconName, "gen:" + text, SplitIconLines(text), iconMap);
                }
                catch { }
                AssignFolderIcons(c, appDir, ref counter, iconMap);
            }
        }

        // 图标随机浅色底：色相按黄金角递增 + 随机扰动，相邻图标不会撞色
        private static readonly Random IconRand = new Random();
        private static double _iconHue = -1.0;

        /// <summary>取一组“随机浅色底 + 同色系深边框”；明度固定在浅色区间，黑字对比足够。</summary>
        private static void NextPastelPair(out System.Drawing.Color bg, out System.Drawing.Color border)
        {
            if (_iconHue < 0)
                _iconHue = IconRand.NextDouble() * 360.0;
            else
                _iconHue += 137.5 + IconRand.NextDouble() * 25.0;
            _iconHue %= 360.0;

            double s = 0.40 + IconRand.NextDouble() * 0.35;   // 饱和度 40%~75%
            double l = 0.86 + IconRand.NextDouble() * 0.07;   // 亮度 86%~93%（浅色系）
            bg = HslToRgb(_iconHue, s, l);
            border = HslToRgb(_iconHue, Math.Min(1.0, s + 0.15), 0.45);
        }

        /// <summary>HSL → RGB（h:0~360, s/l:0~1）。</summary>
        private static System.Drawing.Color HslToRgb(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = l - c / 2.0;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return System.Drawing.Color.FromArgb(
                (int)Math.Round((r + m) * 255),
                (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
        }

        /// <summary>图标文字拆行：≤2 字单行；超过 2 字按 (n+1)/2 拆两行（上行多一字），每行最多 5 字。</summary>
        private static string[] SplitIconLines(string text)
        {
            string t = (text ?? "").Replace("-", "").Replace("_", "").Replace(" ", "");
            if (t.Length == 0)
                t = "OP";
            if (t.Length <= 2)
                return new[] { t };
            int split = (t.Length + 1) / 2;   // 上行多放一个字，视觉更稳
            string l1 = t.Substring(0, split);
            string l2 = t.Substring(split);
            if (l1.Length > 5) l1 = l1.Substring(0, 5);
            if (l2.Length > 5) l2 = l2.Substring(0, 5);
            return new[] { l1, l2 };
        }

        /// <summary>内置图标生成器：随机浅色底 + 同色系深框 + 黑字（System.Drawing 生成，无需外部脚本）。
        /// 字号自动放大到能放进内框的最大值（尽量填满）；lines 为已拆好的 1~2 行文字。</summary>
        private static void MakeFallbackIconLines(string[] lines, string destFile)
        {
            try
            {
                string line1 = (lines != null && lines.Length > 0) ? lines[0] : "OP";
                string line2 = (lines != null && lines.Length > 1) ? lines[1] : null;

                using (var bmp = new System.Drawing.Bitmap(60, 60))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    System.Drawing.Color bgColor, borderColor;
                    NextPastelPair(out bgColor, out borderColor);
                    g.Clear(bgColor);
                    using (var pen = new System.Drawing.Pen(borderColor, 2f))
                        g.DrawRectangle(pen, 1, 1, 57, 57);
                    try
                    {
                        // 尽量填满：从大字往下试，取能放进内框的最大字号（像素单位，非磅）。
                        // 测量须用与绘制一致的 StringFormat（GenericDefault），Typographic 测量偏小会裁字；
                        // 行高按字面高（≈字号像素）计算，不用 MeasureString 的含行距高度（否则双行字号被压小）。
                        const float pad = 3f;
                        float maxW = 60 - pad * 2;
                        float maxH = 60 - pad * 2;
                        var measureSf = (System.Drawing.StringFormat)System.Drawing.StringFormat
                            .GenericDefault.Clone();
                        measureSf.FormatFlags |= System.Drawing.StringFormatFlags.NoWrap;
                        int nLines = line2 == null ? 1 : 2;
                        float px = 52f;
                        for (; px > 8f; px -= 1f)
                        {
                            using (var probe = new System.Drawing.Font("Microsoft YaHei", px,
                                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
                            {
                                var m1 = g.MeasureString(line1, probe, 1000, measureSf);
                                var m2 = line2 == null
                                    ? System.Drawing.SizeF.Empty
                                    : g.MeasureString(line2, probe, 1000, measureSf);
                                float w = Math.Max(m1.Width, m2.Width);
                                float h = px * nLines;
                                if (w <= maxW && h <= maxH)
                                    break;
                            }
                        }

                        var sf = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center,
                            FormatFlags = System.Drawing.StringFormatFlags.NoWrap
                        };
                        using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.Black))
                        using (var font = new System.Drawing.Font("Microsoft YaHei", px,
                            System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
                        {
                            float lineH = px * 1.25f;                    // 绘制矩形略高于字面，避免裁切
                            float top = (60f - px * nLines) / 2f - (lineH - px) / 2f;
                            g.DrawString(line1, font, br,
                                new System.Drawing.RectangleF(0, top, 60, lineH), sf);
                            if (line2 != null)
                                g.DrawString(line2, font, br,
                                    new System.Drawing.RectangleF(0, top + px, 60, lineH), sf);
                        }
                    }
                    catch
                    {
                        // 系统缺该字体时退回默认字体
                        using (var font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif,
                            24f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
                        using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.Black))
                            g.DrawString(line1, font, br,
                                new System.Drawing.RectangleF(0, 0, 60, 60));
                    }
                    bmp.Save(destFile, System.Drawing.Imaging.ImageFormat.Bmp);
                }
            }
            catch { }
        }

        /// <summary>功能区固定按钮的图标定义（第一项为文件名，其后为图标的 1~2 行文字）。
        /// 「显示」组（透明化 / 颜色块 / 配置）不走这里 —— 它们由 ColorSlots.WriteAllIcons 画成 48×48 色块。</summary>
        private static readonly string[][] ToolIconDefs =
        {
            new[] { "lyj_cam_assistant", "加工", "助手" },
            new[] { "lyj_import_auto",  "导入", "图档" },
            new[] { "lyj_export_auto",  "导出", "图档" },
            new[] { "lyj_export_stl",  "导出", "STL" },
            new[] { "lyj_export_stp",  "导出", "STP" },
            new[] { "lyj_export_igs",  "导出", "IGS" },
            new[] { "lyj_export_xt",   "导出", "X_T" },
        };

        /// <summary>读图标记录文件（文件名\t内容标记）。</summary>
        private static Dictionary<string, string> LoadIconMap(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path))
                    return map;
                foreach (string ln in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int p = ln.IndexOf('\t');
                    if (p > 0)
                        map[ln.Substring(0, p).Trim()] = ln.Substring(p + 1).Trim();
                }
            }
            catch { }
            return map;
        }

        /// <summary>保存图标记录（只保留文件仍存在的项）。</summary>
        private static void SaveIconMap(Dictionary<string, string> map)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in map)
                    if (File.Exists(Path.Combine(CamAppDir, kv.Key + ".bmp")))
                        sb.AppendLine(kv.Key + "\t" + kv.Value);
                File.WriteAllText(CamIconMapFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>判断图标是否为指定边长的方图（读文件头，读失败当作不匹配）。</summary>
        private static bool IsIconSquare(string file, int size)
        {
            try
            {
                using (var bmp = new System.Drawing.Bitmap(file))
                    return bmp.Width == size && bmp.Height == size;
            }
            catch { return false; }
        }

        /// <summary>
        /// 图标按需生成（内置生成器）：文件不存在、或记录的内容标记与本次不一致时才画。
        /// 已有且内容一致 → 原样保留（刷新不会把颜色换掉）。
        /// 首次升级（还没有记录文件）时，若已有 60x60 大图、48x48 中号色块或 24x24 小色块则直接采纳，不重画。
        /// </summary>
        private static bool EnsureIcon(string fileName, string mark, string[] lines,
            Dictionary<string, string> iconMap)
        {
            try
            {
                string dest = Path.Combine(CamAppDir, fileName + ".bmp");
                string old;
                bool hasRecord = iconMap.TryGetValue(fileName, out old);
                if (File.Exists(dest))
                {
                    if (hasRecord && old == mark)
                        return false;                       // 内容未变 → 保留
                    if (!hasRecord && (IsIconSquare(dest, 60) || IsIconSquare(dest, 48) || IsIconSquare(dest, 24)))
                    {
                        iconMap[fileName] = mark;           // 旧版/手工图（60 大图或 24 小色块）→ 采纳
                        return false;
                    }
                }
                MakeFallbackIconLines(lines, dest);
                lastIconMade++;
                iconMap[fileName] = mark;
                return true;
            }
            catch { return false; }
        }

        /// <summary>补齐功能区固定按钮图标（导入/导出/加工助手），缺失才画。</summary>
        private static void EnsureToolIcons(Dictionary<string, string> iconMap)
        {
            foreach (string[] def in ToolIconDefs)
            {
                var lines = new string[def.Length - 1];
                Array.Copy(def, 1, lines, 0, lines.Length);
                EnsureIcon(def[0], "gen:" + string.Join("/", lines), lines, iconMap);
            }
        }

        /// <summary>按 cam_strategies.txt 的工序清单补 cam_op 图标（启动时用，不需要打开模板）。</summary>
        private static void EnsureOpIconsFromList(Dictionary<string, string> iconMap)
        {
            try
            {
                if (!File.Exists(CamListFile))
                    return;
                string[] lines = File.ReadAllLines(CamListFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string op = (lines[i] ?? "").Trim();
                    if (op.Length == 0)
                        continue;
                    string iconName = "cam_op_" + (i + 1).ToString("00");
                    string dest = Path.Combine(CamAppDir, iconName + ".bmp");
                    string src = FindIconFile(CamIconDir, op);
                    if (src != null)
                    {
                        if (!File.Exists(dest))
                        {
                            try { File.Copy(src, dest, true); } catch { }
                        }
                        iconMap[iconName] = "src:" + op;
                        continue;
                    }
                    EnsureIcon(iconName, "gen:" + op, SplitIconLines(op), iconMap);
                }
            }
            catch { }
        }

        /// <summary>删除编号不在 [min,max] 范围内的 cam_XXX_NN.bmp（模板删了工序/文件夹后的残留图标）。</summary>
        private static void PruneCamIcons(string prefix, int min, int max)
        {
            try
            {
                foreach (string f in Directory.GetFiles(CamAppDir, prefix + "*.bmp"))
                {
                    string baseName = Path.GetFileNameWithoutExtension(f);
                    if (baseName.Length != prefix.Length + 2)
                        continue;
                    int idx;
                    if (!int.TryParse(baseName.Substring(prefix.Length), out idx))
                        continue;
                    if (idx < min || idx > max)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 打开模板部件（隐藏加载，不切换显示部件；已打开则复用）。
        /// 失败时通过 error 返回 NX 的真实异常信息，便于诊断。
        /// </summary>
        private static Part OpenTemplatePart(string path, out string error)
        {
            error = null;
            try
            {
                // 1. 同路径已加载 → 直接复用
                foreach (BasePart p in theSession.Parts)
                {
                    if (string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureFullyLoaded(p);
                        return p as Part;
                    }
                }

                // 2. 同名不同路径的部件已在会话中（例如模板被移动过），
                //    NX 按部件名判重会导致 Open 失败 → 先关闭旧的（非显示部件且未修改才关）
                foreach (BasePart p in theSession.Parts)
                {
                    if (string.Equals(Path.GetFileName(p.FullPath),
                            Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (p != theSession.Parts.Display &&
                                !p.IsModified &&
                                !string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Close(NXOpen.BasePart.CloseWholeTree.False,
                                    NXOpen.BasePart.CloseModified.CloseModified, null);
                            }
                        }
                        catch { }
                    }
                }

                // 3. 打开部件
                PartLoadStatus st;
                BasePart bp = theSession.Parts.Open(path, out st);
                if (st != null)
                {
                    // 记录加载状态中的错误，便于诊断
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < st.NumberUnloadedParts; i++)
                        sb.Append(st.GetPartName(i) + " → " + st.GetStatusDescription(i) + "; ");
                    st.Dispose();
                    if (bp == null && sb.Length > 0)
                        error = "部件加载失败: " + sb;
                }
                // 4. 强制完全加载——CAM 工序数据是懒加载的，部分加载状态下
                //    CAMOperationCollection 会漏掉部分工序（实测 17 个只出 16 个）
                if (bp != null)
                    EnsureFullyLoaded(bp);
                return bp as Part;
            }
            catch (NXException nex)
            {
                error = "NX错误: " + nex.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>确保部件完全加载（CAM 等模块数据懒加载，部分加载会漏工序）。</summary>
        private static void EnsureFullyLoaded(BasePart p)
        {
            try
            {
                if (p != null && !p.IsFullyLoaded)
                {
                    PartLoadStatus st2 = p.LoadFully();
                    if (st2 != null) st2.Dispose();
                }
            }
            catch { }
        }

        /// <summary>递归遍历程序顺序树，收集全部工序（比 CAMOperationCollection 可靠，
        /// 集合枚举在部分加载状态下会漏工序）。</summary>
        private static void CollectOperations(NXOpen.CAM.NCGroup node, List<NXOpen.CAM.Operation> result)
        {
            if (node == null) return;
            NXOpen.CAM.CAMObject[] members = node.GetMembers();
            if (members == null) return;
            foreach (NXOpen.CAM.CAMObject m in members)
            {
                if (m is NXOpen.CAM.Operation op)
                    result.Add(op);
                else if (m is NXOpen.CAM.NCGroup g)
                    CollectOperations(g, result);
            }
        }

        /// <summary>模板程序树节点：文件夹（NCGroup）或工序（Operation）。</summary>
        private class CamNode
        {
            public string Name;
            public bool IsFolder;
            public string MenuId;                       // 文件夹对应的子菜单 ID
            public string IconName;                     // 文件夹下拉按钮的图标名（cam_grp_NN）
            public List<CamNode> Children = new List<CamNode>();
        }

        /// <summary>
        /// 构建模板的程序顺序树（保留文件夹层级）。
        /// 文件夹递归展开，空文件夹和“未用项”系统组不进树；失败返回空根。
        /// </summary>
        private static CamNode BuildTemplateTree(Part tpl)
        {
            var root = new CamNode { Name = "", IsFolder = true };
            try
            {
                FillTreeNode(tpl.CAMSetup.GetRoot(NXOpen.CAM.CAMSetup.View.ProgramOrder), root);
            }
            catch { }
            return root;
        }

        /// <summary>递归填充程序树节点。</summary>
        private static void FillTreeNode(NXOpen.CAM.NCGroup node, CamNode parent)
        {
            if (node == null) return;
            NXOpen.CAM.CAMObject[] members = null;
            try { members = node.GetMembers(); }
            catch { return; }
            if (members == null) return;
            foreach (NXOpen.CAM.CAMObject m in members)
            {
                if (m is NXOpen.CAM.Operation op)
                {
                    string n = (op.Name ?? "").Trim();
                    if (n.Length > 0)
                        parent.Children.Add(new CamNode { Name = n, IsFolder = false });
                }
                else if (m is NXOpen.CAM.NCGroup g)
                {
                    string gn = (g.Name ?? "").Trim();
                    // 跳过“未用项”系统组
                    if (gn.Length == 0 ||
                        gn.IndexOf("未用", StringComparison.Ordinal) >= 0 ||
                        gn.IndexOf("UNUSED", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    var sub = new CamNode { Name = gn, IsFolder = true };
                    FillTreeNode(g, sub);
                    if (sub.Children.Count > 0)     // 空文件夹不生成子菜单
                        parent.Children.Add(sub);
                }
            }
        }

        /// <summary>树的先序展开：按菜单显示顺序收集全部工序名（叶子）。</summary>
        private static void FlattenTreeOps(CamNode node, List<string> result)
        {
            foreach (CamNode c in node.Children)
            {
                if (c.IsFolder)
                    FlattenTreeOps(c, result);
                else
                    result.Add(c.Name);
            }
        }

        /// <summary>为树中文件夹节点分配全局唯一的子菜单 ID（先序遍历，父子一致）。</summary>
        private static void AssignSubMenuIds(CamNode node, string prefix, ref int counter)
        {
            foreach (CamNode c in node.Children)
            {
                if (!c.IsFolder) continue;
                c.MenuId = prefix + "_SUB" + counter.ToString("00");
                counter++;
                AssignSubMenuIds(c, c.MenuId, ref counter);
            }
        }

        /// <summary>
        /// 把模板程序树写成 MenuScript：文件夹 → CASCADE_BUTTON 子菜单（可多级嵌套），
        /// 工序 → BUTTON（ID/图标按平面清单索引对应）。
        /// </summary>
        private static void EmitMenuScript(StringBuilder men, CamNode node, string menuId,
            Dictionary<string, int> opIndex)
        {
            men.AppendLine("MENU " + menuId);
            men.AppendLine();
            foreach (CamNode c in node.Children)
            {
                if (c.IsFolder)
                {
                    men.AppendLine("    CASCADE_BUTTON " + c.MenuId);
                    men.AppendLine("    LABEL " + c.Name);
                    men.AppendLine();
                }
                else
                {
                    int idx;
                    if (!opIndex.TryGetValue(c.Name, out idx)) continue;
                    string id = CamActionId(idx);
                    men.AppendLine("    BUTTON " + id);
                    men.AppendLine("    LABEL " + c.Name);
                    men.AppendLine("    BITMAP cam_op_" + (idx + 1).ToString("00"));
                    men.AppendLine("    ACTIONS " + id);
                    men.AppendLine();
                }
            }
            men.AppendLine("END_OF_MENU");
            men.AppendLine();
            foreach (CamNode c in node.Children)
            {
                if (c.IsFolder)
                    EmitMenuScript(men, c, c.MenuId, opIndex);
            }
        }


        /// <summary>
        /// UF 按对象类型全量枚举部件内的 CAM 工序。
        /// 做法：先从任一已知工序上取对象 type 值，再用 CycleTypedObjsInPart 遍历。
        /// 用于兜住懒加载状态下程序树遍历/集合枚举漏工序的问题。
        /// </summary>
        private static List<NXOpen.CAM.Operation> CycleAllOperations(Part tpl)
        {
            var result = new List<NXOpen.CAM.Operation>();
            try
            {
                UFSession uf = UFSession.GetUFSession();

                // 1) 找一个样本工序，确定 CAM 工序的对象 type 值
                NXOpen.CAM.Operation sample = null;
                try
                {
                    foreach (NXOpen.CAM.Operation o in tpl.CAMSetup.CAMOperationCollection)
                    {
                        sample = o; break;
                    }
                }
                catch { }
                if (sample == null)
                {
                    var tree = new List<NXOpen.CAM.Operation>();
                    try
                    {
                        CollectOperations(tpl.CAMSetup.GetRoot(NXOpen.CAM.CAMSetup.View.ProgramOrder), tree);
                    }
                    catch { }
                    if (tree.Count > 0) sample = tree[0];
                }
                if (sample == null) return result;

                int operType, subtype;
                uf.Obj.AskTypeAndSubtype(sample.Tag, out operType, out subtype);

                // 2) 按该 type 枚举部件内全部对象（visited 防御性防死循环）
                var visited = new HashSet<Tag>();
                Tag cur = Tag.Null;
                while (true)
                {
                    uf.Obj.CycleTypedObjsInPart(tpl.Tag, operType, ref cur);
                    if (cur == Tag.Null || !visited.Add(cur)) break;
                    var op = NXOpen.Utilities.NXObjectManager.Get(cur) as NXOpen.CAM.Operation;
                    if (op != null) result.Add(op);
                }
            }
            catch { }
            return result;
        }


        /// <summary>枚举模板全部工序名：程序树 + 集合 + UF 按类型全量枚举，三路合并去重（诊断与生成器共用）。</summary>
        private static List<string> ListTemplateOperations(Part tpl)
        {
            var names = new List<string>();
            Action<string> addName = delegate (string n)
            {
                n = (n ?? "").Trim();
                if (n.Length > 0 && !names.Contains(n)) names.Add(n);
            };
            try
            {
                var ops = new List<NXOpen.CAM.Operation>();
                CollectOperations(tpl.CAMSetup.GetRoot(NXOpen.CAM.CAMSetup.View.ProgramOrder), ops);
                foreach (NXOpen.CAM.Operation op in ops)
                    addName(op.Name);
            }
            catch { }
            try
            {
                foreach (NXOpen.CAM.Operation op in tpl.CAMSetup.CAMOperationCollection)
                    addName(op.Name);
            }
            catch { }
            foreach (NXOpen.CAM.Operation op in CycleAllOperations(tpl))
                addName(op.Name);
            return names;
        }

        /// <summary>
        /// 仿官方"创建工序"（插入→工序）流程：在当前部件的加工上下文中，
        /// 通过 CAMOperationCollection.CreateWithUserName 直接从已安装的
        /// 加工模板（模板名 = 模板 prt 文件名）实例化工序。模板 prt 无需打开、无窗口切换、
        /// 无跨部件复制，工序立即出现在加工工序导航器。
        /// </summary>
        private static void InsertTemplateOperation(string opName)
        {
            InsertTemplateOperation(opName, null);
        }

        /// <summary>同上，可指定模板（templateType = 模板 prt 文件名去扩展名；null/空 = 第一个模板）。</summary>
        /// <summary>解析插入工序用的四个父组（程序/方法/刀具/几何）。
        /// 与官方"创建工序"对话框一致：先按默认名找组；用户改过组名时从对应视图根组
        /// 出发按对象类型兜底（根组不可改名，MCS 组是 OrientGeometry 类型）；最后优先用
        /// 工序导航器里当前选中的组。geomRetry：选中的几何组不是 MCS 时的向上 MCS 回退。
        /// 返回 false 表示部件缺少必要的加工组。</summary>
        private static bool ResolveInsertGroups(Part workPart,
            out NXOpen.CAM.NCGroup prog, out NXOpen.CAM.NCGroup method,
            out NXOpen.CAM.NCGroup tool, out NXOpen.CAM.NCGroup geom,
            out NXOpen.CAM.NCGroup geomRetry, out string diag)
        {
            prog = method = tool = geom = geomRetry = null;
            diag = "";
            NXOpen.CAM.CAMSetup cam = workPart.CAMSetup;
            NXOpen.CAM.NCGroupCollection groups = cam.CAMGroupCollection;

            prog = FindCamGroup(groups, "NC_PROGRAM")
                ?? CamRootGroup(cam, NXOpen.CAM.CAMSetup.View.ProgramOrder);
            method = FindCamGroup(groups, "METHOD")
                ?? CamRootGroup(cam, NXOpen.CAM.CAMSetup.View.MachineMethod);
            tool = FindCamGroup(groups, "NONE")
                ?? FindNcGroupDfs(
                    CamRootGroup(cam, NXOpen.CAM.CAMSetup.View.MachineTool),
                    delegate (NXOpen.CAM.NCGroup g)
                    {
                        return string.Equals((g.Name ?? "").Trim(), "NONE",
                            StringComparison.OrdinalIgnoreCase);
                    })
                ?? CamRootGroup(cam, NXOpen.CAM.CAMSetup.View.MachineTool);
            geom = FindCamGroup(groups, "MCS_LOCAL", "MCS_MILL", "MCS")
                ?? FindNcGroupDfs(
                    CamRootGroup(cam, NXOpen.CAM.CAMSetup.View.Geometry),
                    delegate (NXOpen.CAM.NCGroup g)
                    { return g is NXOpen.CAM.OrientGeometry; });

            // 优先用"工序导航器里选中的组"：
            //     选中程序组  → 工序插到该程序组里；
            //     选中几何组  → 工序挂到该几何体/MCS 下；
            //     没选中（或选的是工序/其它对象）→ 保持上面的默认。
            NXOpen.CAM.NCGroup selProg, selGeom, selMethod, selTool;
            AskSelectedCamGroups(cam, out selProg, out selGeom, out selMethod, out selTool, out diag);
            if (selProg != null) prog = selProg;
            if (selGeom != null) geom = selGeom;
            if (selMethod != null) method = selMethod;
            if (selTool != null) tool = selTool;

            if (prog == null || method == null || tool == null || geom == null)
                return false;

            // 选中的几何组若不是 MCS（例如直接选了"几何体"/WORKPIECE 组），
            // 先用它试；NX 不接受时退到它上面最近的 MCS 再试一次，尽量不失败。
            if (!(geom is NXOpen.CAM.OrientGeometry))
                geomRetry = FindMcsAncestor(geom);
            return true;
        }

        private static void InsertTemplateOperation(string opName, string templateType)
        {
            string tplName = string.IsNullOrEmpty(templateType)
                ? CamTemplateTypeName
                : templateType.Trim();
            try
            {
                Part workPart = theSession.Parts.Work ?? theSession.Parts.Display;
                if (workPart == null || workPart.CAMSetup == null)
                {
                    theUI.NXMessageBox.Show("LYJ - 提示",
                        NXMessageBox.DialogType.Warning,
                        "当前部件没有加工设置(CAMSetup)。\r\n" +
                        "请先打开部件并进入加工环境（启动 → 加工），再插入工序。");
                    return;
                }

                NXOpen.CAM.NCGroup prog, method, tool, geom, geomRetry; string selDiag;
                if (!ResolveInsertGroups(workPart,
                        out prog, out method, out tool, out geom, out geomRetry, out selDiag))
                {
                    theUI.NXMessageBox.Show("LYJ - 提示",
                        NXMessageBox.DialogType.Warning,
                        "当前部件缺少加工组（程序/方法/刀具/几何）。\r\n" +
                        "请先进入加工环境（启动 → 加工）并初始化几何组后再插入工序。");
                    return;
                }

                // 2. 从已安装的加工模板直接实例化工序（官方机制，无需打开模板部件）
                Session.UndoMarkId mark = theSession.SetUndoMark(
                    Session.MarkVisibility.Visible, "LYJ 创建工序");
                NXOpen.CAM.Operation op = null;
                NXException createError = null;
                try
                {
                    op = CreateTemplateOperation(workPart, prog, method, tool, geom, opName, tplName);
                }
                catch (NXException ne)
                {
                    createError = ne;
                    if (geomRetry != null && geomRetry.Tag != geom.Tag)
                    {
                        try
                        {
                            op = CreateTemplateOperation(workPart, prog, method, tool, geomRetry, opName, tplName);
                            geom = geomRetry;      // 实际落在了 MCS 上
                            createError = null;
                        }
                        catch (NXException ne2) { createError = ne2; }
                    }
                }
                if (createError != null)
                {
                    theUI.NXMessageBox.Show("LYJ - 错误",
                        NXMessageBox.DialogType.Error,
                        "从加工模板\u201c" + tplName + "\u201d创建工序\u201c" + opName +
                        "\u201d失败。\r\n\r\n" + createError.Message +
                        "\r\n\r\n请确认：\r\n" +
                        "  1. 模板 prt 已安装到 NX 加工模板目录，模板名为 " + tplName + "；\r\n" +
                        "  2. 模板中存在名为\u201c" + opName + "\u201d的工序；\r\n" +
                        "  3. 模板路径可在 加工助手 → 配置 → 模板配置 里新增/修改。");
                    return;
                }

                LogInsertParents(selDiag, prog, method, tool, geom, opName, tplName);
                SetStatus("LYJ: 已插入工序 " + opName +
                    "  程序组: " + CamGroupLabel(prog) + "  几何组: " + CamGroupLabel(geom) +
                    (op != null ? "" : "（警告: 返回为空）"));
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        /// <summary>批量插入模板工序（加工助手模板树多选双击）。
        /// 关键点：目标组整批只解析一次 —— 每插入一个工序 NX 的选择可能变化，
        /// 逐条重取会插错位置；整批共用一个撤销标记，一次 Ctrl+Z 全部撤销。
        /// 不弹窗，结果文字由调用方显示。返回给助手的提示文字。</summary>
        /// <param name="ops">要插入的工序列表（工序名 → 模板类型；模板类型空则用默认模板）。</param>
        internal static string InsertOpsBatch(IList<KeyValuePair<string, string>> ops)
        {
            if (ops == null || ops.Count == 0) return "没有要插入的工序";
            try
            {
                Part workPart = theSession.Parts.Work ?? theSession.Parts.Display;
                if (workPart == null || workPart.CAMSetup == null)
                    return "当前部件没有加工设置(CAMSetup)，请先进入加工环境（启动 → 加工）。";

                NXOpen.CAM.NCGroup prog, method, tool, geom, geomRetry; string selDiag;
                if (!ResolveInsertGroups(workPart,
                        out prog, out method, out tool, out geom, out geomRetry, out selDiag))
                    return "当前部件缺少加工组（程序/方法/刀具/几何），请先初始化几何组。";

                int ok = 0;
                var fails = new List<string>();
                Session.UndoMarkId mark = theSession.SetUndoMark(
                    Session.MarkVisibility.Visible, "加工助手 批量插入工序");
                foreach (KeyValuePair<string, string> kv in ops)
                {
                    string opName = (kv.Key ?? "").Trim();
                    if (opName.Length == 0) continue;
                    string tplName = string.IsNullOrEmpty(kv.Value)
                        ? CamTemplateTypeName
                        : kv.Value.Trim();
                    try
                    {
                        CreateTemplateOperation(workPart, prog, method, tool, geom, opName, tplName);
                        ok++;
                    }
                    catch (NXException ne)
                    {
                        // 与单条插入一致的兜底：几何组不是 MCS 时退到最近的 MCS 再试一次
                        bool done = false;
                        if (geomRetry != null && geomRetry.Tag != geom.Tag)
                        {
                            try
                            {
                                CreateTemplateOperation(workPart, prog, method, tool,
                                    geomRetry, opName, tplName);
                                ok++;
                                done = true;
                            }
                            catch { }
                        }
                        if (!done) fails.Add(opName + "：" + ne.Message);
                    }
                }
                if (ok == 0 && fails.Count > 0)
                {
                    try { theSession.UndoToMark(mark, null); } catch { }
                    return "批量插入失败，已回滚：" + string.Join("；", fails.ToArray());
                }

                LogInsertParents(selDiag, prog, method, tool, geom,
                    ok + "/" + ops.Count + " 个工序(批量)",
                    "批量(" + ops.Count + "项)");
                string msg = "已插入 " + ok + "/" + ops.Count + " 个工序 → " + CamGroupLabel(prog);
                if (fails.Count > 0) msg += "；失败: " + string.Join("；", fails.ToArray());
                return msg;
            }
            catch (Exception ex)
            {
                return "批量插入失败：" + ex.Message;
            }
        }

        /// <summary>在加工组集合中按候选名查找组，全部找不到返回 null。</summary>
        private static NXOpen.CAM.NCGroup FindCamGroup(
            NXOpen.CAM.NCGroupCollection col, params string[] names)
        {
            foreach (string n in names)
            {
                try
                {
                    var g = col.FindObject(n) as NXOpen.CAM.NCGroup;
                    if (g != null)
                        return g;
                }
                catch { }
            }
            return null;
        }

        /// <summary>取 CAM 视图根组（程序/方法/刀具/几何四个根）。根组不可被用户改名，
        /// 所以用户把 MCS、工序组等改了名，从这里出发仍然能遍历到。</summary>
        private static NXOpen.CAM.NCGroup CamRootGroup(NXOpen.CAM.CAMSetup setup,
            NXOpen.CAM.CAMSetup.View view)
        {
            try { return setup.GetRoot(view) as NXOpen.CAM.NCGroup; }
            catch { return null; }
        }

        /// <summary>从根组出发深度优先找第一个满足条件的组（含根自身），visited 防环。
        /// 典型用法：geom = FindNcGroupDfs(几何根, g => g is OrientGeometry)，
        /// 即"类型是 MCS 的组"——用户把 MCS 改名成"111"也照样找得到。</summary>
        private static NXOpen.CAM.NCGroup FindNcGroupDfs(NXOpen.CAM.NCGroup root,
            Func<NXOpen.CAM.NCGroup, bool> match)
        {
            if (root == null) return null;
            var visited = new HashSet<Tag>();
            var stack = new Stack<NXOpen.CAM.NCGroup>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (g == null || !visited.Add(g.Tag)) continue;
                try { if (match(g)) return g; } catch { }
                try
                {
                    foreach (NXOpen.CAM.CAMObject m in g.GetMembers())
                        stack.Push(m as NXOpen.CAM.NCGroup);
                }
                catch { }
            }
            return null;
        }

        /// <summary>读出当前选择里的加工组，按所属视图分类（程序/几何/方法/刀具）。
        /// 用户在工序导航器里选中哪个组，就返回哪个组 —— 后选的覆盖先选的。
        /// 判断组的视图归属用"回溯到视图根组"，根组不可改名，所以随便改名都认得。</summary>
        private static void AskSelectedCamGroups(NXOpen.CAM.CAMSetup cam,
            out NXOpen.CAM.NCGroup prog, out NXOpen.CAM.NCGroup geom,
            out NXOpen.CAM.NCGroup method, out NXOpen.CAM.NCGroup tool,
            out string diag)
        {
            prog = geom = method = tool = null;
            NXOpen.CAM.NCGroup opProg = null, opGeom = null, opMethod = null, opTool = null;
            var sb = new StringBuilder();
            NXOpen.NXObject[] sel = null;
            try
            {
                // NXOpen.Selection 没有"一次性取全部"的接口，只能按索引逐个取
                int n = theUI.SelectionManager.GetNumSelectedObjects();
                if (n > 0)
                {
                    var list = new List<NXOpen.NXObject>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var o = theUI.SelectionManager.GetSelectedObject(i);
                        if (o != null) list.Add(o);
                    }
                    sel = list.ToArray();
                }
            }
            catch (Exception ex) { diag = "读取选择失败: " + ex.Message; return; }
            if (sel == null || sel.Length == 0)
            {
                diag = "当前没有选中对象";
                return;
            }
            foreach (NXOpen.NXObject o in sel)
            {
                // 选中"工序"时，用该工序所在的那几个组作为父组
                //（常见操作：选着上一个工序接着加下一个）
                var op = o as NXOpen.CAM.Operation;
                if (op != null)
                {
                    try
                    {
                        if (opProg == null) opProg = op.ParentProgramOrder;
                        if (opGeom == null) opGeom = op.ParentGeometry;
                        if (opMethod == null) opMethod = op.ParentMachineMethod;
                        if (opTool == null) opTool = op.ParentMachineTool;
                        sb.AppendLine("选中工序: " + CamGroupLabel(op.ParentGeometry) + " 下");
                    }
                    catch { }
                    continue;
                }
                var g = o as NXOpen.CAM.NCGroup;
                if (g == null)
                {
                    try { sb.AppendLine("忽略(非组): " + o.GetType().Name); } catch { }
                    continue;
                }
                NXOpen.CAM.CAMSetup.View view;
                if (!TryGetCamView(cam, g, out view))
                {
                    sb.AppendLine("忽略(不属于当前部件): " + CamGroupLabel(g));
                    continue;
                }
                sb.AppendLine("选中组: " + CamGroupLabel(g) + "  视图: " + view);
                if (view == NXOpen.CAM.CAMSetup.View.ProgramOrder) prog = g;
                else if (view == NXOpen.CAM.CAMSetup.View.Geometry) geom = g;
                else if (view == NXOpen.CAM.CAMSetup.View.MachineMethod) method = g;
                else if (view == NXOpen.CAM.CAMSetup.View.MachineTool) tool = g;
            }
            // 显式选中的组优先；只是选了工序时，用它所在组的父组兜底
            if (prog == null) prog = opProg;
            if (geom == null) geom = opGeom;
            if (method == null) method = opMethod;
            if (tool == null) tool = opTool;
            diag = sb.ToString();
        }

        /// <summary>判断加工组属于哪个视图：沿父组链回溯，看能否走到该视图的根组。</summary>
        private static bool TryGetCamView(NXOpen.CAM.CAMSetup cam, NXOpen.CAM.NCGroup g,
            out NXOpen.CAM.CAMSetup.View view)
        {
            NXOpen.CAM.CAMSetup.View[] all = new NXOpen.CAM.CAMSetup.View[]
            {
                NXOpen.CAM.CAMSetup.View.ProgramOrder,
                NXOpen.CAM.CAMSetup.View.Geometry,
                NXOpen.CAM.CAMSetup.View.MachineMethod,
                NXOpen.CAM.CAMSetup.View.MachineTool
            };
            foreach (NXOpen.CAM.CAMSetup.View v in all)
            {
                if (IsUnderCamRoot(g, CamRootGroup(cam, v))) { view = v; return true; }
            }
            view = NXOpen.CAM.CAMSetup.View.Geometry;
            return false;
        }

        /// <summary>组的父链上是否包含 root（含自身）。visited 防环。</summary>
        private static bool IsUnderCamRoot(NXOpen.CAM.NCGroup group, NXOpen.CAM.NCGroup root)
        {
            if (group == null || root == null) return false;
            var visited = new HashSet<Tag>();
            NXOpen.CAM.NCGroup cur = group;
            while (cur != null && visited.Add(cur.Tag))
            {
                if (cur.Tag == root.Tag) return true;
                try { cur = cur.GetParent() as NXOpen.CAM.NCGroup; }
                catch { break; }
            }
            return false;
        }

        /// <summary>沿父组往上找最近的 MCS 组（OrientGeometry 类型），找不到返回 null。</summary>
        private static NXOpen.CAM.NCGroup FindMcsAncestor(NXOpen.CAM.NCGroup g)
        {
            if (g == null) return null;
            var visited = new HashSet<Tag>();
            NXOpen.CAM.NCGroup cur = g;
            while (cur != null && visited.Add(cur.Tag))
            {
                if (cur is NXOpen.CAM.OrientGeometry) return cur;
                try { cur = cur.GetParent() as NXOpen.CAM.NCGroup; }
                catch { break; }
            }
            return null;
        }

        /// <summary>按模板实例化工序（官方 CreateWithUserName 机制）。templateType = 模板名（prt 文件名去扩展名）。</summary>
        private static NXOpen.CAM.Operation CreateTemplateOperation(Part workPart,
            NXOpen.CAM.NCGroup prog, NXOpen.CAM.NCGroup method,
            NXOpen.CAM.NCGroup tool, NXOpen.CAM.NCGroup geom, string opName, string templateType)
        {
            return workPart.CAMSetup.CAMOperationCollection.CreateWithUserName(
                prog, method, tool, geom,
                string.IsNullOrEmpty(templateType) ? CamTemplateTypeName : templateType,
                opName,                // 模板中的工序名
                NXOpen.CAM.OperationCollection.UseDefaultName.True,
                opName, opName);
        }

        /// <summary>组的显示文字：名字 + 类型（MCS 组会标出来），用于状态栏和日志。</summary>
        private static string CamGroupLabel(NXOpen.CAM.NCGroup g)
        {
            if (g == null) return "(空)";
            try
            {
                string n = (g.Name ?? "").Trim();
                if (n.Length == 0) n = "(未命名)";
                if (g is NXOpen.CAM.OrientGeometry) n += "[MCS]";
                return n;
            }
            catch { return "(未知)"; }
        }

        /// <summary>把本次插入用到的父组写进 config\insert_last.log（每次覆盖），
        /// 插入位置不对时可以直接看这个文件定位。</summary>
        private static void LogInsertParents(string selDiag, NXOpen.CAM.NCGroup prog,
            NXOpen.CAM.NCGroup method, NXOpen.CAM.NCGroup tool, NXOpen.CAM.NCGroup geom,
            string opName, string templateType)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("工序: " + opName);
                sb.AppendLine("选中: " + ((selDiag ?? "").Trim().Length > 0
                    ? selDiag.Replace("\r\n", " | ").TrimEnd(' ', '|') : "(无)"));
                sb.AppendLine("程序组: " + CamGroupLabel(prog));
                sb.AppendLine("方法组: " + CamGroupLabel(method));
                sb.AppendLine("刀具组: " + CamGroupLabel(tool));
                sb.AppendLine("几何组: " + CamGroupLabel(geom));
                sb.AppendLine("模板: " + (string.IsNullOrEmpty(templateType) ? CamTemplateTypeName : templateType));
                string file = Path.Combine(CamCfgDir, "insert_last.log");
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }


        #endregion

        /// <summary>
        /// 控制程序集卸载时机：会话结束时卸载。
        /// 注意：开发调试时 NX 会锁定 DLL，重新编译前需先关闭 NX。
        /// </summary>
        public static int GetUnloadOption(string arg)
        {
            return (int)Session.LibraryUnloadOption.AtTermination;
        }
    }
}
