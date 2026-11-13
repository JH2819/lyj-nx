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

namespace LYJ
{
    /// <summary>
    /// 部件显示相关的两组功能：
    ///
    /// 1) 「透明化」—— 点按钮弹出 NX 原生对话框（Block Styler）：
    ///    上面选部件，下面是透明度滑条，拖动即时预览。
    ///    对话框定义在 application\LYJ_Transparency.dlx（由 工具脚本\gen_transparency_dialog.py 生成）。
    ///
    /// 2) 「部件颜色」—— 五个固定颜色，一键把当前选中对象的颜色改掉。
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
        private static DoubleBlock _transBlock;
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
                _transBlock = _dialog.TopBlock.FindBlock("transValue") as DoubleBlock;
                Log("  OnInit 取块: partSelect=" + (_partBlock != null) +
                    " transValue=" + (_transBlock != null));

                if (_transBlock != null)
                {
                    _transBlock.MinimumValue = 0;
                    _transBlock.MaximumValue = 100;
                    _transBlock.Increment = 1;
                    if (_transBlock.Value <= 0) _transBlock.Value = 50;
                }

                // 放开选择过滤：图形区里体、片体、面、边、特征都能直接点
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
        // 实体 / 片体 / 面 / 边 / 特征。这五类覆盖了"能改透明度的东西"，
        // 又不至于把基准、标注、曲线之类也放进来当噪声。
        private static readonly SelectObjectBlock.FilterType[] SelectionFilterTypes =
        {
            SelectObjectBlock.FilterType.SolidBodies,
            SelectObjectBlock.FilterType.SheetBodies,
            SelectObjectBlock.FilterType.Faces,
            SelectObjectBlock.FilterType.Edges,
            SelectObjectBlock.FilterType.Features,
        };

        /// <summary>
        /// 给选择块设过滤，让图形区里"点哪都能选"。
        ///
        /// .dlx 里 DetailedFilter 是空的，NX 会把它当成"一个类型都不允许"，
        /// 结果图形区里点什么都不上选（只有打开对话框前已经选中的对象能带进来）。
        /// 所以必须在 Initialize 里显式设一遍：实体 / 片体 / 面 / 边 / 特征。
        /// </summary>
        private static void ApplySelectionFilter(SelectObjectBlock block)
        {
            // 先清掉 .dlx 带进来的空过滤；这一步失败也不影响后面加过滤，所以分开 try
            try { block.ResetFilter(); }
            catch (Exception ex) { Log("  ResetFilter 失败（忽略）: " + ex.Message); }

            int ok = 0;
            foreach (SelectObjectBlock.FilterType ft in SelectionFilterTypes)
            {
                try { block.AddFilter(ft); ok++; }
                catch (Exception ex) { Log("  AddFilter(" + ft + ") 失败（忽略）: " + ex.Message); }
            }
            Log("  选择过滤已放开 " + ok + "/" + SelectionFilterTypes.Length + " 类：实体/片体/面/边/特征");
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

                int value = (int)Math.Round(_transBlock.Value);
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

        /// <summary>
        /// 按「配置里的颜色槽」改选中对象的颜色，三级取色（顺序很有讲究，见下）：
        ///   ① colorId &gt; 0 且当前部件调色板里确实有这个号 → 直接用
        ///   ② colorName 非空 → 按名字查（custom 过调色板也能对上）
        ///   ③ 以上都不行 → 按 RGB 在调色板里找最近色（用缓存，只扫一次）
        /// </summary>
        internal static void ApplyColorSlot(int colorId, string colorName, double[] rgb, string displayName)
        {
            UI ui = UI.GetUI();
            try
            {
                Part workPart = Session.GetSession().Parts.Work;
                if (workPart == null)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Error, "请先打开一个部件。");
                    return;
                }

                DisplayableObject[] objs = ResolveForColor(GetPickedObjects());
                if (objs.Length == 0)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Warning,
                        "没有读到可改色对象。\r\n\r\n" +
                        "请在图形窗口里点选实体 / 片体 / 面，或在部件导航器里选中" +
                        "「体」或生成它的特征（如 块(1)），再点颜色按钮。");
                    return;
                }

                int id = -1;
                if (colorId > 0)
                {
                    try { if (workPart.Colors.Find(colorId) != null) id = colorId; }
                    catch { }
                }
                if (id < 0) id = FindColorIdByName(workPart, colorName);
                if (id < 0) id = FindColorByRgb(workPart, rgb);
                if (id < 0)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Error,
                        "当前部件的调色板里找不到这个颜色" +
                        (string.IsNullOrEmpty(displayName) ? "" : "「" + displayName + "」") +
                        "。\r\n\r\n可以点「显示」组最后的配置按钮换一个颜色。");
                    return;
                }

                ApplyDisplayModification(objs, id, -1);
            }
            catch (Exception ex)
            {
                ShowError(ui, ex);
            }
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

        /// <summary>
        /// 按「调色板颜色名 + 色值」改色（旧的五个固定按钮走这条，保留给菜单项用）。
        /// </summary>
        internal static void ApplyColorToSelection(string colorName, int fallbackId,
                                                  string displayName, double[] rgb = null)
        {
            UI ui = UI.GetUI();
            try
            {
                Part workPart = Session.GetSession().Parts.Work;
                if (workPart == null)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Error,
                        "请先打开一个部件。");
                    return;
                }

                DisplayableObject[] objs = ResolveForColor(GetPickedObjects());
                if (objs.Length == 0)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Warning,
                        "没有读到可改色对象。\r\n\r\n" +
                        "请在图形窗口里点选实体 / 片体 / 面，或在部件导航器里选中" +
                        "「体」或生成它的特征（如 块(1)），再点颜色按钮。");
                    return;
                }

                int colorId = ResolveColorId(workPart, colorName, fallbackId, rgb);
                if (colorId < 0)
                {
                    ui.NXMessageBox.Show("部件颜色", NXMessageBox.DialogType.Error,
                        "当前部件的调色板里找不到颜色「" + colorName + "」。\r\n\r\n" +
                        "可以改用 NX 自带的 编辑 → 对象显示 手动指定颜色。");
                    return;
                }

                ApplyDisplayModification(objs, colorId, -1);
            }
            catch (Exception ex)
            {
                ShowError(ui, ex);
            }
        }

        /// <summary>
        /// 三级取色：① 按调色板颜色名；② 按 RGB 找色值相同的颜色（用户改过颜色名时也能对上）；
        /// ③ 兜底编号。
        /// </summary>
        private static int ResolveColorId(Part workPart, string colorName, int fallbackId, double[] rgb)
        {
            try
            {
                NXColor c = workPart.Colors.Find(colorName);
                if (c != null) return c.Id;
            }
            catch
            {
                // 名字查不到往下走
            }

            if (rgb != null)
            {
                int id = FindColorByRgb(workPart, rgb);
                if (id > 0) return id;
            }

            return fallbackId;
        }

        /// <summary>在调色板里找色值与 rgb 最接近的颜色编号，误差超过 2% 视为没找到。</summary>
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
