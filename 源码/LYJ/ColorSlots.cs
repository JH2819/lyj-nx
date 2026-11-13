using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LYJ
{
    /// <summary>「显示」组里的一颗颜色按钮。</summary>
    internal sealed class ColorSlot
    {
        public bool Enabled;        // 是否已配置（未启用的槽画成「＋」空位，点击直接进配置）
        public int ColorId;         // NX 调色板编号；0 = 没有固定编号，改色时按名字 / RGB 现场匹配
        public double R, G, B;      // 图标/兜底用的色值（0~1）
        public string Name = "";    // 显示名（中文备注，只用于提示）
        public string ColorName = "";   // NX 调色板里的英文名（Pale Blue…），按名字取色最快也最准

        public ColorSlot Clone()
        {
            return new ColorSlot
            {
                Enabled = Enabled, ColorId = ColorId, R = R, G = G, B = B,
                Name = Name, ColorName = ColorName
            };
        }
    }

    /// <summary>
    /// 「显示」组配置：透明化按钮是否显示 + 最多 10 颗颜色按钮。
    /// 存在 &lt;部署目录&gt;\config\color_slots.txt，程序与用户都可能改它。
    /// </summary>
    internal sealed class DisplayGroupCfg
    {
        public bool ShowTransparency = true;
        public ColorSlot[] Slots = ColorSlots.Default();

        public static DisplayGroupCfg Load()
        {
            var cfg = new DisplayGroupCfg();
            try
            {
                string file = ColorSlots.CfgFile;
                if (!File.Exists(file)) return cfg;

                var list = new ColorSlot[ColorSlots.Max];
                int n = 0;
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("#") || line.StartsWith(";"))
                    {
                        // 注释行里可能带开关： # transparency=1   （后面还可能跟中文说明）
                        string body = line.TrimStart('#', ';').Trim();
                        int eq = body.IndexOf('=');
                        if (eq > 0 && body.Substring(0, eq).Trim().Equals("transparency", StringComparison.OrdinalIgnoreCase))
                        {
                            string val = body.Substring(eq + 1).Trim();
                            int cut = val.IndexOfAny(new[] { '\t', ' ', '，', '（' });
                            if (cut > 0) val = val.Substring(0, cut);
                            bool v;
                            cfg.ShowTransparency = bool.TryParse(val, out v) ? v : (val != "0");
                        }
                        continue;
                    }

                    string[] f = line.Split('\t');
                    if (f.Length < 6 || n >= ColorSlots.Max) continue;   // 序号 启用 编号 R G B [名称] [调色板名]

                    var s = new ColorSlot();
                    int idx;
                    int.TryParse(f[0].Trim(), out idx);
                    s.Enabled = f[1].Trim() != "0";
                    int.TryParse(f[2].Trim(), out s.ColorId);
                    s.R = ParseD(f[3]);
                    s.G = ParseD(f[4]);
                    s.B = ParseD(f[5]);
                    s.Name = f.Length > 6 ? f[6].Trim() : "";
                    s.ColorName = f.Length > 7 ? f[7].Trim() : "";
                    list[n++] = s;
                }
                for (int i = n; i < ColorSlots.Max; i++) list[i] = new ColorSlot();   // 未写的槽 = 空
                cfg.Slots = list;
            }
            catch
            {
                // 读不动就用默认值（不打断启动）
            }
            return cfg;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LYJ「显示」组配置 —— 也可直接用记事本改，改完回到 NX 点按钮立刻生效");
            sb.AppendLine("# 每行：序号 <TAB> 启用(1/0) <TAB> 颜色编号(0=按名字/RGB) <TAB> R <TAB> G <TAB> B <TAB> 备注 <TAB> 调色板名");
            sb.AppendLine("# 颜色编号与调色板名都是 NX 调色板里的值；编号为 0 时先按调色板名找，再按 RGB 找");
            sb.AppendLine("# transparency=" + (ShowTransparency ? "1" : "0") + "\t（1 = 功能区显示「透明化」按钮）");
            for (int i = 0; i < ColorSlots.Max; i++)
            {
                ColorSlot s = (Slots != null && i < Slots.Length && Slots[i] != null)
                    ? Slots[i] : new ColorSlot();
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3:0.######}\t{4:0.######}\t{5:0.######}\t{6}\t{7}",
                    i + 1, s.Enabled ? 1 : 0, s.ColorId, s.R, s.G, s.B, s.Name, s.ColorName));
                sb.AppendLine();
            }

            string dir = Path.GetDirectoryName(ColorSlots.CfgFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ColorSlots.CfgFile, sb.ToString(), new UTF8Encoding(false));
        }

        private static double ParseD(string s)
        {
            double v;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v < 0 ? 0 : (v > 1 ? 1 : v);
            return 0;
        }
    }

    /// <summary>
    /// 「显示」组颜色按钮的配置读写 + 图标生成。
    /// 图标是 48×48 纯色块（功能区中号按钮 = MEDIUM_IMAGE，只显示图标），由程序按配置现画，
    /// 所以用户改完配置重启 NX，按钮颜色就跟着变了。
    /// </summary>
    internal static class ColorSlots
    {
        internal const int Max = 10;
        internal const string IconPrefix = "lyj_color_";

        internal static string CfgFile
        {
            get { return Path.Combine(LYJApp.CfgDir, "color_slots.txt"); }
        }

        /// <summary>第 index（0 起）颗按钮的图标名（不带扩展名）。</summary>
        internal static string IconName(int index)
        {
            return IconPrefix + (index + 1).ToString("00");
        }

        /// <summary>出厂默认：淡蓝 / 浅绿 / 浅灰 / 白色 / 黑色，其余槽为空。</summary>
        internal static ColorSlot[] Default()
        {
            var list = new ColorSlot[Max];
            for (int i = 0; i < Max; i++) list[i] = new ColorSlot();

            list[0].Enabled = true; list[0].R = 0.5372549; list[0].G = 0.7137255; list[0].B = 1.0;        list[0].Name = "淡蓝"; list[0].ColorName = "Pale Blue";
            list[1].Enabled = true; list[1].R = 0.8431373; list[1].G = 1.0;       list[1].B = 0.7803922;  list[1].Name = "浅绿"; list[1].ColorName = "Pale Green";
            list[2].Enabled = true; list[2].R = 0.6;       list[2].G = 0.6;       list[2].B = 0.6;        list[2].Name = "浅灰"; list[2].ColorName = "Silver Gray";
            list[3].Enabled = true; list[3].R = 1.0;       list[3].G = 1.0;       list[3].B = 1.0;        list[3].Name = "白色"; list[3].ColorName = "White";
            list[4].Enabled = true; list[4].R = 0.0;       list[4].G = 0.0;       list[4].B = 0.0;        list[4].Name = "黑色"; list[4].ColorName = "Black";
            return list;
        }

        /// <summary>第 index 个槽是否已配置（空槽 / 未读到配置都算空）。</summary>
        internal static bool IsSlotReady(ColorSlot[] slots, int index)
        {
            return slots != null && index < slots.Length && slots[index] != null && slots[index].Enabled;
        }

        /// <summary>
        /// 中文备注 → NX 调色板英文名。旧配置文件只有中文名，靠它先按名字取色，
        /// 免得退回"按 RGB 全调色板扫描"那条慢路径。
        /// </summary>
        internal static string EnglishName(string chinese)
        {
            if (string.IsNullOrEmpty(chinese)) return "";
            switch (chinese.Trim())
            {
                case "淡蓝": return "Pale Blue";
                case "浅绿": return "Pale Green";
                case "浅灰": return "Silver Gray";
                case "白色": return "White";
                case "黑色": return "Black";
                default: return "";
            }
        }

        #region 图标

        // 中号按钮（MEDIUM_IMAGE）用的原图尺寸。功能区按自己的像素尺寸缩放这张图，
        // 48 是 24 的整两倍 —— 不管 NX 最后画成 24 还是 32/40，缩放都是整比，色块不会发虚。
        private const int IconSize = 48;

        /// <summary>按配置重画全部显示组图标（颜色块 / 空位 / 透明化棋盘格 / 配置按钮）。</summary>
        internal static void WriteAllIcons(DisplayGroupCfg cfg)
        {
            try
            {
                string dir = LYJApp.AppDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                ColorSlot[] slots = (cfg != null && cfg.Slots != null) ? cfg.Slots : Default();
                for (int i = 0; i < Max; i++)
                {
                    ColorSlot s = (i < slots.Length && slots[i] != null) ? slots[i] : new ColorSlot();
                    string path = Path.Combine(dir, IconName(i) + ".bmp");
                    if (s.Enabled)
                        WriteIcon(path, s.R, s.G, s.B);
                    else
                        WriteAddIcon(path);          // 没配置的槽画成「＋」空位
                }

                WriteCheckerIcon(Path.Combine(dir, "lyj_transparency.bmp"));
                WritePaletteIcon(Path.Combine(dir, "lyj_color_config.bmp"));
            }
            catch
            {
                // 图标写不出来不影响其它功能（只是按钮显示成占位图）
            }
        }

        /// <summary>48×48 纯色块（无边框、无文字）。</summary>
        internal static void WriteIcon(string path, double r, double g, double b)
        {
            const int size = IconSize;
            byte cr = ToByte(r), cg = ToByte(g), cb = ToByte(b);
            var px = new byte[size, size, 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    px[y, x, 0] = cr; px[y, x, 1] = cg; px[y, x, 2] = cb;
                }
            WriteRaw(path, px);
        }

        /// <summary>空位图标：极浅的方块 + 中间一个「＋」，表示这个颜色按钮还没配置。</summary>
        internal static void WriteAddIcon(string path)
        {
            const int size = IconSize;
            const int u = size / 24;                 // 24→1、48→2：线宽/臂长都按倍数放大，中号也不发细
            const byte bg = 246, edge = 224, ink = 138;
            var px = new byte[size, size, 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool border = (x < u || y < u || x >= size - u || y >= size - u);
                    byte v = border ? edge : bg;
                    px[y, x, 0] = v; px[y, x, 1] = v; px[y, x, 2] = v;
                }

            // 「＋」：横臂 y=11u..13u-1、x=7u..17u-1；竖臂 x=11u..13u-1、y=7u..17u-1
            for (int x = 7 * u; x < 17 * u; x++)
                for (int y = 11 * u; y < 13 * u; y++)
                { px[y, x, 0] = ink; px[y, x, 1] = ink; px[y, x, 2] = ink; }
            for (int y = 7 * u; y < 17 * u; y++)
                for (int x = 11 * u; x < 13 * u; x++)
                { px[y, x, 0] = ink; px[y, x, 1] = ink; px[y, x, 2] = ink; }

            WriteRaw(path, px);
        }

        /// <summary>透明化图标：黑白棋盘格（表示透明），无边框。</summary>
        internal static void WriteCheckerIcon(string path)
        {
            const int size = IconSize;
            const int cell = 3 * (size / 24);        // 格子跟着尺寸放大：24→3、48→6
            var px = new byte[size, size, 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool white = ((x / cell) + (y / cell)) % 2 == 0;
                    byte v = white ? (byte)255 : (byte)184;
                    px[y, x, 0] = v; px[y, x, 1] = v; px[y, x, 2] = v;
                }
            WriteRaw(path, px);
        }

        /// <summary>配置按钮图标：3×3 的彩色小方块（NX 调色板的样子），无边框。</summary>
        internal static void WritePaletteIcon(string path)
        {
            const int size = IconSize;
            const int cell = 8 * (size / 24);        // 3 格正好铺满：24→8、48→16
            var colors = new[]
            {
                new byte[] { 92, 92, 92 },  new byte[] { 60, 76, 231 },  new byte[] { 48, 162, 65 },
                new byte[] { 34, 34, 34 },  new byte[] { 219, 219, 219 }, new byte[] { 42, 142, 216 },
                new byte[] { 232, 232, 232 }, new byte[] { 20, 20, 20 }, new byte[] { 168, 168, 168 },
            };
            var px = new byte[size, size, 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int cx = x / cell, cy = y / cell;
                    if (cx > 2 || cy > 2) { px[y, x, 0] = 245; px[y, x, 1] = 245; px[y, x, 2] = 245; continue; }
                    byte[] c = colors[cy * 3 + cx];
                    px[y, x, 0] = c[0]; px[y, x, 1] = c[1]; px[y, x, 2] = c[2];
                }
            WriteRaw(path, px);
        }

        private static void WriteRaw(string path, byte[, ,] px)
        {
            int size = px.GetLength(0);
            int stride = ((size * 3 + 3) / 4) * 4;
            int dataSize = stride * size;

            var buf = new byte[54 + dataSize];
            buf[0] = (byte)'B'; buf[1] = (byte)'M';
            BitConverter.GetBytes(54 + dataSize).CopyTo(buf, 2);
            BitConverter.GetBytes(54).CopyTo(buf, 10);
            BitConverter.GetBytes(40).CopyTo(buf, 14);
            BitConverter.GetBytes(size).CopyTo(buf, 18);
            BitConverter.GetBytes(size).CopyTo(buf, 22);
            buf[26] = 1; buf[28] = 24;
            BitConverter.GetBytes(dataSize).CopyTo(buf, 34);

            int p = 54;
            for (int y = size - 1; y >= 0; y--)          // BMP 自底向上
            {
                for (int x = 0; x < size; x++)
                {
                    buf[p++] = px[y, x, 2];              // B
                    buf[p++] = px[y, x, 1];              // G
                    buf[p++] = px[y, x, 0];              // R
                }
                p += stride - size * 3;
            }
            File.WriteAllBytes(path, buf);
        }

        private static byte ToByte(double v)
        {
            int i = (int)Math.Round(v * 255.0);
            if (i < 0) i = 0;
            if (i > 255) i = 255;
            return (byte)i;
        }

        #endregion
    }
}
