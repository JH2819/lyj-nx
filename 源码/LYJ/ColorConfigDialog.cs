using System;
using System.IO;
using NXOpen;
using NXOpen.BlockStyler;

namespace LYJ
{
    /// <summary>
    /// 「显示」组配置对话框（NX 原生 Block Styler，颜色选择用 NX 内置调色板）。
    ///
    /// 布局（见 资源\对话框\LYJ_ColorConfig.dlx）：
    ///   颜色按钮 1~5   □ 按钮 1  [色块]   ……
    ///   颜色按钮 6~10  □ 按钮 6  [色块]   ……
    ///
    /// 功能区固定写 12 个按钮（透明化 + 颜色 1~10 + 配置），配置只决定每个槽的颜色与是否可用，
    /// **不改按钮结构** —— 所以改完颜色不用重启 NX：颜色值是点按钮时现场读配置的。
    /// 按钮图标（那个小色块）是启动时读的，图标颜色要到下次启动 NX 才刷新。
    /// </summary>
    internal static class ColorConfigDialog
    {
        private const string DlgFileName = "LYJ_ColorConfig.dlx";

        private static BlockDialog _dialog;
        private static readonly Toggle[] _chk = new Toggle[ColorSlots.Max];
        private static readonly ObjectColorPicker[] _col = new ObjectColorPicker[ColorSlots.Max];
        private static bool _loading;

        internal static void Show()
        {
            UI ui = UI.GetUI();
            if (_dialog != null) return;

            string dlx = DisplayTools.FindDlx(DlgFileName);
            DisplayTools.Log("配置显示组：dlx = " + (dlx ?? "<未找到>"));
            if (dlx == null)
            {
                ui.NXMessageBox.Show("显示组配置", NXMessageBox.DialogType.Error,
                    "找不到对话框定义 " + DlgFileName + "。\r\n\r\n" +
                    "请重新安装 LYJ —— 该文件应位于安装目录的 application 下。");
                return;
            }

            try
            {
                BlockDialog dlg = DisplayTools.CreateDialog(ui, DlgFileName, dlx);
                dlg.AddInitializeHandler(OnInit);
                dlg.AddUpdateHandler(OnUpdate);
                dlg.AddOkHandler(OnOk);
                dlg.AddCancelHandler(OnCancel);
                _dialog = dlg;

                DisplayTools.Log("配置显示组：Launch() 进入");
                dlg.Launch();
                DisplayTools.Log("配置显示组：Launch() 返回");
            }
            catch (Exception ex)
            {
                DisplayTools.Log("配置显示组：打开失败 " + ex);
                ui.NXMessageBox.Show("显示组配置", NXMessageBox.DialogType.Error,
                    "打开配置窗口失败：\r\n" + ex.Message);
            }
            finally
            {
                _dialog = null;
                for (int i = 0; i < ColorSlots.Max; i++) { _chk[i] = null; _col[i] = null; }
                _loading = false;
            }
        }

        private static void OnInit()
        {
            DisplayTools.Log("配置显示组：OnInit 进入");
            try
            {
                DisplayGroupCfg cfg = DisplayGroupCfg.Load();
                Part part = Session.GetSession().Parts.Work;
                _loading = true;

                for (int i = 0; i < ColorSlots.Max; i++)
                {
                    _chk[i] = Find<Toggle>("chk" + (i + 1).ToString("00"));
                    _col[i] = Find<ObjectColorPicker>("col" + (i + 1).ToString("00"));

                    ColorSlot s = (cfg.Slots != null && i < cfg.Slots.Length && cfg.Slots[i] != null)
                        ? cfg.Slots[i] : new ColorSlot();

                    if (_chk[i] != null)
                    {
                        _chk[i].Value = s.Enabled;
                        // 勾选框标题带上颜色名，用户才知道「按钮 3」是哪个颜色
                        if (!string.IsNullOrEmpty(s.Name))
                        {
                            try { _chk[i].Label = "按钮 " + (i + 1) + " · " + s.Name; }
                            catch { }
                        }
                    }
                    if (_col[i] == null) continue;

                    // 取色顺序：编号 → 调色板名 → RGB（都走 DisplayTools，RGB 那条有缓存，最多扫一次）
                    int id = s.ColorId;
                    string colorName = s.ColorName;
                    if (string.IsNullOrEmpty(colorName)) colorName = ColorSlots.EnglishName(s.Name);
                    if (id <= 0) id = DisplayTools.FindColorIdByName(part, colorName);
                    if (id <= 0) id = DisplayTools.FindColorIdByRgb(part, s.R, s.G, s.B);
                    SetPicker(_col[i], id > 0 ? id : 1);
                    _col[i].Enable = s.Enabled;
                }
                DisplayTools.Log("配置显示组：OnInit 完成");
            }
            catch (Exception ex)
            {
                DisplayTools.Log("配置显示组：初始化失败 " + ex);
            }
            finally
            {
                _loading = false;
            }
        }

