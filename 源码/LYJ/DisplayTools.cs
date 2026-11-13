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
    /// 2) 「部件颜色」—— 点颜色按钮同样先弹 NX 原生对话框：选对象 → 选中即预览改色
    ///    → 确定保留 / 取消恢复原色。对话框定义在 application\LYJ_ColorPick.dlx
    ///    （由 工具脚本\gen_color_pick_dialog.py 生成）。
    ///
    /// 两个功能都通过 DisplayModification 生效，与 NX 自带"编辑对象显示"走同一套显示机制，
    /// 不写部件数据、不进撤销列表以外的地方。
    /// </summary>
    internal static class DisplayTools
    {
        private const string DlgFileName = "LYJ_Transparency.dlx";
        private const string ColorDlgFileName = "LYJ_ColorPick.dlx";

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

            try
            {
                BlockDialog dlg = CreateDialog(ui, DlgFileName, dlx);
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
            }
        }

        /// <summary>
        /// UI.CreateDialog 用文件名还是绝对路径在不同版本上表现不一：
        /// 先用纯文件名（NX 走 custom_dirs 里的 application 搜索，这是官方推荐用法），
        /// 失败再退回绝对路径。两条路都记日志，出问题一眼能看出卡在哪。
        /// </summary>
        internal static BlockDialog CreateDialog(UI ui, string fileName, string dlxPath)
        {
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

        // 允许选择的类型（NXOpen.BlockStyler.SelectObject.FilterType）：
        // 实体 / 片体 / 面 / 边。这四类覆盖了"能改色、能改透明度的东西"。
        //
        // 这里**故意不含 Features**：一旦放开 Feature 类型，NX 会往下拉里塞一整套
        // 特征类过滤项（基准 / 曲线特征 / 模具基准 / 测量对象 / 焊接对象 / 草图），
        // 而改色根本用不到它们 —— 实测这就是下拉"多出一堆用不上的项"的来源。
        // 特征本身仍可由下面的 UF_feature_type 掩码选中（部件导航器里点特征照样算）。
        private static readonly SelectObjectBlock.FilterType[] SelectionFilterTypes =
        {
            SelectObjectBlock.FilterType.SolidBodies,
            SelectObjectBlock.FilterType.SheetBodies,
            SelectObjectBlock.FilterType.Faces,
            SelectObjectBlock.FilterType.Edges,
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
        /// 清单与 NX 原生「编辑对象显示」的选择过滤器完全一致，只有三项：
        /// 无选择过滤器 / 实体 / 片体（2026-11-13 用户按原生界面比对后要求对齐）。
        /// 面 / 边不单独列进下拉，但在「无选择过滤器」下照样能点中 ——
        /// 能不能选由上面的 AddFilter / 掩码决定，下拉只是 UI 清单。
        /// </summary>
        private static readonly NXOpen.Select.FilterMember[] SelectionFilterMembers =
        {
            NXOpen.Select.FilterMember.AllSolidBodies,
            NXOpen.Select.FilterMember.AllSheetBodies,
        };

        /// <summary>
        /// 给选择块设过滤，让图形区里"点哪都能选"，并让顶部下拉列出常用类型。
        ///
        /// .dlx 里 DetailedFilter 是空的，NX 会把它当成"一个类型都不允许"，
        /// 结果图形区里点什么都不上选（只有打开对话框前已经选中的对象能带进来）。
        /// 所以必须在 Initialize 里显式设一遍：实体 / 片体 / 面 / 边。
        ///
        /// 三条路各管一件事，缺一个就有症状：
        ///   ① 枚举 AddFilter(FilterType) —— 块允许选的类型（保证图形区点得中）；
        ///   ② 掩码 AddFilter(type, subtype, solidType) —— 同上，走 UF 掩码；
        ///   ③ AddFilterMember(s) —— **顶部下拉里显示哪些项**，只管 UI 清单。
        /// </summary>
        private static void ApplySelectionFilter(SelectObjectBlock block)
        {
            // ClearFilter / ResetFilter 只清"允许选什么类型"，**不动下拉清单** ——
            // 实测（NX2606）清完再 AddFilter，下拉里的默认项（基准 / 曲线特征 / 模具基准 /
            // 测量对象 / 焊接对象 / 草图）又原样回来了。所以要先把默认项逐个删掉，
            // 再注册自己的下拉项（顺序见下面 ③④，不能反）。
            try { block.ClearFilter(); }
            catch (Exception ex)
            {
                Log("  ClearFilter 失败，退回 ResetFilter: " + ex.Message);
                try { block.ResetFilter(); }
                catch (Exception e2) { Log("  ResetFilter 也失败（忽略）: " + e2.Message); }
            }

            int ok = 0;
            foreach (SelectObjectBlock.FilterType ft in SelectionFilterTypes)
            {
                try { block.AddFilter(ft); ok++; }
                catch (Exception ex) { Log("  AddFilter(" + ft + ") 失败（忽略）: " + ex.Message); }
            }

            // 掩码写法：实体 / 片体 用 solid 类型 + 实体/片体子类型，面 / 边 / 特征各一个类型码
            // （特征这里只走掩码，不加 FilterType.Features —— 见上面 SelectionFilterTypes 的说明）
            int maskOk = 0;
            maskOk += TryAddMaskFilter(block, NXOpen.UF.UFConstants.UF_solid_type, 0,
                                       NXOpen.UF.UFConstants.UF_UI_SEL_FEATURE_SOLID_BODY);
            maskOk += TryAddMaskFilter(block, NXOpen.UF.UFConstants.UF_solid_type, 0,
                                       NXOpen.UF.UFConstants.UF_UI_SEL_FEATURE_SHEET_BODY);
            maskOk += TryAddMaskFilter(block, NXOpen.UF.UFConstants.UF_face_type, 0, 0);
            maskOk += TryAddMaskFilter(block, NXOpen.UF.UFConstants.UF_edge_type, 0, 0);
            maskOk += TryAddMaskFilter(block, NXOpen.UF.UFConstants.UF_feature_type, 0, 0);

            // ③ 先把 NX 默认挂上来的杂项（基准 / 曲线特征 / 模具基准 / 测量对象 / 焊接对象 / 草图…）
            //    扫掉。**必须排在注册自己下拉项之前**：RemoveFilterMember 是按内部
            //    (type, subtype, solid) 三元组移除的，清理过程中碰到同三元组的成员时，
            //    刚加进去的「片体」会被一起带走 —— 2026-11-13 实测症状就是下拉只剩「实体 / 面」。
            int removed = StripExtraFilterMembers(block);

            // ④ 最后注册自己的下拉项：无选择过滤器 / 实体 / 片体（对齐 NX 原生「编辑对象显示」）
            int memberOk = AddOwnFilterMembers(block);

            Log("  选择过滤：" + ok + "/" + SelectionFilterTypes.Length + " 枚举 + " + maskOk +
                "/5 掩码，清理额外下拉项 " + removed + " 个，注册下拉项 " +
                memberOk + "/" + SelectionFilterMembers.Length);
        }

        /// <summary>
        /// 注册顶部的「实体 / 片体」两项。必须在上面的清理之后调用，否则会被清理一起删掉。
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
        ///
        /// 说明：NX 的 FilterMember 有 600 多个枚举值，这里全量扫一遍逐个 RemoveFilterMember ——
        /// 没挂上去的值 NX 会直接忽略（个别会抛，包在 catch 里）。一次扫描约几十毫秒，
        /// 只在打开对话框时做一次，可以接受；反过来按名字猜"基准/草图"对应哪个枚举值太不可靠。
        /// </summary>
        private static int StripExtraFilterMembers(SelectObjectBlock block)
        {
            Array all;
            try { all = Enum.GetValues(typeof(NXOpen.Select.FilterMember)); }
            catch (Exception ex)
            {
                Log("  枚举 FilterMember 失败（忽略）: " + ex.Message);
                return 0;
            }

            HashSet<NXOpen.Select.FilterMember> keep =
                new HashSet<NXOpen.Select.FilterMember>(SelectionFilterMembers);

            int removed = 0, failed = 0;
            string firstFail = null;
            int t0 = Environment.TickCount;

            foreach (object o in all)
            {
                NXOpen.Select.FilterMember fm = (NXOpen.Select.FilterMember)o;
                if (keep.Contains(fm)) continue;                 // 自己注册的「实体 / 片体」留着
                if (fm.ToString() == "None") continue;           // 「无选择过滤器」留着

                try { block.RemoveFilterMember(fm); removed++; }
                catch (Exception ex)
                {
                    failed++;
                    if (firstFail == null) firstFail = fm + " → " + ex.Message;
                }
            }

            Log("  下拉项清理：尝试移除 " + removed + " 项 / 失败 " + failed +
                " 项，耗时 " + (Environment.TickCount - t0) + " ms" +
                (firstFail == null ? "" : "；首个失败 " + firstFail));
            return removed;
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

                ApplyDisplayModification(objs, -1, value);
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

        // 改色对话框存活期间的块引用与状态
        private static BlockDialog _colorDialog;
        private static SelectObjectBlock _colorSelBlock;
        private static LabelBlock _colorInfoBlock;
        private static int _colorTargetId = -1;        // 本次要应用的调色板编号
        private static string _colorTargetName = "";   // 颜色名（写进对话框说明）
        private static bool _colorShown;               // 对话框已显示（之前的 update 都算初始化）
        private static bool _colorApplying;            // 防 update 嵌套触发
        private static int _savedSelColor = -1;        // 预览期间临时改掉的"选择色"，关闭时还原
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
        /// 颜色按钮入口：先弹 NX 原生选择对话框，用户在图形区点对象（选中即预览改色），
        /// 确定保留 / 取消恢复原色。
        ///
        /// 取色仍是三级（顺序很有讲究，见 ResolveSlotColorId）：
        ///   ① colorId &gt; 0 且当前部件调色板里确实有这个号 → 直接用
        ///   ② colorName 非空 → 按名字查（custom 过调色板也能对上）
        ///   ③ 以上都不行 → 按 RGB 在调色板里找最近色（用缓存，只扫一次）
        /// 取色在弹窗**之前**做完，颜色不对当场报错，不会让用户白选一通。
        /// </summary>
        internal static void ApplyColorSlot(int colorId, string colorName, double[] rgb, string displayName)
        {
            UI ui = UI.GetUI();
            Log("=== 点击颜色按钮" + (string.IsNullOrEmpty(displayName) ? "" : "：" + displayName) + " ===");
            try
            {
                // 模态对话框正常不会并发，这条只是防重入
                if (_colorDialog != null) { Log("已有改色对话框在显示，忽略本次点击"); return; }

                Part workPart = Session.GetSession().Parts.Work;
                if (workPart == null)
                {
                    ui.NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Error, "请先打开一个部件。");
                    return;
                }

                int id = ResolveSlotColorId(workPart, colorId, colorName, rgb, displayName);
                if (id < 0) return;      // 报错已在里面弹过

                string dlx = FindDlx(ColorDlgFileName);
                Log("改色对话框 dlx = " + (dlx ?? "<未找到>"));
                if (dlx == null)
                {
                    ui.NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Error,
                        "找不到对话框定义 " + ColorDlgFileName + "。\r\n\r\n" +
                        "请重新安装 LYJ —— 该文件应位于安装目录的 application 下。");
                    return;
                }

                _colorTargetId = id;
                _colorTargetName = string.IsNullOrEmpty(displayName) ? "选定颜色" : displayName;

                // 预览期间把「选择色」临时指到目标色：否则选中对象被橙色高亮整片盖住，
                // 用户根本看不出改成了什么颜色（关闭对话框时在 finally 里还原）
                SetSelectionColorForPreview(workPart, id);

                BlockDialog dlg = CreateDialog(ui, ColorDlgFileName, dlx);
                dlg.AddInitializeHandler(OnColorInit);
                dlg.AddUpdateHandler(OnColorUpdate);
                dlg.AddDialogShownHandler(OnColorShown);
                dlg.AddOkHandler(OnColorOk);
                dlg.AddCancelHandler(OnColorCancel);
                _colorDialog = dlg;

                dlg.Launch();            // 模态：用户关闭后返回
            }
            catch (Exception ex)
            {
                ShowError(ui, ex);
            }
            finally
            {
                RestoreSelectionColor();
                _colorDialog = null;
                _colorSelBlock = null;
                _colorInfoBlock = null;
                _colorShown = false;
                _colorApplying = false;
                _colorOrigin.Clear();
                _colorTargetId = -1;
                _colorTargetName = "";
            }
        }

        private static void OnColorInit()
        {
            try
            {
                _colorSelBlock = _colorDialog.TopBlock.FindBlock("colorSelect") as SelectObjectBlock;
                _colorInfoBlock = _colorDialog.TopBlock.FindBlock("colorInfo") as LabelBlock;
                Log("  OnInit 取块: colorSelect=" + (_colorSelBlock != null) +
                    " colorInfo=" + (_colorInfoBlock != null));

                if (_colorInfoBlock != null)
                {
                    // 说明文字运行时才定得下来（颜色名来自配置），所以不写死在 .dlx 里
                    try
                    {
                        _colorInfoBlock.Label =
                            "目标颜色：" + _colorTargetName + "\r\n" +
                            "在图形区点选实体 / 片体 / 面或边，选中即预览" +
                            "（选实体时它的所有面会一起刷，包括之前单独改过色的面）；" +
                            "点「确定」保留，点「取消」恢复原色。";
                    }
                    catch (Exception ex) { Log("  设置说明文字失败（忽略）: " + ex.Message); }
                }

                if (_colorSelBlock != null)
                {
                    // 和透明化同样要放开过滤，否则图形区点什么都不上选
                    ApplySelectionFilter(_colorSelBlock);

                    // 打开对话框前已经在图形区选好的对象直接带进来，省一次重新选择
                    TaggedObject[] pre = GetPickedObjects();
                    if (pre.Length > 0) _colorSelBlock.SetSelectedObjects(ResolveForColor(pre));
                }
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
            }
        }

        /// <summary>对话框显示完成：之后的 update 才是用户操作。</summary>
        private static void OnColorShown()
        {
            _colorShown = true;
            // 带进来的预选对象在 shown 之前不会应用，这里补一次预览
            PreviewColor(false);
        }

        private static int OnColorUpdate(UIBlock block)
        {
            if (!_colorShown || _colorApplying) return 0;
            if (block == _colorSelBlock) PreviewColor(false);
            return 0;
        }

        private static int OnColorOk()
        {
            if (ReadColorSelection().Length == 0)
            {
                UI.GetUI().NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Warning,
                    "请先选择要改色的对象。\r\n\r\n" +
                    "可以在图形区直接点实体、片体、面，或在部件导航器里选" +
                    "「体」/生成它的特征（如 块(1)）。");
                return 1;       // 非 0 = 不关闭对话框，等用户选完再点确定
            }

            PreviewColor(false);    // 保险：确保当前选择都已上色
            return 0;
        }

        /// <summary>
        /// 取消：把预览过的对象逐个还原成打开对话框前的颜色。
        /// 这里**不用**撤销标记回滚（透明化用的那招）—— 预览是"边选边改"，
        /// 用 UndoToMark 会把期间所有显示修改一起撤掉，和逐个还原互相打架。
        /// </summary>
        private static int OnColorCancel()
        {
            RestoreOriginColors();
            return 0;
        }

        /// <summary>
        /// 预览期间把「选择色」临时设成目标色。
        ///
        /// NX 选中对象时是用「选择色」整片覆盖的（默认亮橙），改色预览就变成"点谁都是一片橙"，
        /// 看不出实际改成了什么颜色。选择色是部件级显示偏好（int 色号，可读写），
        /// 这里临时指向目标色 —— 高亮色和对象色一致，视觉上就等于没有高亮。
        /// 对话框关闭后由 <see cref="RestoreSelectionColor"/> 还原，不会污染用户设置。
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

        /// <summary>把当前选择刷成目标色；移出选择的对象还原原色。</summary>
        private static void PreviewColor(bool reportEmpty)
        {
            try
            {
                if (_colorSelBlock == null || _colorTargetId <= 0) return;

                TaggedObject[] picked = ReadColorSelection();
                DisplayableObject[] objs = picked.Length == 0
                    ? new DisplayableObject[0]
                    : ExpandBodiesWithFaces(ResolveForColor(picked));

                if (objs.Length == 0)
                {
                    RestoreOriginColors();      // 选择被清空 → 之前预览过的也退回去
                    if (reportEmpty)
                        UI.GetUI().NXMessageBox.Show("改颜色", NXMessageBox.DialogType.Warning,
                            "请先选择要改色的对象。");
                    return;
                }

                _colorApplying = true;

                HashSet<Tag> keep = new HashSet<Tag>();
                foreach (DisplayableObject o in objs) keep.Add(o.Tag);

                // ① 已经不在选择里、但预览时改过色的：还原
                List<Tag> drop = null;
                foreach (KeyValuePair<Tag, ColorOrigin> kv in _colorOrigin)
                {
                    if (keep.Contains(kv.Key)) continue;
                    if (drop == null) drop = new List<Tag>();
                    drop.Add(kv.Key);
                }
                if (drop != null)
                {
                    foreach (Tag t in drop)
                    {
                        ColorOrigin co;
                        if (!_colorOrigin.TryGetValue(t, out co)) continue;
                        _colorOrigin.Remove(t);
                        if (co.Obj == null || co.Color < 0) continue;
                        try { ApplyDisplayModification(new[] { co.Obj }, co.Color, -1); }
                        catch (Exception ex) { Log("  还原原色失败（忽略）: " + ex.Message); }
                    }
                }

                // ② 新进选择的对象先记原色，再统一刷目标色
                foreach (DisplayableObject o in objs)
                {
                    if (_colorOrigin.ContainsKey(o.Tag)) continue;
                    int orig = -1;
                    try { orig = o.Color; } catch { }
                    _colorOrigin[o.Tag] = new ColorOrigin { Obj = o, Color = orig };
                }

                ApplyDisplayModification(objs, _colorTargetId, -1);
                Log("  预览改色：选 " + picked.Length + " 个 → 实际刷 " + objs.Length +
                    " 个对象（体+面）→ 色号 " + _colorTargetId);
            }
            catch (Exception ex)
            {
                ShowError(UI.GetUI(), ex);
            }
            finally
            {
                _colorApplying = false;
            }
        }

        /// <summary>把记录里的对象全部还原成原色（取消 / 选择清空时用）。</summary>
        private static void RestoreOriginColors()
        {
            if (_colorOrigin.Count == 0) return;

            bool wasApplying = _colorApplying;
            _colorApplying = true;
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
                    try { ApplyDisplayModification(kv.Value.ToArray(), kv.Key, -1); }
                    catch (Exception ex) { Log("  还原原色失败（忽略）: " + ex.Message); }
                }
                Log("  已还原 " + _colorOrigin.Count + " 个对象的原色（" + byColor.Count + " 组）");
            }
            finally
            {
                _colorOrigin.Clear();
                _colorApplying = wasApplying;
            }
        }

        private static TaggedObject[] ReadColorSelection()
        {
            if (_colorSelBlock == null) return new TaggedObject[0];
            try
            {
                TaggedObject[] list = _colorSelBlock.GetSelectedObjects();
                return list ?? new TaggedObject[0];
            }
            catch { return new TaggedObject[0]; }
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
        /// 统一的显示修改：colorId &lt; 0 表示不改颜色，translucency &lt; 0 表示不改透明度。
        /// </summary>
        private static void ApplyDisplayModification(DisplayableObject[] objs, int colorId, int translucency)
        {
            DisplayModification dm = null;
            try
            {
                // DisplayModification 没有公共构造函数，必须经 DisplayManager 创建
                dm = Session.GetSession().DisplayManager.NewDisplayModification();
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
        /// 改色用：面 / 边 / 体本身就是显示对象，原样改（NX 支持单给某个面上色）；
        /// 只有特征要换算成它生成的体。
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
