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
// ==============================================================================
//  加工助手悬浮窗（v2.0：可配置行 + 排序 + 余量/公差）
//
//  实现要点（2026-11-05 方案定稿）：
//    1. 无边框置顶 WinForm，无模式 Show() —— 窗体在 NX 主线程（菜单回调）上创建，
//       之后所有点击 / Timer 事件都由同一条线程的消息泵分发，直接调 NXOpen 线程安全；
//       绝不在窗体里另开线程调 NXOpen。
//    2. Z 序：OnLoad 里把悬浮窗挂为 NX 主窗口的 owned window（GWL_HWNDPARENT）——
//       永远在 NX 主窗口之上、低于 NX 弹出的对话框；编辑态（输入框/刀具列表）临时
//       TopMost，编辑结束交还。背景/标题栏/输入框透明用 TransparencyKey 色键。
//    3. 显示模式为纯文本行：点击文本进入编辑态（数值行变输入框、刀具行弹出列表），
//       回车确认；Esc 或点击其他地方（窗体 Deactivate）均取消，不写参数。
//    4. 行定义走注册表 RowDefs：工序/刀具/转速/进给/余量(部件)/余量(底面)/内公差/
//       外公差。显示哪几行、按什么顺序，由配置（mach_assistant.cfg 的 order/hidden）
//       决定，与配置窗口"显示内容"列表完全同步（勾选即显示、取消即隐藏）。
//    5. 余量/公差走 UF Param（e2925 验证过的路径）：
//         UF_PARAM_STOCK_PART / UF_PARAM_STOCK_FLOOR  —— AskDoubleValue / SetDoubleValue
//         UF_PARAM_TOL_IN_OUT（二维：内公差/外公差）   —— Ask2dValue   / Set2dValue
//       部分工序类型不支持这些参数，读取失败显示空白，写前必须校验。
//    6. 刀具列表用窗体内的 ListBox（非 ComboBox）——ComboBox 弹层是独立窗口，
//       会干扰进程 MainWindowHandle 导致窗口移位（v1.3 踩坑）。
//    7. 200ms Timer 轮询选中对象实时回填；每 1s 轮询机床刀具视图刷新刀具清单 +
//       回读所有数值行（ToArray 全量物化 + 视图树多策略并集）。
//    8. 修改参数用 UndoMark 包裹，可一步撤销；换刀用 CAMSetup.MoveObjects，
//       转速/进给用 CAMSetup.CreateFeedsBuilder。
//    9. 窗口停靠在图形区（NX 主窗口最大子窗口）左上角，不可拖动，锚点句柄缓存。
//   10. 标题栏左上角齿轮打开配置窗口：字体、字号、显示内容（勾选+上移/下移排序），
//       保存到 %APPDATA%\LYJ\mach_assistant.cfg，启动时自动加载。
//   11. 显示部件关闭（Parts.Display == null）时自动关闭悬浮窗，避免退出 NX 崩溃。
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NXOpen;

namespace LYJ
{
    /// <summary>
    /// 悬浮窗管理器：静态字段保住窗体引用防止 GC 回收，菜单按钮做显示/隐藏开关。
    /// </summary>
    internal static class MachAssistant
    {
        internal static MachAssistantForm Form;

        public static void Toggle()
        {
            if (Form == null || Form.IsDisposed)
            {
                Form = new MachAssistantForm();
                Form.Show();                    // 无模式，与 NX 并存
            }
            else
            {
                Form.Close();
            }
        }
    }

    // ------------------------------------------------------------------------- 配置
    /// <summary>
    /// 悬浮窗用户配置：字体/字号 + 行顺序（order）+ 隐藏行（hidden），
    /// 存 %APPDATA%\LYJ\mach_assistant.cfg。
    /// 行顺序与勾选状态由配置窗口维护，悬浮窗与配置窗口共用这一份数据（同步）。
    /// </summary>
    internal static class MachAssistantConfig
    {
        public static string FontName = "Microsoft YaHei UI";
        public static float FontSize = 9f;

        /// <summary>加粗：有些中文字体（仿宋、宋体等）细笔画在深色模型上看不清，加粗后清楚很多。</summary>
        public static bool Bold = false;

        /// <summary>全部行的显示名（与 RowDefs 的 Id 对应，悬浮窗与配置窗口共用）。
        /// 顺序按常用加工助手的参数表排（转速、进给在前）。</summary>
        public static readonly string[] AllRows =
            { "group", "op", "mcs", "optype", "tool", "toolno", "realtool", "tooldia", "toolcorner",
              "tooltype", "holder", "toollen", "toollflen", "tooldesc", "taperang", "tipang",
              "holderlib", "holderoff", "minlen",
              // "tiptype",   ← 刀尖类型行临时移除（旧版钻孔的类型无任何可读参数，6 种 CLSF 格式 + 325 项
              //                参数快照全量对比已证实）。找到数据源后把 id 加回本数组与 RowNames 同位置即可。
              "tipdepth", "tipstep",
              "tolinner", "tolouter", "partstock", "floorstock",
              "cutmode", "cutdir", "cutdir2", "levelmode", "sideflooreq",
              "spindle", "feed", "stepover", "depthpercut", "time" };

        public static readonly string[] RowNames =
            { "程序组", "刀路名称", "刀路坐标", "刀路类型", "刀具名称", "刀具编号", "真实刀名", "刀具直径", "刀具圆角",
              "刀具类型", "刀头名称", "刀具长度", "刀具刃长", "刀具描述", "刀具锥角", "刀具尖角",
              "夹持库号", "刀头偏置", "最短刀长",
              "刀尖深度", "刀尖步距",
              "内公差", "外公差", "侧面余量", "底面余量",
              "切削模式", "切削方向", "刀路方向", "层到层", "侧底一致",
              "转速", "进给", "行距", "下刀量", "加工时间" };

        /// <summary>默认不显示的行（B 组新增项，配置里可手动勾开）。
        /// 注意：行数组与 RowNames 必须一一对应；tiptype（刀尖类型）已临时移除，恢复时两处同时加回。</summary>
        public static readonly string[] DefaultHiddenRows =
            { "optype", "toollen", "toollflen", "tooldesc", "taperang", "tipang",
              "holderlib", "holderoff", "minlen",
              "cutmode", "cutdir", "cutdir2", "levelmode", "sideflooreq" };

        /// <summary>计算慢的行（配置窗口里在这些行名后面标“（计算慢）”，提示用户斟酌是否常开）。</summary>
        public static readonly System.Collections.Generic.HashSet<string> SlowRows =
            new System.Collections.Generic.HashSet<string> { "time" };

        /// <summary>行定义版本：新增/重排参数后 +1，老配置的行顺序会自动重置为新默认顺序
        /// （隐藏项与颜色保留）。6：移除 tiptype（刀尖类型）行。</summary>
        public const int RowSetVersion = 6;

        /// <summary>行显示顺序（含未勾选的，顺序即配置窗口列表顺序）。</summary>
        public static List<string> RowOrder = new List<string>(AllRows);

        /// <summary>被取消勾选（不显示）的行。</summary>
        public static List<string> HiddenRows = new List<string>();

        /// <summary>每行自定义颜色（id -> 颜色）；不在表内 = 用默认色。</summary>
        public static Dictionary<string, Color> RowColors = new Dictionary<string, Color>();

        /// <summary>
        /// 停靠时相对图形区顶边额外让出的像素（96 DPI 基准，实际按 DPI 缩放）。
        /// **0 = 自动**（推荐）：先看能不能抓到屏幕上的顶层横条（工具条 / 文档标签栏），
        /// 抓到就精确让到它下沿；抓不到（标签栏是 NX 自绘的，多数机器如此）就按
        /// <see cref="AutoDockGap"/> 让一档 —— 因为 NX 会把“文档标签栏”叠画在图形区最
        /// 上面 20~30px 里，不让开会把悬浮窗的标题（加工助手 / 模板 / 加工参数）压到
        /// 部件名标签上，看起来像"挡住了工作区标题"。
        /// 想更贴顶就填一个小数字（例如 dock_gap=6），配置写在
        /// %APPDATA%\LYJ\mach_assistant.cfg。
        /// </summary>
        public static int DockGap = 0;

        /// <summary>
        /// dock_gap=0（自动）且抓不到标签栏时的兜底让位，96 DPI 基准。
        /// 取 24：实测 NX 2606 的文档标签栏约 30px 高、叠进图形区上沿约 21px，
        /// 让 24 之后悬浮窗标题正好落在标签栏下沿再空出十几个像素，既不压字也不显空。
        /// </summary>
        public const int AutoDockGap = 24;

        public static bool IsHidden(string row) { return HiddenRows.Contains(row); }

        /// <summary>行文字默认色（未单独配置颜色时），#009688 青绿。</summary>
        public static readonly Color DefaultRowColor = Color.FromArgb(0x00, 0x96, 0x88);

        public static string NameOf(string row)
        {
            int i = Array.IndexOf(AllRows, row);
            return i >= 0 ? RowNames[i] : row;
        }