        private static int OnUpdate(UIBlock block)
        {
            if (_loading) return 0;
            for (int i = 0; i < ColorSlots.Max; i++)
            {
                if (block == _chk[i] && _col[i] != null && _chk[i] != null)
                {
                    _col[i].Enable = _chk[i].Value;
                    break;
                }
            }
            return 0;
        }

        private static int OnOk()
        {
            try
            {
                Part part = Session.GetSession().Parts.Work;
                DisplayGroupCfg oldCfg = DisplayGroupCfg.Load();     // 没打开部件时用它保住原色值
                var cfg = new DisplayGroupCfg();
                cfg.ShowTransparency = oldCfg.ShowTransparency;      // 这个开关在配置文件里，界面上不再暴露
                cfg.Slots = new ColorSlot[ColorSlots.Max];

                int ready = 0;
                for (int i = 0; i < ColorSlots.Max; i++)
                {
                    var s = new ColorSlot();
                    s.Enabled = _chk[i] != null && _chk[i].Value;

                    int id = GetPicker(_col[i]);
                    s.ColorId = id;

                    double[] rgb = DisplayTools.GetColorRgb(part, id);
                    if (rgb != null)
                    {
                        s.R = rgb[0]; s.G = rgb[1]; s.B = rgb[2];
                        s.ColorName = DisplayTools.GetColorName(part, id);
                        s.Name = s.ColorName;
                    }
                    else
                    {
                        // 当前没有工作部件（或该色号取不到）→ 沿用原配置的色值，别把图标画黑
                        ColorSlot old = (oldCfg.Slots != null && i < oldCfg.Slots.Length) ? oldCfg.Slots[i] : null;
                        if (old != null) { s.R = old.R; s.G = old.G; s.B = old.B; s.Name = old.Name; s.ColorName = old.ColorName; }
                    }
                    if (string.IsNullOrEmpty(s.Name)) s.Name = "颜色 " + (i + 1);

                    if (s.Enabled) ready++;
                    cfg.Slots[i] = s;
                }

                cfg.Save();
                ColorSlots.WriteAllIcons(cfg);
                LYJApp.RebuildDisplayGroup();

                UI.GetUI().NXMessageBox.Show("显示组配置", NXMessageBox.DialogType.Information,
                    "已保存，共 " + ready + " 个颜色按钮可用。\r\n\r\n" +
                    "✔ 新颜色**立刻可用**：回到图形区选中对象，点颜色按钮就是新颜色（不需要重启）。\r\n" +
                    "· 按钮上那颗小色块是 NX 启动时读的图，颜色要到下次启动 NX 才刷新。\r\n" +
                    "· 勾上的按钮才有颜色，没勾的画成「＋」空位。\r\n\r\n" +
                    "配置也可以直接用记事本改：\r\n" + ColorSlots.CfgFile);
            }
            catch (Exception ex)
            {
                DisplayTools.Log("配置显示组：保存失败 " + ex);
                UI.GetUI().NXMessageBox.Show("显示组配置", NXMessageBox.DialogType.Error,
                    "保存失败：\r\n" + ex.Message);
            }
            return 0;
        }

        private static int OnCancel()
        {
            return 0;
        }

        #region 小工具

        private static T Find<T>(string blockId) where T : class
        {
            try
            {
                if (_dialog == null) return null;
                return _dialog.TopBlock.FindBlock(blockId) as T;
            }
            catch { return null; }
        }

        /// <summary>给颜色块赋值：正常只要一个颜色号，个别版本要求 [号, 0]。</summary>
        private static void SetPicker(ObjectColorPicker picker, int colorId)
        {
            if (picker == null) return;
            if (colorId <= 0) colorId = 1;
            try { picker.SetValue(new[] { colorId }); return; }
            catch { }
            try { picker.SetValue(new[] { colorId, 0 }); }
            catch { }
        }

        private static int GetPicker(ObjectColorPicker picker)
        {
            if (picker == null) return 0;
            try
            {
                int[] v = picker.GetValue();
                if (v != null && v.Length > 0) return v[0];
            }
            catch { }
            return 0;
        }

        #endregion
    }
}
