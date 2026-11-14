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
using NXOpen;
using NXOpen.BlockStyler;
using SelectObjectBlock = NXOpen.BlockStyler.SelectObject;   // 与 NXOpen.SelectObject 重名，取别名
using LabelBlock = NXOpen.BlockStyler.Label;

namespace LYJ
{
    /// <summary>
    /// 部件显示相关的三组功能：
    ///
    /// 1) 「透明化」—— 点按钮弹出 NX 原生对话框（Block Styler）：
    ///    上面选部件，下面是透明度滑条，拖动即时预览。
    ///    对话框定义在 application\LYJ_Transparency.dlx（由 工具脚本\gen_transparency_dialog.py 生成）。
    ///
    /// 2) 「部件颜色」—— 点颜色按钮先用 **NX 原生类选择对话框**选对象（图形区已选就直接用），
    ///    立刻预览改色，再弹一个只有「确定 / 取消」的确认面板：确定落成一次可撤销的显示修改，
    ///    取消恢复原色。确认面板定义在 application\LYJ_ColorConfirm.dlx
    ///    （由 工具脚本\gen_color_confirm_dialog.py 生成）。
    ///
    /// 两个功能都通过 DisplayModification 生效，与 NX 自带"编辑对象显示"走同一套显示机制，
    /// 不写部件数据、不进撤销列表以外的地方。
    /// </summary>
    internal static class DisplayTools
    {
        private const string DlgFileName = "LYJ_Transparency.dlx";

        // 对话框存活期间的块引用（回调里用）
        private static BlockDialog _dialog;
        private static SelectObjectBlock _partBlock;
        private static IntegerBlock _transBlock;        // 整数块做滑条：值天然是 1~100 的级别
        private static bool _applying;                 // 防止 update 回调里嵌套触发
        private static bool _shown;                    // 对话框已经显示出来（之前的 update 都是初始化）
        private static bool _markCreated;              // 本次对话框已经建过撤销标记
        private static Session.UndoMarkId _mark;       // 打开对话框时的撤销标记（取消时回滚到这里）

        #region 透明化

        /// <summary>菜单/功能区入口：打开「透明化」对话框。</summary>
        internal static void ShowTransparencyDialog()
        {
            Log("=== 点击「透明化」 ===");
            UI ui = UI.GetUI();

            // Launch 是模态的，正常不会并发；这条只是兜底，防止重入建两个对话框
            if (_dialog != null) { Log("已有对话框在显示，忽略本次点击"); return; }

            string dlx = FindDlx(DlgFileName);
            Log("dlx = " + (dlx ?? "<未找到>"));
            if (dlx == null)
            {
                ui.NXMessageBox.Show("透明化", NXMessageBox.DialogType.Error,
                    "找不到对话框定义 " + DlgFileName + "。\r\n\r\n" +
                    "请重新安装 LYJ —— 该文件应位于安装目录的 application 下。");
                return;
            }

            // 用一次性文件名，绕开 NX 的对话框记忆（它会把上次的选择过滤器恢复回来）
            string tempDlx = CloneDlgWithUniqueName(dlx);
            string dlgArg = tempDlx != null ? Path.GetFileName(tempDlx) : DlgFileName;
            string dlgPath = tempDlx ?? dlx;

            try
            {
                BlockDialog dlg = CreateDialog(ui, dlgArg, dlgPath);
                Log("CreateDialog 成功，StartLaunch");
                dlg.AddInitializeHandler(OnInit);
                dlg.AddUpdateHandler(OnUpdate);
                dlg.AddDialogShownHandler(OnShown);
                dlg.AddOkHandler(OnOk);
                dlg.AddCancelHandler(OnCancel);
                _dialog = dlg;

                Log("Launch() 调用中…");
                dlg.Launch();          // 模态：用户关闭后返回
                Log("Launch() 已返回（对话框关闭）");
            }
            catch (Exception ex)
            {
                Log("EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message);
                ShowError(ui, ex);
            }
            finally
            {
                _dialog = null;
                _partBlock = null;
                _transBlock = null;
                _shown = false;
                _markCreated = false;
                TryDeleteFile(tempDlx);
            }
        }

        /// <summary>
        /// 把 .dlx 复制成"一次性名字"再打开。
        ///
        /// NX 的对话框记忆是按 **文件名** 建 key 的（`DialogMemory.dlx` 里那条
        /// `MORE::UGS::BlockStyler::DialogCreatorLYJ_Transparency` 就是从文件名扩展来的）。
        /// 名字不变，NX 每次打开都会把上次的 DetailedFilter（也就是顶部的选择过滤器）
        /// 恢复回来；而块级 API 清不掉它 —— PropertyList 里真实的属性名是 `SelectionFilter`，
        /// `ClearFilter` 返回成功，UI 上仍是上次那一项（实测 2026-11-14）。
        /// 换一个没用过的文件名，NX 找不到对应记忆，下拉自然回到默认值。
        ///
        /// 现在只剩透明化用得上（改色已改走 NX 原生类选择对话框，没有自绘选择块）。
        /// 用完即删，只在 %TEMP%\LYJ\dlg 下躺一会儿。
        /// </summary>
        internal static string CloneDlgWithUniqueName(string srcPath)
        {
            if (string.IsNullOrEmpty(srcPath)) return null;

            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            string name = baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dlx";

            // 优先写到安装目录的 application 下 —— NX 的 CreateDialog 按纯文件名只在这个搜索路径里找，
            // 放这儿成功率高；写不进去（只读安装、权限不足）再退系统临时目录，那里只能走绝对路径。
            string[] dirs =
            {
                Path.Combine(LYJApp.DeployRoot, "application"),
                Path.Combine(Path.GetTempPath(), "LYJ", "dlg"),
            };

            foreach (string dir in dirs)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string dst = Path.Combine(dir, name);
                    File.Copy(srcPath, dst, true);
                    Log("  对话框改用一次性名字（绕开记忆）：" + dst);
                    return dst;
                }
                catch (Exception ex)
                {
                    Log("  在 " + dir + " 建临时对话框失败（换下一个）: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Log("  复制对话框失败（回退用原文件）");
            return null;
        }

        /// <summary>安静地删一个临时文件，失败只记日志（关对话框后再删，NX 已经不用它了）。</summary>
        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log("  删除临时文件失败（忽略）: " + ex.Message); }
        }