        private static string CfgPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LYJ", "mach_assistant.cfg");
            }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(CfgPath))
                {
                    // 全新安装：B 组扩展行默认不显示（配置窗口可勾开）
                    HiddenRows = new List<string>(DefaultHiddenRows);
                    return;
                }
                int rowset = 0;
                foreach (string line in File.ReadAllLines(CfgPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "font": if (v.Length > 0) FontName = v; break;
                        case "size": { float f; if (float.TryParse(v, out f) && f >= 7 && f <= 24) FontSize = f; break; }
                        case "bold": Bold = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); break;
                        case "dock_gap":
                            {
                                int g;
                                if (int.TryParse(v, out g) && g >= 0 && g <= 200) DockGap = g;
                                break;
                            }
                        case "rowset": int.TryParse(v, out rowset); break;
                        case "order":
                            List<string> order = new List<string>();
                            foreach (string p in v.Split(','))
                            {
                                string id = p.Trim();
                                if (Array.IndexOf(AllRows, id) >= 0 && !order.Contains(id))
                                    order.Add(id);
                            }
                            foreach (string id in AllRows)              // 新增行补到末尾
                                if (!order.Contains(id)) order.Add(id);
                            RowOrder = order;
                            break;
                        case "hidden":
                            HiddenRows = new List<string>();
                            foreach (string p in v.Split(','))
                            {
                                string id = p.Trim();
                                if (Array.IndexOf(AllRows, id) >= 0 && !HiddenRows.Contains(id))
                                    HiddenRows.Add(id);
                            }
                            break;
                        default:
                            if (k.StartsWith("color_"))
                            {
                                string cid = k.Substring(6);
                                Color c;
                                if (Array.IndexOf(AllRows, cid) >= 0 &&
                                    TryParseColor(v, out c))
                                    RowColors[cid] = c;
                            }
                            break;
                        // ---- 旧版 cfg（v1.5 及以前）迁移 ----
                        case "show_op": SetHidden("op", v != "1"); break;
                        case "show_tool": SetHidden("tool", v != "1"); break;
                        case "show_spindle": SetHidden("spindle", v != "1"); break;
                        case "show_feed": SetHidden("feed", v != "1"); break;
                    }
                }
                // 参数表结构变了（新增/重排）：行顺序回到新默认，隐藏项与颜色照旧保留
                if (rowset < RowSetVersion)
                {
                    RowOrder = new List<string>(AllRows);
                    // 老配置升级：新增行默认隐藏（它们不可能出现在老 hidden 列表里，
                    // 补进来只影响首装升级这一次，用户在配置窗口勾开后会随 rowset=新版保存）
                    foreach (string id in DefaultHiddenRows)
                        if (!HiddenRows.Contains(id)) HiddenRows.Add(id);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CfgPath));
                List<string> lines = new List<string>();
                lines.Add("font=" + FontName);
                lines.Add("size=" + FontSize.ToString("0.#"));
                lines.Add("bold=" + (Bold ? "1" : "0"));
                lines.Add("rowset=" + RowSetVersion);
                lines.Add("order=" + string.Join(",", RowOrder.ToArray()));
                lines.Add("hidden=" + string.Join(",", HiddenRows.ToArray()));
                // 停靠下移量：0 = 自动（躲开 NX 的文档标签栏，推荐）；正数 = 手工指定像素。
                lines.Add("# dock_gap：0=自动（默认，躲开 NX 的文档标签栏）；填像素数可手动微调（如 6 更贴顶、36 更靠下）");
                lines.Add("dock_gap=" + DockGap);
                foreach (string id in AllRows)
                    if (RowColors.ContainsKey(id))
                        lines.Add("color_" + id + "=" +
                            string.Format("{0:X2}{1:X2}{2:X2}",
                                RowColors[id].R, RowColors[id].G, RowColors[id].B));
                File.WriteAllLines(CfgPath, lines.ToArray());
            }
            catch { }
        }

        /// <summary>解析 RRGGBB（可带 #）十六进制颜色。</summary>
        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.Black;
            if (s == null) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v))
                return false;
            c = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }

        private static void SetHidden(string row, bool hidden)
        {
            if (hidden && !HiddenRows.Contains(row)) HiddenRows.Add(row);
            else if (!hidden) HiddenRows.Remove(row);
        }
    }

    // ------------------------------------------------------------------------- 配置窗口
    /// <summary>配置对话框：字体、字号、显示内容（勾选+排序）、行颜色。</summary>
    internal class MachAssistantConfigDialog : Form
    {
        private ListView _listRows;                      // 用 ListView 而非 CheckedListBox：后者不支持多选
        private List<string> _ids = new List<string>();  // 与列表项同序的行 Id
        private Dictionary<string, Color> _colors;       // 行颜色编辑副本
        private Panel _pnlSwatch;
        private Label _lblColor;

        // ---- 顶部页签条 + 下方两个配置页 ----
        private const int TabH = 46;                     // 顶部页签条高度
        private const int FootH = 48;                    // 底部按钮条高度
        private Button _sbTpl;                           // 页签：模板配置
        private Button _sbParam;                         // 页签：加工参数配置
        private Panel _pageTpl;                          // 模板配置页
        private Panel _pageParam;                        // 加工参数配置页（原有内容整体搬进来）
        private ListBox _lstTpl;                         // 模板页：模板路径清单（√ 存在 / × 找不到）
        private List<string> _tplPaths = new List<string>();  // 模板页：编辑中的路径副本（与列表项同序）
        private Label _lblTplHint;                       // 模板页：操作反馈（新增/编辑/删除后提示）
        private DateTime _tplCfgWriteAt = TemplateCfgWriteTime();  // 配置文件上次写入时间（检测外部编辑）

        /// <summary>本次会话里改过模板清单（配置窗口关闭后由宿主读取，触发模板树重载）。</summary>
        public bool TemplatesChanged { get; set; }

        public MachAssistantConfigDialog()
        {
            _colors = new Dictionary<string, Color>(MachAssistantConfig.RowColors);
            Text = "加工助手 - 配置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(574, 430);        // 原 478x356，整体放大 ~20%（宽 +96 / 高 +74）

            BuildTabBar();
            BuildTemplatePage();

            // 关闭配置窗口时：cam_template.txt 若被外部编辑器改过 → 让宿主重载模板树
            FormClosing += (s, e) =>
            {
                if (TemplateCfgWriteTime() != _tplCfgWriteAt) TemplatesChanged = true;
            };

            // 加工参数配置页：下面的控件都挂在它里面（坐标按新窗口重排）
            _pageParam = new Panel
            {
                Bounds = new Rectangle(0, TabH, ClientSize.Width, ClientSize.Height - TabH - FootH),
                Visible = false
            };
            Controls.Add(_pageParam);

            int y = 16;
            _pageParam.Controls.Add(MakeLabel("字体", 15, y + 3, 56));
            ComboBox cmbFont = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(80, y, 300, 24)
            };
            foreach (FontFamily fam in FontFamily.Families) cmbFont.Items.Add(fam.Name);
            int fi = cmbFont.Items.IndexOf(MachAssistantConfig.FontName);
            if (fi >= 0) cmbFont.SelectedIndex = fi;
            _pageParam.Controls.Add(cmbFont);

            // 加粗：仿宋/宋体这类细笔画字体在模型上看不清，加粗一档明显改善
            CheckBox chkBold = new CheckBox
            {
                Text = "加粗",
                Bounds = new Rectangle(392, y + 4, 72, 22),
                Checked = MachAssistantConfig.Bold
            };
            _pageParam.Controls.Add(chkBold);
            y += 36;

            _pageParam.Controls.Add(MakeLabel("大小", 15, y + 3, 56));
            ComboBox cmbSize = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(80, y, 300, 24)
            };
            float[] sizes = new float[] { 8f, 9f, 10f, 11f, 12f, 14f, 16f, 18f, 20f };
            int si = 1;
            for (int i = 0; i < sizes.Length; i++)
            {
                cmbSize.Items.Add(sizes[i].ToString("0.#"));
                if (sizes[i] <= MachAssistantConfig.FontSize) si = i;
            }
            cmbSize.SelectedIndex = si;
            _pageParam.Controls.Add(cmbSize);
            y += 40;

            // ---- 显示内容：顺序即列表顺序；勾选 = 显示（与悬浮窗同步）----
            // CheckedListBox 不支持多选（SelectionMode 只能 One/None），故用 ListView + 复选框：
            // 点文字只选中，点前面的框才切换显示。
            _pageParam.Controls.Add(MakeLabel("显示内容", 15, y + 3, 56));
            _listRows = new ListView
            {
                View = System.Windows.Forms.View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                MultiSelect = true,             // Ctrl/Shift 多选，批量设色/整块排序
                CheckBoxes = true,
                HideSelection = false,
                Bounds = new Rectangle(80, y, 340, 200)
            };
            _listRows.Columns.Add("", 310, HorizontalAlignment.Left);
            foreach (string id in MachAssistantConfig.RowOrder)
            {
                _ids.Add(id);                               // 与列表项同序的 id 表（不靠文本反查）
                // 计算慢的行在名字后面标注，提示用户斟酌是否常开（如加工时间要按刀轨实时算）
                ListViewItem it = new ListViewItem(MachAssistantConfig.NameOf(id) +
                    (MachAssistantConfig.SlowRows.Contains(id) ? "（计算慢）" : ""));
                it.Checked = !MachAssistantConfig.IsHidden(id);
                it.Tag = id;
                _listRows.Items.Add(it);
            }
            _listRows.SelectedIndexChanged += (s, e) => RefreshColorState();
            _pageParam.Controls.Add(_listRows);

            Button up = new Button { Text = "上移", Bounds = new Rectangle(436, y + 12, 76, 28) };
            up.Click += (s, e) => MoveItem(-1);
            Button down = new Button { Text = "下移", Bounds = new Rectangle(436, y + 48, 76, 28) };
            down.Click += (s, e) => MoveItem(1);
            Button color = new Button { Text = "颜色...", Bounds = new Rectangle(436, y + 84, 76, 28) };
            color.Click += (s, e) => EditColor();
            Button defc = new Button { Text = "默认色", Bounds = new Rectangle(436, y + 120, 76, 28) };
            defc.Click += (s, e) => ClearColor();
            _pageParam.Controls.Add(up);
            _pageParam.Controls.Add(down);
            _pageParam.Controls.Add(color);
            _pageParam.Controls.Add(defc);

            // 选中行的颜色预览（色块 + 说明）：说明文字放在列表下方，色块对齐按钮列底部
            _pnlSwatch = new Panel
            {
                Bounds = new Rectangle(436, y + 208, 20, 16),
                BorderStyle = BorderStyle.FixedSingle
            };
            _lblColor = new Label
            {
                Bounds = new Rectangle(80, y + 206, 340, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _pageParam.Controls.Add(_pnlSwatch);
            _pageParam.Controls.Add(_lblColor);

            // 确定/取消放到窗体底部的公共按钮条（两个模块页共用）
            int footY = ClientSize.Height - FootH + 10;
            Button ok = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Bounds = new Rectangle(185, footY, 96, 28)
            };
            Button cancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Bounds = new Rectangle(293, footY, 96, 28)
            };
            ok.Click += (s, e) =>
            {
                if (cmbFont.SelectedIndex >= 0 && cmbFont.Text.Length > 0)
                    MachAssistantConfig.FontName = cmbFont.Text;
                float sz;
                if (float.TryParse(cmbSize.Text, out sz) && sz >= 7 && sz <= 24)
                    MachAssistantConfig.FontSize = sz;
                MachAssistantConfig.Bold = chkBold.Checked;
                // 列表顺序 -> 行顺序；勾选状态 -> 隐藏表；颜色副本 -> 行颜色（与悬浮窗同步）
                MachAssistantConfig.RowOrder = new List<string>(_ids);
                MachAssistantConfig.HiddenRows = new List<string>();
                MachAssistantConfig.RowColors = new Dictionary<string, Color>(_colors);
                for (int i = 0; i < _ids.Count && i < _listRows.Items.Count; i++)
                {
                    if (!_listRows.Items[i].Checked)
                        MachAssistantConfig.HiddenRows.Add(_ids[i]);
                }
                MachAssistantConfig.Save();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            RefreshColorState();
            ShowModule(1);              // 默认停在“加工参数配置”
        }

        // ---------------------------------------------------------- 顶部页签 + 两个模块页
        private void BuildTabBar()
        {
            Panel bar = new Panel
            {
                Bounds = new Rectangle(0, 0, ClientSize.Width, TabH),
                BackColor = Color.FromArgb(246, 246, 248)
            };
            bar.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(224, 224, 228)))
                    e.Graphics.DrawLine(pen, 0, TabH - 1, ((Control)s).Width, TabH - 1);
            };
            Controls.Add(bar);

            _sbTpl = MakeTabButton("模板配置", 14, 108);
            _sbParam = MakeTabButton("加工参数配置", 128, 132);
            _sbTpl.Click += (s, e) => ShowModule(0);
            _sbParam.Click += (s, e) => ShowModule(1);
            bar.Controls.Add(_sbTpl);
            bar.Controls.Add(_sbParam);
        }

        private Button MakeTabButton(string text, int left, int width)
        {
            Button b = new Button
            {
                Text = text,
                Bounds = new Rectangle(left, 8, width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(246, 246, 248),
                ForeColor = Color.FromArgb(60, 60, 64),
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 240, 238);
            return b;
        }

        /// <summary>切换页签：0=模板配置，1=加工参数配置。</summary>
        private void ShowModule(int index)
        {
            if (_pageTpl == null || _pageParam == null) return;
            _pageTpl.Visible = index == 0;
            _pageParam.Visible = index == 1;
            if (index == 0) _pageTpl.BringToFront(); else _pageParam.BringToFront();
            StyleSideButton(_sbTpl, index == 0);
            StyleSideButton(_sbParam, index == 1);
            if (index == 0) RefreshTemplateList();      // 每次进模板页都重读一次配置
        }

        private static void StyleSideButton(Button b, bool selected)
        {
            if (b == null) return;
            b.BackColor = selected ? Color.FromArgb(0, 150, 136) : Color.FromArgb(246, 246, 248);
            b.ForeColor = selected ? Color.White : Color.FromArgb(60, 60, 64);
        }

        /// <summary>模板配置页：模板清单 + 新增/编辑/删除/打开配置文件按钮 + 操作说明。</summary>
        private void BuildTemplatePage()
        {
            _pageTpl = new Panel
            {
                Bounds = new Rectangle(0, TabH, ClientSize.Width, ClientSize.Height - TabH - FootH),
                Visible = false
            };
            Controls.Add(_pageTpl);

            Label title = new Label
            {
                Text = "加工模板",
                Bounds = new Rectangle(14, 10, 340, 22),
                Font = new Font(Font, FontStyle.Bold)
            };
            _pageTpl.Controls.Add(title);

            _lstTpl = new ListBox
            {
                Bounds = new Rectangle(14, 38, 424, 182),
                IntegralHeight = false,
                HorizontalScrollbar = true
            };
            _pageTpl.Controls.Add(_lstTpl);

            Button add = new Button { Text = "新增...", Bounds = new Rectangle(452, 38, 108, 28) };
            add.Click += (s, e) => AddTemplates();
            Button edit = new Button { Text = "编辑...", Bounds = new Rectangle(452, 74, 108, 28) };
            edit.Click += (s, e) => EditTemplate();
            Button del = new Button { Text = "删除", Bounds = new Rectangle(452, 110, 108, 28) };
            del.Click += (s, e) => DeleteTemplate();
            Button open = new Button { Text = "打开配置文件", Bounds = new Rectangle(452, 146, 108, 28) };
            open.Click += (s, e) => OpenTemplateCfg();
            _pageTpl.Controls.Add(add);
            _pageTpl.Controls.Add(edit);
            _pageTpl.Controls.Add(del);
            _pageTpl.Controls.Add(open);

            _lblTplHint = new Label
            {
                Bounds = new Rectangle(14, 230, 546, 18),
                ForeColor = Color.FromArgb(0, 130, 118)
            };
            _pageTpl.Controls.Add(_lblTplHint);

            Label help = new Label
            {
                Text = "操作说明：\r\n" +
                       "· 模板就是带加工策略的 prt 文件，列表顺序即模板页显示顺序；\r\n" +
                       "· 新增可选多个模板，编辑/删除前先在列表中选中一项；\r\n" +
                       "· 批量添加可直接“打开配置文件”，一行一个路径粘贴保存；\r\n" +
                       "· 新模板会自动注册到 NX，重新打开模板页即生效。",
                Bounds = new Rectangle(14, 256, 546, 76),
                ForeColor = Color.FromArgb(90, 90, 96)
            };
            _pageTpl.Controls.Add(help);
        }

        /// <summary>cam_template.txt 的最后写入时间（检测外部编辑用）。</summary>
        private static DateTime TemplateCfgWriteTime()
        {
            try { return File.GetLastWriteTime(LYJApp.TemplateCfgPath); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>打开 cam_template.txt（系统默认编辑器）：方便一次性批量粘贴/调整多个模板路径。
        /// 打开前先从文件重读清单（外部改动能同步进来，避免之后的新增/删除覆盖外部修改）；
        /// 关闭配置窗口时检测到文件被外部改过 → 标记 TemplatesChanged，宿主重载模板树。</summary>
        private void OpenTemplateCfg()
        {
            string path;
            try { path = LYJApp.TemplateCfgPath; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "获取配置文件路径失败：" + ex.Message, "加工助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DateTime w = TemplateCfgWriteTime();
            if (w != _tplCfgWriteAt)          // 外部改过 → 同步进清单
            {
                _tplCfgWriteAt = w;
                RefreshTemplateList();
                TemplatesChanged = true;
                _lblTplHint.Text = "检测到配置文件被外部修改，清单已重新读取。";
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
                _lblTplHint.Text = "已在系统默认编辑器打开，改完保存，关闭本窗口后模板树自动重载。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开配置文件：\r\n" + path + "\r\n\r\n" + ex.Message,
                    "加工助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>重读 cam_template.txt 的原始配置（不过滤/不回退），列出模板（√ 存在 / × 找不到）。</summary>
        private void RefreshTemplateList()
        {
            if (_lstTpl == null) return;
            try { _tplPaths = LYJApp.GetTemplatePathsRaw(); }
            catch { _tplPaths = new List<string>(); }

            _lstTpl.BeginUpdate();
            _lstTpl.Items.Clear();
            foreach (string path in _tplPaths)
            {
                bool exists = false;
                try { exists = File.Exists(path); } catch { }
                _lstTpl.Items.Add((exists ? "√ " : "× ") + path + (exists ? "" : "   （文件不存在）"));
            }
            if (_lstTpl.Items.Count == 0)
                _lstTpl.Items.Add("（还没有模板，点“新增...”添加）");
            _lstTpl.EndUpdate();
        }

        /// <summary>写回配置文件并标记“模板清单已变”（宿主窗口关闭配置后重载模板树）。</summary>
        private void SaveTemplates()
        {
            LYJApp.SaveTemplatePaths(_tplPaths);
            TemplatesChanged = true;
            _tplCfgWriteAt = TemplateCfgWriteTime();   // 自己写的别当成“外部修改”
        }

        /// <summary>路径是否已在清单里（大小写不敏感）。</summary>
        private static bool ContainsPath(List<string> list, string path)
        {
            foreach (string q in list)
                if (string.Equals(q, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>新增模板：文件对话框（可多选），逐个登记、注册到 NX 并写回配置。</summary>
        private void AddTemplates()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择加工模板";
                dlg.Filter = "加工模板 (*.prt)|*.prt|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int added = 0;
                foreach (string f in dlg.FileNames)
                {
                    string p = (f ?? "").Trim();
                    if (p.Length == 0 || ContainsPath(_tplPaths, p)) continue;
                    _tplPaths.Add(p);
                    added++;
                    LYJApp.TryRegisterTemplateToNx(p);   // 立即复制到本机加工模板目录
                }
                if (added > 0)
                {
                    SaveTemplates();
                    _lblTplHint.Text = "已新增 " + added + " 个模板并保存。";
                }
                RefreshTemplateList();
            }
        }

        /// <summary>编辑选中模板：换成另一个 prt 文件。</summary>
        private void EditTemplate()
        {
            int i = _lstTpl.SelectedIndex;
            if (i < 0 || i >= _tplPaths.Count)
            {
                MessageBox.Show(this, "请先在列表中选中要编辑的模板。", "加工助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "修改加工模板";
                dlg.Filter = "加工模板 (*.prt)|*.prt|所有文件 (*.*)|*.*";
                try { dlg.InitialDirectory = Path.GetDirectoryName(_tplPaths[i]); } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string p = (dlg.FileName ?? "").Trim();
                if (p.Length == 0) return;
                if (ContainsPath(_tplPaths, p) &&
                    !string.Equals(_tplPaths[i], p, StringComparison.OrdinalIgnoreCase))
                {
                    _tplPaths.RemoveAt(i);               // 与另一项重复：以新路径为准，去掉旧项
                    _lblTplHint.Text = "该模板已在清单中，已去除重复项。";
                }
                else
                {
                    _tplPaths[i] = p;
                    _lblTplHint.Text = "已修改并保存。";
                }
                LYJApp.TryRegisterTemplateToNx(p);
                SaveTemplates();
                RefreshTemplateList();
            }
        }

        /// <summary>删除选中模板（只从清单移除，不删除 prt 文件本身）。</summary>
        private void DeleteTemplate()
        {
            int i = _lstTpl.SelectedIndex;
            if (i < 0 || i >= _tplPaths.Count)
            {
                MessageBox.Show(this, "请先在列表中选中要删除的模板。", "加工助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string removed = _tplPaths[i];
            _tplPaths.RemoveAt(i);
            SaveTemplates();
            RefreshTemplateList();
            _lblTplHint.Text = "已删除：" + Path.GetFileName(removed) + "（prt 文件未删除）。";
        }

        /// <summary>刷新选中行的颜色预览（色块 + 文字说明）；多选时显示行数。</summary>
        private void RefreshColorState()
        {
            if (_pnlSwatch == null || _lblColor == null) return;
            if (_listRows.SelectedIndices.Count == 0)
            {
                _pnlSwatch.Visible = false;
                _lblColor.Text = "";
                return;
            }
            int last = _listRows.SelectedIndices[_listRows.SelectedIndices.Count - 1];
            string lastId = _ids[last];
            _pnlSwatch.Visible = true;
            _pnlSwatch.BackColor = _colors.ContainsKey(lastId)
                ? _colors[lastId] : MachAssistantConfig.DefaultRowColor;
            if (_listRows.SelectedIndices.Count == 1)
                _lblColor.Text = _colors.ContainsKey(lastId)
                    ? "自定义颜色" : "默认颜色 #009688";
            else
                _lblColor.Text = "已选 " + _listRows.SelectedIndices.Count + " 行，可同设颜色";
        }

        /// <summary>给当前选中的所有行设置显示颜色（打开系统取色器，批量生效）。</summary>
        private void EditColor()
        {
            if (_listRows.SelectedIndices.Count == 0) return;
            int last = _listRows.SelectedIndices[_listRows.SelectedIndices.Count - 1];
            string lastId = _ids[last];
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = _colors.ContainsKey(lastId)
                    ? _colors[lastId] : MachAssistantConfig.DefaultRowColor;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    foreach (int i in _listRows.SelectedIndices)
                        _colors[_ids[i]] = dlg.Color;
                    RefreshColorState();
                }
            }
        }

        /// <summary>把当前选中的所有行恢复为默认颜色。</summary>
        private void ClearColor()
        {
            if (_listRows.SelectedIndices.Count == 0) return;
            foreach (int i in _listRows.SelectedIndices)
                _colors.Remove(_ids[i]);
            RefreshColorState();
        }

        /// <summary>
        /// 上移/下移选中项（支持多选：选中块整体移动一位，保持相对顺序）。
        /// 排序后整表重建（Clear + Add）而不是替换 Items[i]——直接替换会让
        /// CheckedListBox 内部勾选索引错位，确定时报“索引超出数组界限”（v2.1 踩坑）。
        /// </summary>
        private void MoveItem(int delta)
        {
            int n = _ids.Count;
            if (n == 0 || _listRows.SelectedIndices.Count == 0) return;

            bool[] sel = new bool[n];               // 选中标记
            foreach (int i in _listRows.SelectedIndices) sel[i] = true;
            bool[] chk = new bool[n];               // 勾选标记
            for (int k = 0; k < n; k++) chk[k] = _listRows.Items[k].Checked;

            if (delta < 0)
            {
                // 整块上移一位：自上而下，选中且上邻未选中的项与上邻交换
                for (int i = 1; i < n; i++)
                {
                    if (!(sel[i] && !sel[i - 1])) continue;
                    SwapAt(i - 1, i, _ids);
                    bool t = sel[i - 1]; sel[i - 1] = sel[i]; sel[i] = t;
                    t = chk[i - 1]; chk[i - 1] = chk[i]; chk[i] = t;
                }
            }
            else
            {
                // 整块下移一位：自下而上
                for (int i = n - 2; i >= 0; i--)
                {
                    if (!(sel[i] && !sel[i + 1])) continue;
                    SwapAt(i, i + 1, _ids);
                    bool t = sel[i + 1]; sel[i + 1] = sel[i]; sel[i] = t;
                    t = chk[i + 1]; chk[i + 1] = chk[i]; chk[i] = t;
                }
            }

            _listRows.BeginUpdate();
            _listRows.Items.Clear();
            for (int k = 0; k < n; k++)
            {
                ListViewItem it = new ListViewItem(MachAssistantConfig.NameOf(_ids[k]) +
                    (MachAssistantConfig.SlowRows.Contains(_ids[k]) ? "（计算慢）" : ""));
                it.Checked = chk[k];
                it.Tag = _ids[k];
                _listRows.Items.Add(it);
                if (sel[k]) it.Selected = true;
            }
            _listRows.EndUpdate();
            RefreshColorState();
        }

        private static void SwapAt(int a, int b, List<string> list)
        {
            string t = list[a]; list[a] = list[b]; list[b] = t;
        }

        private static Label MakeLabel(string text, int x, int y, int w)
        {
            return new Label
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }
    }

    // ------------------------------------------------------------------------- 悬浮窗
    public partial class MachAssistantForm : Form
    {
        private readonly Session _session = Session.GetSession();
        private readonly UI _ui = UI.GetUI();
        private readonly NXOpen.UF.UFSession _ufs = NXOpen.UF.UFSession.GetUFSession();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

        private NXOpen.CAM.Operation _op;       // 当前显示的工序
        private Part _opPart;                   // 工序所在部件
        private List<ToolItem> _tools = new List<ToolItem>();
        private bool _loading;                  // 回填显示期间抑制事件
        private string _toolSig;                // 刀具清单签名（变化才刷新列表）
        private int _tickCount;                 // 轮询分频计数
        private IntPtr _nxMain = IntPtr.Zero;   // NX 主窗口句柄（缓存，防弹层干扰）
        private double _feedsS, _feedsF;                        // 转速/进给缓存（每轮刷新只读一次）
        private NXOpen.Tag _feedsOpTag = NXOpen.Tag.Null;       // 缓存对应的工序
        private int _selCount = -1;                             // 上一拍的选中对象数量
        private NXOpen.Tag _selLastTag = NXOpen.Tag.Null;       // 上一拍末位选中对象的 Tag
        private Rectangle _lastAnchor = Rectangle.Empty;        // 上一次的停靠锚点矩形
        private int _lastRefresh;                               // 上次刷新时刻（TickCount），用于节流
        // ---- 单次刷新内的读取缓存：同一轮里多个行要用的同一个 NX 查询只做一次 ----
        private NXOpen.CAM.NCGroup _toolCacheGrp;               // 当前工序所属刀具（机床视图父组）
        private NXOpen.Tag _toolCacheTag = NXOpen.Tag.Null;
        private NXOpen.Tag _tolTag = NXOpen.Tag.Null;           // 内外公差缓存对应的工序
        private double _tolIn, _tolOut;
        private NXOpen.Tag _timeTag = NXOpen.Tag.Null;          // 加工时间缓存（按工序）
        private string _timeText = "";
        private int _timeUfIdx;                                 // 「Toolpath Time」参数索引：0=未探测 -1=本版本不可用
        private NXOpen.CAM.Operation _pendingOp;                // 待读取的工序（延后一拍）
        private bool _loadQueued;                               // 已经排了一次 BeginInvoke
        private NXOpen.Tag _drillTag = NXOpen.Tag.Null;         // 是否钻孔类工序的判定缓存
        private bool _isDrill;
        private static readonly Color KeyColor = Color.FromArgb(255, 0, 255); // 透明色键

        // ---- 图档生命周期：窗口只服务于打开它的那个图档 ----
        private NXOpen.Tag _partTag = NXOpen.Tag.Null;        // 打开助手时的显示部件（图档切换/关闭后不一致）
        private bool _partClosed;               // NX 的 PartClosed 回调置位，关窗留给下一次 Tick
        private int _partClosedId = -1;         // PartClosed 回调句柄（关窗时注销）
        private bool _configOpen;               // 配置窗口（模态）打开中
        private bool _closePending;              // 模态窗口挡着，图档关了但还没关窗

        /// <summary>行定义注册表：新参数只需在此加一行 + 在 Read/Apply 里各加一个 case。</summary>
        /// <summary>行的交互方式：Text=点值输入，Pick=弹出候选项列表，ReadOnly=只读展示。</summary>
        private enum RowEdit { Text, Pick, ReadOnly }

        private class RowDef
        {
            public string Id;           // 与 MachAssistantConfig.AllRows 对应
            public string Caption;      // 悬浮窗行标题
            public RowEdit Kind;        // 交互方式
            public RowDef(string id, string caption, RowEdit kind)
            {
                Id = id; Caption = caption; Kind = kind;
            }
            public bool Editable { get { return Kind != RowEdit.ReadOnly; } }
        }

        /// <summary>参数表：常用参数按使用频率排序。</summary>
        private static readonly RowDef[] RowDefs = new RowDef[]
        {
            new RowDef("group",      "程序组",     RowEdit.Pick),   // 所属程序组，下拉选
            new RowDef("op",         "刀路名称",   RowEdit.Text),   // 重命名工序
            new RowDef("mcs",        "刀路坐标",   RowEdit.Pick),   // 工序的 MCS，下拉选
            new RowDef("optype",     "刀路类型",   RowEdit.ReadOnly), // 只读：模板子类型（默认隐藏）
            new RowDef("tool",       "刀具名称",   RowEdit.Pick),   // 换刀
            new RowDef("toolno",     "刀具编号",   RowEdit.Text),   // 改刀具号 + 补偿寄存器
            new RowDef("realtool",   "真实刀名",   RowEdit.ReadOnly), // 只读：按刀具真实直径算的 D 名
            new RowDef("tooldia",    "刀具直径",   RowEdit.ReadOnly),
            new RowDef("toolcorner", "刀具圆角",   RowEdit.ReadOnly),
            new RowDef("tooltype",   "刀具类型",   RowEdit.ReadOnly),
            new RowDef("holder",     "刀头名称",   RowEdit.ReadOnly),
            new RowDef("toollen",    "刀具长度",   RowEdit.ReadOnly), // TL_HEIGHT（默认隐藏）
            new RowDef("toollflen",  "刀具刃长",   RowEdit.ReadOnly), // TL_FLUTE_LN（默认隐藏）
            new RowDef("tooldesc",   "刀具描述",   RowEdit.ReadOnly), // TL_DESCRIPTION 字符串（默认隐藏）
            new RowDef("taperang",   "刀具锥角",   RowEdit.ReadOnly), // TL_TAPER_ANG（默认隐藏）
            new RowDef("tipang",     "刀具尖角",   RowEdit.ReadOnly), // TL_TIP_ANG 钻头顶角（默认隐藏）
            new RowDef("holderlib",  "夹持库号",   RowEdit.ReadOnly), // TL_HOLDER_LIBREF 字符串（默认隐藏）
            new RowDef("holderoff",  "刀头偏置",   RowEdit.ReadOnly), // TL_HOLDER_OFFSET（默认隐藏）
            new RowDef("minlen",     "最短刀长",   RowEdit.ReadOnly), // TL_MIN_LENGTH（默认隐藏）
            new RowDef("tipdepth",   "刀尖深度",   RowEdit.Text),   // 钻孔相关（Cycle：Depth (Tip)）
            // 刀尖类型行临时移除：旧版钻孔的「模型深度/刀尖深度」不是独立参数（只是循环深度值的存储差异），
            // 325 项参数快照 + 6 种 CLSF 格式对比均无差异，暂时读不到也写不了。找到数据源后放开本行，
            // 并恢复 ReadTipTypeText / BuildPicks 的 tiptype 分支 / CommitPick / ApplyTipType。
            // new RowDef("tiptype", "刀尖类型", RowEdit.Pick),
            new RowDef("tipstep",    "刀尖步距",   RowEdit.Text),   // 钻孔相关（Cycle 参数：Step 值，可多段）
            new RowDef("tolinner",   "内公差",     RowEdit.Text),
            new RowDef("tolouter",   "外公差",     RowEdit.Text),
            new RowDef("partstock",  "侧面余量",   RowEdit.Text),
            new RowDef("floorstock", "底面余量",   RowEdit.Text),
            new RowDef("cutmode",    "切削模式",   RowEdit.Pick),   // 300 往复/单向/跟随周边…（默认隐藏）
            new RowDef("cutdir",     "切削方向",   RowEdit.Pick),   // 305 顺铣/逆铣…（默认隐藏）
            new RowDef("cutdir2",    "刀路方向",   RowEdit.Pick),   // 277 向内/向外（默认隐藏）
            new RowDef("levelmode",  "层到层",     RowEdit.Pick),   // 350 转移方式（默认隐藏）
            new RowDef("sideflooreq","侧底一致",   RowEdit.Pick),   // 346 布尔（默认隐藏）
            new RowDef("spindle",    "转速",       RowEdit.Text),
            new RowDef("feed",       "进给",       RowEdit.Text),
            new RowDef("stepover",   "行距",       RowEdit.Text),
            new RowDef("depthpercut","下刀量",     RowEdit.Text),
            new RowDef("time",       "加工时间",   RowEdit.ReadOnly)
        };

        private static RowDef DefOf(string id)
        {
            foreach (RowDef d in RowDefs) if (d.Id == id) return d;
            return null;
        }

        private string _editing = null;         // 当前编辑行 Id；null = 无编辑
        private bool _editingTool;              // 编辑态是刀具列表（否则是数值输入框）

        // ---- 界面控件 ----
        private Panel _header;
        private Label _lblTitle;
        private HeaderGlyphButton _btnGear;              // 左上角齿轮：打开配置窗口
        private HeaderGlyphButton _btnClose;             // 右上角关闭（自绘，保证完整显示）
        private HeaderTab _tabTpl;                      // 标题栏内的页签按钮（自绘，可全透明）
        private HeaderTab _tabParam;
        private Panel _pageHost;                        // 页面容器（手动定位在标题栏下方）
        private Panel _pageTpl;                         // 页0：模板（色键透明，程序视图样式）
        private Panel _pageParam;                       // 页1：加工参数（色键透明）
        private int _curPage;                           // 当前页：0=模板 1=加工参数
        private int _tplAnchorH;                        // 模板页撑满时记录的图形区高度（用于跟随 NX 窗口缩放）
        private int _anchorW;                           // 上次重排时图形区宽度（宽度变了要重排，防止标题栏顶出去）
        private TreeView _treeTpl;                      // 模板页：程序视图样式的加工策略树
        private bool _tplLoaded;                        // 模板树已加载（只加载一次）
        private bool _tplLoading;
        private readonly List<TreeNode> _tplSel = new List<TreeNode>();   // 模板树多选：选中的节点（Ctrl/Shift）
        private TreeNode _tplAnchor;                    // 多选锚点（Shift 范围选择的起点）
        private Dictionary<string, Label> _capLabels = new Dictionary<string, Label>();
        private Dictionary<string, Panel> _hot = new Dictionary<string, Panel>();   // 整行点击热区
        private Dictionary<string, Label> _valLabels = new Dictionary<string, Label>();
        private TextBox _txtEdit;                       // 编辑态：数值输入（全行共用）
        private ListBox _lstPick;                       // 编辑态：候选项列表
        private Form _pickHost;                         // 候选项弹层：独立无边框窗口（可超出悬浮窗范围）
        private bool _pickOpen;                         // 弹层已弹出（此时本窗 Deactivate 不能取消编辑）
        private bool _pickClosing;                      // 正在收起弹层（抑制 Deactivate 递归）
        private Label _lblHint;
        private Label _lblTplHint;                      // 模板页底部提示行（与 _lblHint 同字体）
        private Font _hintFont;                         // 提示行专用字体（固定 8pt）

        // ---- 模板页背景：浅灰半透明 ----
        // 色键窗口（TransparencyKey）只能"全透明 / 不透明"二选一，画不出半透明：
        // 半透明的像素会和洋红键混色，结果是发粉的实色块。
        // 所以这里另开一个**分层窗口**当衬底（Form.Opacity = 真 alpha 混合），
        // 垫在悬浮窗下面、NX 上面一层 —— 模板页的色键区域就透出这层浅灰，
        // 文字/树线仍是实色，观感就是"浅灰色半透明面板"。
        private static readonly Color TplBackColor = Color.FromArgb(236, 238, 242);   // 浅灰
        private const double TplBackOpacity = 0.55;                                  // 0=全透 1=不透明
        private Form _tplBack;                          // 模板页衬底窗口（懒建）

        public MachAssistantForm()
        {
            MachAssistantConfig.Load();
            BuildUi();
            ApplyConfigToUi();
            _timer.Interval = 200;              // 近实时：跟随鼠标点选刷新
            _timer.Tick += OnTick;
            _timer.Start();
            HookPartLifecycle();
            FormClosed += (s, e) =>
            {
                _timer.Stop();
                try { HidePick(); if (_pickHost != null) _pickHost.Dispose(); } catch { }
                try { if (_tplBack != null) { _tplBack.Close(); _tplBack.Dispose(); _tplBack = null; } } catch { }
                UnhookPartLifecycle();
                if (MachAssistant.Form == this) MachAssistant.Form = null;
            };
        }

        // -------------------------------------------------- 图档生命周期（关/切图档即关窗）
        /// <summary>
        /// 记住“打开助手时”的显示部件，并挂上 NX 的 PartClosed 回调。
        /// 之后图档被关闭或被切走，窗口跟着关闭，避免图档没了助手还挂在屏幕上。
        /// </summary>
        private void HookPartLifecycle()
        {
            try
            {
                Part disp = _session.Parts.Display;
                _partTag = (disp == null) ? NXOpen.Tag.Null : disp.Tag;
                _partClosedId = _session.Parts.AddPartClosedHandler(OnPartClosedNotify);
            }
            catch { }
        }

        private void UnhookPartLifecycle()
        {
            try
            {
                if (_partClosedId >= 0) _session.Parts.RemovePartClosedHandler(_partClosedId);
            }
            catch { }
            _partClosedId = -1;
        }

        /// <summary>
        /// NX 的“部件正在关闭”回调。文档明确要求回调里不要做打开/保存/改显示部件等操作，
        /// 所以这里只置标志，真正关窗交给下一次 Tick（同一 UI 线程的消息泵，此时关闭已完成）。
        /// </summary>
        private void OnPartClosedNotify(NXOpen.BasePart part)
        {
            try
            {
                // 只关心“本窗口所属的那个图档”：关闭别的已加载部件（非本图档）不该连带关窗。
                if (part == null || _partTag.Equals(NXOpen.Tag.Null) || part.Tag.Equals(_partTag))
                    _partClosed = true;
            }
            catch { _partClosed = true; }
        }

        /// <summary>当前显示部件是否已不是打开助手时那个（含图档被关闭后变成空）。</summary>
        private bool PartChanged()
        {
            try
            {
                Part disp = _session.Parts.Display;
                NXOpen.Tag now = (disp == null) ? NXOpen.Tag.Null : disp.Tag;
                return !now.Equals(_partTag);
            }
            catch
            {
                return true;            // 读不到 = 图档已经不在了
            }
        }

        // ---------------------------------------------------- 模板页半透明衬底
        /// <summary>
        /// 模板页的半透明衬底窗口：无边框、不激活、鼠标穿透、不进任务栏。
        /// 用 Form.Opacity 做真正的 alpha 混合（不是色键），所以能"浅灰 + 半透明"。
        /// 挂成 NX 主窗口的 owned window：永远浮在 NX 之上，再由 KeepTplBackdropBelow()
        /// 压到悬浮窗下面一层，于是只有模板页那块区域透出浅灰。
        /// </summary>
        private sealed class TplBackdrop : Form
        {
            public TplBackdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = TplBackColor;        // 外层类的私有静态字段，嵌套类可直接用
                Opacity = TplBackOpacity;
                DoubleBuffered = true;
            }

            /// <summary>Show() 时不要抢焦点（否则会把 NX/悬浮窗的焦点夺走）。</summary>
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE   点它不激活
                    cp.ExStyle |= 0x00000020;    // WS_EX_TRANSPARENT  鼠标穿透，不挡点模型
                    cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW   不进任务栏 / Alt-Tab
                    return cp;
                }
            }
        }

        /// <summary>
        /// 让衬底跟上模板页：位置 = 模板页的屏幕矩形；只在模板页可见时显示。
        /// keepOrder=true 时顺便把衬底重新压回悬浮窗下面一层（NX 抢焦点/弹窗后 z 序会变）。
        /// </summary>
        private void SyncTplBackdrop(bool keepOrder)
        {
            try
            {
                bool want = Visible && !IsDisposed
                            && _curPage == 0 && _pageTpl != null && _pageTpl.Visible;
                if (!want)
                {
                    HideTplBackdrop();
                    return;
                }

                // 先算好模板页的屏幕矩形：新窗口直接按这个位置创建，避免"先在左上角闪一下"
                Rectangle r = _pageTpl.RectangleToScreen(_pageTpl.ClientRectangle);
                if (r.Width <= 0 || r.Height <= 0) return;

                bool fresh = false;      // 本次刚显示（新窗口/从隐藏变显示）：必须立刻压到下面一层
                if (_tplBack == null || _tplBack.IsDisposed)
                {
                    if (_nxMain == IntPtr.Zero) AnchorRect();   // 顺带缓存 NX 主窗口句柄
                    _tplBack = new TplBackdrop();
                    _tplBack.Bounds = r;
                    _tplBack.Show();
                    // 挂成 NX 主窗口的 owned window（Show 之后 Handle 才稳定，owner 也一定已经解析出来了）
                    try
                    {
                        if (_nxMain != IntPtr.Zero)
                            SetWindowLongPtr(_tplBack.Handle, GWL_HWNDPARENT, _nxMain);
                    }
                    catch { }
                    fresh = true;
                }

                if (_tplBack.Bounds != r) _tplBack.Bounds = r;
                if (!_tplBack.Visible) { _tplBack.Show(); fresh = true; }
                // 新显示出来的窗口默认在最上面，会盖住树 —— 必须马上压下去，不能等下一次 keepOrder
                if (fresh || keepOrder) KeepTplBackdropBelow();
            }
            catch { }
        }

        /// <summary>把衬底压到悬浮窗下面一层（两个窗口都 owned 在 NX 主窗口上，同一 z 序带）。</summary>
        private void KeepTplBackdropBelow()
        {
            try
            {
                if (_tplBack == null || _tplBack.IsDisposed || !_tplBack.Visible) return;
                SetWindowPos(_tplBack.Handle, Handle, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            catch { }
        }

        private void HideTplBackdrop()
        {
            try
            {
                if (_tplBack != null && !_tplBack.IsDisposed && _tplBack.Visible)
                    _tplBack.Hide();
            }
            catch { }
        }

        // ------------------------------------------------------------------ UI
        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;                // 不抢系统顶层；平时“owned”在 NX 主窗口之上
            StartPosition = FormStartPosition.Manual;   // 见 OnLoad：GWL_HWNDPARENT 挂到 NX 主窗口，比弹窗低一级；编辑态临时置顶
            Size = new Size(272, 196);
            PlaceAtAnchor();

            // 背景与输入框完全透明：色键方案（GDI 按 RGB 精确匹配抠除）。
            Color key = KeyColor;               // 洋红键，窗体内无同色元素
            BackColor = key;
            TransparencyKey = key;
            Opacity = 1.0;
            DoubleBuffered = true;

            // 鼠标移回窗口 = 用户想看当前值：补刷一次（节流），弥补“没有定时刷新”的空档。
            // 子控件不处理 MouseEnter，所以这里只在真正从外面移进来时触发。
            MouseEnter += (s2, e2) => RefreshIfStale();

            // 标题栏：背景透明，只留文字、齿轮、页签与关闭钮（齿轮 = 配置入口）
            _header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = key
            };
            _lblTitle = new SolidColorLabel
            {
                Text = "加工助手",
                ForeColor = Color.FromArgb(45, 90, 140),
                BackColor = Color.Transparent,
                AutoSize = false,
                Bounds = new Rectangle(34, 5, 120, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // 左上角齿轮：配置入口（原来的“点标题进配置”改到这里）
            _btnGear = new HeaderGlyphButton { Kind = HeaderGlyphButton.GlyphKind.Gear };
            _btnGear.Click += (s, e) => OpenConfig();
            new ToolTip().SetToolTip(_btnGear, "配置字体、显示内容与行颜色");

            // 右上角关闭：自绘 ×，避免标准 Button 在色键透明窗体上被裁掉/发虚
            _btnClose = new HeaderGlyphButton { Kind = HeaderGlyphButton.GlyphKind.Close };
            _btnClose.Click += (s, e) => Close();
            new ToolTip().SetToolTip(_btnClose, "关闭");

            // 标题栏里的页签按钮：自绘（单色栅格文字 + 选中下划线），
            // 背景直接是洋红色键 → 标题栏依旧完全透明，看不到任何方块。
            _tabTpl = new HeaderTab { Text = "模板" };
            _tabParam = new HeaderTab { Text = "加工参数" };
            _tabTpl.Click += (s, e) => SwitchPage(0);
            _tabParam.Click += (s, e) => SwitchPage(1);
            new ToolTip().SetToolTip(_tabTpl, "自定义模板里的加工策略（双击插入）");
            new ToolTip().SetToolTip(_tabParam, "多选工序后回车可把参数一次改完");

            _header.Controls.Add(_lblTitle);
            _header.Controls.Add(_btnGear);
            _header.Controls.Add(_tabTpl);
            _header.Controls.Add(_tabParam);
            _header.Controls.Add(_btnClose);
            Controls.Add(_header);

            // ---- 页面容器：两页叠放，用标题栏按钮切换。
            //      原 TabControl 的页签条由主题绘制，无法做色键透明，故改为自绘按钮 + Panel 换页。
            _pageHost = new Panel
            {
                BackColor = key,
                Visible = true
            };
            // 模板页同样用色键背景（不再是白底）：能看到后面的模型，树线和文字仍是实色
            _pageTpl = new Panel { Dock = DockStyle.Fill, BackColor = key };
            _pageParam = new Panel { Dock = DockStyle.Fill, BackColor = key };
            _pageHost.Controls.Add(_pageParam);
            _pageHost.Controls.Add(_pageTpl);
            Controls.Add(_pageHost);
            _pageTpl.BringToFront();

            // 模板页：程序顺序视图样式的策略树（背景走色键全透明，只剩树线/图标/文字可见）
            // 多选用自绘（TreeView 原生不支持）：Ctrl 单个切换、Shift 范围、双击批量插入
            _treeTpl = new TreeView
            {
                BackColor = key,
                ForeColor = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.None,
                HideSelection = true,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                DrawMode = TreeViewDrawMode.OwnerDrawText,
                Dock = DockStyle.Fill
            };
            _treeTpl.ImageList = MakeTplImages();
            _treeTpl.DrawNode += Tpl_DrawNode;
            _treeTpl.NodeMouseClick += Tpl_NodeClick;
            _treeTpl.NodeMouseDoubleClick += Tpl_DoubleClick;
            _pageTpl.Controls.Add(_treeTpl);

            // 模板页底部操作提示（色键透明底 + 单色栅格文字：不会在白/洋红之间留抗锯齿毛边）
            _lblTplHint = new SolidColorLabel
            {
                Text = "Ctrl/Shift 多选策略，左键双击批量插入到当前选中的组",
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = Color.Transparent,      // 父面板是色键 → 抠成全透明
                ForeColor = Color.Black,
                TextIndent = 6,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };
            if (_hintFont != null) _lblTplHint.Font = _hintFont;
            _pageTpl.Controls.Add(_lblTplHint);

            // ---- 按注册表创建全部行（显示/顺序由 Relayout 依配置排列）----
            foreach (RowDef def in RowDefs)
            {
                Label cap = new CaptionLabel(def.Caption);
                Label val = new SolidColorLabel
                {
                    Text = def.Id == "op" ? "未选中工序" : "",
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = MachAssistantConfig.DefaultRowColor,
                    BackColor = Color.Transparent,
                    AutoEllipsis = true
                };

                if (def.Id == "op")
                {
                    // 工序行：点击（标题或名称）重命名
                    val.Cursor = Cursors.Hand;
                    cap.Cursor = Cursors.Hand;
                    val.Click += (s, e) => BeginTextEdit("op");
                    cap.Click += (s, e) => BeginTextEdit("op");
                }
                else if (def.Kind == RowEdit.Pick)
                {
                    string id = def.Id;
                    if (id == "tool") val.Text = "（未指定）";
                    val.Cursor = Cursors.Hand;
                    cap.Cursor = Cursors.Hand;
                    val.Click += (s, e) => TogglePick(id);
                    cap.Click += (s, e) => TogglePick(id);
                }
                else if (def.Editable)
                {
                    val.Cursor = Cursors.Hand;
                    string id = def.Id;
                    val.Click += (s, e) => BeginTextEdit(id);
                    cap.Click += (s, e) => BeginTextEdit(id);
                }

                _capLabels[def.Id] = cap;
                _valLabels[def.Id] = val;
                _pageParam.Controls.Add(cap);
                _pageParam.Controls.Add(val);

                // 整行热区面板：覆盖标题+数值的整行（色键透明不挡显示），
                // 点击区不再局限于文字本身（改参数难点中，v2.7 反馈）
                Panel hot = new Panel
                {
                    BackColor = KeyColor,               // 与色键同色 → 抠透明
                    Cursor = def.Editable || def.Id == "tool" ? Cursors.Hand : Cursors.Default
                };
                string rowId = def.Id;
                hot.Click += (s, e) => ClickRow(rowId);
                _hot[def.Id] = hot;
                _pageParam.Controls.Add(hot);                      // 添加在行控件之后 → 热区在最上层接收点击
            }

            // ---- 编辑态控件（全行共用一个输入框）----
            _txtEdit = new TransparentTextBox();
            _txtEdit.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CommitTextEdit(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { CancelEdit(); e.Handled = e.SuppressKeyPress = true; }
            };
            _pageParam.Controls.Add(_txtEdit);

            // 候选项列表放在一个独立的无边框窗口里（不是悬浮窗的子控件）：
            // 子控件会被悬浮窗的客户区裁掉，选项一多就只能缩成带滚动条的矮框；
            // 独立窗口可以直接铺到屏幕范围内，一次把所有候选项列全。
            _lstPick = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 30, 30),
                Font = new Font("Microsoft YaHei UI", 9f),
                ItemHeight = 18,
                IntegralHeight = false,         // 高度按内容精确给，不按整数行吸附
                Visible = true
            };
            _lstPick.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                if (_pickOpen && _editing != null) { CommitPick(); return; }
                // 死弹层（编辑态已被清空但窗体还显示着）：点一下就收走，别挡住参数行
                if (_pickHost != null && _pickHost.Visible) HidePick();
            };
            _lstPick.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CommitPick(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { CancelEdit(); e.Handled = e.SuppressKeyPress = true; }
            };

            _pickHost = new PickPopup
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
                BackColor = Color.FromArgb(150, 150, 150),   // 1px 边（ListView 内的白底盖住其余）
                Padding = new Padding(1),
                MinimizeBox = false,
                MaximizeBox = false,
                Visible = false
            };
            _pickHost.Controls.Add(_lstPick);

            // 状态提示（小字号、固定黑色，不跟随行颜色配置）
            _lblHint = new SolidColorLabel
            {
                Text = "点数值修改：回车确认，点其他处取消",
                ForeColor = Color.Black,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _pageParam.Controls.Add(_lblHint);

            // 编辑态点“其他地方”也算取消：悬浮窗是 NX 的 owned window，
            // 用户点到 NX 图形区/工具条/其他窗口时本窗会收到 Deactivate，
            // 在这里结束编辑（不写参数），不必非按 Esc。
            Deactivate += (s, e) =>
            {
                if (_editing != null) { CancelEdit(); return; }
                if (_pickOpen) HidePick();          // 死弹层兜底：切走焦点也一并收走
            };

            // 弹层是不抢焦点的窗口，键盘仍在助手窗体上：回车/Esc 由这里转发
            KeyPreview = true;
            KeyDown += (s2, e2) =>
            {
                if (!_pickOpen) return;
                if (e2.KeyCode == Keys.Enter) { CommitPick(); e2.Handled = e2.SuppressKeyPress = true; }
                else if (e2.KeyCode == Keys.Escape) { CancelEdit(); e2.Handled = e2.SuppressKeyPress = true; }
            };

            SwitchPage(0);                              // 默认页：模板（与之前的页签顺序一致）
        }

        private class CaptionLabel : SolidColorLabel
        {
            public CaptionLabel(string text)
            {
                Text = text;
                TextAlign = ContentAlignment.MiddleLeft;
                ForeColor = MachAssistantConfig.DefaultRowColor;
                BackColor = Color.Transparent;
            }
        }

        /// <summary>
        /// 标题栏内的页签按钮（自绘）。
        /// **透明性说明**：本窗口用“洋红色键”抠透明，而色键区域在 Windows 里
        /// **鼠标是穿透的**（点击会落到下面的 NX 窗口）。所以按钮若完全无底色，
        /// 只有文字笔画那一两个像素能点中 —— 因此默认画成浅色胶囊：
        /// 选中 = 主色填充白字，未选中 = 浅灰填充。想试字面全透明可把 Filled 置 false，
        /// 代价是只能点文字。
        /// </summary>
        private class HeaderTab : Control
        {
            public static bool Filled = true;       // false = 完全透明（仅文字可点）
            public bool Selected;

            public HeaderTab()
            {
                SetStyle(ControlStyles.UserPaint
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.StandardClick, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                AutoSize = false;
            }

            private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
            {
                System.Drawing.Drawing2D.GraphicsPath p =
                    new System.Drawing.Drawing2D.GraphicsPath();
                int d = rad * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaintBackground(e);      // 铺父背景（洋红键），随后被 GDI 抠除 → 背景透明
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                Color accent = MachAssistantConfig.DefaultRowColor;
                Color fore = Selected
                    ? (Filled ? Color.White : accent)
                    : (Filled ? Color.FromArgb(70, 90, 90) : Color.FromArgb(120, 120, 120));

                if (Filled)
                {
                    Rectangle pr = new Rectangle(0, 1, Width - 1, Height - 3);
                    using (System.Drawing.Drawing2D.GraphicsPath path = Round(pr, 4))
                    using (SolidBrush b = new SolidBrush(
                        Selected ? accent : Color.FromArgb(232, 238, 238)))
                        e.Graphics.FillPath(b, path);
                }

                Rectangle r = ClientRectangle;
                if (Selected && !Filled) r.Height -= 4;   // 透明模式用下划线表示选中
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    using (SolidBrush b = new SolidBrush(fore))
                        e.Graphics.DrawString(Text, Font, b, r, sf);
                }
                if (Selected && !Filled)
                {
                    using (SolidBrush b = new SolidBrush(accent))
                        e.Graphics.FillRectangle(b, 2, Height - 3, Width - 4, 2);
                }
            }
        }

        /// <summary>
        /// 标题栏图标按钮（齿轮 / 关闭）：完全自绘，不依赖字体字形。
        /// 标准 Button 在这个“洋红色键透明”窗体上，文字笔画会与色键混色、贴边还会被裁，
        /// 所以改成 GDI 线条绘制；悬停时垫一层浅色圆角底，兼作可点提示。
        /// </summary>
        private class HeaderGlyphButton : Control
        {
            public enum GlyphKind { Gear, Close }
            public GlyphKind Kind = GlyphKind.Close;
            private bool _hover;

            public HeaderGlyphButton()
            {
                SetStyle(ControlStyles.UserPaint
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.StandardClick, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                AutoSize = false;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hover = true; Invalidate(); base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hover = false; Invalidate(); base.OnMouseLeave(e);
            }

            private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
            {
                System.Drawing.Drawing2D.GraphicsPath p =
                    new System.Drawing.Drawing2D.GraphicsPath();
                int d = rad * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaintBackground(e);      // 铺父背景（洋红键，随后被 GDI 抠除）
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 底色必须是不透明像素：色键区域鼠标是穿透的，纯线条会“看得见点不着”，
                // 而且线条抗锯齿边与洋红混色正是原来 × 显示残缺的原因。
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (System.Drawing.Drawing2D.GraphicsPath p = Round(r, 4))
                using (SolidBrush b = new SolidBrush(
                    Kind == GlyphKind.Close
                        ? (_hover ? Color.FromArgb(242, 214, 214) : Color.FromArgb(240, 242, 242))
                        : (_hover ? Color.FromArgb(222, 232, 232) : Color.FromArgb(240, 242, 242))))
                    g.FillPath(b, p);

                Color fore = _hover
                    ? (Kind == GlyphKind.Close ? Color.FromArgb(165, 40, 40) : Color.FromArgb(25, 25, 25))
                    : Color.FromArgb(85, 85, 85);

                using (Pen pen = new Pen(fore, 1.6f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    if (Kind == GlyphKind.Close) DrawClose(g, pen);
                    else DrawGear(g, pen);
                }
            }

            /// <summary>关闭：两条对角细线，居中且留足边距（不会像 Button 那样被裁）。</summary>
            private void DrawClose(Graphics g, Pen pen)
            {
                float cx = Width / 2f, cy = Height / 2f, d = Math.Min(Width, Height) * 0.22f;
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }

            /// <summary>齿轮：8 齿轮廓 + 中心孔，纯线条绘制（无需图标资源/字体）。</summary>
            private void DrawGear(Graphics g, Pen pen)
            {
                float cx = Width / 2f, cy = Height / 2f;
                float R = Math.Min(Width, Height) * 0.40f;      // 齿顶半径
                float rIn = R * 0.74f;                          // 齿根半径
                const int teeth = 8;
                double step = Math.PI * 2 / teeth;
                PointF[] pts = new PointF[teeth * 4];
                for (int i = 0; i < teeth; i++)
                {
                    double a = i * step;
                    pts[i * 4 + 0] = Pt(cx, cy, rIn, a + step * 0.00);
                    pts[i * 4 + 1] = Pt(cx, cy, R, a + step * 0.18);
                    pts[i * 4 + 2] = Pt(cx, cy, R, a + step * 0.58);
                    pts[i * 4 + 3] = Pt(cx, cy, rIn, a + step * 0.76);
                }
                using (System.Drawing.Drawing2D.GraphicsPath p =
                       new System.Drawing.Drawing2D.GraphicsPath())
                {
                    p.AddPolygon(pts);
                    g.DrawPath(pen, p);
                }
                float hole = R * 0.34f;
                g.DrawEllipse(pen, cx - hole, cy - hole, hole * 2, hole * 2);
            }

            private static PointF Pt(float cx, float cy, float r, double a)
            {
                return new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
            }
        }

        /// <summary>
        /// 透明悬浮窗专用标签：GDI+ 单色栅格绘制文字（SingleBitPerPixelGridFit，无 alpha 边缘）。
        /// 普通 Label 的抗锯齿/ClearType 半透明像素会和洋红色键混色，
        /// 造成“配置的颜色和显示的颜色不一致”（青绿 #009688 显示成紫色，v2.4 踩坑）。
        /// </summary>
        private class SolidColorLabel : Label
        {
            /// <summary>文字左侧留白（自绘不用 Padding，单给一个左边距）。</summary>
            public int TextIndent;

            public SolidColorLabel()
            {
                BackColor = Color.Transparent;
                SetStyle(ControlStyles.UserPaint
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.OptimizedDoubleBuffer, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaintBackground(e);      // 先铺父背景（洋红色键，随后被 GDI 抠除）
                e.Graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                Rectangle r = ClientRectangle;
                if (TextIndent > 0) { r.X += TextIndent; r.Width -= TextIndent; }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    using (SolidBrush b = new SolidBrush(ForeColor))
                        e.Graphics.DrawString(Text, Font, b, r, sf);
                }
            }
        }

        /// <summary>候选项弹层：无边框 + 不抢焦点（WS_EX_NOACTIVATE）。
        /// 不抢焦点很关键——否则一弹出助手窗就失活，用户再点别的行会被吞掉一次点击。</summary>
        private class PickPopup : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000;      // WS_EX_NOACTIVATE
                    return cp;
                }
            }
        }

        private class TransparentTextBox : TextBox
        {
            public TransparentTextBox()
            {
                BackColor = KeyColor;           // 背景随色键抠透明
                BorderStyle = BorderStyle.FixedSingle;
                Visible = false;
            }
        }

        /// <summary>配置窗口（标题栏齿轮进入）：确定后重排界面并持久化。</summary>
        private void OpenConfig()
        {
            CancelEdit();                   // 收起候选弹层/输入框，免得盖在模态窗口上
            _configOpen = true;
            try
            {
                using (MachAssistantConfigDialog dlg = new MachAssistantConfigDialog())
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        ApplyConfigToUi();
                    if (dlg.TemplatesChanged)
                    {
                        _tplLoaded = false;         // 模板清单变了：重载模板树
                        LoadTemplateTree();         // 当前就在模板页也能立即刷新
                    }
                }
            }
            finally
            {
                _configOpen = false;
                // 配置窗口开着的时候图档被关了：现在补关宿主窗口
                if (_closePending)
                {
                    _closePending = false;
                    _timer.Stop();
                    Close();
                }
            }
        }

        /// <summary>应用配置：字体 + 行颜色 + 动态重排各行 + 停靠位置。</summary>
        private void ApplyConfigToUi()
        {
            CancelEdit();
            Font = MakeUiFont(MachAssistantConfig.FontName, MachAssistantConfig.FontSize,
                MachAssistantConfig.Bold);
            if (_treeTpl != null) _treeTpl.Font = Font;    // 树是自绘，行高与自绘字体必须同一份
            ApplyRowColors();
            Relayout();
            RefreshValues();
            PlaceAtAnchor();
        }

        /// <summary>
        /// 按配置建字体。字体名必须本机真的存在：new Font("不存在", size) 不会报错，
        /// 而是悄悄换成 Microsoft Sans Serif —— 那套字体没有中文字形，中文全变成空白。
        /// 没有粗体的字体（仿宋/宋体大多只有常规体）由 GDI+ 合成加粗，效果够用。
        /// </summary>
        private static Font MakeUiFont(string name, float size, bool bold)
        {
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            try
            {
                if (!string.IsNullOrEmpty(name))
                    foreach (FontFamily fam in FontFamily.Families)
                        if (string.Equals(fam.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            // 字体族自称支持 Bold 就直接用；不支持时 new Font 会自己合成，不用管
                            return new Font(fam, size, style);
                        }
            }
            catch { }
            return new Font("Microsoft YaHei UI", size, style);
        }

        /// <summary>
        /// 把悬浮窗设为 NX 主窗口的 owned window（GWL_HWNDPARENT）：
        /// 永远浮在 NX 主窗口之上（不会被点 NX 操作压到后面），
        /// 但不抢其他顶层窗口——NX 弹出的参数对话框激活时会在悬浮窗之上。
        /// 即“只比 NX 高一级、比弹窗低一级”。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                if (_nxMain == IntPtr.Zero) AnchorRect();   // 顺带缓存 NX 主窗口句柄
                if (_nxMain != IntPtr.Zero)
                    SetWindowLongPtr(Handle, GWL_HWNDPARENT, _nxMain);
            }
            catch { }
        }

        /// <summary>窗口移动/改尺寸：模板页衬底跟着走（窗口由 PlaceAtAnchor 停靠，不支持拖动）。</summary>
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            SyncTplBackdrop(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncTplBackdrop(true);
        }

        /// <summary>窗口隐藏（关闭/切图档）时衬底必须一起收起，否则会留下一块灰。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) HideTplBackdrop();
            else SyncTplBackdrop(true);
        }

        /// <summary>按配置应用每行文字颜色（标题+数值同色），未配置的用默认色。</summary>
        private void ApplyRowColors()
        {
            foreach (string id in _valLabels.Keys)
            {
                Color c = MachAssistantConfig.RowColors.ContainsKey(id)
                    ? MachAssistantConfig.RowColors[id]
                    : MachAssistantConfig.DefaultRowColor;
                _capLabels[id].ForeColor = c;
                _valLabels[id].ForeColor = c;
            }
        }

        /// <summary>按配置顺序返回要显示的行（过滤 hidden）。</summary>
        private List<string> VisibleRows()
        {
            List<string> rows = new List<string>();
            foreach (string id in MachAssistantConfig.RowOrder)
                if (!MachAssistantConfig.IsHidden(id) && _valLabels.ContainsKey(id))
                    rows.Add(id);
            return rows;
        }

        /// <summary>
        /// 按当前字体重排所有行：
        /// 行高随字体；标题列宽度按“最长标题”实测值计算（保证大字号下标题不折行）；
        /// 值列统一从 valX 起左对齐，窗口宽度不足时自动加宽；
        /// 行集合与顺序来自配置（与配置窗口"显示内容"同步）。
        /// </summary>
        private void Relayout()
        {
            List<string> rows = VisibleRows();
            // 行高 = 字高 + 行间距；行间距 6px（原为 12px，按要求收紧一半，显示更密）
            int rowH = Font.Height + 6;

            // 提示行字体：固定 8pt，但跟随主字体的字体族与「加粗」；有变化才重建
            const float hs = 8f;
            FontStyle hsStyle = MachAssistantConfig.Bold ? FontStyle.Bold : FontStyle.Regular;
            if (_hintFont == null ||
                Math.Abs(_hintFont.SizeInPoints - hs) > 0.01f ||
                _hintFont.FontFamily.Name != Font.FontFamily.Name ||
                _hintFont.Style != hsStyle)
            {
                Font old = _hintFont;
                _hintFont = new Font(Font.FontFamily, hs, hsStyle);
                _lblHint.Font = _hintFont;
                if (old != null) old.Dispose();
            }

            int capW = MeasureCaptionWidth(rows);      // 标题列宽（最长标题）
            int valX = 12 + capW + 8;                  // 值列起点：所有值左对齐
            int w = Math.Max(272, valX + 132);
            // 提示行固定 8pt，字号小；窗口最小宽度按两页提示文字实测值兜底
            int hintMinW = TextRenderer.MeasureText("点数值修改：回车确认，点其他处取消", _hintFont).Width + 26;
            int tplHintW = TextRenderer.MeasureText("双击策略插入到当前选中的组", _hintFont).Width + 22;
            if (tplHintW > hintMinW) hintMinW = tplHintW;
            if (w < hintMinW) w = hintMinW;

            // ---- 可用宽度上限：不得超出“显示部件”窗口（图形区）----
            // 不同机器 DPI/字体不同，按文字实测出来的宽度可能比图形区还宽，
            // 窗口又固定左上角对齐（PlaceAtAnchor），于是标题栏右侧（× 按钮）就顶出图形区。
            // 这里先算出图形区能给的宽度，再按剩余空间压缩标题文字/页签内边距。
            Rectangle anchor = AnchorRect();
            int maxW;
            if (!anchor.IsEmpty)
            {
                maxW = anchor.Width - 24;                        // 左缩进 12 + 右侧留 12
                // 图形区跨屏/异常变大时再夹一道屏幕宽（只收不放，避免 DPI 换算把窗口压太窄）
                int scrW = Screen.FromRectangle(anchor).WorkingArea.Width;
                if (scrW > 0 && maxW > scrW - 24) maxW = scrW - 24;
            }
            else
                maxW = Screen.PrimaryScreen.WorkingArea.Width - 24;
            if (maxW < 180) maxW = 180;
            if (w > maxW) w = maxW;
            _anchorW = anchor.IsEmpty ? 0 : anchor.Width;

            // 标题栏高度随字体，避免大字号把“加工助手”裁掉
            int headH = Math.Max(30, Font.Height + 10);

            // 标题栏各段宽度（页签按钮在标题栏内，宽度按文字实测）
            int tabH = Math.Max(18, headH - 10);
            int gearW = 20;                            // 左上角齿轮按钮
            int closeW = 24;                           // 右上角关闭按钮
            int t1 = MeasureSingleLine("模板") + 14;
            int t2 = MeasureSingleLine("加工参数") + 14;
            int titleFull = MeasureSingleLine("加工助手") + 4;

            // 标题：放得下就整块显示，放不下就整块隐藏（不留半截字），
            // 隐藏时连同它后面的 14px 间距一起收回，把宽度让给页签。
            // 间距构成：左 8 + 齿轮 + 6 + [标题] + titleGap + 页签1 + 6 + 页签2 + 12 + × + 右 8
            int titleW = titleFull;
            int titleGap = 14;
            int baseW = 8 + gearW + 6 + titleW + titleGap + 6 + 12 + closeW + 8;   // 不含页签宽度
            int tabsW = t1 + t2 + 6;
            if (baseW + tabsW > maxW)
            {
                titleW = 0;
                titleGap = 0;
                baseW = 8 + gearW + 6 + titleW + titleGap + 6 + 12 + closeW + 8;
            }
            // 还放不下就收页签内边距（每个最多收 8px，文字本身不压）
            int over = baseW + tabsW - maxW;
            if (over > 0)
            {
                int cut = Math.Min(8, (over + 1) / 2 + 1);
                t1 -= cut;
                t2 -= cut;
            }
            _lblTitle.Visible = titleW > 0;
            int needW = baseW + tabsW;
            if (w < needW) w = needW;                  // 字号调大时窗口自动加宽，标题栏不挤
            if (w > maxW) w = maxW;                    // 极端窄屏兜底：宁可页签略挤，也不许顶出图形区

            // 窗口被夹窄时，标题列也不能把值列挤成负宽度（值列至少留 36px）
            if (valX > w - 36)
            {
                valX = Math.Max(24, w - 36);
                capW = Math.Max(16, valX - 20);
            }

            _header.Height = headH;
            _btnGear.SetBounds(8, (headH - gearW) / 2, gearW, gearW);
            _lblTitle.SetBounds(8 + gearW + 6, 0, titleW, headH);

            int y = 6;
            foreach (string id in rows)
            {
                // 所有行统一两列（工序行也带“工序”标题，值列与其他行对齐）。
                // 坐标相对“加工参数”页（行控件都在 _pageParam 内）。
                _capLabels[id].Visible = true;
                _valLabels[id].Visible = _editing != id;
                _capLabels[id].SetBounds(12, y, capW, rowH);
                _valLabels[id].SetBounds(valX, y, w - valX - 12, rowH);
                if (_hot.ContainsKey(id))
                {
                    _hot[id].SetBounds(0, y - 2, w, rowH + 4);   // 整行热区
                    _hot[id].Visible = true;
                }
                y += rowH;
            }
            // 不在显示集合里的行全部藏起
            foreach (string id in _valLabels.Keys)
                if (!rows.Contains(id))
                {
                    _capLabels[id].Visible = false;
                    _valLabels[id].Visible = false;
                    if (_hot.ContainsKey(id)) _hot[id].Visible = false;
                }

            int hintH = Math.Max(20, _hintFont.Height + 6);
            _lblHint.SetBounds(12, y + 2, w - 24, hintH);
            if (_lblTplHint != null)
            {
                _lblTplHint.Font = _hintFont;
                _lblTplHint.Height = hintH + 4;
            }
            int pageH = y + hintH + 10;

            // ---- 标题栏布局：⚙ | 加工助手 | 模板 | 加工参数 | × ----
            int tx = 8 + gearW + 6 + titleW + titleGap;
            _tabTpl.SetBounds(tx, (headH - tabH) / 2, t1, tabH);
            _tabParam.SetBounds(tx + t1 + 6, (headH - tabH) / 2, t2, tabH);
            // 右边距 6px、正方形按钮，避免贴边被裁
            _btnClose.SetBounds(w - closeW - 6, (headH - closeW) / 2, closeW, closeW);

            // 模板页：窗口高度撑满图形区（显示部件窗口），尽量多显示策略
            if (_curPage == 0)
            {
                Rectangle a = anchor;
                // 可用高度要扣掉标题栏（headH）和上下各 12px 边距 —— pageH 只是页面部分，
                // 窗口总高 = headH + pageH，不减 headH 会整体超出图形区底部（v3.8 bug）。
                int avail = (a.IsEmpty
                    ? Screen.PrimaryScreen.WorkingArea.Height
                    : a.Height) - 24 - headH;
                if (avail > pageH) pageH = avail;
                _tplAnchorH = a.IsEmpty ? 0 : a.Height;
            }
            else
            {
                // 参数页：行数多了（参数表 20 来项）会把窗口撑出图形区，
                // 这里按图形区高度封顶，超出部分用竖直滚动条滚动查看，避免遮挡/越界。
                int availP = (anchor.IsEmpty
                    ? Screen.PrimaryScreen.WorkingArea.Height
                    : anchor.Height) - 24 - headH;
                bool needScroll = availP > 120 && pageH > availP;
                if (needScroll) pageH = availP;
                if (_pageParam != null) _pageParam.AutoScroll = needScroll;
            }

            // 页面容器紧贴标题栏下方，两页叠放（Dock=Fill），由按钮切换可见性
            _pageHost.SetBounds(0, headH, w, pageH);
            Size = new Size(w, headH + pageH);
        }

        /// <summary>切换页面（标题栏按钮点击）：0=模板 1=加工参数。</summary>
        private void SwitchPage(int index)
        {
            CancelEdit();
            _curPage = index;
            if (_pageTpl != null)
            {
                _pageTpl.Visible = index == 0;
                if (_pageTpl.Visible) _pageTpl.BringToFront();
            }
            if (_pageParam != null)
            {
                _pageParam.Visible = index == 1;
                if (_pageParam.Visible) _pageParam.BringToFront();
            }
            if (_tabTpl != null) { _tabTpl.Selected = index == 0; _tabTpl.Invalidate(); }
            if (_tabParam != null) { _tabParam.Selected = index == 1; _tabParam.Invalidate(); }
            if (index == 0 && !_tplLoaded) LoadTemplateTree();   // 首次进入模板页才加载

            // 模板页要撑满图形区、参数页回到内容高度 —— 切页后立即重排并重新停靠
            if (_pageHost != null && _header != null)
            {
                Relayout();
                PlaceAtAnchor();
            }
            SyncTplBackdrop(true);        // 换页后衬底跟着显示/隐藏
        }

        /// <summary>收集 NX 里当前多选的工序（顺序 = 选择顺序）。</summary>
        private List<NXOpen.CAM.Operation> SelectedOperations()
        {
            List<NXOpen.CAM.Operation> list = new List<NXOpen.CAM.Operation>();
            try
            {
                int n = _ui.SelectionManager.GetNumSelectedObjects();
                for (int i = 0; i < n; i++)
                {
                    NXOpen.CAM.Operation op =
                        _ui.SelectionManager.GetSelectedObject(i) as NXOpen.CAM.Operation;
                    if (op != null) list.Add(op);
                }
            }
            catch { }
            return list;
        }

        /// <summary>读单道工序的内外公差（失败返回 NaN）。</summary>
        private void BatchAskTol(NXOpen.CAM.Operation op, out double tin, out double tout)
        {
            tin = tout = double.NaN;
            try
            {
                double[] t = new double[2];
                _ufs.Param.Ask2dValue(op.Tag,
                    (int)NXOpen.UF.UFConstants.UF_PARAM_TOL_IN_OUT, t);
                tin = t[0]; tout = t[1];
            }
            catch { }
        }

        // ---- 批量写入（逐工序；回车提交时若多选了工序则由 ApplyValueBatch 调用）----
        private void BatchWriteFeeds(NXOpen.CAM.Operation op, bool hasS, double s, bool hasF, double f)
        {
            double curS, curF;
            AskFeeds(op, out curS, out curF);
            double newS = hasS ? s : curS;
            double newF = hasF ? f : curF;
            if (newS <= 0 || newF <= 0) throw new Exception("转速/进给存在无效值");
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) throw new Exception("无可用工作部件");
            NXOpen.CAM.ObjectsFeedsBuilder fb =
                work.CAMSetup.CreateFeedsBuilder(new NXOpen.CAM.CAMObject[] { op });
            try
            {
                fb.FeedsBuilder.SpindleRpmBuilder.Value = newS;
                fb.FeedsBuilder.RecalculateData(
                    NXOpen.CAM.FeedsBuilder.RecalculateBasedOn.SpindleSpeed);
                fb.FeedsBuilder.FeedCutBuilder.Value = newF;
                fb.FeedsBuilder.RecalculateData(
                    NXOpen.CAM.FeedsBuilder.RecalculateBasedOn.CutFeedRate);
                fb.Commit();
            }
            finally { try { fb.Destroy(); } catch { } }
        }

        private void BatchWriteTol(NXOpen.CAM.Operation op, bool hasIn, double tin, bool hasOut, double tout)
        {
            double tin0, tout0;
            BatchAskTol(op, out tin0, out tout0);
            if (double.IsNaN(tin0) && double.IsNaN(tout0)) throw new Exception("不支持公差");
            double[] t = new double[2];
            t[0] = double.IsNaN(tin0) ? 0.001 : tin0;
            t[1] = double.IsNaN(tout0) ? 0.001 : tout0;
            if (hasIn) t[0] = tin;
            if (hasOut) t[1] = tout;
            _ufs.Param.Set2dValue(op.Tag,
                (int)NXOpen.UF.UFConstants.UF_PARAM_TOL_IN_OUT, t);
        }

        private void BatchWriteStepover(NXOpen.CAM.Operation op, double v)
        {
            int type = 1;
            try { _ufs.Param.AskIntValue(op.Tag, UF_STEPOVER_TYPE, out type); } catch { }
            int param;
            switch (type)
            {
                case 2: param = UF_STEPOVER_SCALLOP; break;
                case 4: param = UF_STEPOVER_PERCENT; break;
                case 7: throw new Exception("行距为“同下刀量”");
                default: param = UF_STEPOVER_DIST; break;
            }
            _ufs.Param.SetDoubleValue(op.Tag, param, v);
        }

        private void BatchWriteDepthPerCut(NXOpen.CAM.Operation op, double v)
        {
            try { _ufs.Param.SetDoubleValue(op.Tag, UF_CUTLEV_GLOBAL_CUT_DEPTH, v); }
            catch { _ufs.Param.SetDoubleValue(op.Tag, UF_CUTLEV_DEPTH_PER_CUT, v); }
        }

        private void BatchWriteToolNo(NXOpen.CAM.Operation op, int no)
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(op);
            if (t == null) throw new Exception("未指定刀具");
            int done = SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_NUMBER)
                     + SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_ADJ_REG)
                     + SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_CUTCOM_REG);
            if (done == 0) throw new Exception("刀具不支持改编号");
        }

        // ------------------------------------------------------------- 模板页
        /// <summary>
        /// 模板树图标列表：
        ///   0 = 程序组文件夹（直接取 NX 安装目录里的位图）
        ///   1 = 全透明占位（工序节点用它 —— 视觉上是纯文本，只是与文件夹行左对齐）
        /// </summary>
        private ImageList MakeTplImages()
        {
            ImageList il = new ImageList
            {
                ImageSize = new Size(16, 16),
                ColorDepth = ColorDepth.Depth32Bit
            };
            il.Images.Add(MakeFolderGlyph());
            // 空白位图（32bppArgb 新图全 0 = 全透明）：工序节点专用，视觉上就是纯文本
            il.Images.Add(new Bitmap(16, 16,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb));
            return il;
        }

        // ---------------------------------------------------- NX 内置图标（直接读安装目录）
        /// <summary>NX 安装根目录（UGII_BASE_DIR）；取不到返回空串。</summary>
        private static string NxInstallRoot()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
                if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
            }
            catch { }
            // 兜底：由 NXOpen.dll 位置向上找带 NXPARTS 的目录
            try
            {
                string loc = typeof(NXOpen.Session).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    DirectoryInfo d = new DirectoryInfo(Path.GetDirectoryName(loc));
                    for (int i = 0; i < 4 && d != null; i++)
                    {
                        if (Directory.Exists(Path.Combine(d.FullName, "NXPARTS")))
                            return d.FullName;
                        d = d.Parent;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 程序组图标：直接用 NX 安装目录里自带的文件夹位图（不重绘）。
        /// 依次尝试清晰度最高的一份；全部找不到才返回 null（由上层退回矢量重绘保底）。
        /// </summary>
        private static Bitmap LoadNxFolderIcon()
        {
            string root = NxInstallRoot();
            if (root.Length == 0) return null;
            string[] rels =
            {
                @"CFD_DESIGNER\cfddesigner\application\IDB_PM_FOLDER_PNG_32x32_32_T_.bmp",      // 32px 灰底，缩小后最锐利
                @"CFD_DESIGNER\cfddesigner\application\IDB_PM_FOLDER_PNG_20x20_32_T_.bmp",      // 20px 灰底
                @"NXPARTS\Reuse Library\Configure\folder.bmp"                                   // 128px 白底（缩得太多会发虚，放最后）
            };
            foreach (string rel in rels)
            {
                try
                {
                    string p = Path.Combine(root, rel);
                    if (!File.Exists(p)) continue;
                    Bitmap b = LoadIconAndScale(p, 16);
                    if (b != null) return b;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 读一张 NX 位图 → 抠掉四周连通的背景色（白/灰）→ 高质量缩放到 size×size。
        /// NX 这些位图不带 alpha，必须先键控再缩放，否则缩完边缘会带一圈白边。
        /// </summary>
        private static Bitmap LoadIconAndScale(string path, int size)
        {
            using (Bitmap src = new Bitmap(path))
            {
                int w = src.Width, h = src.Height;
                using (Bitmap a = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(a))
                        g.DrawImage(src, new Rectangle(0, 0, w, h));

                    KeyOutBorderBackground(a);

                    Bitmap dst = new Bitmap(size, size,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(a, new Rectangle(0, 0, size, size));
                    }
                    return dst;
                }
            }
        }

        /// <summary>从图幅四边向内泛洪，把与角点背景色相近的像素置为全透明（内部高光不受影响）。</summary>
        private static void KeyOutBorderBackground(Bitmap a)
        {
            int w = a.Width, h = a.Height;
            Color bg = a.GetPixel(0, 0);
            bool[,] seen = new bool[w, h];
            Queue<System.Drawing.Point> q = new Queue<System.Drawing.Point>();
            for (int x = 0; x < w; x++)
            {
                q.Enqueue(new System.Drawing.Point(x, 0));
                q.Enqueue(new System.Drawing.Point(x, h - 1));
            }
            for (int y = 0; y < h; y++)
            {
                q.Enqueue(new System.Drawing.Point(0, y));
                q.Enqueue(new System.Drawing.Point(w - 1, y));
            }
            while (q.Count > 0)
            {
                System.Drawing.Point pt = q.Dequeue();
                int x = pt.X, y = pt.Y;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                if (seen[x, y]) continue;
                seen[x, y] = true;

                Color c = a.GetPixel(x, y);
                int d = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (d > 100) continue;                      // 已到图形边界，不再往里扩散

                a.SetPixel(x, y, Color.FromArgb(0, c.R, c.G, c.B));
                q.Enqueue(new System.Drawing.Point(x + 1, y));
                q.Enqueue(new System.Drawing.Point(x - 1, y));
                q.Enqueue(new System.Drawing.Point(x, y + 1));
                q.Enqueue(new System.Drawing.Point(x, y - 1));
            }
        }

        /// <summary>圆角矩形路径（模板页文件夹图标用）。</summary>
        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(1, rad) * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// 程序组文件夹图标：直接用 NX 安装目录自带的位图（见 LoadNxFolderIcon）；
        /// 读不到该位图时才用下面的矢量重绘保底，保证任何环境都有图标可显示。
        /// </summary>
        private static Bitmap MakeFolderGlyph()
        {
            Bitmap nxIcon = LoadNxFolderIcon();
            if (nxIcon != null) return nxIcon;

            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color edge = Color.FromArgb(150, 100, 6);
                Rectangle rcTab = new Rectangle(1, 2, 7, 5);
                Rectangle rcBody = new Rectangle(1, 5, 14, 10);
                using (System.Drawing.Drawing2D.GraphicsPath pTab = RoundRect(rcTab, 1))
                using (System.Drawing.Drawing2D.GraphicsPath pBody = RoundRect(rcBody, 2))
                {
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 226, 122)))
                        g.FillPath(tb, pTab);
                    using (System.Drawing.Drawing2D.LinearGradientBrush lg =
                        new System.Drawing.Drawing2D.LinearGradientBrush(
                            rcBody, Color.FromArgb(255, 248, 170),
                            Color.FromArgb(240, 182, 52),
                            System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                        g.FillPath(lg, pBody);
                    using (SolidBrush hl = new SolidBrush(Color.FromArgb(170, 255, 255, 245)))
                    {
                        g.FillRectangle(hl, 3, 7, 10, 2);       // 主体顶部高光
                        g.FillRectangle(hl, 2, 3, 5, 1);        // 标签顶部高光
                    }
                    using (Pen pe = new Pen(edge, 1f))
                    {
                        g.DrawPath(pe, pTab);
                        g.DrawPath(pe, pBody);
                    }
                }
            }
            return bmp;
        }

        /// <summary>
        /// 读取自定义模板（路径与“页签显示模板策略按钮”共用 cam_template.txt），
        /// 展开其程序顺序视图填充模板树。启动后自动执行一次；模板不在会话中时
        /// 打开后即关闭，不污染窗口菜单。
        /// </summary>
        /// <summary>
        /// 模板树节点数据（工序节点用）：工序名 + 所属模板的注册名（prt 文件名去扩展名）。
        /// 用对象而不是裸字符串，避免"模板根节点"被误当成工序名插入。
        /// </summary>
        private class TplNodeTag
        {
            public string Op;
            public string TemplateType;
        }

        /// <summary>
        /// 加载模板页的树：配置里每行一个模板 prt（可多个）→ 每个模板一个根节点（名字=文件名去扩展名），
        /// 下级是模板里的工序文件夹 + 加工策略。
        /// </summary>
        private void LoadTemplateTree()
        {
            if (_tplLoading || _tplLoaded) return;
            _tplLoading = true;
            try
            {
                List<string> paths = null;
                try { paths = LYJApp.TemplatePaths; } catch { }
                if (paths == null) paths = new List<string>();

                _treeTpl.Nodes.Clear();
                _tplSel.Clear();               // 旧节点已销毁，多选状态一并清掉
                _tplAnchor = null;
                if (paths.Count == 0)
                {
                    _treeTpl.Nodes.Add("还没有模板：请在配置窗口 → 模板配置 里新增");
                    return;
                }

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int okCount = 0;
                foreach (string path in paths)
                {
                    string baseName = Path.GetFileNameWithoutExtension(path ?? "");
                    if (string.IsNullOrEmpty(baseName)) baseName = (path ?? "(未知模板)");

                    // 不同目录下可能有同名模板 → 根节点自动加序号区分
                    string showName = baseName;
                    int n = 2;
                    while (!usedNames.Add(showName))
                        showName = baseName + " (" + (n++) + ")";

                    TreeNode root = new TreeNode(showName)
                    {
                        ImageIndex = 0,
                        SelectedImageIndex = 0,
                        ToolTipText = path ?? ""
                    };
                    string err = FillTemplateRoot(root, path);
                    if (err != null)
                        root.Nodes.Add(new TreeNode(err) { ImageIndex = 1, SelectedImageIndex = 1 });
                    else
                        okCount++;                 // 有模板读成功才记为已加载，全失败下次切页重试
                    _treeTpl.Nodes.Add(root);
                }
                _treeTpl.ExpandAll();
                if (okCount > 0) _tplLoaded = true;
            }
            catch (Exception ex)
            {
                _treeTpl.Nodes.Add("读取失败: " + ex.Message);
            }
            finally
            {
                _tplLoading = false;
            }
        }

        /// <summary>打开一个模板 prt 并把它的程序树挂到 root 下；出错返回错误文字，成功返回 null。
        /// 会话里本来就开着的模板直接复用，用完不关；自己打开的用完关闭。</summary>
        private string FillTemplateRoot(TreeNode root, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "文件不存在";

            Part tpl = null;
            bool wasOpen = false;
            foreach (BasePart p in _session.Parts)
            {
                if (string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    tpl = p as Part;
                    wasOpen = tpl != null;
                    break;
                }
            }

            PartLoadStatus st = null;
            try
            {
                if (tpl == null)
                    tpl = _session.Parts.Open(path, out st) as Part;   // 只读加载，不切显示部件
            }
            catch { }
            finally
            {
                if (st != null) st.Dispose();
            }
            if (tpl == null) return "模板无法打开";

            try
            {
                // CAM 数据懒加载，部分加载会漏工序（与生成器同坑）
                try
                {
                    if (!tpl.IsFullyLoaded)
                    {
                        PartLoadStatus st2 = tpl.LoadFully();
                        if (st2 != null) st2.Dispose();
                    }
                }
                catch { }

                NXOpen.CAM.CAMSetup setup = tpl.CAMSetup;
                if (setup == null) return "模板中没有加工设置";
                FillTplTree(setup.GetRoot(NXOpen.CAM.CAMSetup.View.ProgramOrder),
                    root.Nodes, Path.GetFileNameWithoutExtension(path));
                if (root.Nodes.Count == 0) return "模板里没有工序/文件夹";
                return null;
            }
            catch (Exception ex)
            {
                return "读取失败: " + ex.Message;
            }
            finally
            {
                if (!wasOpen)
                {
                    try
                    {
                        tpl.Close(BasePart.CloseWholeTree.True,
                            BasePart.CloseModified.CloseModified, null);
                    }
                    catch { }
                }
            }
        }

        /// <summary>递归填充模板程序树（跳过“未用项”系统组与空文件夹）。
        /// 工序节点纯文本显示、不带图标；程序组带 NX 的文件夹图标。
        /// templateType 一路带下去，双击插工序时才能定位到正确模板。</summary>
        private void FillTplTree(NXOpen.CAM.NCGroup node, TreeNodeCollection parent, string templateType)
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
                    if (n.Length == 0) continue;
                    // 工序节点纯文本显示（配透明占位图标，只为与文件夹行左对齐）
                    parent.Add(new TreeNode(n)
                    {
                        ImageIndex = 1,
                        SelectedImageIndex = 1,
                        Tag = new TplNodeTag { Op = n, TemplateType = templateType }
                    });
                }
                else if (m is NXOpen.CAM.NCGroup g)
                {
                    string gn = (g.Name ?? "").Trim();
                    if (gn.Length == 0 ||
                        gn.IndexOf("未用", StringComparison.Ordinal) >= 0 ||
                        gn.IndexOf("UNUSED", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    TreeNode sub = new TreeNode(gn) { ImageIndex = 0, SelectedImageIndex = 0 };
                    FillTplTree(g, sub.Nodes, templateType);
                    if (sub.Nodes.Count > 0)
                        parent.Add(sub);
                }
            }
        }

        /// <summary>
        /// 双击模板策略（工序节点）→ 插入到当前选中的组。
        /// 多选状态（Ctrl/Shift 选中多个工序）下双击 → 批量插入：目标组整批只解析一次，
        /// 整批一个撤销标记（LYJApp.InsertOpsBatch）。
        /// 工序插入的唯一入口（功能区已不再有策略按钮），底层走 LYJApp.InsertTemplateOperation，
        /// 目标组同样优先取工序导航器里当前选中的程序/几何/方法/刀具组。
        /// 文件夹节点（Tag 为 null）不处理，交给 TreeView 默认的展开/折叠。
        /// </summary>
        private void Tpl_DoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (_tplLoading || e.Node == null) return;

            // ---- 批量：多选了多个工序 → 全部按树内顺序插入 ----
            List<TplNodeTag> batch = CollectSelectedOps();
            if (batch.Count > 1)
            {
                var kv = new List<KeyValuePair<string, string>>();
                foreach (TplNodeTag t in batch)
                    kv.Add(new KeyValuePair<string, string>(t.Op, t.TemplateType));
                try
                {
                    SetHint(LYJApp.InsertOpsBatch(kv));
                    // 插入成功后清掉多选，避免再次双击重复插入
                    _tplSel.Clear();
                    _tplAnchor = null;
                    _treeTpl.Invalidate();
                    // 新工序可能成为当前选中对象，顺手刷一遍刀具/参数
                    PollToolList();
                    RefreshValues();
                }
                catch (Exception ex)
                {
                    SetHint("批量插入失败：" + ex.Message);
                }
                return;
            }

            TplNodeTag tag = e.Node.Tag as TplNodeTag;      // 只有工序节点带 Tag
            if (tag == null || string.IsNullOrEmpty(tag.Op)) return;

            try
            {
                LYJApp.InsertOpByName(tag.Op, tag.TemplateType);
                SetHint("已插入工序：" + tag.Op);
                // 新工序可能成为当前选中对象，顺手刷一遍刀具/参数
                PollToolList();
                RefreshValues();
            }
            catch (Exception ex)
            {
                SetHint("插入失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 模板树多选（自绘）

        /// <summary>自绘节点：多选高亮浅蓝底，未选中不铺底（让色键透出模型）。
        /// 文字用 GDI+ 单色栅格绘制——走 TextRenderer 会按 ClearType 采样到洋红色键，
        /// 字边会出现粉边（和 SolidColorLabel 同一个坑）。
        ///
        /// ⚠ 绝对不能用 StringFormat.GenericTypographic：它走 GDI+ 的“排版模式”，
        /// 画中文时会整段丢字（纯英文正常）。表现出来就是——把配置里的字体改成
        /// 华文仿宋 / 宋体 这类中文字体后，模板树里的中文节点名（程序组、工序名）
        /// 一片空白，但行、图标、虚线都在，看着像“树没内容”。用普通 StringFormat
        /// 就正常，且仍是 1bit 栅格、无抗锯齿，不会和洋红色键混色。</summary>
        private void Tpl_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            bool sel = _tplSel.Contains(e.Node);
            Color fore = sel ? Color.FromArgb(20, 60, 110) : _treeTpl.ForeColor;
            if (sel)
                using (SolidBrush back = new SolidBrush(Color.FromArgb(204, 228, 247)))
                    e.Graphics.FillRectangle(back, e.Bounds);

            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            // 行高不够时按字高居中撑开（大字号下 TreeView 给的 e.Bounds 偶尔比字面矮，字会被裁没）
            Font font = _treeTpl.Font;
            Rectangle r = e.Bounds;
            int h = Math.Max(r.Height, font.Height);
            r.Y += (e.Bounds.Height - h) / 2;
            r.Height = h;

            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                using (SolidBrush b = new SolidBrush(fore))
                    e.Graphics.DrawString(e.Node.Text, font, b, r, sf);
            }
        }

        /// <summary>模板树左键点击：维护多选状态。
        /// Ctrl=单个切换；Shift=从锚点范围选择；普通点击=点在选区内不动（等待批量双击）、
        /// 点在选区外重置为单选；点文件夹清空多选（交给默认展开/折叠）。</summary>
        private void Tpl_NodeClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (_tplLoading || e.Node == null || e.Button != MouseButtons.Left) return;
            bool isOp = e.Node.Tag is TplNodeTag;
            Keys mod = Control.ModifierKeys;
            if (isOp && (mod & Keys.Control) != 0)
            {
                ToggleTplSelection(e.Node);
            }
            else if (isOp && (mod & Keys.Shift) != 0)
            {
                RangeTplSelection(e.Node);
            }
            else if (isOp)
            {
                // 普通左键点在多选节点上：先不动，保留选区等批量双击；
                // 双击的第一击也走这里，所以必须不重置，否则批量就丢了
                if (!(_tplSel.Count > 1 && _tplSel.Contains(e.Node)))
                    SetTplSelection(e.Node);
            }
            else
            {
                _tplSel.Clear();
                _tplAnchor = null;
                _treeTpl.Invalidate();
            }
        }

        /// <summary>清空多选并重绘。</summary>
        private void ClearTplSelection()
        {
            _tplSel.Clear();
            _tplAnchor = null;
            if (_treeTpl != null) _treeTpl.Invalidate();
        }

        /// <summary>重置为单选（锚点同步更新）。</summary>
        private void SetTplSelection(TreeNode node)
        {
            _tplSel.Clear();
            if (node != null) _tplSel.Add(node);
            _tplAnchor = node;
            _treeTpl.Invalidate();
        }

        /// <summary>Ctrl 点击：切换一个节点的选中状态（锚点跟随）。</summary>
        private void ToggleTplSelection(TreeNode node)
        {
            if (_tplSel.Remove(node)) _tplAnchor = node;
            else { _tplSel.Add(node); _tplAnchor = node; }
            _treeTpl.Invalidate();
        }

        /// <summary>Shift 点击：从锚点到当前节点的显示顺序范围全选。</summary>
        private void RangeTplSelection(TreeNode node)
        {
            var all = new List<TreeNode>();
            FlattenNodes(_treeTpl.Nodes, all);
            int ia = _tplAnchor != null ? all.IndexOf(_tplAnchor) : -1;
            int ib = all.IndexOf(node);
            _tplSel.Clear();
            if (ia >= 0 && ib >= 0)
            {
                int lo = Math.Min(ia, ib), hi = Math.Max(ia, ib);
                for (int i = lo; i <= hi; i++) _tplSel.Add(all[i]);
            }
            else
            {
                _tplSel.Add(node);
            }
            _treeTpl.Invalidate();
        }

        /// <summary>按显示顺序摊平整棵树的节点（Shift 范围选择用）。</summary>
        private static void FlattenNodes(TreeNodeCollection coll, List<TreeNode> outList)
        {
            foreach (TreeNode n in coll)
            {
                outList.Add(n);
                FlattenNodes(n.Nodes, outList);
            }
        }

        /// <summary>当前多选里的工序节点（按选择顺序；只有工序节点带 TplNodeTag）。</summary>
        private List<TplNodeTag> CollectSelectedOps()
        {
            var ops = new List<TplNodeTag>();
            foreach (TreeNode n in _tplSel)
            {
                TplNodeTag t = n.Tag as TplNodeTag;
                if (t != null && !string.IsNullOrEmpty(t.Op)) ops.Add(t);
            }
            return ops;
        }

        /// <summary>标题列宽度 = 当前显示的各行标题里最长的实测宽度（单行测量，禁折行）。</summary>
        private int MeasureCaptionWidth(List<string> rows)
        {
            int max = 0;
            foreach (string id in rows)
            {
                RowDef def = DefOf(id);
                if (def != null) max = Math.Max(max, MeasureSingleLine(def.Caption));
            }
            return max + 6;                             // 留一点余量，防止边缘被裁
        }

        private int MeasureSingleLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        }

        // ----------------------------------------------------------- 编辑状态机
        /// <summary>整行热区点击分派（热区盖在行控件上，标签自身的 Click 收不到）。</summary>
        private void ClickRow(string id)
        {
            if (_editing == id) { CancelEdit(); return; }   // 再点同一行 = 取消编辑
            RowDef def = DefOf(id);
            if (def == null) return;
            if (def.Kind == RowEdit.Pick) { TogglePick(id); return; }
            if (def.Kind == RowEdit.ReadOnly)
            {
                RefreshIfStale();                          // 先按最新数据刷一遍再提示
                SetHint("该行只读：点选工序后自动显示");
                return;
            }
            BeginTextEdit(id);
        }

        /// <summary>鼠标移回窗口时补刷一次（用户在 NX 自己的对话框里改了参数，回到这里就能看到）。
        /// 400ms 内不重复刷，避免拖动鼠标蹭出一串刷新。</summary>
        private void RefreshIfStale()
        {
            if (_op == null || _editing != null) return;
            if (unchecked(Environment.TickCount - _lastRefresh) < 400) return;
            RefreshValues();
        }

        /// <summary>数值行进入编辑态：文本标签换成输入框。<param name="id">行 Id</param></summary>
        private void BeginTextEdit(string id)
        {
            if (_editing == id) return;
            CancelEdit();
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            Label val;
            if (!_valLabels.TryGetValue(id, out val) || val == null) return;

            _loading = true;
            _txtEdit.SetBounds(val.Left, val.Top, Math.Max(60, val.Width), val.Height);
            _txtEdit.Text = (val.Text == "") ? "" : val.Text;
            _txtEdit.BackColor = Color.White;       // 编辑时输入框不透明，结束时恢复色键
            _txtEdit.ForeColor = Color.FromArgb(30, 30, 30);
            _txtEdit.Visible = true;
            _txtEdit.BringToFront();                // 盖在整行热区面板之上
            val.Visible = false;
            _editing = id;
            _editingTool = false;
            _loading = false;

            TopMost = true;                         // 编辑态临时置顶，保证输入框可见
            _txtEdit.Focus();
            _txtEdit.SelectAll();
            // 多选工序时提前告知：回车会把该值应用到全部所选工序
            List<NXOpen.CAM.Operation> batch = IsBatchRow(id) ? SelectedOperations() : null;
            if (batch != null && batch.Count > 1)
                SetHint("已选 " + batch.Count + " 道工序：回车会同时改完全部，Esc 取消");
            else
                SetHint("输入后按回车确认，点其他处取消");
        }

        /// <summary>回车：提交正在编辑的那一行。</summary>
        private void CommitTextEdit()
        {
            if (_editing == null) return;
            string row = _editing;
            string text = (_txtEdit.Text ?? "").Trim();

            if (row == "op")
            {
                if (text.Length == 0) { SetHint("名称不能为空"); return; }   // 留在编辑态等修正
                EndEdit();
                ApplyRename(text);
                return;
            }

            // 刀尖步距可以有多段：输入用逗号分隔（3,2,1），显示用“丨”串起来
            if (row == "tipstep")
            {
                double[] vals;
                if (!TryParseStepList(text, out vals))
                {
                    SetHint("请输入数字，多段用逗号分隔，如 3,2,1");
                    return;
                }
                EndEdit();
                ApplyTipStep(vals);
                return;
            }

            double v;
            if (!double.TryParse(text, out v))
            {
                SetHint("请输入数字");
                return;                                 // 留在编辑态等待修正
            }
            string err;
            if (!ValidateValue(row, v, out err))
            {
                SetHint(err);
                return;
            }
            EndEdit();

            // 批量：NX 里 Ctrl 多选了多道工序时，数值行回车 = 一次改完全部（单选走原路径）
            List<NXOpen.CAM.Operation> multi = IsBatchRow(row) ? SelectedOperations() : null;
            if (multi != null && multi.Count > 1)
            {
                ApplyValueBatch(multi, row, v);
                return;
            }
            ApplyValue(row, v);
        }

        /// <summary>该行是否支持批量写入（与原批量页同范围的 9 个数值行）。</summary>
        private static bool IsBatchRow(string id)
        {
            switch (id)
            {
                case "spindle": case "feed": case "tolinner": case "tolouter":
                case "partstock": case "floorstock": case "stepover":
                case "depthpercut": case "toolno":
                    return true;
                default: return false;
            }
        }

        /// <summary>
        /// 多选工序的批量写入：一次 UndoMark 包裹（Ctrl+Z 一步撤销）。
        /// 单行只改一个值，其余参数各工序保持原样；个别工序不支持该参数时记入失败名单。
        /// </summary>
        private void ApplyValueBatch(List<NXOpen.CAM.Operation> ops, string row, double v)
        {
            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：批量修改");
            int ok = 0;
            List<string> failed = new List<string>();
            foreach (NXOpen.CAM.Operation op in ops)
            {
                try
                {
                    switch (row)
                    {
                        case "spindle": BatchWriteFeeds(op, true, v, false, 0); break;
                        case "feed": BatchWriteFeeds(op, false, 0, true, v); break;
                        case "tolinner": BatchWriteTol(op, true, v, false, 0); break;
                        case "tolouter": BatchWriteTol(op, false, 0, true, v); break;
                        case "partstock":
                            _ufs.Param.SetDoubleValue(op.Tag,
                                (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_PART, v);
                            break;
                        case "floorstock":
                            _ufs.Param.SetDoubleValue(op.Tag,
                                (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_FLOOR, v);
                            break;
                        case "stepover": BatchWriteStepover(op, v); break;
                        case "depthpercut": BatchWriteDepthPerCut(op, v); break;
                        case "toolno": BatchWriteToolNo(op, (int)Math.Round(v)); break;
                    }
                    ok++;
                }
                catch { failed.Add(op.Name); }
            }

            if (ok == 0)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("全部失败（参数类型可能不支持），已撤销");
                return;
            }
            RefreshValues();
            RowDef def = DefOf(row);
            SetHint(string.Format("已把 {0}={1:0.####} 应用到 {2} 道工序（Ctrl+Z 可撤销）{3}",
                def == null ? row : def.Caption, v, ok,
                failed.Count > 0 ? "；失败: " + string.Join("、", failed.ToArray()) : ""));
        }

        /// <summary>按行校验输入（不合法返回 false 并给出提示语）。</summary>
        private bool ValidateValue(string row, double v, out string err)
        {
            err = null;
            if (double.IsNaN(v) || double.IsInfinity(v)) { err = "数值无效"; return false; }

            switch (row)
            {
                case "spindle":
                case "feed":
                    if (v <= 0 || v > 100000) err = "请输入 0 以上的数值";
                    break;
                case "stepover":
                case "depthpercut":
                    if (v < 0) err = "请输入 0 或正数";
                    else if (v >= 9000) err = "数值超出范围";
                    break;
                case "tolinner":
                case "tolouter":
                    if (v < 0 || v >= 9000) err = "请输入 0 以上的数值";
                    break;
                case "partstock":
                case "floorstock":
                    if (Math.Abs(v) >= 9000) err = "数值超出范围";
                    break;
                case "toolno":
                    if (v < 0 || v > 9999 || Math.Abs(v - Math.Round(v)) > 1e-6)
                        err = "刀具编号需为 0~9999 的整数";
                    break;
                case "tipdepth":
                case "tipstep":
                    if (v < 0) err = "请输入 0 或正数";
                    break;
            }
            return err == null;
        }

        /// <summary>候选项：程序组 / 刀路坐标（MCS）/ 刀具，三者共用同一个列表控件；
        /// 刀尖类型这种固定选项表用 Value 字段带返回值（Group 为 null）。</summary>
        private class PickItem
        {
            public string Text;
            public NXOpen.CAM.NCGroup Group;
            public int Value = int.MinValue;      // 固定选项表（刀尖类型）用
            public override string ToString() { return Text; }
        }

        /// <summary>展开/收起候选项列表（程序组、刀路坐标、刀具、枚举行共用）。
        /// 异常一律在本层吃掉：WinForms 消息循环里的未处理异常会弹出 .NET 错误框（NX 里很吓人）。</summary>
        private void TogglePick(string id)
        {
            try
            {
                TogglePickCore(id);
            }
            catch (Exception ex)
            {
                try { CancelEdit(); } catch { }
                SetHint("展开选项失败：" + ex.Message);
            }
        }

        private void TogglePickCore(string id)
        {
            if (_editing == id) { CancelEdit(); return; }   // 再点同一行 = 收起
            CancelEdit();
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            List<PickItem> items = BuildPicks(id);
            if (items.Count == 0)
            {
                SetHint(id == "group" ? "程序视图里没有可选程序组"
                      : id == "mcs" ? "几何视图里没有可选 MCS 坐标"
                      : "机床上没有可用刀具");
                return;
            }

            _loading = true;
            _lstPick.BeginUpdate();
            _lstPick.Items.Clear();
            int selIdx = -1;
            string curName = _valLabels[id].Text;
            foreach (PickItem it in items)
            {
                _lstPick.Items.Add(it);
                if (it.Text == curName) selIdx = _lstPick.Items.Count - 1;
            }
            if (selIdx >= 0) _lstPick.SelectedIndex = selIdx;
            _lstPick.EndUpdate();

            // ---- 尺寸：宽度按最长选项，高度按选项个数（一次列全，屏幕装不下才滚动）----
            int itemH = 18;
            try
            {
                _lstPick.CreateControl();                                  // 先建句柄，行高才是真实值
                itemH = Math.Max(itemH, _lstPick.GetItemHeight(0));
            }
            catch { }
            int textW = 0;
            foreach (PickItem it in items)
                textW = Math.Max(textW, TextRenderer.MeasureText(it.Text, _lstPick.Font).Width);
            int scrollW = System.Windows.Forms.SystemInformation.VerticalScrollBarWidth;
            int w = Math.Max(170, textW + 16 + scrollW);
            int wantH = _lstPick.Items.Count * itemH + 4;

            // ---- 位置：行在屏幕上的真实矩形 ----
            // 关键：PointToScreen(Point.Empty) 才是控件左上角的屏幕坐标；
            // 之前传 (val.Left, val.Bottom) 等于把标签在父容器里的偏移又算了一遍，
            // 于是弹层整体往右下角跑偏（右移一个值列宽、下移一个行号高度），
            // 而且算出的“下方空间”偏小，第一次打开就只能缩成带滚动条的矮框。
            System.Drawing.Point valPos = val0(id).PointToScreen(System.Drawing.Point.Empty);
            Size valSize = val0(id).Size;
            Rectangle screen = Screen.FromControl(this).WorkingArea;

            int belowY = valPos.Y + valSize.Height + 2;
            int spaceBelow = screen.Bottom - 2 - belowY;
            int spaceAbove = valPos.Y - 2 - (screen.Top + 2);

            int h, y;
            if (wantH <= spaceBelow || spaceBelow >= spaceAbove)
            {
                h = Math.Min(wantH, Math.Max(spaceBelow, itemH * 2));      // 优先向下展开
                y = belowY;
            }
            else
            {
                h = Math.Min(wantH, Math.Max(spaceAbove, itemH * 2));      // 下方不够就向上展开
                y = valPos.Y - 2 - h;
            }
            h = Math.Max(itemH * 2, h);

            int x = valPos.X;
            if (w > screen.Width - 8) w = screen.Width - 8;
            if (x + w > screen.Right - 2) x = screen.Right - 2 - w;
            if (x < screen.Left + 2) x = screen.Left + 2;

            _pickHost.Bounds = new Rectangle(x, y, Math.Max(140, w), h);

            // 关键时序：Show 期间保持 _loading=true、_editing/_pickOpen 不置位。
            // Show 内部创建/重建弹层句柄时可能触发一次 SelectedIndexChanged，
            // 若此刻 _pickOpen/_editing 已就位，会误触发 CommitPick：EndEdit 清空编辑态后
            // Show 继续完成 → 弹层可见但已成“死层”（点选项不生效、还挡住下面的参数行）。
            if (_pickHost.Visible)
            {
                _pickClosing = true;
                try { _pickHost.Hide(); }
                catch { }
                finally { _pickClosing = false; }
            }

            try
            {
                _pickHost.Show(this);       // 不激活：助手窗仍保有焦点，点其他行不用点两次
                _pickHost.BringToFront();
            }
            catch (Exception)
            {
                // 极少数情况下仍判定为可见：收掉重试一次，别把异常抛到 NX 里弹未处理错误框
                try
                {
                    _pickHost.Visible = false;
                    _pickHost.Show(this);
                    _pickHost.BringToFront();
                }
                catch
                {
                    _loading = false;
                    _pickOpen = false;
                    _editing = null;
                    _editingTool = false;
                    SetHint("选项列表展开失败，请再点一次该行");
                    return;
                }
            }

            _editing = id;
            _editingTool = true;
            _loading = false;
            _pickOpen = true;              // 弹层已稳定显示，现在才允许选择事件提交
            SetHint(id == "tool" ? "选择刀具即生效，点其他处取消"
                  : id == "group" ? "选择程序组即生效，点其他处取消"
                  : "选择 MCS 即生效，点其他处取消");
        }

        private Label val0(string id)
        {
            Label l;
            return _valLabels.TryGetValue(id, out l) ? l : null;
        }

        /// <summary>收起候选项弹层（幂等；不改变编辑状态）。</summary>
        private void HidePick()
        {
            _pickOpen = false;
            if (_pickHost == null || _pickHost.IsDisposed || !_pickHost.Visible) return;
            _pickClosing = true;
            try { _pickHost.Hide(); }
            catch { }
            finally { _pickClosing = false; }
            // Hide 万一没生效（窗口正处在显示过程中等），再强制一次，务必让 Visible=false，
            // 否则下一次 Show(owner) 会抛“已经可见的窗体不能显示为模式对话框”。
            if (_pickHost.Visible)
            {
                try { _pickHost.Visible = false; } catch { }
            }
        }

        /// <summary>提交候选选择（列表选中即生效）。</summary>
        private void CommitPick()
        {
            if (_editing == null) return;
            string id = _editing;
            PickItem it = _lstPick.SelectedItem as PickItem;
            EndEdit();
            if (it == null) return;
            // if (id == "tiptype") { ApplyTipType(it.Value); return; }   // 刀尖类型行已临时移除
            if (id == "cutmode") { ApplyEnumParam("切削模式", UF_CUT_MODE, it.Value); return; }
            if (id == "cutdir") { ApplyEnumParam("切削方向", UF_CUT_DIRECTION, it.Value); return; }
            if (id == "cutdir2") { ApplyEnumParam("刀路方向", UF_CUTDIR_TYPE, it.Value); return; }
            if (id == "levelmode") { ApplyEnumParam("层到层", UF_LEVEL_MODE, it.Value); return; }
            if (id == "sideflooreq") { ApplyEnumParam("侧底一致", UF_SIDE_FLOOR_EQ, it.Value); return; }
            if (it.Group == null) return;
            if (id == "tool") ApplyToolGroup(it.Group);
            else if (id == "group") ApplyMoveTo(NXOpen.CAM.CAMSetup.View.ProgramOrder, it.Group, "改程序组");
            else if (id == "mcs") ApplyMoveTo(NXOpen.CAM.CAMSetup.View.Geometry, it.Group, "改刀路坐标");
        }

        /// <summary>Esc / 切换编辑目标：还原显示，不写任何参数。</summary>
        private void CancelEdit()
        {
            if (_editing == null)
            {
                // 没有编辑态但弹层还挂着（Deactivate 里 Hide 被吞、异常中断等）：
                // 一并收起，否则下次 Show 会抛“已经可见的窗体不能显示为模式对话框”。
                HidePick();
                return;
            }
            EndEdit();
            SetHint(" ");
        }

        private void EndEdit()
        {
            _editing = null;
            _editingTool = false;
            _loading = true;
            HidePick();
            _txtEdit.Visible = false;
            _txtEdit.BackColor = KeyColor;      // 恢复色键透明
            _loading = false;
            TopMost = false;                // 编辑结束：交还顶层，不再遮挡其他窗口
            Relayout();                         // 恢复各行显示状态与位置
        }

        private void SetHint(string text)
        {
            if (_lblHint != null) _lblHint.Text = text;
        }

        // --------------------------------------------------------------- 轮询
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // 图档关了/切走了：窗口立即跟着关，不留残留窗口
                if (_partClosed || PartChanged())
                {
                    // 配置窗口是模态的，它开着时不能销毁宿主窗体；先挂起，等它关掉再收
                    if (_configOpen) { _closePending = true; return; }
                    _timer.Stop();
                    Close();
                    return;
                }

                _tickCount++;

                // 定时器只做两件“必须看住”的轻活，都不扫参数：
                //   1) 选中对象变没变 —— 每拍只看“显示部件 + 选中数量 + 末位对象 Tag”，O(1)；
                //   2) 图形视口挪没挪 —— 每秒一次，锚点没变就直接跳过。
                // 参数值不再定时回读，改为关键动作触发：选中工序 / 提交修改 / 点某一行 / 鼠标移回窗口。
                {
                    Part disp = _session.Parts.Display;
                    if (disp == null)
                    {
                        if (_op != null) ResetDisplay("部件已关闭");
                        return;
                    }
                    int n = _ui.SelectionManager.GetNumSelectedObjects();
                    NXOpen.Tag last = NXOpen.Tag.Null;
                    if (n > 0)
                    {
                        NXOpen.NXObject o = _ui.SelectionManager.GetSelectedObject(n - 1);
                        if (o != null) last = o.Tag;
                    }
                    if (n != _selCount || !last.Equals(_selLastTag))
                    {
                        _selCount = n;
                        _selLastTag = last;
#if LYJ_HOLEPROBE
                        ProbeSelected();
#endif
                        // 末位选中的正好是当前正在显示的工序 = 重复点选同一个对象，
                        // 参数一个都没变，连识别都不用做，直接跳过。
                        if (_op == null || !last.Equals(_op.Tag))
                        {
                            NXOpen.CAM.Operation op = LastSelectedOperation();
                            if (op != null && (_op == null || !op.Tag.Equals(_op.Tag)))
                                QueueLoadOperation(op);  // 换工序 = 扫一遍参数（延后一拍，不挡 NX 自己的选中处理）
                        }
                    }
                }

                if (_tickCount % 5 == 0)                 // 约 1s：停靠跟随
                {
                    Rectangle a = AnchorRect();
                    // 正在编辑时不要动窗口：否则弹层/输入框会跟着窗口错位
                    if (!a.IsEmpty && a != _lastAnchor && _editing == null)
                    {
                        _lastAnchor = a;
                        PlaceAtAnchor(a);
                        // 模板页撑满图形区：视口大小变了要重排，否则溢出/留白
                        bool resize = (_curPage == 0 && a.Height != _tplAnchorH) ||
                                      Math.Abs(a.Width - _anchorW) >= 2;
                        if (resize) Relayout();
                    }
                }

                // 启动约 1s 后自动读取一次模板策略树（主线程内一次小开销，之后不再读）
                if (_tickCount == 5)
                    LoadTemplateTree();

                // 模板页衬底：位置每拍对齐（便宜），z 序每秒重申一次
                SyncTplBackdrop(_tickCount % 5 == 0);
            }
            catch { }
        }

        /// <summary>
        /// 轮询识别机床刀具视图下的所有刀具：重扫清单，签名比对，有变化才更新
        /// （列表展开时跳过，避免打断选择）。
        /// </summary>
        private void PollToolList() { PollToolList(false); }

        /// <summary>force=true：忽略签名缓存强制重扫（打开刀具下拉时用）。
        /// 平时不再周期调用——物化整棵 CAM 组树在工序多的图档上很贵。</summary>
        private void PollToolList(bool force)
        {
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) return;
            if (_editing == "tool") return;

            List<ToolItem> tools = CollectTools(work);
            string sig = string.Join("|", tools.ConvertAll(t => t.Name));
            if (!force && sig == _toolSig) return;
            _toolSig = sig;
            _tools = tools;
        }

        /// <summary>选中对象里从后往前找第一个加工工序。</summary>
        private NXOpen.CAM.Operation LastSelectedOperation()
        {
            int n = _ui.SelectionManager.GetNumSelectedObjects();
            // 选中的对象可能成百上千（框选面/体），每次 tick 全遍历会拖慢 NX：
            // 只从末位往前找最近 60 个，够覆盖“多点选了几刀路”的常规场景。
            int stop = Math.Max(0, n - 60);
            for (int i = n - 1; i >= stop; i--)
            {
                var o = _ui.SelectionManager.GetSelectedObject(i);
                var op = o as NXOpen.CAM.Operation;
                if (op != null) return op;
            }
            return null;
        }

        // ------------------------------------------------------------ 读与显示
        /// <summary>排队读取新选中的工序：先让 NX 处理完这次点选（选中集、高亮、显示），
        /// 我们再在消息队列里读参数。原来在定时器回调里同步读，NX 会觉得“点一下顿一下”。</summary>
        private void QueueLoadOperation(NXOpen.CAM.Operation op)
        {
            _pendingOp = op;
            if (_loadQueued) return;                     // 已有排队：只更新目标，不重复排队
            _loadQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    _loadQueued = false;
                    NXOpen.CAM.Operation o = _pendingOp;
                    _pendingOp = null;
                    if (o == null || IsDisposed) return;
                    if (_op == null || !o.Tag.Equals(_op.Tag)) LoadOperation(o);
                }));
            }
            catch { _loadQueued = false; }
        }

        private void LoadOperation(NXOpen.CAM.Operation op)
        {
            CancelEdit();
            _loading = true;
            _op = op;
            try
            {
                _opPart = _session.Parts.Work;
                SetHint(" ");
                // 刀具清单不在这里扫：CollectTools 要遍历整棵 CAM 组树（工序多的图档慢到卡 UI），
                // 改成只在真正点开“刀具名称”下拉时扫一次。
                _timeTag = NXOpen.Tag.Null;         // 换工序 → 加工时间重算
                _timeText = "";
                RefreshValues();
            }
            catch (Exception ex)
            {
                SetHint("读取失败: " + ex.Message);
            }
            finally { _loading = false; }
        }

        /// <summary>回读当前工序所有行的值（只在关键时刻调用：选中工序、提交修改、点行、鼠标移回窗口）。</summary>
        private void RefreshValues()
        {
            if (_op == null) return;
            InvalidateCaches();                         // 本轮重新读一次转速/进给、刀具、公差
            _lastRefresh = Environment.TickCount;
            _loading = true;
            try
            {
                SetVal("op", _op.Name);

                // 刀具
                NXOpen.CAM.NCGroup curTool = CurrentToolOf(_op);
                SetVal("tool", curTool != null && !string.IsNullOrEmpty(curTool.Name)
                    ? curTool.Name : "（未指定）");

                // 加工时间：换工序后先清空，避免短暂显示上一工序的时间（真正取值在 ReadToolpathTime）
                if (!_timeTag.Equals(_op.Tag)) SetVal("time", "");

                // 其余数值行（加工时间单独处理：它要按刀轨实时算，最慢的一项）
                foreach (string id in VisibleRows())
                {
                    if (id == "op" || id == "tool" || id == _editing) continue;
                    if (id == "time") continue;
                    SetVal(id, ReadRowText(id));
                }
            }
            catch { }
            finally { _loading = false; }

            ScheduleTimeRead();
        }

        /// <summary>清掉“单次刷新缓存”（换工序、写参数、重新回读时调用）。</summary>
        private void InvalidateCaches()
        {
            _feedsOpTag = NXOpen.Tag.Null;
            _toolCacheTag = NXOpen.Tag.Null;
            _toolCacheGrp = null;
            _tolTag = NXOpen.Tag.Null;
            _drillTag = NXOpen.Tag.Null;
        }

        /// <summary>当前工序是不是钻孔类（决定“刀尖深度/类型/步距”三行要不要去问 NX）。
        /// 铣削工序上这三个参数根本不存在，以前每行都要 try/catch 抛几次异常，白耗时间。</summary>
        private bool IsDrillOp()
        {
            if (_op == null) return false;
            if (!NXOpen.Tag.Null.Equals(_drillTag) && _drillTag.Equals(_op.Tag)) return _isDrill;

            bool drill = false;
            try
            {
                NXOpen.CAM.Tool t = CurrentToolOf(_op) as NXOpen.CAM.Tool;
                if (t != null)
                {
                    NXOpen.CAM.Tool.Types ty;
                    NXOpen.CAM.Tool.Subtypes st;
                    t.GetTypeAndSubtype(out ty, out st);
                    drill = ty == NXOpen.CAM.Tool.Types.Drill;
                }
            }
            catch { }
            _drillTag = _op.Tag;
            _isDrill = drill;
            return drill;
        }

        /// <summary>加工时间读取：优先直读 NX 存在工序里的「Toolpath Time」参数——
        /// 程序顺序视图显示的就是这个值，NX 每次生成刀轨时算好写入，读取零开销、不做实时计算。
        /// 参数索引 124 为实测（三次重生成刀轨值随之更新）；用名字校验防版本差异，对不上再走官方 API 兜底。
        /// 直读足够便宜，每次触发刷新都重读一次，重新生成刀轨后时间能立即跟上。</summary>
        private void ScheduleTimeRead()
        {
            if (_op == null || !IsHandleCreated || IsDisposed) return;
            if (!VisibleRows().Contains("time")) return;

            NXOpen.Tag tag = _op.Tag;
            try { BeginInvoke((MethodInvoker)(() => ReadToolpathTime(tag))); }
            catch { }
        }

        /// <summary>UF 直读「Toolpath Time」参数（索引 124，名字校验通过才用）；读不到返回 false。</summary>
        private bool AskToolpathTimeParam(out double minutes)
        {
            minutes = 0;
            if (_op == null) return false;
            const int idxToolpathTime = 124;
            if (_timeUfIdx == 0)
            {
                try
                {
                    NXOpen.UF.UFParam.IndexAttribute a;
                    _ufs.Param.AskParamAttributes(idxToolpathTime, out a);
                    _timeUfIdx = (a.name != null &&
                        a.name.Trim().Equals("Toolpath Time", StringComparison.OrdinalIgnoreCase))
                        ? idxToolpathTime : -1;
                }
                catch { _timeUfIdx = -1; }
            }
            if (_timeUfIdx != idxToolpathTime) return false;
            return TryAskUfDouble(idxToolpathTime, out minutes);
        }

        /// <summary>分钟 → H:mm:ss（小时不封顶、不进位成天，最高单位就是小时）。</summary>
        private static string FormatMinutes(double t)
        {
            int sec = (int)Math.Round(t * 60.0);
            return (sec / 3600) + ":" +
                ((sec % 3600) / 60).ToString("00") + ":" +
                (sec % 60).ToString("00");
        }

        private void ReadToolpathTime(NXOpen.Tag tag)
        {
            if (IsDisposed || _op == null || !_op.Tag.Equals(tag)) return;
            string text = "";
            double v;
            if (AskToolpathTimeParam(out v) && v > 0)
            {
                text = FormatMinutes(v);                    // NX 存好的时间（程序视图同款）
            }
            else
            {
                try
                {
                    double t = _op.GetToolpathTime();       // 分钟（NX6+），兜底
                    if (t > 0) text = FormatMinutes(t);
                }
                catch { }
            }
            _timeTag = tag;
            _timeText = text;
            if (_op != null && _op.Tag.Equals(tag)) SetVal("time", text);
        }

        /// <summary>文本没变就不赋值——Label.Text 的 setter 会触发重绘和布局，轮询时反复写会明显耗 CPU。</summary>
        private void SetVal(string id, string text)
        {
            Label l;
            if (!_valLabels.TryGetValue(id, out l) || l == null) return;
            if (!string.Equals(l.Text, text, StringComparison.Ordinal)) l.Text = text;
        }

        /// <summary>读单行显示文本（读取失败或该工序类型不支持时返回空白）。</summary>
        private string ReadRowText(string id)
        {
            try
            {
                switch (id)
                {
                    case "group": return GroupNameOf(_op);
                    case "mcs": return McsNameOf(_op);
                    case "toolno": return ToolNoText();
                    case "realtool": return RealToolNameText();
                    case "optype": return OpTypeText();
                    case "toollen": return ToolRealText(1001, "TL_HEIGHT", "");
                    case "toollflen": return ToolRealText(1002, "TL_FLUTE_LN", "");
                    case "tooldesc": return ToolStrText(1068);
                    case "taperang": return ToolRealText(1003, "TL_TAPER_ANG", "°");
                    case "tipang": return ToolRealText(1004, "TL_TIP_ANG", "°");
                    case "holderlib": return ToolStrText(1063);
                    case "holderoff": return ToolRealText(1049, "TL_HOLDER_OFFSET", "");
                    case "minlen": return ToolRealText(5709, "TL_MIN_LENGTH", "");
                    case "cutmode": return EnumRowText(UF_CUT_MODE, CutModeLabels, true);
                    case "cutdir": return EnumRowText(UF_CUT_DIRECTION, CutDirLabels, true);
                    case "cutdir2": return EnumRowText(UF_CUTDIR_TYPE, CutDir2Labels, true);
                    case "levelmode": return EnumRowText(UF_LEVEL_MODE, LevelModeLabels, false);
                    case "sideflooreq": return EnumRowText(UF_SIDE_FLOOR_EQ, SideFloorEqLabels, false);
                    case "tooldia": return ToolDiameterText();
                    case "toolcorner": return ToolCornerText();
                    case "tooltype": return ToolTypeText();
                    case "holder": return ToolHolderText();
                    case "tipdepth": return ReadTipDepthText();
                    // case "tiptype": return ReadTipTypeText();      // 刀尖类型行已临时移除
                    case "tipstep": return ReadTipStepText();
                    case "spindle":
                    case "feed":
                        {
                            // FeedsBuilder 的创建/销毁不算便宜：同一轮刷新里两行共用一次读取
                            if (!_feedsOpTag.Equals(_op.Tag))
                            {
                                double s0, f0;
                                AskFeeds(_op, out s0, out f0);
                                _feedsS = s0; _feedsF = f0; _feedsOpTag = _op.Tag;
                            }
                            double v = id == "spindle" ? _feedsS : _feedsF;
                            return v > 0 ? FormatValue(v) : "";
                        }
                    case "partstock":
                        return FormatValue(AskUfDouble(
                            (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_PART));
                    case "floorstock":
                        return FormatValue(AskUfDouble(
                            (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_FLOOR));
                    case "tolinner":
                        {
                            double[] t = AskTol();
                            return t[0] >= 9000 ? "" : FormatValue(t[0]);
                        }
                    case "tolouter":
                        {
                            double[] t = AskTol();
                            return t[1] >= 9000 ? "" : FormatValue(t[1]);
                        }
                    case "stepover":
                        return ReadStepoverText();
                    case "depthpercut":
                        return ReadDepthPerCutText();
                    case "time":
                        // 实际计算在 ReadToolpathTime 里做（异步、带缓存），这里只取缓存值
                        return _timeTag.Equals(_op.Tag) ? _timeText : "";
                }
            }
            catch { }
            return "";
        }

        private static string FormatValue(double v)
        {
            return v.ToString("0.####");
        }

        private void ResetDisplay(string why)
        {
            CancelEdit();
            _loading = true;
            _op = null; _opPart = null;
            _valLabels["op"].Text = why + "，请点选工序";
            _valLabels["tool"].Text = "（未指定）";
            foreach (string id in _valLabels.Keys)
                if (id != "op" && id != "tool")
                    _valLabels[id].Text = "";
            _tools = new List<ToolItem>();
            _toolSig = null;
            InvalidateCaches();
            _timeTag = NXOpen.Tag.Null;
            _timeText = "";
            SetHint(" ");
            _loading = false;
        }

        private class ToolItem
        {
            public string Name;
            public NXOpen.CAM.NCGroup Group;
            public override string ToString() { return Name; }
        }

        /// <summary>
        /// 收集当前部件的刀具（三策略并集，Tag 去重）：
        ///   1) CAMGroupCollection.ToArray() 全量物化后判型 NXOpen.CAM.Tool
        ///      —— e2925 验证过的可靠路径（迭代器枚举可能漏未触碰对象）；
        ///   2) 自底向上：父链能走到机床刀具视图根的叶子组（不依赖托管类型）；
        ///   3) 自顶向下：机床刀具视图树 DFS。
        /// </summary>
        private List<ToolItem> CollectTools(Part part)
        {
            var list = new List<ToolItem>();
            var seen = new HashSet<Tag>();
            var setup = part.CAMSetup;
            if (setup == null) return list;

            // 1) 全量物化 + 判型
            try
            {
                foreach (NXOpen.CAM.NCGroup g in setup.CAMGroupCollection.ToArray())
                {
                    if (!(g is NXOpen.CAM.Tool)) continue;
                    if (seen.Add(g.Tag))
                        list.Add(new ToolItem { Name = g.Name, Group = g });
                }
            }
            catch { }

            // 常规情况一次物化就够；只有判型一个都没找到时才走下面两轮兜底
            // （每轮都要把整棵树再走一遍，属于 O(n²) 级别的调用，不能每秒做）
            if (list.Count > 0) return list;

            // 2) 自底向上：机床视图里的叶子组（覆盖判型漏掉的旧刀具）
            NXOpen.CAM.NCGroup mctRoot = RootOf(setup);
            if (mctRoot != null)
            {
                try
                {
                    foreach (NXOpen.CAM.NCGroup g in setup.CAMGroupCollection.ToArray())
                    {
                        if (seen.Contains(g.Tag)) continue;
                        if (!IsMachineToolViewLeaf(g, mctRoot.Tag)) continue;
                        if (seen.Add(g.Tag) && !string.IsNullOrEmpty(g.Name))
                            list.Add(new ToolItem { Name = g.Name, Group = g });
                    }
                }
                catch { }
            }

            // 3) 视图树 DFS 兜底
            CollectToolDfs(RootOf(setup), list, seen);
            return list;
        }

        /// <summary>
        /// 组是否为机床刀具视图下的叶子（无子组）——即一把刀具。
        /// NCGroup.GetParent() 无视图参数，改用“父链能走到机床视图根”判定归属。
        /// </summary>
        private bool IsMachineToolViewLeaf(NXOpen.CAM.NCGroup g, Tag mctRoot)
        {
            try
            {
                NXOpen.CAM.NCGroup p = g.GetParent();
                bool inView = false;
                while (p != null)
                {
                    if (p.Tag.Equals(mctRoot)) { inView = true; break; }
                    p = p.GetParent();
                }
                if (!inView) return false;
                NXOpen.CAM.CAMObject[] kids = null;
                try { kids = g.GetMembers(); } catch { }
                return kids == null || kids.Length == 0;
            }
            catch { return false; }
        }

        private NXOpen.CAM.NCGroup RootOf(NXOpen.CAM.CAMSetup setup)
        {
            try
            {
                return setup.GetRoot(NXOpen.CAM.CAMSetup.View.MachineTool)
                    as NXOpen.CAM.NCGroup;
            }
            catch { return null; }
        }

        private void CollectToolDfs(NXOpen.CAM.NCGroup g, List<ToolItem> list, HashSet<Tag> seen)
        {
            if (g == null || !seen.Add(g.Tag)) return;
            NXOpen.CAM.CAMObject[] members = null;
            try { members = g.GetMembers(); } catch { }
            if (members == null || members.Length == 0)
            {
                if (!string.IsNullOrEmpty(g.Name))
                    list.Add(new ToolItem { Name = g.Name, Group = g });
                return;
            }
            foreach (NXOpen.CAM.CAMObject m in members)
                CollectToolDfs(m as NXOpen.CAM.NCGroup, list, seen);
        }

        /// <summary>工序在机床刀具视图里的父组（即刀具），取不到返回 null。</summary>
        private NXOpen.CAM.NCGroup CurrentToolOf(NXOpen.CAM.Operation op)
        {
            if (op == null) return null;
            // 6 个刀具行都要用它，单轮刷新里只查一次（GetParent 是要走 NX 内部的）
            if (!NXOpen.Tag.Null.Equals(_toolCacheTag) && _toolCacheTag.Equals(op.Tag))
                return _toolCacheGrp;
            NXOpen.CAM.NCGroup g = null;
            try
            {
                g = op.GetParent(NXOpen.CAM.CAMSetup.View.MachineTool)
                    as NXOpen.CAM.NCGroup;
            }
            catch { }
            _toolCacheTag = op.Tag;
            _toolCacheGrp = g;
            return g;
        }

        // ------------------------------------------------- 程序组 / MCS / 刀具信息
        /// <summary>工序所属程序组名。</summary>
        private string GroupNameOf(NXOpen.CAM.Operation op)
        {
            try
            {
                NXOpen.CAM.NCGroup g = op.ParentProgramOrder;
                if (g != null && !string.IsNullOrEmpty(g.Name)) return g.Name;
            }
            catch { }
            return "";
        }

        /// <summary>工序所属 MCS 坐标名：从几何父组往上找 MCS 组（NX7.5+ 类型为 OrientGeometry）。</summary>
        private string McsNameOf(NXOpen.CAM.Operation op)
        {
            try
            {
                NXOpen.CAM.NCGroup g = op.ParentGeometry;
                int guard = 0;
                while (g != null && guard++ < 64)
                {
                    if (g is NXOpen.CAM.OrientGeometry && !string.IsNullOrEmpty(g.Name))
                        return g.Name;
                    g = g.GetParent();
                }
            }
            catch { }
            return "";
        }

        /// <summary>候选项列表：程序组 / MCS / 刀具，三者共用。</summary>
        private List<PickItem> BuildPicks(string id)
        {
            List<PickItem> list = new List<PickItem>();
            try
            {
                // 刀尖类型行已临时移除（定位不到参数），恢复时放开本分支
                //if (id == "tiptype")
                //{
                //    // 固定选项表：与 NX「Cycle 深度」对话框一致（仅新版钻孔可改）
                //    if (!IsDrillOp()) return list;
                //    if (NewHoleBuilder() == null) return list;    // 旧版点位钻孔：无公开写接口，不给候选
                //    for (int i = 0; i < DepthTypeLabels.Length; i++)
                //        list.Add(new PickItem { Text = DepthTypeLabels[i], Value = i });
                //    return list;
                //}
                if (id == "cutmode" || id == "cutdir" || id == "cutdir2" ||
                    id == "levelmode" || id == "sideflooreq")
                {
                    // 切削枚举固定选项表：选项文本与 NX 对话框一致
                    string[] labels; bool oneBased;
                    switch (id)
                    {
                        case "cutmode": labels = CutModeLabels; oneBased = true; break;
                        case "cutdir": labels = CutDirLabels; oneBased = true; break;
                        case "cutdir2": labels = CutDir2Labels; oneBased = true; break;
                        case "levelmode": labels = LevelModeLabels; oneBased = false; break;
                        default: labels = SideFloorEqLabels; oneBased = false; break;
                    }
                    for (int i = 0; i < labels.Length; i++)
                        list.Add(new PickItem { Text = labels[i], Value = oneBased ? i + 1 : i });
                    return list;
                }
                if (id == "tool")
                {
                    // 有缓存就直接用（CollectTools 要遍历整棵 CAM 组树，别每次点开都扫）；
                    // 只有当缓存里找不到当前工序正在用的刀具时才重扫（新加的刀具能立刻出现）。
                    bool needScan = _tools.Count == 0;
                    if (!needScan)
                    {
                        string cur = _valLabels["tool"].Text;
                        needScan = true;
                        foreach (ToolItem t in _tools)
                            if (t.Name == cur) { needScan = false; break; }
                    }
                    if (needScan) PollToolList(true);

                    foreach (ToolItem t in _tools)
                        list.Add(new PickItem { Text = t.Name, Group = t.Group });
                    return list;
                }
                Part work = _session.Parts.Work;
                if (work == null || work.CAMSetup == null) return list;
                NXOpen.CAM.CAMSetup.View view = id == "group"
                    ? NXOpen.CAM.CAMSetup.View.ProgramOrder
                    : NXOpen.CAM.CAMSetup.View.Geometry;
                NXOpen.CAM.NCGroup root = work.CAMSetup.GetRoot(view) as NXOpen.CAM.NCGroup;
                CollectGroups(root, id, list, new HashSet<Tag>());
            }
            catch { }
            return list;
        }

        private void CollectGroups(NXOpen.CAM.NCGroup g, string kind, List<PickItem> list, HashSet<Tag> seen)
        {
            if (g == null || !seen.Add(g.Tag)) return;
            NXOpen.CAM.CAMObject[] kids = null;
            try { kids = g.GetMembers(); } catch { }
            if (kids == null) return;
            foreach (NXOpen.CAM.CAMObject m in kids)
            {
                NXOpen.CAM.NCGroup c = m as NXOpen.CAM.NCGroup;
                if (c == null) continue;
                // 工序不继承 NCGroup（上面的 as 已经把它们过滤掉了），
                // 所以程序组这一支只需要收所有组；刀路坐标只收 MCS 组。
                bool take = kind == "group"
                    ? true
                    : (c is NXOpen.CAM.OrientGeometry);             // 刀路坐标：只要 MCS 组
                if (take && !string.IsNullOrEmpty(c.Name))
                    list.Add(new PickItem { Text = c.Name, Group = c });
                CollectGroups(c, kind, list, seen);
            }
        }

        /// <summary>把当前工序移到目标组（程序组走程序顺序视图，MCS 走几何视图）。</summary>
        private void ApplyMoveTo(NXOpen.CAM.CAMSetup.View view, NXOpen.CAM.NCGroup target, string what)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) { SetHint("无可用工作部件"); return; }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：" + what);
            try
            {
                work.CAMSetup.MoveObjects(view,
                    new NXOpen.CAM.CAMObject[] { _op }, target,
                    NXOpen.CAM.CAMSetup.Paste.Inside);
                SetHint(what + "：" + target.Name + "（Ctrl+Z 可撤销）");
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint(what + "失败: " + ex.Message);
            }
        }

        // ---- 刀具详细信息：走 UF_PARAM（与刀具对话框里的字段一一对应；不再靠字符串探测，也就没有异常开销） ----
        private bool ToolParamDouble(NXOpen.CAM.NCGroup t, int param, out double v)
        {
            v = 0;
            if (t == null) return false;
            try { _ufs.Param.AskDoubleValue(t.Tag, param, out v); return true; }
            catch { return false; }
        }

        private bool ToolParamInt(NXOpen.CAM.NCGroup t, int param, out int v)
        {
            v = 0;
            if (t == null) return false;
            try { _ufs.Param.AskIntValue(t.Tag, param, out v); return true; }
            catch { return false; }
        }

        private bool ToolParamString(NXOpen.CAM.NCGroup t, int param, out string v)
        {
            v = null;
            if (t == null) return false;
            try { _ufs.Param.AskStrValue(t.Tag, param, out v); return !string.IsNullOrEmpty(v); }
            catch { return false; }
        }

        /// <summary>刀具编号：对话框「编号」页的“刀具号”。</summary>
        private string ToolNoText()
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            int n;
            if (ToolParamInt(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_NUMBER, out n)) return n.ToString();
            // 兜底：极少数环境 UF_PARAM 索引对不上，退回 TCL 参数名（正常路径不会走到这里）
            if (t != null)
            {
                try { return t.GetIntegerValue("TL_NUMBER").ToString(); } catch { }
            }
            return "";
        }

        /// <summary>刀具直径：对话框「尺寸」页的 (D) 直径。</summary>
        /// <summary>真实刀名（只读）：按刀具「真实参数 + 类型」拼出来，不取树名。
        /// 例：建 D10 立铣刀但直径参数 9.9 → D9.9。
        /// 命名规则（用户定稿）：
        ///     圆鼻刀 D*R*（圆角参数>0 的铣刀）  球刀 D*R*（球头/球形，R 缺省取 D/2）
        ///     平底铣刀 D*                       钻头 ZT*      丝锥 M*（DrillTap/螺纹类）
        ///     中心钻 C*（点钻/定心钻）          倒角刀 DJ*
        /// 直径 = UF_PARAM_TL_DIAMETER(1000)，圆角 = TL_COR1_RAD(1005) / TL_LOW_COR_RAD；
        /// 读不到再退 GetRealValue。</summary>
        private string RealToolNameText()
        {
            NXOpen.CAM.NCGroup g = CurrentToolOf(_op);
            if (g == null) return "";
            NXOpen.CAM.Tool t = g as NXOpen.CAM.Tool;

            // 真实直径
            double dia;
            if (!ToolParamDouble(g, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_DIAMETER, out dia) || dia <= 0)
            {
                try { dia = g.GetRealValue("TL_DIAMETER"); } catch { return ""; }
                if (dia <= 0) return "";
            }
            string d = FormatValue(dia);

            // 真实圆角（圆鼻刀/球刀用）
            double cor = 0, cv;
            if (ToolParamDouble(g, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_COR1_RAD, out cv) && cv > 0) cor = cv;
            else if (ToolParamDouble(g, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_LOW_COR_RAD, out cv) && cv > 0) cor = cv;

            NXOpen.CAM.Tool.Types ty = NXOpen.CAM.Tool.Types.Mill;
            NXOpen.CAM.Tool.Subtypes st = NXOpen.CAM.Tool.Subtypes.Undefined;
            if (t != null) { try { t.GetTypeAndSubtype(out ty, out st); } catch { } }

            switch (st)
            {
                case NXOpen.CAM.Tool.Subtypes.MillBall:            // 球刀：R 缺省 D/2
                case NXOpen.CAM.Tool.Subtypes.MillSpherical:
                    return "D" + d + "R" + FormatValue(cor > 0 ? cor : dia / 2.0);
                case NXOpen.CAM.Tool.Subtypes.MillChamfer:         // 倒角刀
                case NXOpen.CAM.Tool.Subtypes.FrontBackChamferMill:
                    return "DJ" + d;
                case NXOpen.CAM.Tool.Subtypes.DrillSpotDrill:      // 中心钻/点钻
                case NXOpen.CAM.Tool.Subtypes.DrillCenterBell:     // 定心钻
                    return "C" + d;
                case NXOpen.CAM.Tool.Subtypes.DrillTap:            // 丝锥
                    return "M" + d;
            }
            switch (ty)
            {
                case NXOpen.CAM.Tool.Types.Thread:                 // 螺纹类按丝锥规则
                    return "M" + d;
                case NXOpen.CAM.Tool.Types.Drill:                  // 其余钻类一律钻头
                    return "ZT" + d;
            }
            // 铣刀类：有圆角 = 圆鼻刀，无圆角 = 平底
            if (cor > 0) return "D" + d + "R" + FormatValue(cor);
            return "D" + d;
        }

        // ------------------------------------------------- B 组扩展行（刀具参数 / 切削枚举）
        // 数值索引取自 uf_param_indices.h 里的编号：
        // 1001 刀具长度  1002 刃长  1003 锥角  1004 尖角  1049 刀头偏置  1063 夹持库号  1068 刀具描述
        // 5709 最短刀长  300 切削模式  305 切削方向  277 刀路方向  350 层到层  346 侧底一致
        private const int UF_CUT_MODE = 300;          // int：1往复…18同心单向步进
        private const int UF_CUT_DIRECTION = 305;     // int：1顺铣…5混合
        private const int UF_CUTDIR_TYPE = 277;       // int：1向内 2向外
        private const int UF_LEVEL_MODE = 350;        // int：0使用转移方法…3沿部件交叉斜进刀
        private const int UF_SIDE_FLOOR_EQ = 346;     // int：0 False 1 True

        private static readonly string[] CutModeLabels = {
            "往复", "单向", "单向轮廓", "跟随周边", "轮廓", "标准驱动", "跟随部件", "单向步进",
            "摆线", "径向", "径向往复", "径向单向", "径向单向轮廓", "径向单向步进",
            "同心往复", "同心单向", "同心单向轮廓", "同心单向步进" };               // 值 1~18
        private static readonly string[] CutDirLabels = {
            "顺铣", "逆铣", "跟随边界", "边界反向", "混合" };                       // 值 1~5
        private static readonly string[] CutDir2Labels = { "向内", "向外" };        // 值 1~2
        private static readonly string[] LevelModeLabels = {
            "使用转移方法", "直接对部件进刀", "沿部件斜进刀", "沿部件交叉斜进刀" };   // 值 0~3
        private static readonly string[] SideFloorEqLabels = { "False", "True" };   // 值 0~1

        /// <summary>读刀具长度类参数：主索引读不到退 GetRealValue 兜底（同刀具直径行）。</summary>
        private string ToolRealText(int param, string fallbackName, string suffix)
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            if (t == null) return "";
            double v;
            if (!ToolParamDouble(t, param, out v) || v <= 0 || v >= 9000)
            {
                try { v = t.GetRealValue(fallbackName); } catch { return ""; }
                if (v <= 0 || v >= 9000) return "";
            }
            return FormatValue(v) + suffix;
        }

        private string ToolStrText(int param)
        {
            string s;
            if (ToolParamString(CurrentToolOf(_op), param, out s)) return s;
            return "";
        }

        /// <summary>刀路类型：工序参数表里名字带 TEMPLATE+SUBTYPE 的模板子类型（如 DRILLING）。</summary>
        private string OpTypeText()
        {
            if (_op == null) return "";
            EnsureOpParams();
            if (_opParamIdx != null)
                foreach (KeyValuePair<string, int> kv in _opParamIdx)
                {
                    if (kv.Key.IndexOf("TEMPLATE", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        kv.Key.IndexOf("SUBTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string s;
                        try { _ufs.Param.AskStrValue(_op.Tag, kv.Value, out s); return s; }
                        catch { }
                        break;
                    }
                }
            return "";
        }

        /// <summary>读切削枚举行：oneBased=true 表示枚举值从 1 开始（界面显示前导 / 的含义）。</summary>
        private string EnumRowText(int param, string[] labels, bool oneBased)
        {
            int v;
            if (!TryAskUfInt(param, out v)) return "";
            int i = oneBased ? v - 1 : v;
            return (i >= 0 && i < labels.Length) ? labels[i] : ("类型" + v);
        }

        /// <summary>写切削枚举（下拉选择），UndoMark 包裹可撤销。</summary>
        private void ApplyEnumParam(string what, int param, int value)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：修改" + what);
            try
            {
                _ufs.Param.SetIntValue(_op.Tag, param, value);
                SetHint("已应用 " + what + "（Ctrl+Z 可撤销）");
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("修改失败（该工序类型可能不支持此参数）: " + ex.Message);
            }
        }

        private string ToolDiameterText()
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            double v;
            if (ToolParamDouble(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_DIAMETER, out v) && v > 0)
                return FormatValue(v);
            if (t != null)
            {
                try { v = t.GetRealValue("TL_DIAMETER"); if (v > 0) return FormatValue(v); } catch { }
            }
            return "";
        }

        /// <summary>刀具圆角：对话框「尺寸」页的 (R1) 下半径；5 参数刀用 COR1，
        /// 7/10 参数牛鼻刀的下圆角在 LOW_COR_RAD，取非零的那个。</summary>
        private string ToolCornerText()
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            if (t == null) return "";
            double c1 = 0, c2 = 0;
            bool ok1 = ToolParamDouble(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_COR1_RAD, out c1);
            bool ok2 = ToolParamDouble(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_LOW_COR_RAD, out c2);
            if (ok1 || ok2) return FormatValue(c1 > 0 ? c1 : c2);
            try { return FormatValue(t.GetRealValue("TL_COR1_RAD")); } catch { }   // 兜底
            return "";
        }

        /// <summary>刀头名称：优先取刀柄描述，没有就退回刀具描述。</summary>
        private string ToolHolderText()
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            string s;
            if (ToolParamString(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_HOLDER_DESCRIPTION, out s))
                return s;
            if (ToolParamString(t, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_DESCRIPTION, out s))
                return s;
            return "";
        }

        /// <summary>刀具类型：按 (类型, 子类型) 给中文名。</summary>
        private string ToolTypeText()
        {
            NXOpen.CAM.Tool t = CurrentToolOf(_op) as NXOpen.CAM.Tool;
            if (t == null) return "";
            try
            {
                NXOpen.CAM.Tool.Types ty;
                NXOpen.CAM.Tool.Subtypes st;
                t.GetTypeAndSubtype(out ty, out st);
                return ToolTypeName(ty, st);
            }
            catch { }
            return "";
        }

        private static string ToolTypeName(NXOpen.CAM.Tool.Types ty, NXOpen.CAM.Tool.Subtypes st)
        {
            switch (st)
            {
                case NXOpen.CAM.Tool.Subtypes.Mill5: return "立铣刀(5参数)";
                case NXOpen.CAM.Tool.Subtypes.Mill7: return "牛鼻刀(7参数)";
                case NXOpen.CAM.Tool.Subtypes.Mill10: return "铣刀(10参数)";
                case NXOpen.CAM.Tool.Subtypes.MillBall: return "球头刀";
                case NXOpen.CAM.Tool.Subtypes.DrillStandard: return "标准钻头";
                case NXOpen.CAM.Tool.Subtypes.DrillCenterBell: return "定心钻";
                case NXOpen.CAM.Tool.Subtypes.DrillSpotDrill: return "中心钻";
                case NXOpen.CAM.Tool.Subtypes.DrillSpotFace: return "锪平钻";
                case NXOpen.CAM.Tool.Subtypes.DrillCountersink: return "锥口钻";
                case NXOpen.CAM.Tool.Subtypes.DrillCounterbore: return "锪孔刀";
                case NXOpen.CAM.Tool.Subtypes.DrillBore: return "镗刀";
                case NXOpen.CAM.Tool.Subtypes.DrillReam: return "铰刀";
                case NXOpen.CAM.Tool.Subtypes.DrillTap: return "丝锥";
            }
            switch (ty)
            {
                case NXOpen.CAM.Tool.Types.Mill: return "铣刀";
                case NXOpen.CAM.Tool.Types.Drill: return "钻头";
                case NXOpen.CAM.Tool.Types.Turn: return "车刀";
                case NXOpen.CAM.Tool.Types.Thread: return "螺纹刀";
                case NXOpen.CAM.Tool.Types.Wedm: return "线割";
            }
            return st.ToString();
        }

        // ------------------------------------------------- 钻孔循环（Cycle）参数
        // 「刀尖深度 / 刀尖类型 / 刀尖步距」是钻孔工序自己的 CAM 参数
        // （就是 NX 的「Cycle 深度」和「Cycle 参数」两个对话框里的项），跟刀具无关：
        //     刀尖深度 = Cycle 参数里的 Depth (Tip)
        //     刀尖类型 = Cycle 深度里选的那一项（模型深度/刀尖深度/刀肩深度/至底面/穿过底面/至选定点）
        //     刀尖步距 = Cycle 参数里的 Step 值（允许分成多段，如 3、2、1）
        // 参数索引各版本会变，所以不硬编码：运行时把该工序的参数表拉出来，按名字定位。
        // 刀尖类型选项表：刀尖类型行已临时停用，本表保留供恢复时使用（当前无引用）
        private static readonly string[] DepthTypeLabels = new string[]
        {
            "模型深度", "刀尖深度", "刀肩深度", "至底面", "穿过底面", "至选定点"
        };

        private NXOpen.Tag _opParamTag = NXOpen.Tag.Null;    // 参数表缓存对应的工序
        private Dictionary<string, int> _opParamIdx;         // 参数名 -> 索引
        private int _tipDepthIdx = -1, _tipStepIdx = -1;
        // private int _tipTypeIdx = -1;        // 刀尖类型行已临时移除（参数定位不到）
        private NXOpen.Tag _paramDumpTag = NXOpen.Tag.Null;  // 已导出参数表的工序（每工序只导一次）

        /// <summary>拉取当前工序的参数表（名字→索引），并按名字定位刀尖三项。
        /// AskRequiredParams 只列本工序真正拥有的参数，铣削工序里不会带这些名字。</summary>
        private void EnsureOpParams()
        {
            if (_op == null) return;
            if (!NXOpen.Tag.Null.Equals(_opParamTag) && _opParamTag.Equals(_op.Tag)) return;
            _opParamTag = _op.Tag;
            _tipDepthIdx = _tipStepIdx = -1;
            _opParamIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int count = 0;
            int[] idx = null;
            try { _ufs.Param.AskRequiredParams(_op.Tag, out count, out idx); }
            catch { return; }
            if (idx == null) return;
            for (int i = 0; i < idx.Length; i++)
            {
                string nm = ParamName(idx[i]);
                if (!string.IsNullOrEmpty(nm) && !_opParamIdx.ContainsKey(nm))
                    _opParamIdx[nm] = idx[i];
            }

            // 先按常见全名精确找，再按关键字组合兜底（NX 各版本命名略有差别）
            _tipDepthIdx = FindParam(
                new string[] { "CYCLE_DEPTH_TIP", "DEPTH_TIP", "TIP_DEPTH", "CYCLE_TIP_DEPTH" },
                new string[][] { new string[] { "DEPTH" }, new string[] { "TIP" } }, null);
            // 刀尖类型参数已确认「不存在」（旧版钻孔），停止定位，避免无效日志与耗时
            //_tipTypeIdx = FindParam(
            //    new string[] { "CYCLE_DEPTH_TYPE", "DEPTH_TYPE", "CYCLE_DEPTH_OPTION", "DEPTH_OPTION" },
            //    new string[][] { new string[] { "DEPTH" }, new string[] { "TYPE", "OPTION", "MODE" } }, null);
            _tipStepIdx = FindParam(
                new string[] { "CYCLE_STEP", "STEP_VALUES", "CYCLE_STEP_VALUE", "STEP_VALUE", "CYCLE_Q" },
                new string[][] { new string[] { "STEP" } },
                new string[] { "STEPOVER", "STEP_AHEAD" });
        }

        /// <summary>按“全名精确 → 关键字组合”两轮找参数索引，找不到返回 -1。</summary>
        private int FindParam(string[] exacts, string[][] groups, string[] excludes)
        {
            if (_opParamIdx == null || _opParamIdx.Count == 0) return -1;
            foreach (string e in exacts)
            {
                int hit;
                if (_opParamIdx.TryGetValue(e, out hit)) return hit;
            }
            foreach (KeyValuePair<string, int> kv in _opParamIdx)
            {
                string nm = kv.Key;
                bool skip = false;
                if (excludes != null)
                    foreach (string x in excludes)
                        if (nm.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
                if (skip) continue;

                bool all = true;
                foreach (string[] g in groups)
                {
                    bool any = false;
                    foreach (string t in g)
                        if (nm.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) { any = true; break; }
                    if (!any) { all = false; break; }
                }
                if (all) return kv.Value;
            }
            return -1;
        }

        private string ParamName(int index)
        {
            try
            {
                NXOpen.UF.UFParam.IndexAttribute a;
                _ufs.Param.AskParamAttributes(index, out a);
                return a.name;
            }
            catch { return null; }
        }


        // ---- 新版钻孔（HoleDrilling / HoleDrillingBuilder）----
        // 新版钻孔（刀路名称 DEEP_HOLE_DRILLING 这类）的循环表是结构化的：
        //     刀尖深度 = 特征几何体「使用预定义深度-深度」→ PredefinedDepth.Value（Status=勾选状态）
        //     刀尖步距 = 循环参数「步进」→ CycleTable.AxialStepover（恒定→DistanceBuilder，多段→UserDefined）
        // 旧版点位加工（Point to Point）的 CycleTable 恒 null，仍走上面的 UF 参数表。
        /// <summary>给当前工序取 HoleDrillingBuilder；不是新版钻孔返回 null。
        /// 读取用完必须 Destroy（写了就 Commit 后 Destroy）。</summary>
        private NXOpen.CAM.HoleDrillingBuilder NewHoleBuilder()
        {
            if (_op == null) return null;
            try
            {
                NXOpen.Part owner = _op.OwningPart as NXOpen.Part;
                if (owner == null || owner.CAMSetup == null) return null;
                NXOpen.CAM.OperationBuilder ob = owner.CAMSetup.CAMOperationCollection.CreateBuilder(_op);
                NXOpen.CAM.HoleDrillingBuilder hb = ob as NXOpen.CAM.HoleDrillingBuilder;
                if (hb == null) { try { ob.Destroy(); } catch { } }
                return hb;
            }
            catch { return null; }
        }

        /// <summary>读步距文本：恒定→单值；多段→每段按次数展开成 *丨*丨*。</summary>
        private static string NewHoleStepText(NXOpen.CAM.StepoverBuilder st)
        {
            try
            {
                if (st.StepoverType == NXOpen.CAM.StepoverBuilder.StepoverTypes.UserDefined)
                {
                    NXOpen.CAM.MultipleStepoverBuilder mu = st.UserDefined;
                    int cnt = mu == null ? 0 : mu.GetNumberOfStepovers();
                    if (cnt <= 0) return "";
                    List<string> parts = new List<string>();
                    for (int i = 0; i < cnt; i++)
                    {
                        int passes; double dist; int intent;
                        mu.GetData(i, out passes, out dist, out intent);
                        for (int k = 0; k < Math.Max(passes, 1); k++) parts.Add(FormatValue(dist));
                    }
                    return parts.Count == 0 ? "" : string.Join("丨", parts.ToArray());
                }
                return FormatValue(st.DistanceBuilder.Value);
            }
            catch { return ""; }
        }

        // ---- 旧版点位钻孔循环参数：CLSF 导出解析（只读；NX 对旧版循环无公开写 API）----
        //  原理：CAMSetup.OutputClsf("CLSF_STANDARD") 把刀轨导成文本，G83 的一行形如：
        //    CYCLE/DRILL,DEEP,STEP,111.0000,222.0000,333.0000,CLEAR,3.0000,FEDTO,-3333.0000,RTRCTO,AUTO,MMPM,2500.0000
        //  关键字头部=循环类型；STEP 后的数值=递增步距列表；FEDTO=刀尖深度（CLSF 为负 Z，取绝对值=对话框值）。
        private sealed class LegacyCycle
        {
            public string Kind;                    // 循环关键字，如 "DRILL,DEEP"
            public double[] Steps;                 // STEP 递增步距列表
            public double FedTo = double.NaN;      // FEDTO 深度
            public double Clear = double.NaN;      // CLEAR 安全间隙
        }

        private LegacyCycle _legacyCycle;                        // 解析缓存
        private NXOpen.Tag _legacyCycleTag = NXOpen.Tag.Null;
        private DateTime _legacyCycleAt = DateTime.MinValue;

        /// <summary>解析当前工序刀轨的 CYCLE 行（2 秒缓存：深度/类型/步距三行显示共用一次导出）。</summary>
        private LegacyCycle ReadLegacyCycle()
        {
            if (_op == null) return null;
            if (_legacyCycle != null && _legacyCycleTag.Equals(_op.Tag) &&
                (DateTime.Now - _legacyCycleAt).TotalMilliseconds < 2000)
                return _legacyCycle;

            _legacyCycle = null;
            _legacyCycleTag = _op.Tag;
            _legacyCycleAt = DateTime.Now;
            try
            {
                NXOpen.Part owner = _op.OwningPart as NXOpen.Part;
                if (owner == null || owner.CAMSetup == null) return null;
                string clsf = Path.Combine(Path.GetTempPath(), "lyj_legacy_cycle.cls");
                // OutputClsf 对已存在文件是「追加」写：不删旧文件的话第一行永远是历史值，
                // 导致刀尖深度/步距修改后显示不更新。导出前必须删掉。
                try { if (File.Exists(clsf)) File.Delete(clsf); } catch { }
                owner.CAMSetup.OutputClsf(new NXOpen.CAM.CAMObject[] { _op },
                    "CLSF_STANDARD", clsf, NXOpen.CAM.CAMSetup.OutputUnits.Metric);
                if (!File.Exists(clsf)) return null;

                foreach (string raw in File.ReadAllLines(clsf))
                {
                    string ln = raw.Trim();
                    if (!ln.StartsWith("CYCLE/", StringComparison.OrdinalIgnoreCase)) continue;
                    string body = ln.Substring(6).Trim();
                    if (body.StartsWith("OFF", StringComparison.OrdinalIgnoreCase)) continue;  // CYCLE/OFF 是结束行

                    LegacyCycle d = new LegacyCycle();
                    string[] tk = body.Split(',');
                    List<string> head = new List<string>();
                    List<double> steps = null;
                    bool stepOpen = false;                      // STEP 段是否还没遇到下一个关键字
                    bool sectionSeen = false;
                    for (int i = 0; i < tk.Length; i++)
                    {
                        string t = tk[i].Trim();
                        double num;
                        if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out num))
                        {
                            // 只有 STEP 后面的连续数值才算步距；
                            // 后面的 CLEAR/FEDTO/RTRCTO/AUTO/MMPM 之后的数值（如进给率 2500）不能算进来
                            if (stepOpen) steps.Add(num);
                            continue;
                        }
                        if (stepOpen) stepOpen = false;          // 遇到关键字 → STEP 段结束
                        if (t.Equals("STEP", StringComparison.OrdinalIgnoreCase))
                        { steps = new List<double>(); stepOpen = true; sectionSeen = true; continue; }
                        if (t.Equals("FEDTO", StringComparison.OrdinalIgnoreCase) && i + 1 < tk.Length)
                        {
                            double sv;
                            if (double.TryParse(tk[++i].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out sv)) d.FedTo = sv;
                            sectionSeen = true; continue;
                        }
                        if (t.Equals("CLEAR", StringComparison.OrdinalIgnoreCase) && i + 1 < tk.Length)
                        {
                            double sv;
                            if (double.TryParse(tk[++i].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out sv)) d.Clear = sv;
                            sectionSeen = true; continue;
                        }
                        if (!sectionSeen) head.Add(t);              // 头部关键字（DRILL,DEEP 等）
                    }
                    d.Kind = head.Count > 0 ? string.Join(",", head.ToArray()) : "";
                    if (steps != null && steps.Count > 0) d.Steps = steps.ToArray();
                    // 不在第一条就 break：万一删文件失败仍有历史残留，以最后一条 CYCLE 行为准
                    _legacyCycle = d;
                }
                return _legacyCycle;
            }
            catch { return null; }
        }

        /// <summary>循环关键字 → 中文标签。</summary>
        private static string LegacyCycleKindText(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return "";
            string k = kind.ToUpperInvariant();
            if (k.Contains("DEEP")) return "深孔钻(G83)";
            if (k.Contains("BRKCHP")) return "断屑钻";
            if (k.Contains("SPOT")) return "点钻";
            if (k == "DRILL") return "钻(G81)";
            if (k.Contains("TAP")) return "攻丝";
            if (k.Contains("BORE")) return "镗孔";
            if (k.Contains("REAM")) return "铰孔";
            return kind;
        }

        // ---- 读 ----
        // 旧版点位加工的刀尖深度/步距读取（CLSF 导出）已停用（2026-11-06）：
        // OutputClsf 每次要导出整个刀轨到临时文件（0.3~1.5s），选中工序/鼠标移回都会触发，明显拖慢助手。
        // 现在选中旧版策略时这两行直接显示空白；LegacyCycle/ReadLegacyCycle 保留，找到更快的读取通道后恢复。
        private string ReadTipDepthText()
        {
            if (_op == null || !IsDrillOp()) return "";
            // 新版钻孔：预定义深度（没勾「使用预定义深度」就没有显式值）
            NXOpen.CAM.HoleDrillingBuilder hb = NewHoleBuilder();
            if (hb == null) return "";                  // 旧版点位加工：读取已停用，直接不显示
            string r;
            try { r = hb.PredefinedDepth.Status ? FormatValue(hb.PredefinedDepth.Value) : ""; }
            catch { r = ""; }
            finally { try { hb.Destroy(); } catch { } }
            return r;
        }

        // ---- 刀尖类型（临时停用）：旧版钻孔无任何可读参数，6 种 CLSF 格式 + 325 项参数快照对比全无差异。
        //      找到数据源后放开本方法，并把 "tiptype" 加回 AllRows / RowNames / RowDefs 与 ReadText 分支。
        //private string ReadTipTypeText()
        //{
        //    if (_op == null || !IsDrillOp()) return "";
        //    // 钻孔通用：CLSF 循环关键字（刀轨里的 CYCLE/DRILL,DEEP → 深孔钻(G83)）
        //    {
        //        LegacyCycle lc = ReadLegacyCycle();
        //        if (lc != null && !string.IsNullOrEmpty(lc.Kind))
        //            return LegacyCycleKindText(lc.Kind);
        //    }
        //    EnsureOpParams();
        //    if (_tipTypeIdx < 0) return "";
        //    int code;
        //    if (TryAskOpInt(_tipTypeIdx, out code))
        //        return (code >= 0 && code < DepthTypeLabels.Length)
        //            ? DepthTypeLabels[code] : ("类型" + code);
        //    string s;
        //    if (TryAskOpString(_tipTypeIdx, out s) && !string.IsNullOrEmpty(s)) return s;
        //    return "";
        //}

        private string ReadTipStepText()
        {
            if (_op == null || !IsDrillOp()) return "";
            // 新版钻孔：循环参数「步进」
            NXOpen.CAM.HoleDrillingBuilder hb = NewHoleBuilder();
            if (hb == null) return "";                  // 旧版点位加工：读取已停用，直接不显示
            string r = "";
            try
            {
                NXOpen.CAM.Cycle cyc = hb.CycleTable;
                if (cyc != null) r = NewHoleStepText(cyc.AxialStepover);
            }
            catch { }
            finally { try { hb.Destroy(); } catch { } }
            return r;
        }

        // ---- 写 ----
        private void ApplyTipDepth(double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            // 新版钻孔：走构建器（PredefinedDepth）
            NXOpen.CAM.HoleDrillingBuilder hb = NewHoleBuilder();
            if (hb != null)
            {
                Session.UndoMarkId mk = _session.SetUndoMark(
                    Session.MarkVisibility.Visible, "加工助手：刀尖深度");
                try
                {
                    hb.PredefinedDepth.Value = v;
                    hb.Commit();
                    hb.Destroy();
                    SetHint(string.Format("已应用 刀尖深度={0:0.####}（Ctrl+Z 可撤销）", v));
                    RefreshValues();
                }
                catch (Exception ex)
                {
                    try { _session.UndoToMark(mk, null); } catch { }
                    try { hb.Destroy(); } catch { }
                    SetHint("修改刀尖深度失败: " + ex.Message);
                }
                return;
            }

            // 旧版点位加工：NX 无公开的循环参数写接口（UF 参数表不含，只读走 CLSF）
            SetHint("旧版钻孔的循环参数暂不支持在此修改（NX 无公开接口），请用新版钻孔工序或直接改对话框");
        }

        // ---- 刀尖类型（临时停用）：见 ReadTipTypeText 上方说明，恢复时一并放开。
        //private void ApplyTipType(int code)
        //{
        //    if (_op == null) { SetHint("尚未选中加工工序"); return; }
        //    EnsureOpParams();
        //    if (_tipTypeIdx < 0) { SetHint("当前工序没有刀尖类型（只有钻孔类工序有）"); return; }
        //    if (code < 0 || code >= DepthTypeLabels.Length) return;
        //
        //    Session.UndoMarkId mark = _session.SetUndoMark(
        //        Session.MarkVisibility.Visible, "加工助手：刀尖类型");
        //    try
        //    {
        //        _ufs.Param.SetIntValue(_op.Tag, _tipTypeIdx, code);
        //        SetHint("已应用 刀尖类型=" + DepthTypeLabels[code] + "（Ctrl+Z 可撤销）");
        //        RefreshValues();
        //    }
        //    catch (Exception ex)
        //    {
        //        try { _session.UndoToMark(mark, null); } catch { }
        //        SetHint("修改刀尖类型失败: " + ex.Message);
        //    }
        //}

        private void ApplyTipStep(double[] vals)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            if (vals == null || vals.Length == 0) return;

            // 新版钻孔：走构建器（AxialStepover）
            NXOpen.CAM.HoleDrillingBuilder hb = NewHoleBuilder();
            if (hb != null)
            {
                Session.UndoMarkId mk = _session.SetUndoMark(
                    Session.MarkVisibility.Visible, "加工助手：刀尖步距");
                try
                {
                    NXOpen.CAM.Cycle cyc = hb.CycleTable;
                    if (cyc == null)
                        throw new Exception("当前工序没有循环，请先在「循环参数」里选一个非“无循环”的类型");
                    NXOpen.CAM.StepoverBuilder st = cyc.AxialStepover;
                    if (vals.Length == 1)
                    {
                        st.StepoverType = NXOpen.CAM.StepoverBuilder.StepoverTypes.Constant;
                        st.DistanceBuilder.Value = vals[0];
                    }
                    else
                    {
                        st.StepoverType = NXOpen.CAM.StepoverBuilder.StepoverTypes.UserDefined;
                        NXOpen.CAM.MultipleStepoverBuilder mu = st.UserDefined;
                        while (mu.GetNumberOfStepovers() > 0) mu.Delete(0);
                        for (int i = 0; i < vals.Length; i++)
                            mu.Add(Math.Max(i - 1, 0), 1, vals[i], 0);
                    }
                    hb.Commit();
                    hb.Destroy();
                    string[] parts = new string[vals.Length];
                    for (int i = 0; i < vals.Length; i++) parts[i] = FormatValue(vals[i]);
                    SetHint("已应用 刀尖步距=" + string.Join("丨", parts) + "（Ctrl+Z 可撤销）");
                    RefreshValues();
                }
                catch (Exception ex)
                {
                    try { _session.UndoToMark(mk, null); } catch { }
                    try { hb.Destroy(); } catch { }
                    SetHint("修改刀尖步距失败: " + ex.Message);
                }
                return;
            }

            // 旧版点位加工：NX 无公开的循环参数写接口（UF 参数表不含，只读走 CLSF）
            SetHint("旧版钻孔的循环参数暂不支持在此修改（NX 无公开接口），请用新版钻孔工序或直接改对话框");
        }

        /// <summary>解析多段步距输入：中英文逗号、顿号、分号、竖线、空格都当分隔符。</summary>
        private static bool TryParseStepList(string text, out double[] vals)
        {
            vals = null;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(
                new char[] { ',', '，', '、', ';', '；', '|', '丨', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            List<double> list = new List<double>();
            foreach (string p in parts)
            {
                double v;
                if (!double.TryParse(p.Trim(), out v)) return false;
                if (v < 0 || v >= 9000) return false;
                list.Add(v);
            }
            vals = list.ToArray();
            return vals.Length > 0;
        }

        // ---- 参数表导出（核对用）----
        /// <summary>首次遇到某个工序时，把它的参数表 + 循环参数（Builder/Cycle/Ude）写到
        /// %TEMP%\lyj_drill_params.txt，方便按实际 NX 版本核对参数名。</summary>

        /// <summary>把 ParamBuilder 上的循环信息整个导出来：孔深/深度类型/Cycle 表/CycleEvent。</summary>


        /// <summary>反射导出对象的公开属性值；下一层只钻步距/继承值这类值容器。</summary>


        /// <summary>导出 MassEdit 的属性树（Name/QualifiedName/Value + 子节点）。</summary>

        /// <summary>导出 UdeSet 里的全部 UDE（循环事件就挂在路径起点的 UDE 集合里）。</summary>

        /// <summary>导出 CycleEvent（Ude）的名字与全部参数。</summary>

        /// <summary>导出某个对象的参数表；followPointers=true 时再把值的 Tag 参数指到的对象也导一层。</summary>



        /// <summary>读 UDE 参数值（实数/整数/开关/字符串都试一遍），只用于核对导出。</summary>

        /// <summary>读对象属性值（实数/整数/字符串都试一遍），只用于核对导出。</summary>

        private bool TryAskOpDouble(int index, out double v)
        {
            v = 0;
            if (_op == null) return false;
            try { _ufs.Param.AskDoubleValue(_op.Tag, index, out v); return true; }
            catch { return false; }
        }

        private bool TryAskOpInt(int index, out int v)
        {
            v = 0;
            if (_op == null) return false;
            try { _ufs.Param.AskIntValue(_op.Tag, index, out v); return true; }
            catch { return false; }
        }

        private bool TryAskOpString(int index, out string v)
        {
            v = null;
            if (_op == null) return false;
            try { _ufs.Param.AskStrValue(_op.Tag, index, out v); return true; }
            catch { return false; }
        }

        private bool TryAskOpVla(int index, out double[] vals)
        {
            vals = null;
            if (_op == null) return false;
            int n = 0;
            try { _ufs.Param.AskDoubleVla(_op.Tag, index, out n, out vals); }
            catch { return false; }
            return vals != null && vals.Length > 0;
        }

        /// <summary>刀具编号：刀具号 + 补偿寄存器 + 刀具补偿寄存器一起改成同一个号。</summary>
        private void ApplyToolNumber(int no)
        {
            NXOpen.CAM.NCGroup t = CurrentToolOf(_op);
            if (t == null) { SetHint("当前工序没有指定刀具"); return; }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：刀具编号");
            int done = 0;
            try
            {
                done += SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_NUMBER);
                done += SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_ADJ_REG);
                done += SetToolParam(t, no, (int)NXOpen.UF.UFConstants.UF_PARAM_TL_CUTCOM_REG);
                if (done > 0)
                {
                    SetHint("刀具编号已改为 " + no + "（Ctrl+Z 可撤销）");
                    RefreshValues();
                }
                else
                {
                    try { _session.UndoToMark(mark, null); } catch { }
                    SetHint("该刀具不支持直接改编号");
                }
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("改编号失败: " + ex.Message);
            }
        }

        private int SetToolParam(NXOpen.CAM.NCGroup t, int v, int param)
        {
            try { _ufs.Param.SetIntValue(t.Tag, param, v); return 1; }
            catch { return 0; }
        }

        // ------------------------------------------------- UF Param（余量/公差）
        // 步距/切深常量：数值取自 NX2606 的 UGOPEN\uf_param_indices.h（头文件逐条核对）。
        // 用字面值而不是 UFConstants，避免包装类缺字段。
        private const int UF_STEPOVER_TYPE = 270;            // int：1恒定 2残余高度 4%刀具 7同下刀量
        private const int UF_STEPOVER_SCALLOP = 271;         // double 残余高度
        private const int UF_STEPOVER_DIST = 272;            // double 恒定距离
        private const int UF_STEPOVER_PERCENT = 273;         // double 刀具直径百分比
        private const int UF_CUTLEV_DEPTH_PER_CUT = 336;     // double 每层切深
        private const int UF_CUTLEV_GLOBAL_CUT_DEPTH = 342;  // double 公共每刀切削深度（界面“恒定”）

        /// <summary>
        /// 读步距：必须按步距类型取值——恒定=距离，%刀具=百分比，残余高度=高度。
        /// 直接读 STEPOVER_DIST 会拿到与当前模式无关的陈旧数值
        /// （界面上是“%刀具平直 75”时，DIST 里可能还留着上次的 15，v2.1 踩坑）。
        /// </summary>
        private string ReadStepoverText()
        {
            int type = 1;
            if (!TryAskUfInt(UF_STEPOVER_TYPE, out type)) type = 1;
            switch (type)
            {
                case 1: return FormatValue(AskUfDouble(UF_STEPOVER_DIST));       // 恒定
                case 2: return FormatValue(AskUfDouble(UF_STEPOVER_SCALLOP));    // 残余高度
                case 4: return FormatValue(AskUfDouble(UF_STEPOVER_PERCENT)) + "%"; // %刀具平直
                case 7: return "同下刀量";
                default: return "";
            }
        }

        /// <summary>
        /// 读每刀切削深度：优先“公共每刀切削深度”（界面里“恒定 0.5”那一栏），
        /// 读不到再回退单层切深。0 表示未设置，显示空白。
        /// </summary>
        private string ReadDepthPerCutText()
        {
            double v;
            if (TryAskUfDouble(UF_CUTLEV_GLOBAL_CUT_DEPTH, out v) && v > 0 && v < 9000)
                return FormatValue(v);
            if (TryAskUfDouble(UF_CUTLEV_DEPTH_PER_CUT, out v) && v > 0 && v < 9000)
                return FormatValue(v);
            return "";
        }

        /// <summary>读当前工序的单值参数（UF Param），失败抛异常由调用方转空白。</summary>
        private double AskUfDouble(int param)
        {
            if (_op == null) throw new InvalidOperationException();
            double v;
            _ufs.Param.AskDoubleValue(_op.Tag, param, out v);
            return v;
        }

        /// <summary>读单值参数，失败返回 false（不抛）。</summary>
        private bool TryAskUfDouble(int param, out double v)
        {
            v = 0;
            if (_op == null) return false;
            try
            {
                _ufs.Param.AskDoubleValue(_op.Tag, param, out v);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读整型参数（步距类型等），失败返回 false。</summary>
        private bool TryAskUfInt(int param, out int v)
        {
            v = 0;
            if (_op == null) return false;
            try
            {
                _ufs.Param.AskIntValue(_op.Tag, param, out v);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读当前工序的内外公差（二维参数：[0]=内公差 [1]=外公差）。</summary>
        private double[] AskTol()
        {
            if (_op == null) throw new InvalidOperationException();
            // 内/外公差在同一个参数里，两行共用一次取值（每轮刷新只问 NX 一次）
            if (!NXOpen.Tag.Null.Equals(_tolTag) && _tolTag.Equals(_op.Tag))
                return new double[] { _tolIn, _tolOut };
            double[] t = new double[2];
            _ufs.Param.Ask2dValue(_op.Tag,
                (int)NXOpen.UF.UFConstants.UF_PARAM_TOL_IN_OUT, t);
            _tolTag = _op.Tag;
            _tolIn = t[0];
            _tolOut = t[1];
            return t;
        }

        /// <summary>读工序当前转速/进给。优先直读（零开销、无 builder 创建/销毁）：
        /// 转速 = UF_PARAM_SPINDLE_RPM(4005, Double)；进给 = CAMObject.GetFeedRate("Feed Cut")
        /// （官方托管接口，NX4.0+，直读 Feed Cut 指针参数的生效值）。
        /// UF ask 系列返回的是"当前正被使用的值"，从父组继承的值也能读到。
        /// 任一直读失败/无效（如恒线速模式下 4005=0）→ 整体退回 CreateFeedsBuilder 兜底。</summary>
        private void AskFeeds(NXOpen.CAM.Operation op, out double spindle, out double feed)
        {
            spindle = feed = 0;
            if (op == null) return;
            // ---- 快路：直读 ----
            try
            {
                double s0, f0;
                TryAskUfDouble((int)NXOpen.UF.UFConstants.UF_PARAM_SPINDLE_RPM, out s0);
                op.GetFeedRate("Feed Cut", out f0);
                if (s0 > 0 && f0 > 0) { spindle = s0; feed = f0; return; }
            }
            catch { }
            // ---- 慢路：FeedsBuilder 兜底（恒线速/直读失败/从方法组继承且直读无效时）----
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) return;
            NXOpen.CAM.ObjectsFeedsBuilder fb = null;
            try
            {
                fb = work.CAMSetup.CreateFeedsBuilder(new NXOpen.CAM.CAMObject[] { op });
                spindle = fb.FeedsBuilder.SpindleRpmBuilder.Value;
                feed = fb.FeedsBuilder.FeedCutBuilder.Value;
            }
            finally
            {
                if (fb != null) fb.Destroy();
            }
        }

        // ------------------------------------------------------------ 写（提交）
        /// <summary>按行提交数值（每行各自的 API），UndoMark 包裹可撤销。</summary>
        private void ApplyValue(string row, double v)
        {
            switch (row)
            {
                case "spindle": ApplyFeeds(true, v); break;
                case "feed": ApplyFeeds(false, v); break;
                case "partstock":
                    ApplyUfDouble("余量", (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_PART, v);
                    break;
                case "floorstock":
                    ApplyUfDouble("底面余量", (int)NXOpen.UF.UFConstants.UF_PARAM_STOCK_FLOOR, v);
                    break;
                case "tolinner": ApplyTol(0, v); break;
                case "tolouter": ApplyTol(1, v); break;
                case "stepover": ApplyStepover(v); break;
                case "depthpercut": ApplyDepthPerCut(v); break;
                case "toolno": ApplyToolNumber((int)Math.Round(v)); break;
                case "tipdepth":
                    ApplyTipDepth(v);
                    break;
                case "tipstep":
                    ApplyTipStep(new double[] { v });
                    break;
            }
        }

        /// <summary>写步距：按当前步距类型写对应参数（%模式写百分比、恒定写距离、残余高度写高度）。</summary>
        private void ApplyStepover(double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            int type = 1;
            if (!TryAskUfInt(UF_STEPOVER_TYPE, out type)) type = 1;

            int param;
            string what;
            switch (type)
            {
                case 2:
                    param = UF_STEPOVER_SCALLOP;
                    what = "行距(残余高度)";
                    break;
                case 4:
                    if (v <= 0 || v > 1000) { SetHint("百分比请填 0~1000"); return; }
                    param = UF_STEPOVER_PERCENT;
                    what = "行距(刀具百分比)";
                    break;
                case 7:
                    SetHint("当前行距取“同下刀量”，请直接改下刀量");
                    return;
                default:
                    param = UF_STEPOVER_DIST;
                    what = "行距";
                    break;
            }
            ApplyUfDouble(what, param, v);
        }

        /// <summary>写每刀切削深度：优先“公共每刀切削深度”，不支持时回退单层切深。</summary>
        private void ApplyDepthPerCut(double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：修改下刀量");
            try
            {
                try { _ufs.Param.SetDoubleValue(_op.Tag, UF_CUTLEV_GLOBAL_CUT_DEPTH, v); }
                catch { _ufs.Param.SetDoubleValue(_op.Tag, UF_CUTLEV_DEPTH_PER_CUT, v); }
                SetHint(string.Format("已应用 下刀量={0:0.####}  （Ctrl+Z 可撤销）", v));
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("修改失败（该工序类型可能不支持此参数）: " + ex.Message);
            }
        }

        /// <summary>重命名当前工序（NXObject.SetName，UndoMark 包裹可撤销）。</summary>
        private void ApplyRename(string newName)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            if (newName == _op.Name) return;        // 未变化不写
            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手 - 重命名工序");
            try
            {
                _op.SetName(newName);
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("重命名失败: " + ex.Message);
                return;
            }
            _valLabels["op"].Text = newName;
            SetHint("已重命名");
        }

        /// <summary>只改转速或进给之一（另一个保持当前值），UndoMark 包裹可撤销。</summary>
        private void ApplyFeeds(bool isSpindle, double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) { SetHint("无可用工作部件"); return; }

            double curS, curF;
            AskFeeds(_op, out curS, out curF);
            double newS = isSpindle ? v : curS;
            double newF = isSpindle ? curF : v;
            if (newS <= 0 || newF <= 0)
            {
                SetHint(isSpindle ? "当前进给无效，请先把进给也设置一次" : "当前转速无效，请先把转速也设置一次");
                return;
            }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：修改转速/进给");
            try
            {
                NXOpen.CAM.ObjectsFeedsBuilder fb =
                    work.CAMSetup.CreateFeedsBuilder(new NXOpen.CAM.CAMObject[] { _op });
                fb.FeedsBuilder.SpindleRpmBuilder.Value = newS;
                fb.FeedsBuilder.RecalculateData(
                    NXOpen.CAM.FeedsBuilder.RecalculateBasedOn.SpindleSpeed);
                fb.FeedsBuilder.FeedCutBuilder.Value = newF;
                fb.FeedsBuilder.RecalculateData(
                    NXOpen.CAM.FeedsBuilder.RecalculateBasedOn.CutFeedRate);
                fb.Commit();
                fb.Destroy();

                SetHint(string.Format("已应用  S={0:0.##}  F={1:0.##}  （Ctrl+Z 可撤销）", newS, newF));
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("修改失败: " + ex.Message);
            }
        }

        /// <summary>写当前工序的单值参数（余量类），UndoMark 包裹可撤销。</summary>
        private void ApplyUfDouble(string what, int param, double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：修改" + what);
            try
            {
                _ufs.Param.SetDoubleValue(_op.Tag, param, v);
                SetHint(string.Format("已应用 {0}={1:0.####}  （Ctrl+Z 可撤销）", what, v));
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("修改失败（该工序类型可能不支持此参数）: " + ex.Message);
            }
        }

        /// <summary>写内外公差之一（二维参数整体回写，另一分量保持当前值）。</summary>
        private void ApplyTol(int idx, double v)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            string what = idx == 0 ? "内公差" : "外公差";

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：修改" + what);
            try
            {
                double[] t = AskTol();              // 先读当前值，只替换对应分量
                if (t[idx] >= 9000 && t[1 - idx] >= 9000)
                    t = new double[] { 0.001, 0.001 }; // 原值无效时给 NX 默认公差
                t[idx] = v;
                _ufs.Param.Set2dValue(_op.Tag,
                    (int)NXOpen.UF.UFConstants.UF_PARAM_TOL_IN_OUT, t);
                SetHint(string.Format("已应用 {0}={1:0.####}  （Ctrl+Z 可撤销）", what, v));
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("修改失败（该工序类型可能不支持此参数）: " + ex.Message);
            }
        }

        /// <summary>换刀：MoveObjects 到所选刀具组，UndoMark 包裹可撤销。</summary>
        private void ApplyToolGroup(NXOpen.CAM.NCGroup tool)
        {
            if (_op == null) { SetHint("尚未选中加工工序"); return; }
            Part work = _session.Parts.Work;
            if (work == null || work.CAMSetup == null) { SetHint("无可用工作部件"); return; }

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Visible, "加工助手：换刀");
            try
            {
                work.CAMSetup.MoveObjects(NXOpen.CAM.CAMSetup.View.MachineTool,
                    new NXOpen.CAM.CAMObject[] { _op }, tool,
                    NXOpen.CAM.CAMSetup.Paste.Inside);

                SetHint("已换刀: " + tool.Name + "  （Ctrl+Z 可撤销）");
                RefreshValues();
            }
            catch (Exception ex)
            {
                try { _session.UndoToMark(mark, null); } catch { }
                SetHint("换刀失败: " + ex.Message);
            }
        }

        // ------------------------------------------------------ 停靠（不可拖动）
        private void PlaceAtAnchor() { PlaceAtAnchor(AnchorRect()); }

        private void PlaceAtAnchor(Rectangle anchor)
        {
            System.Drawing.Point p;
            Rectangle a = anchor;
            if (!a.IsEmpty)
                p = new System.Drawing.Point(a.Left + 12, a.Top + 12);
            else
            {
                // 兜底：找不到图形区时不要贴屏幕最上沿（那里正是 NX 的文档标签栏），
                // 往下让出一行标签栏的高度，避免标题栏压到 NX 的标签上
                var wa0 = Screen.PrimaryScreen.WorkingArea;
                p = new System.Drawing.Point(wa0.Left + 12, wa0.Top + 60);
            }

            // 夹紧到所在屏幕的工作区内，避免锚点异常时窗口跑到屏幕外或标题被裁
            Rectangle wa = Screen.FromPoint(p).WorkingArea;
            if (p.X < wa.Left) p.X = wa.Left;
            if (p.Y < wa.Top) p.Y = wa.Top;
            if (Location != p) Location = p;

            // 模板页撑满图形区时，底边不得越过屏幕工作区底边（图形区可能延伸到任务栏后面）
            if (_curPage == 0)
            {
                int maxH = wa.Bottom - p.Y;
                if (maxH > 120 && Height > maxH) Height = maxH;
            }
        }

        /// <summary>
        /// 打开部件的显示区 = NX 主窗口的图形视口。
        /// 判定：优先选“贴着客户区右下角”的子窗口（图形视口总是延伸到客户区右下角，
        /// 而标签栏/工具条的容器窗口虽然更大但不贴底，会把锚点算高、标题被裁）；
        /// 无匹配时退回“面积最大”的老策略。
        /// 另外：有些机器上图形视口会把顶部的“文档标签栏”一起包进来（顶边=客户区顶边），
        /// 直接按它定位会让悬浮窗标题压到标签栏上，所以再把顶边下移到标签栏下沿。
        /// 主窗口句柄首次解析后缓存——弹层会短暂改变进程 MainWindowHandle
        /// 导致锚点算错、窗口移位（v1.3 踩坑），缓存后不受影响。
        /// </summary>
        private Rectangle AnchorRect()
        {
            try
            {
                // 主窗口句柄：优先自己找（按进程枚举顶层窗口取最大可见者）。
                // Process.MainWindowHandle 在某些机器/时机下会返回 0 或指向别的窗口，
                // 一旦为 0 就退回“屏幕左上角”，悬浮窗正好压在 NX 的文档标签栏上（曾出现过）。
                if (_nxMain == IntPtr.Zero || _nxMain == Handle)
                    _nxMain = FindMainWindow();
                if (_nxMain == IntPtr.Zero)
                    _nxMain = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (_nxMain == IntPtr.Zero || _nxMain == Handle) return Rectangle.Empty;

                // 主窗口客户区（屏幕坐标）：图形视口应贴其右下角
                int cLeft = int.MinValue, cTop = int.MinValue;
                int cRight = int.MinValue, cBottom = int.MinValue;
                RECT cl;
                POINT org = new POINT();
                if (GetClientRect(_nxMain, out cl) && ClientToScreen(_nxMain, ref org))
                {
                    cLeft = org.X;
                    cTop = org.Y;
                    cRight = org.X + cl.Right;
                    cBottom = org.Y + cl.Bottom;
                }

                IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
                long fbArea = 0;
                int bestTop = int.MinValue;
                List<RECT> all = new List<RECT>();   // 全部子窗口矩形（含细高的标签栏，用于修正顶边）
                EnumChildWindows(_nxMain, (h, l) =>
                {
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    all.Add(r);                      // 先全收：顶部标签栏只有 20 多像素高
                    long w = r.Right - r.Left, ht = r.Bottom - r.Top;
                    if (w <= 100 || ht <= 100) return true;
                    long area = w * ht;
                    if (area > fbArea) { fbArea = area; fallback = h; }
                    if (cRight == int.MinValue || cLeft == int.MinValue) return true;

                    // 候选 = 贴着客户区右下角、且足够大的子窗口。
                    // 图形视口一定满足；同时"整块客户区容器"也满足，但它的顶边更高（把
                    // 功能区/文档标签栏一起包了进来）。所以取"顶边最低"的那个 —— 它就是
                    // 最内层、真正的图形视口。窗口非最大化时底部可能多出状态条/滚动条，
                    // 故底边容差放宽到 120，右边缘仍按 20 判定。
                    long cw = cRight - cLeft, ch = cBottom - cTop;
                    if (Math.Abs(r.Right - cRight) > 20) return true;
                    if (r.Bottom > cBottom + 8 || cBottom - r.Bottom > 120) return true;
                    if (w < cw * 0.35 || ht < ch * 0.35) return true;
                    if (area < cw * ch / 4) return true;
                    if (r.Top > bestTop)
                    {
                        bestTop = r.Top; best = h;
                    }
                    return true;
                }, IntPtr.Zero);
                if (best == IntPtr.Zero) best = fallback;

                RECT rr;
                if (best != IntPtr.Zero && GetWindowRect(best, out rr))
                {
                    int w = rr.Right - rr.Left, h = rr.Bottom - rr.Top;

                    // 顶边修正：视口上方常压着“工具条 / 文档标签栏”这类横条，
                    // 直接按视口顶边停靠会把悬浮窗的标题（加工助手 / 模板 / 加工参数）
                    // 压到部件名标签上，看起来就是“挡住了工作区标题”。
                    //
                    // 搜索窗以**视口顶边**为基准（±80）。以前以客户区顶边为基准，
                    // 只能覆盖“视口把功能区整条包进来”的情况；而更常见的是
                    // “文档标签栏叠在视口最上面 20~30px”，那时标签栏的 Top 离客户区顶边
                    // 有上百像素，被这条 ±60 的窗口直接漏掉 —— 2026-11-13 又踩到。
                    int top = rr.Top;
                    int stripLo = rr.Top - 80, stripHi = rr.Top + 80;
                    int stripMaxH = Math.Max(30, h / 3);
                    foreach (RECT r in all)
                    {
                        int rw = r.Right - r.Left, rh = r.Bottom - r.Top;
                        if (rw <= 0 || rh <= 0) continue;
                        if (r.Top < stripLo || r.Top > stripHi) continue;           // 不在视口顶部区域
                        if (rw < w / 3) continue;                                  // 太窄，不是横条
                        if (rh > stripMaxH) continue;                              // 太高，是视口本身
                        if (r.Right <= rr.Left || r.Left >= rr.Right) continue;    // 与视口不重叠
                        // 左侧停靠面板（工序导航器）也从客户区左边起，但它的右沿离视口右沿还远，
                        // 必须排除；而“通栏的功能区 / 文档标签栏”（视口把整条功能区一起包进来时）
                        // 右沿会一直伸到视口右边，得保留。所以只在“左边在外面、右边也没到”时才丢。
                        if (r.Left < rr.Left - 4 && r.Right < rr.Right - 40) continue;
                        if (r.Bottom > top) top = r.Bottom;
                    }
                    int stripInset = top - rr.Top;     // 抓到的标签栏/横条厚度，0 = 没抓到

                    // 抓不到时按经验让一档：NX 的文档标签栏是**自绘**的（没有 HWND，
                    // EnumChildWindows 看不到），实测 2606 上它压在图形区上沿 20~30px。
                    // dock_gap = 0 → 自动（用 AutoDockGap）；>0 → 用手工给的像素。
                    int gap = MachAssistantConfig.DockGap;
                    int gapPx = gap > 0 ? gap * DeviceDpi / 96
                                        : (stripInset > 0 ? 0 : MachAssistantConfig.AutoDockGap * DeviceDpi / 96);
                    if (gapPx > 0 && rr.Bottom - (rr.Top + gapPx) > 120)
                        top = Math.Max(top, Math.Min(rr.Top + gapPx, rr.Top + 120));

                    Rectangle result;
                    if (top > rr.Top && rr.Bottom - top > 120)
                        result = new Rectangle(rr.Left, top, w, rr.Bottom - top);
                    else
                        result = new Rectangle(rr.Left, rr.Top, w, h);
                    DumpAnchor(result, rr, top, cLeft, cTop, cRight, cBottom, all,
                        "让位 " + (top - rr.Top) + "px（" + (stripInset > 0 ? "抓到横条" :
                        (gap > 0 ? "手填 dock_gap=" + gap : "自动兜底 " + MachAssistantConfig.AutoDockGap)) + "）");
                    return result;
                }

                // best 取不到：用主窗口客户区（不要整窗，标题栏/边框会把锚点算偏）
                if (cLeft != int.MinValue)
                {
                    Rectangle cr = new Rectangle(cLeft, cTop + 34 * DeviceDpi / 96, cRight - cLeft,
                        cBottom - cTop - 34 * DeviceDpi / 96);
                    DumpAnchor(cr, new RECT(), int.MinValue, cLeft, cTop, cRight, cBottom, all);
                    return cr;
                }
                if (GetWindowRect(_nxMain, out rr))
                {
                    Rectangle wr = new Rectangle(rr.Left, rr.Top, rr.Right - rr.Left, rr.Bottom - rr.Top);
                    DumpAnchor(wr, new RECT(), int.MinValue, cLeft, cTop, cRight, cBottom, all);
                    return wr;
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        /// <summary>按进程枚举顶层窗口，取面积最大的可见窗口 = NX 主窗口（比 Process.MainWindowHandle 可靠）。</summary>
        private IntPtr FindMainWindow()
        {
            IntPtr found = IntPtr.Zero;
            long bestArea = 0;
            try
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                IntPtr self = Handle;
                EnumWindows((h, l) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid != myPid || h == self) return true;
                    if (!IsWindowVisible(h)) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    long w = r.Right - r.Left, ht = r.Bottom - r.Top;
                    if (w < 200 || ht < 200) return true;
                    long area = w * ht;
                    if (area > bestArea) { bestArea = area; found = h; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private static bool _anchorDumped;
        private static Rectangle _anchorLastDump;

        /// <summary>锚点结果变化时追加一段诊断日志（同一结果只记一次），方便排查“停靠位置不对”。</summary>
        private void DumpAnchor(Rectangle a, RECT viewport, int topAfterFix,
            int cLeft, int cTop, int cRight, int cBottom, List<RECT> all, string gapNote = null)
        {
            if (_anchorDumped && a == _anchorLastDump) return;
            _anchorDumped = true;
            _anchorLastDump = a;
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LYJ");
                System.IO.Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== 加工助手 停靠锚点诊断 ===");
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("DPI: " + DeviceDpi);
                sb.AppendLine("NX主窗口句柄: 0x" + _nxMain.ToInt64().ToString("X"));
                sb.AppendLine("客户区(屏幕坐标): L" + cLeft + " T" + cTop + " R" + cRight + " B" + cBottom);
                if (topAfterFix != int.MinValue)
                {
                    sb.AppendLine("视口原始矩形: L" + viewport.Left + " T" + viewport.Top +
                        " R" + viewport.Right + " B" + viewport.Bottom);
                    sb.AppendLine("顶边修正后: " + topAfterFix +
                        (string.IsNullOrEmpty(gapNote) ? "" : "   [" + gapNote + "]"));
                }
                sb.AppendLine("锚点结果: L" + a.Left + " T" + a.Top + " W" + a.Width + " H" + a.Height);
                sb.AppendLine("子窗口矩形（最多 120 个）:");
                int n = 0;
                foreach (RECT r in all)
                {
                    if (n++ >= 120) break;
                    sb.AppendLine("  L" + r.Left + " T" + r.Top + " R" + r.Right + " B" + r.Bottom);
                }
                string logPath = System.IO.Path.Combine(dir, "anchor.log");
                try
                {
                    if (System.IO.File.Exists(logPath) &&
                        new System.IO.FileInfo(logPath).Length > 64 * 1024)
                        System.IO.File.Delete(logPath);
                }
                catch { }
                System.IO.File.AppendAllText(logPath, sb.ToString());
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int GWL_HWNDPARENT = -8;

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        // 模板页衬底：把窗口插到指定窗口下面一层（用来让浅灰衬底垫在悬浮窗底下）
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
    }
}