        /// <summary>
        /// UI.CreateDialog 用文件名还是绝对路径在不同版本上表现不一：
        /// 先用纯文件名（NX 走 custom_dirs 里的 application 搜索，这是官方推荐用法），
        /// 失败再退回绝对路径。两条路都记日志，出问题一眼能看出卡在哪。
        /// </summary>
        internal static BlockDialog CreateDialog(UI ui, string fileName, string dlxPath)
        {
            // 一次性名字的临时 dlx 是绝对路径：直接走路径，省一次注定失败的尝试
            if (!string.IsNullOrEmpty(fileName) && Path.IsPathRooted(fileName))
            {
                BlockDialog d0 = ui.CreateDialog(dlxPath);
                Log("  按绝对路径创建成功：" + dlxPath);
                return d0;
            }

            try
            {
                BlockDialog d = ui.CreateDialog(fileName);
                Log("  按文件名创建成功：" + fileName);
                return d;
            }
            catch (Exception ex1)
            {
                Log("  按文件名创建失败: " + ex1.Message);
            }

            BlockDialog d2 = ui.CreateDialog(dlxPath);
            Log("  按绝对路径创建成功：" + dlxPath);
            return d2;
        }

        /// <summary>在安装目录里找 .dlx（application 优先，其次 startup）。</summary>
        internal static string FindDlx(string fileName)
        {
            try
            {
                string root = LYJApp.DeployRoot;
                string[] dirs = { Path.Combine(root, "application"), Path.Combine(root, "startup") };
                foreach (string d in dirs)
                {
                    string p = Path.Combine(d, fileName);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        private static void OnInit()
        {
            try
            {
                Log("  OnInit 进入");
                _partBlock = _dialog.TopBlock.FindBlock("partSelect") as SelectObjectBlock;
                _transBlock = _dialog.TopBlock.FindBlock("transValue") as IntegerBlock;
                Log("  OnInit 取块: partSelect=" + (_partBlock != null) +
                    " transValue=" + (_transBlock != null));

                if (_transBlock != null)
                {
                    // 0~100 级整数。用 Integer 块（PresentationStyle=Scale）而不是 Double，
                    // 是为了不出现小数点 —— Double 的 VisibleDecimals 管不到滑条上那个数值气泡。
                    _transBlock.MinimumValue = 0;
                    _transBlock.MaximumValue = 100;
                    _transBlock.Increment = 1;
                    if (_transBlock.Value < 0) _transBlock.Value = 50;
                }

                // 放开选择过滤：图形区里体、片体、面、边都能直接点
                if (_partBlock != null)
                {
                    ApplySelectionFilter(_partBlock);

                    // 打开对话框时把图形区已经选中的对象带进来，省一次重新选择
                    TaggedObject[] pre = GetPickedObjects();
                    if (pre.Length > 0) _partBlock.SetSelectedObjects(ResolveToBodies(pre));
                }
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
            }
        }

        // 允许选择的类型（NXOpen.BlockStyler.SelectObject.FilterType），分两套：
        //
        //   正常模式（默认）：只放 实体 / 片体，**故意不放 Faces** ——
        //     NX 的预高亮是"最小实体优先"（面 > 边 > 体），只要面在可选项里，
        //     鼠标一放到模型上就先把那一个面高亮出来（提示文字「面 / 块(1)」），
        //     点了才变成体，看着就像实体选不中。把面从可选项里拿掉后，
        //     NX 预高亮的直接就是整个体，所见即所得。
        //   面模式：用户在下拉里切到「面」时才放开 Faces，保留单面改色。
        //
        // 这里**故意不含 Features**：一旦放开 Feature 类型，NX 会往下拉里塞一整套
        // 特征类过滤项（基准 / 曲线特征 / 模具基准 / 测量对象 / 焊接对象 / 草图），
        // 而改色根本用不到它们 —— 实测这就是下拉"多出一堆用不上的项"的来源。
        // 特征本身仍可由下面的 UF_feature_type 掩码选中（部件导航器里点特征照样算）。
        private static readonly SelectObjectBlock.FilterType[] SelectionFilterTypes =
        {
            SelectObjectBlock.FilterType.SolidBodies,
            SelectObjectBlock.FilterType.SheetBodies,
        };

        /// <summary>面模式：下拉切到「面」时用，只让面可选。</summary>
        private static readonly SelectObjectBlock.FilterType[] FaceFilterTypes =
        {
            SelectObjectBlock.FilterType.Faces,
        };

        /// <summary>
        /// 顶部「选择过滤器」下拉里要列出的条目（NXOpen.Select.FilterMember）。
        ///
        /// 这里必须单独注册：<see cref="SelectObjectBlock.AddFilter(SelectObjectBlock.FilterType)"/>
        /// 只决定"块允许选什么类型"，**完全不影响那个下拉的清单**；
        /// 下拉的内容来自块注册的 FilterMember 列表，由 AddFilterMembers 写入。
        /// 只调 AddFilter 时下拉里永远只有 NX 默认那几项（基准/曲线特征/草图…），
        /// 用户会以为选不了实体和片体。
        ///
        /// 下拉清单（顺序即显示顺序），照用户 2026-11-14 给的 NX 原生「编辑对象显示」截图：
        ///   无选择过滤器 / 实体 / 坐标系 / 基准平面 / 点 / 面
        /// 用户要求「实体排最靠前」，所以实体放在数组第一位，紧跟在「无选择过滤器」下面。
        /// 片体 / 边不再单独列进下拉（用户明确要求去掉），但允许类型里照样放开 ——
        /// 「无选择过滤器」下点片体、边都能选中改色。
        /// </summary>
        private static readonly NXOpen.Select.FilterMember[] SelectionFilterMembers =
        {
            NXOpen.Select.FilterMember.AllSolidBodies,      // 实体：优先级最高，排最前
            NXOpen.Select.FilterMember.CoordinateSystem,
            NXOpen.Select.FilterMember.DatumPlane,
            NXOpen.Select.FilterMember.Point,
            NXOpen.Select.FilterMember.AllFaces,
        };

        /// <summary>
        /// 给选择块设过滤，让图形区里"点哪都能选"，并让顶部下拉列出常用类型。
        ///
        /// .dlx 里 DetailedFilter 是空的，NX 会把它当成"一个类型都不允许"，
        /// 结果图形区里点什么都不上选（只有打开对话框前已经选中的对象能带进来）。
        /// 所以必须在 Initialize 里显式设一遍：实体 / 片体。
        ///
        /// 三条路各管一件事，缺一个就有症状：
        ///   ① 枚举 AddFilter(FilterType) —— 块允许选的类型（保证图形区点得中）；
        ///   ② 掩码 AddFilter(type, subtype, solidType) —— 同上，走 UF 掩码；
        ///   ③ AddFilterMember(s) —— **顶部下拉里显示哪些项**，只管 UI 清单。
        ///
        /// 「过滤器记着上次」已经由"一次性文件名"（<see cref="CloneDlgWithUniqueName"/>）
        /// 从根上解决：NX 的对话框记忆按 dlx 文件名建 key，名字每回都不同就没有记忆可恢复。
        /// 之前那套 ResetFilter + PropertyList.ClearFilter 只剩反作用 —— 实测每次打开白花
        /// 0.7~1.2 秒（GetPropertyNames 300ms、清不存在的属性名一次 450ms），已整段删掉。
        /// </summary>
        private static void ApplySelectionFilter(SelectObjectBlock block)
        {
            ApplyFilterTypes(block, false);

            // 清掉 NX 默认挂上来的杂项（基准 / 曲线特征 / 模具基准 / 测量对象 / 焊接对象 / 草图…）。
            // **必须排在注册自己下拉项之前**：RemoveFilterMember 是按内部
            // (type, subtype, solid) 三元组移除的，清理过程中碰到同一三元组的成员时，
            // 刚加进去的「实体 / 片体」会被一起带走 —— 2026-11-13 实测症状就是下拉只剩「实体 / 面」。
            int removed = StripExtraFilterMembers(block);

            // 最后注册自己的下拉项（无选择过滤器由 NX 自带，其余见 SelectionFilterMembers）
            int memberOk = AddOwnFilterMembers(block);

            Log("  选择过滤：清理额外下拉项 " + removed + " 个，注册下拉项 " +
                memberOk + "/" + SelectionFilterMembers.Length);
        }

        /// <summary>
        /// 只重设"允许选什么类型"，**不碰顶部下拉清单**（ClearFilter 不动 FilterMember 列表，
        /// 所以切模式时下拉里那几项不会被打回默认）。
        ///
        /// faceMode = false：实体 / 片体 —— 面不在可选项里，NX 预高亮直接给整个体；
        /// faceMode = true ：只有面 —— 用户在下拉里切到「面」做单面改色时用。
        /// clearFirst：先清一遍 DetailedFilter 再写。OnInit 里建完下拉清单后要清（clearFirst=true），
        /// 由 <see cref="ApplySelectionFilter"/> 的 ③ 步传 false 再补允许类型。
        ///
        /// ★ **用户切顶部下拉时绝对不能调**：ClearFilter() 清掉的就是 DetailedFilter，
        ///   也就是下拉当前选中的那一项 —— 用户刚切到「面」，一反手就被清回空白，
        ///   模式判定跟着回退，症状正是"选了面，整个体照样变色"（2026-11-14 实测）。
        /// </summary>
        private static void ApplyFilterTypes(SelectObjectBlock block, bool faceMode)
        {
            try { block.ClearFilter(); }
            catch (Exception ex) { Log("  ClearFilter 失败（忽略）: " + ex.Message); }

            SelectObjectBlock.FilterType[] types = faceMode ? FaceFilterTypes : SelectionFilterTypes;
            int ok = 0;
            foreach (SelectObjectBlock.FilterType ft in types)
            {
                try { block.AddFilter(ft); ok++; }
                catch (Exception ex) { Log("  AddFilter(" + ft + ") 失败（忽略）: " + ex.Message); }
            }

            // 掩码写法：实体 / 片体 用 solid 类型 + 实体/片体子类型，面 / 特征各一个类型码
            // （特征这里只走掩码，不加 FilterType.Features —— 见上面 SelectionFilterTypes 的说明）
            int[][] maskDefs = faceMode
                ? new[] { new[] { NXOpen.UF.UFConstants.UF_face_type, 0, 0 } }
                : new[]
                {
                    new[] { NXOpen.UF.UFConstants.UF_solid_type, 0,
                            NXOpen.UF.UFConstants.UF_UI_SEL_FEATURE_SOLID_BODY },
                    new[] { NXOpen.UF.UFConstants.UF_solid_type, 0,
                            NXOpen.UF.UFConstants.UF_UI_SEL_FEATURE_SHEET_BODY },
                    new[] { NXOpen.UF.UFConstants.UF_feature_type, 0, 0 },
                };

            int maskOk = 0;
            foreach (int[] m in maskDefs)
                maskOk += TryAddMaskFilter(block, m[0], m[1], m[2]);

            Log("  允许类型：" + (faceMode ? "面模式" : "体模式") + "，" + ok + "/" + types.Length +
                " 枚举 + " + maskOk + "/" + maskDefs.Length + " 掩码");
        }

        /// <summary>
        /// 注册顶部的下拉项（坐标系 / 基准平面 / 实体 / 点 / 面）。必须在上面的清理之后调用，
        /// 否则会被清理一起删掉。
        /// </summary>
        private static int AddOwnFilterMembers(SelectObjectBlock block)
        {
            try
            {
                block.AddFilterMembers(SelectionFilterMembers);
                return SelectionFilterMembers.Length;
            }
            catch (Exception ex)
            {
                Log("  AddFilterMembers 失败，改为逐个加: " + ex.GetType().Name + ": " + ex.Message);
                int n = 0;
                foreach (NXOpen.Select.FilterMember fm in SelectionFilterMembers)
                {
                    try { block.AddFilterMember(fm); n++; }
                    catch (Exception e2) { Log("  AddFilterMember(" + fm + ") 失败（忽略）: " + e2.Message); }
                }
                return n;
            }
        }

        /// <summary>
        /// 摘掉下拉里多余的过滤项，只保留 <see cref="SelectionFilterMembers"/> 那几项和「无选择过滤器」。
        /// FilterMember 有 620 多个枚举值，按名字猜"基准/草图"对应哪个值不可靠，只能全量扫一遍批量删。
        /// </summary>
        private static int StripExtraFilterMembers(SelectObjectBlock block)
        {
            NXOpen.Select.FilterMember[] extra = ExtraFilterMembers;
            if (extra == null)
            {
                Log("  枚举 FilterMember 失败（忽略）");
                return 0;
            }

            int t0 = Environment.TickCount;
            int removed = 0;

            // 先用批量接口（NX1980+）：620 项一次过去
            try
            {
                block.RemoveFilterMembers(extra);
                removed = extra.Length;
            }
            catch (Exception ex)
            {
                Log("  RemoveFilterMembers 批量失败，改为逐个删: " + ex.GetType().Name + ": " + ex.Message);

                int failed = 0;
                string firstFail = null;
                foreach (NXOpen.Select.FilterMember fm in extra)
                {
                    try { block.RemoveFilterMember(fm); removed++; }
                    catch (Exception e2)
                    {
                        failed++;
                        if (firstFail == null) firstFail = fm + " → " + e2.Message;
                    }
                }
                if (firstFail != null) Log("  首个失败 " + firstFail + "（失败 " + failed + " 项）");
            }

            Log("  下拉项清理：移除 " + removed + " 项，耗时 " + (Environment.TickCount - t0) + " ms");
            return removed;
        }

        /// <summary>
        /// 除自己那几项和「无选择过滤器」之外的全部 FilterMember，惰性算一次就缓存 ——
        /// 枚举值有 620 多个，每次 Enum.GetValues + HashSet 也是白花时间。
        /// </summary>
        private static NXOpen.Select.FilterMember[] _extraFilterMembers;

        private static NXOpen.Select.FilterMember[] ExtraFilterMembers
        {
            get
            {
                if (_extraFilterMembers != null) return _extraFilterMembers;
                try
                {
                    HashSet<NXOpen.Select.FilterMember> keep =
                        new HashSet<NXOpen.Select.FilterMember>(SelectionFilterMembers);

                    List<NXOpen.Select.FilterMember> list = new List<NXOpen.Select.FilterMember>(640);
                    foreach (object o in Enum.GetValues(typeof(NXOpen.Select.FilterMember)))
                    {
                        NXOpen.Select.FilterMember fm = (NXOpen.Select.FilterMember)o;
                        if (keep.Contains(fm)) continue;                 // 自己注册的那几项留着
                        if (fm.ToString() == "None") continue;           // 「无选择过滤器」留着
                        list.Add(fm);
                    }
                    _extraFilterMembers = list.ToArray();
                }
                catch (Exception ex)
                {
                    Log("  枚举 FilterMember 失败（忽略）: " + ex.Message);
                    _extraFilterMembers = null;
                }
                return _extraFilterMembers;
            }
        }

        private static int TryAddMaskFilter(SelectObjectBlock block, int type, int subtype, int solidType)
        {
            try { block.AddFilter(type, subtype, solidType); return 1; }
            catch (Exception ex)
            {
                Log("  AddFilter(" + type + "," + subtype + "," + solidType + ") 失败（忽略）: " + ex.Message);
                return 0;
            }
        }

        /// <summary>对话框显示完成：之后的 update 才是用户操作。</summary>
        private static void OnShown()
        {
            _shown = true;
        }

        private static int OnUpdate(UIBlock block)
        {
            // 打开对话框前那段初始化也会触发 update（带入预选对象、设默认值），
            // 此时不能应用 —— 否则一打开就把图形区已选中的部件改成半透明。
            if (!_shown || _applying) return 0;

            // 选部件或拖滑条都即时生效，所见即所得
            if (block == _transBlock || block == _partBlock)
                ApplyTransparency(false);
            return 0;
        }

        private static int OnOk()
        {
            ApplyTransparency(true);
            return 0;
        }

        /// <summary>取消：把对话框期间的即时预览全部回滚（撤销标记在第一次应用前建好）。</summary>
        private static int OnCancel()
        {
            if (_markCreated)
            {
                try { Session.GetSession().UndoToMark(_mark, null); }
                catch { }
                _markCreated = false;
            }
            return 0;
        }

        /// <summary>把当前选择 + 滑条值写到对象显示上。</summary>
        private static void ApplyTransparency(bool reportEmpty)
        {
            try
            {
                if (_partBlock == null || _transBlock == null) return;

                TaggedObject[] picked = _partBlock.GetSelectedObjects();
                DisplayableObject[] objs = picked == null
                    ? new DisplayableObject[0]
                    : ResolveToBodies(picked);

                if (objs.Length == 0)
                {
                    if (reportEmpty)
                        UI.GetUI().NXMessageBox.Show("透明化", NXMessageBox.DialogType.Warning,
                            "请先选择要调整透明度的对象。\r\n\r\n" +
                            "可以在图形区直接点实体、片体、面，或在部件导航器里选" +
                            "「体」/生成它的特征（如 块(1)）。");
                    return;
                }

                // 整数滑条：值本来就在 0~100 之间，这里只做范围兜底
                int value = _transBlock.Value;
                if (value < 0) value = 0;
                if (value > 100) value = 100;

                _applying = true;

                // 第一次真正改显示之前建撤销标记：一次对话框只留一条记录，
                // 拖滑条的几十次预览全落在这一个标记里，取消时一次撤到底。
                if (!_markCreated)
                {
                    _mark = Session.GetSession().SetUndoMark(
                        Session.MarkVisibility.Visible, "LYJ 透明化");
                    _markCreated = true;
                }

                ApplyDisplayModification(objs, -1, value, false);
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
            }
            finally
            {
                _applying = false;
            }
        }

        #endregion

        #region 部件颜色

        /// <summary>改色确认面板：一个说明标签 + 确定/取消（没有选择块，选对象交给 NX 原生类选择）。</summary>
        private const string ColorConfirmDlgFileName = "LYJ_ColorConfirm.dlx";

        // 改色流程里的状态
        private static BlockDialog _colorDialog;
        private static LabelBlock _colorInfoBlock;
        private static int _colorTargetId = -1;          // 本次要应用的调色板编号
        private static string _colorTargetName = "";     // 颜色名（写进对话框说明）
        private static TaggedObject[] _colorPicked;      // 类选择 / 图形区拿到的原始对象
        private static DisplayableObject[] _colorObjs;   // 展开后真正要刷的对象（体 + 它的面）
        private static int _savedSelColor = -1;          // 预览期间临时改掉的"选择色"，关闭时还原
        private static bool _selColorChanged;

        /// <summary>预览前记下的原色，取消时逐个还原（Tag → 对象 + 原色号）。</summary>
        private sealed class ColorOrigin
        {
            public DisplayableObject Obj;
            public int Color;          // &lt; 0 表示读不到原色，不还原
        }

        private static readonly Dictionary<Tag, ColorOrigin> _colorOrigin
            = new Dictionary<Tag, ColorOrigin>();

        /// <summary>
        /// 颜色按钮入口：先定要改哪些对象（图形区已选就用现成的，否则拉起 NX 原生类选择对话框），
        /// 立刻预览改色，再弹一个只有「确定 / 取消」的小面板：
        /// 确定 → 落成一次可撤销的显示修改；取消 → 把预览过的对象还原原色。
        ///
        /// 取色仍是三级（见 ResolveSlotColorId），在弹任何框之前做完 —— 颜色不对当场报错，
        /// 不会让用户白选一通。
        /// </summary>
        internal static void ApplyColorSlot(int colorId, string colorName, double[] rgb, string displayName)
        {
            UI ui = UI.GetUI();
            Log("=== 点击颜色按钮" + (string.IsNullOrEmpty(displayName) ? "" : "：" + displayName) + " ===");
            try
            {
                // 模态对话框正常不会并发，这条只是防重入
                if (_colorDialog != null) { Log("已有改色面板在显示，忽略本次点击"); return; }

                Part workPart = Session.GetSession().Parts.Work;
                if (workPart == null)
                {
                    ui.NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Error, "请先打开一个部件。");
                    return;
                }

                int id = ResolveSlotColorId(workPart, colorId, colorName, rgb, displayName);
                if (id < 0) return;      // 报错已在里面弹过

                TaggedObject[] picked = PickForColor();
                if (picked == null || picked.Length == 0) { Log("  没有选中对象，放弃本次改色"); return; }

                DisplayableObject[] objs = ExpandBodiesWithFaces(ResolveForColor(picked));
                if (objs.Length == 0)
                {
                    ui.NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Warning,
                        "选中的对象不能改色。\r\n\r\n" +
                        "选实体 / 片体 / 面，或在部件导航器里选生成了实体的特征（如「块(1)」）。");
                    return;
                }

                _colorTargetId = id;
                _colorTargetName = string.IsNullOrEmpty(displayName) ? "选定颜色" : displayName;
                _colorPicked = picked;
                _colorObjs = objs;

                // 预览期间把"选择色"临时指到目标色：否则对象被橙色高亮整片盖住，看不出改成了什么色
                SetSelectionColorForPreview(workPart, id);
                ApplyColorPreview();

                string dlx = FindDlx(ColorConfirmDlgFileName);
                if (dlx == null)
                {
                    // 少了确认面板不该让功能瘫痪：直接提交，用户照样能 Ctrl+Z 撤回
                    Log("  找不到 " + ColorConfirmDlgFileName + "，直接提交改色");
                    CommitColor();
                    return;
                }

                BlockDialog dlg = CreateDialog(ui, ColorConfirmDlgFileName, dlx);
                dlg.AddInitializeHandler(OnColorInit);
                dlg.AddOkHandler(OnColorOk);
                dlg.AddCancelHandler(OnColorCancel);
                _colorDialog = dlg;

                dlg.Launch();            // 模态：用户点确定 / 取消后返回
            }
            catch (Exception ex)
            {
                ShowError(ui, ex);
            }
            finally
            {
                RestoreSelectionColor();
                _colorDialog = null;
                _colorInfoBlock = null;
                _colorPicked = null;
                _colorObjs = null;
                _colorOrigin.Clear();
                _colorTargetId = -1;
                _colorTargetName = "";
            }
        }

        /// <summary>
        /// 类选择对话框里可供勾选的类型（supported）——
        /// 勾「实体 / 片体」→ 点模型任意一处都是整个体改色；只勾「面」→ 单面改色。
        /// </summary>
        private static readonly NXOpen.Select.FilterMember[] ColorSelectTypes =
        {
            NXOpen.Select.FilterMember.AllSolidBodies,
            NXOpen.Select.FilterMember.AllSheetBodies,
            NXOpen.Select.FilterMember.AllFaces,
            NXOpen.Select.FilterMember.DatumPlane,
            NXOpen.Select.FilterMember.CoordinateSystem,
            NXOpen.Select.FilterMember.Point,
        };

        /// <summary>默认勾上的类型：实体 + 片体 —— 即"点哪都是整个体"，和原生「编辑对象显示」一致。</summary>
        private static readonly NXOpen.Select.FilterMember[] ColorSelectDefaults =
        {
            NXOpen.Select.FilterMember.AllSolidBodies,
            NXOpen.Select.FilterMember.AllSheetBodies,
        };

        /// <summary>
        /// 类选择的对象过滤回调：全放行（NX 约定返回 1 = 允许选中）。
        /// 第 7 个参数**不能传 null** —— NX 每个候选对象都回调它，传 null 就是每次点选一个
        /// NullReferenceException（选不中任何对象，关对话框时抛「回调自动化代码时遇到未处理的异常」）。
        /// </summary>
        private static int AcceptAnyObject(TaggedObject obj)
        {
            try
            {
                _filterHits++;
                return 1;
            }
            catch
            {
                return 1;      // 回调里抛异常会穿出 NX 的对话框循环，一律吞掉
            }
        }

        private static int _filterHits;      // 日志诊断：这次对话框里过滤器被回调了几次

        private static readonly Selection.FilterTaggedObject ColorFilterAcceptAll = AcceptAnyObject;

        /// <summary>
        /// 挑出要改色的对象：图形区已选就直接用，否则拉 NX 原生类选择对话框（可多选）。
        /// 用原生的类选择而不是自绘选择块 —— 自绘那套过滤器在 2606 上清单会消失、
        /// 会记住上次的勾选、也读不出 DetailedFilter；原生对话框由 NX 自己维护。
        /// </summary>
        private static TaggedObject[] PickForColor()
        {
            TaggedObject[] pre = GetPickedObjects();
            if (pre.Length > 0)
            {
                Log("  图形区已选 " + pre.Length + " 个对象（" + DescribePicked(pre) + "），直接用");
                return pre;
            }

            TaggedObject[] picked = null;
            NXOpen.Selection.Response resp;
            _filterHits = 0;
            try
            {
                // 选择管理器是实例（UI.SelectionManager），不是静态类
                Selection sel = UI.GetUI().SelectionManager;
                resp = sel.SelectTaggedObjectsWithFullFiltering(
                    "选择要改色的对象（可多选；勾选类型可限定实体 / 面）",
                    "改颜色 - 选择对象",
                    NXOpen.Selection.SelectionScope.WorkPart,
                    NXOpen.Selection.SelectionAction.ClearAndEnableSpecific,
                    ColorSelectTypes,
                    ColorSelectDefaults,
                    ColorFilterAcceptAll,       // 不能传 null，见 AcceptAnyObject 的说明
                    out picked);
            }
            catch (Exception ex)
            {
                Log("  类选择对话框失败: " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            int n = picked == null ? 0 : picked.Length;
            Log("  类选择返回 " + resp + "，" + n + " 个对象（" + DescribePicked(picked) +
                "），过滤器回调 " + _filterHits + " 次");

            // 对话框改的是"全局选择过滤器"的成员集合，回图形区前恢复成默认 ——
            // 否则下一次在图形区点东西，可能还带着"只能选实体"的限制。
            try { UI.GetUI().SelectionManager.ResetEnabledGlobalFilterMembers(); }
            catch (Exception ex) { Log("  重置全局过滤器失败（忽略）: " + ex.Message); }

            if (resp != NXOpen.Selection.Response.Ok) return null;
            return picked ?? new TaggedObject[0];
        }

        /// <summary>把要改的对象刷成目标色，并记下它们的原色（取消时按原色还原）。</summary>
        private static void ApplyColorPreview()
        {
            if (_colorObjs == null || _colorObjs.Length == 0 || _colorTargetId <= 0) return;

            foreach (DisplayableObject o in _colorObjs)
            {
                if (_colorOrigin.ContainsKey(o.Tag)) continue;
                int orig = -1;
                try { orig = o.Color; } catch { }
                _colorOrigin[o.Tag] = new ColorOrigin { Obj = o, Color = orig };
            }

            ApplyDisplayModification(_colorObjs, _colorTargetId, -1);
            Log("  预览改色：选 " + (_colorPicked == null ? 0 : _colorPicked.Length) +
                " 个（" + DescribePicked(_colorPicked) + "）→ 刷 " + _colorObjs.Length +
                " 个对象 → 色号 " + _colorTargetId);
        }

        private static void OnColorInit()
        {
            try
            {
                _colorInfoBlock = _colorDialog.TopBlock.FindBlock("confirmInfo") as LabelBlock;
                if (_colorInfoBlock == null) { Log("  OnInit: 找不到 confirmInfo 标签块"); return; }

                _colorInfoBlock.Label =
                    "目标颜色：" + _colorTargetName + "\r\n" +
                    "已选 " + (_colorPicked == null ? 0 : _colorPicked.Length) + " 个对象（" +
                    DescribePicked(_colorPicked) + "），改色已在上方预览。\r\n" +
                    "点「确定」保留（之后可按 Ctrl+Z 一步撤销），点「取消」恢复原色。";

                Log("  OnInit 就绪：待提交 " + (_colorObjs == null ? 0 : _colorObjs.Length) + " 个对象");
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
            }
        }

        private static int OnColorOk()
        {
            CommitColor();
            return 0;                 // 0 = 关闭面板
        }

        /// <summary>
        /// 确定：先静默退掉预览，再在**可见撤销标记**里重刷一遍并 DoUpdate —— Ctrl+Z 一步回原色。
        /// </summary>
        private static void CommitColor()
        {
            if (_colorObjs == null || _colorObjs.Length == 0 || _colorTargetId <= 0) return;

            Session session = Session.GetSession();

            // ① 静默退掉预览（不刷新界面也能生效，反正紧接着就要重刷）
            try { RestoreOriginColors(); }
            catch (Exception ex) { Log("  提交前还原预览失败（忽略）: " + ex.Message); }

            // ② 在可见撤销标记内重新刷色（= 日记账的 SetUndoMark + Apply + DoUpdate）
            Session.UndoMarkId mark = default(Session.UndoMarkId);
            bool markOk = false;
            try
            {
                mark = session.SetUndoMark(Session.MarkVisibility.Visible,
                    "LYJ 改颜色 " + _colorTargetName);
                markOk = true;
            }
            catch (Exception ex) { Log("  建撤销标记失败（忽略）: " + ex.Message); }

            try
            {
                ApplyDisplayModification(_colorObjs, _colorTargetId, -1);
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
                return;
            }

            if (markOk)
            {
                try { session.UpdateManager.DoUpdate(mark); }
                catch (Exception ex) { Log("  DoUpdate 失败（忽略）: " + ex.Message); }
            }

            Log("  提交改色：刷 " + _colorObjs.Length + " 个对象 → 色号 " + _colorTargetId +
                "（已放进可见撤销标记，可 Ctrl+Z）");
        }

        /// <summary>
        /// 取消：把预览过的对象逐个还原成改色前的颜色。
        /// **不用**撤销标记回滚（透明化用的那招）—— 预览不在撤销记录里，
        /// 用 UndoToMark 会连带撤掉用户之前的操作，和逐个还原互相打架。
        /// </summary>
        private static int OnColorCancel()
        {
            RestoreOriginColors();
            return 0;
        }

        /// <summary>
        /// 预览期间把「选择色」临时指到目标色：否则对象被选中高亮（默认亮橙）整片盖住，
        /// 看不出改成了什么颜色。部件级偏好，关闭面板后由 <see cref="RestoreSelectionColor"/> 还原。
        /// </summary>
        private static void SetSelectionColorForPreview(Part workPart, int colorId)
        {
            _selColorChanged = false;
            try
            {
                NXOpen.Preferences.PartVisualizationColorSetting cs =
                    workPart.Preferences.ColorSettingVisualization;
                int old = cs.SelectionColor;
                if (old == colorId) return;                 // 恰好一样，不用动
                cs.SelectionColor = colorId;
                _savedSelColor = old;
                _selColorChanged = true;
                Log("  选择色临时改为目标色 " + colorId + "（原 " + old + "）");
            }
            catch (Exception ex)
            {
                Log("  设置选择色失败（忽略，预览仍会带橙色高亮）: " + ex.Message);
            }
        }

        /// <summary>还原预览前的「选择色」。异常路径也必须调用，否则用户的选择色会被改掉。</summary>
        private static void RestoreSelectionColor()
        {
            if (!_selColorChanged) return;
            _selColorChanged = false;
            try
            {
                Part workPart = Session.GetSession().Parts.Work;
                if (workPart != null)
                    workPart.Preferences.ColorSettingVisualization.SelectionColor = _savedSelColor;
                Log("  选择色已还原为 " + _savedSelColor);
            }
            catch (Exception ex)
            {
                Log("  还原选择色失败（忽略）: " + ex.Message);
            }
        }

        /// <summary>日志用：把选中的对象类型拼成一句话（体 / 片体 / 面 / 特征各几个）。</summary>
        private static string DescribePicked(TaggedObject[] picked)
        {
            if (picked == null || picked.Length == 0) return "空";

            int solid = 0, sheet = 0, face = 0, feat = 0, other = 0;
            foreach (TaggedObject o in picked)
            {
                Body b = o as Body;
                if (b != null)
                {
                    try
                    {
                        if (b.IsSolidBody) solid++;
                        else sheet++;
                    }
                    catch { other++; }
                    continue;
                }
                if (o is Face) { face++; continue; }
                if (o is NXOpen.Features.Feature) { feat++; continue; }
                other++;
            }

            List<string> parts = new List<string>();
            if (solid > 0) parts.Add("实体 " + solid);
            if (sheet > 0) parts.Add("片体 " + sheet);
            if (face > 0) parts.Add("面 " + face);
            if (feat > 0) parts.Add("特征 " + feat);
            if (other > 0) parts.Add("其他 " + other);
            return parts.Count == 0 ? "空" : string.Join(" + ", parts.ToArray());
        }

        /// <summary>把记录里的对象全部还原成原色（点「取消」时用）。</summary>
        private static void RestoreOriginColors()
        {
            if (_colorOrigin.Count == 0) return;

            try
            {
                // 按原色分组：展开成"体 + 面"之后可能有几百个对象，
                // 每个都单独建 DisplayModification 太慢，同色的合成一次调用
                Dictionary<int, List<DisplayableObject>> byColor =
                    new Dictionary<int, List<DisplayableObject>>();
                foreach (KeyValuePair<Tag, ColorOrigin> kv in _colorOrigin)
                {
                    ColorOrigin co = kv.Value;
                    if (co == null || co.Obj == null || co.Color < 0) continue;
                    List<DisplayableObject> bucket;
                    if (!byColor.TryGetValue(co.Color, out bucket))
                    {
                        bucket = new List<DisplayableObject>();
                        byColor[co.Color] = bucket;
                    }
                    bucket.Add(co.Obj);
                }

                foreach (KeyValuePair<int, List<DisplayableObject>> kv in byColor)
                {
                    try { ApplyDisplayModification(kv.Value.ToArray(), kv.Key, -1, false); }
                    catch (Exception ex) { Log("  还原原色失败（忽略）: " + ex.Message); }
                }
                Log("  已还原 " + _colorOrigin.Count + " 个对象的原色（" + byColor.Count + " 组）");
            }
            finally
            {
                _colorOrigin.Clear();
            }
        }

        /// <summary>
        /// 配置里的颜色槽 → 当前部件调色板里的色号，三级取色，查不到返回 -1（并弹提示）。
        /// </summary>
        private static int ResolveSlotColorId(Part workPart, int colorId, string colorName,
                                              double[] rgb, string displayName)
        {
            if (colorId > 0)
            {
                try { if (workPart.Colors.Find(colorId) != null) return colorId; }
                catch { }
            }

            int id = FindColorIdByName(workPart, colorName);
            if (id < 0) id = FindColorByRgb(workPart, rgb);
            if (id < 0)
            {
                UI.GetUI().NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Error,
                    "当前部件的调色板里找不到这个颜色" +
                    (string.IsNullOrEmpty(displayName) ? "" : "「" + displayName + "」") +
                    "。\r\n\r\n可以点「显示」组最后的配置按钮换一个颜色。");
            }
            return id;
        }

        /// <summary>调色板编号 → RGB（0~1）；查不到返回 null。</summary>
        internal static double[] GetColorRgb(Part part, int id)
        {
            if (part == null || id <= 0) return null;
            try
            {
                NXColor c = part.Colors.Find(id);
                if (c == null) return null;
                NXColor.Rgb v = c.GetRgb();
                return new[] { v.R, v.G, v.B };
            }
            catch { return null; }
        }

        /// <summary>调色板编号 → 颜色名；查不到返回空串。</summary>
        internal static string GetColorName(Part part, int id)
        {
            if (part == null || id <= 0) return "";
            try
            {
                NXColor c = part.Colors.Find(id);
                return c == null ? "" : (c.Label ?? "");
            }
            catch { return ""; }
        }

        /// <summary>给定 RGB 在调色板里找最接近的编号（0~1，误差 2% 内）；没有返回 -1。</summary>
        internal static int FindColorIdByRgb(Part part, double r, double g, double b)
        {
            if (part == null) return -1;
            return FindColorByRgb(part, new[] { r, g, b });
        }

        /// <summary>按调色板颜色名查编号；查不到返回 -1。</summary>
        internal static int FindColorIdByName(Part part, string colorName)
        {
            if (part == null || string.IsNullOrEmpty(colorName)) return -1;
            try
            {
                NXColor c = part.Colors.Find(colorName);
                return c == null ? -1 : c.Id;
            }
            catch { return -1; }
        }

        // 调色板缓存：ColorManager 只有 Find(id) / Find(name)，没有"列出全部"的接口，
        // 按 RGB 匹配只能 1..256 逐个 Find —— 每次调用都要翻一遍调色板。
        // 配置对话框有 10 个颜色槽，不缓存就是 2560 次查找（实测卡几十秒）。
        private sealed class PaletteSnapshot
        {
            public DateTime Stamp;
            public Dictionary<int, double[]> Map;
        }

        private static readonly Dictionary<int, PaletteSnapshot> _paletteCache
            = new Dictionary<int, PaletteSnapshot>();

        private static Dictionary<int, double[]> GetPalette(Part part)
        {
            int key = part.Tag.GetHashCode();
            PaletteSnapshot snap;
            if (_paletteCache.TryGetValue(key, out snap)
                && (DateTime.Now - snap.Stamp).TotalSeconds < 120)
                return snap.Map;

            var map = new Dictionary<int, double[]>(320);
            for (int id = 1; id <= 256; id++)
            {
                try
                {
                    NXColor c = part.Colors.Find(id);
                    if (c == null) continue;
                    NXColor.Rgb v = c.GetRgb();          // 结构体，成员是字段 R / G / B（0~1）
                    map[id] = new[] { v.R, v.G, v.B };
                }
                catch
                {
                    // 该编号不存在，跳过
                }
            }
            _paletteCache[key] = new PaletteSnapshot { Stamp = DateTime.Now, Map = map };
            return map;
        }

        private static int FindColorByRgb(Part workPart, double[] rgb)
        {
            if (workPart == null || rgb == null || rgb.Length < 3) return -1;

            int best = -1;
            double bestDist = 0.02 * 0.02 * 3;      // 三个通道各允许 2% 误差
            foreach (KeyValuePair<int, double[]> kv in GetPalette(workPart))
            {
                double[] v = kv.Value;
                double dr = v[0] - rgb[0], dg = v[1] - rgb[1], db = v[2] - rgb[2];
                double d = dr * dr + dg * dg + db * db;
                if (d < bestDist) { bestDist = d; best = kv.Key; }
            }
            return best;
        }

        #endregion

        #region 公共

        /// <summary>
        /// 统一的显示修改：colorId &lt; 0 不改颜色，translucency &lt; 0 不改透明度。参数照「编辑对象显示」日记账。
        ///
        /// ApplyToAllFaces=true 只对**体**生效（体带动它所有的面；NX 里单独设过色的面不跟随体色，
        /// 不打开就会出现"整体改了色、某个面还是旧颜色"）；对**面**不生效，只改那一个面。
        /// **还原原色时必须传 allFaces:false** —— 否则恢复体会把正要单独恢复的面又盖掉。
        /// </summary>
        private static void ApplyDisplayModification(DisplayableObject[] objs, int colorId, int translucency,
                                                    bool allFaces = true)
        {
            DisplayModification dm = null;
            try
            {
                // DisplayModification 没有公共构造函数，必须经 DisplayManager 创建
                dm = Session.GetSession().DisplayManager.NewDisplayModification();
                dm.ApplyToAllFaces = allFaces;
                dm.ApplyToOwningParts = false;
                dm.EndPointDisplayState = false;
                if (colorId >= 0) dm.NewColor = colorId;
                if (translucency >= 0) dm.NewTranslucency = translucency;
                dm.Apply(objs);
            }
            finally
            {
                if (dm != null) { try { dm.Dispose(); } catch { } }
            }
        }

        /// <summary>取图形区当前选中的对象（用 SelectionManager，跨版本兼容的取法）。</summary>
        private static TaggedObject[] GetPickedObjects()
        {
            try
            {
                Selection sel = UI.GetUI().SelectionManager;
                int n = sel.GetNumSelectedObjects();
                TaggedObject[] list = new TaggedObject[n];
                for (int i = 0; i < n; i++)
                    list[i] = sel.GetSelectedTaggedObject(i);
                return list;
            }
            catch
            {
                return new TaggedObject[0];
            }
        }

        /// <summary>
        /// 透明化用：把选中对象换算成**体**。
        /// NX 的透明度是"每个体一个值"，面/边点上来也只能落到它所属的体上，
        /// 所以这里统一往体上收：
        ///   体        → 自身
        ///   面 / 边   → 所属体（GetBody()）
        ///   特征      → 该特征生成的体（部件导航器里常选中的是 块(1) 这种特征）
        /// 收不出体的（基准、曲线、点等）直接忽略 —— 对它们改透明度没有意义。
        /// </summary>
        private static DisplayableObject[] ResolveToBodies(TaggedObject[] picked)
        {
            if (picked == null || picked.Length == 0) return new DisplayableObject[0];

            List<DisplayableObject> list = new List<DisplayableObject>();
            HashSet<Tag> seen = new HashSet<Tag>();

            foreach (TaggedObject o in picked)
            {
                if (o == null) continue;

                Body body = o as Body;
                if (body == null)
                {
                    Face face = o as Face;
                    if (face != null)
                    {
                        try { body = face.GetBody(); }
                        catch { }
                    }
                }
                if (body == null)
                {
                    Edge edge = o as Edge;
                    if (edge != null)
                    {
                        try { body = edge.GetBody(); }
                        catch { }
                    }
                }

                if (body != null)
                {
                    if (seen.Add(body.Tag)) list.Add(body);
                    continue;
                }

                NXOpen.Features.Feature feat = o as NXOpen.Features.Feature;
                if (feat == null) continue;

                Body[] bodies = null;
                try { bodies = feat.GetBodies(); }
                catch { }
                if (bodies == null) continue;
                foreach (Body b in bodies)
                    if (b != null && seen.Add(b.Tag)) list.Add(b);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 改色用：选中什么就改什么，**不再把面提升成体**。
        ///
        /// 以前这里恒定"面 → 所属体"，是为了让用户点实体任意一处都整个体改色；但那样一来
        /// 单面改色就没了。现在改成和原生命令一致：
        ///   · 类选择默认只勾「实体 + 片体」，所以正常点模型拿到的就是整个体；
        ///   · 想只改一个面，在类选择里只勾「面」（或直接先在图形区选中面），
        ///     这里原样透传 → 只改那个面。
        /// 体的"连同所有面一起刷"交给 DisplayModification.ApplyToAllFaces
        /// （见 ApplyDisplayModification，照日记账的写法）。
        ///
        /// 特征（部件导航器里常选中的是 块(1) 这种）仍然换算成它生成的体：
        /// 特征不是可显式对象，直接丢给 DisplayModification 会失败。
        /// </summary>
        private static DisplayableObject[] ResolveForColor(TaggedObject[] picked)
        {
            if (picked == null || picked.Length == 0) return new DisplayableObject[0];

            List<DisplayableObject> list = new List<DisplayableObject>();
            HashSet<Tag> seen = new HashSet<Tag>();

            foreach (TaggedObject o in picked)
            {
                if (o == null) continue;

                DisplayableObject disp = o as DisplayableObject;
                if (disp != null)
                {
                    if (seen.Add(disp.Tag)) list.Add(disp);
                    continue;
                }

                NXOpen.Features.Feature feat = o as NXOpen.Features.Feature;
                if (feat == null) continue;

                Body[] bodies = null;
                try { bodies = feat.GetBodies(); }
                catch { }
                if (bodies == null) continue;
                foreach (Body b in bodies)
                    if (b != null && seen.Add(b.Tag)) list.Add(b);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 应用颜色前把「体」展开成「体 + 它的所有面」。
        ///
        /// NX 的颜色是分级的：面一旦单独设过色，就**不再跟随体颜色**，只给体设色时那个面会保持旧色。
        /// 用户看到的现象就是"整个模型改成紫色，之前单独改过色的面还是灰的"。
        /// 所以整体改色必须连面一起刷。
        ///
        /// 选择块里仍然只放体（不然列表里会冒出几百个面），只在应用这一步展开。
        /// </summary>
        private static DisplayableObject[] ExpandBodiesWithFaces(DisplayableObject[] objs)
        {
            if (objs == null || objs.Length == 0) return new DisplayableObject[0];

            List<DisplayableObject> list = new List<DisplayableObject>(objs.Length + 16);
            HashSet<Tag> seen = new HashSet<Tag>();

            foreach (DisplayableObject o in objs)
            {
                if (o == null) continue;
                if (seen.Add(o.Tag)) list.Add(o);

                Body body = o as Body;
                if (body == null) continue;

                Face[] faces = null;
                try { faces = body.GetFaces(); }
                catch { }
                if (faces == null) continue;

                foreach (Face f in faces)
                    if (f != null && seen.Add(f.Tag)) list.Add(f);
            }
            return list.ToArray();
        }

        private static void ShowError(UI ui, Exception ex)
        {
            try
            {
                ui.NXMessageBox.Show("LYJ - 错误", NXMessageBox.DialogType.Error, ex.Message);
            }
            catch { }
        }

        /// <summary>
        /// 诊断日志：写到 &lt;部署目录&gt;\config\lyj_transparency.log。
        /// NX 会把菜单回调里的异常吞掉（点了没反应），只能靠日志判断卡在哪一步。
        /// 功能稳定后可以整段删掉。
        /// </summary>
        internal static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(LYJApp.DeployRoot, "config");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "lyj_transparency.log"),
                    DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        #endregion
    }
}
