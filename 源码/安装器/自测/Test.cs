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
using System.Text;

namespace NxToolboxSetup.Test
{
    /// <summary>安装器核心逻辑自测（不需要管理员权限，不碰真实 NX 安装）。</summary>
    internal static class TestMain
    {
        private static int _fail;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + what);
            if (!ok) _fail++;
        }

        private static int Main(string[] args)
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "lyj_setup_test_" + DateTime.Now.ToString("HHmmss"));
            if (Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); } catch { }
            }
            Directory.CreateDirectory(temp);

            string fakeNx = Path.Combine(temp, "FakeNX");
            string install = Path.Combine(temp, "Installed");
            string customDirs = Path.Combine(fakeNx, @"UGII\menus\custom_dirs.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(customDirs));
            Directory.CreateDirectory(Path.Combine(fakeNx, @"NXBIN\managed"));

            // 假 NX：拷一个真实 NXOpen.dll 用于版本读取
            // 真实 NXOpen.dll 仅用于读取版本号，路径由环境变量提供；没有就退化为 stub。
            string nxBase = Environment.GetEnvironmentVariable("NX_BASE_DIR");
            string realNxOpen = string.IsNullOrEmpty(nxBase)
                ? null
                : Path.Combine(nxBase, @"NXBIN\managed\NXOpen.dll");
            if (realNxOpen != null && File.Exists(realNxOpen))
                File.Copy(realNxOpen, Path.Combine(fakeNx, @"NXBIN\managed\NXOpen.dll"), true);
            else
                File.WriteAllText(Path.Combine(fakeNx, @"NXBIN\managed\NXOpen.dll"), "stub");

            string original = "# 原有配置\r\nD:\\OtherPluginA\r\nD:\\OtherPluginB\r\n";
            File.WriteAllText(customDirs, original, Encoding.Default);

            var log = new StringBuilder();
            var nx = new NxInstall { Root = fakeNx, Major = 2606 };

            Console.WriteLine("== 1. 释放 payload ==");
            int n = SetupCore.ExtractPayload(install, log);
            Check(n >= 20 && n <= 30, "释放文件数 = " + n + "（精简包）");
            Check(File.Exists(Path.Combine(install, @"startup\LYJ.dll")), "startup\\LYJ.dll 存在");
            Check(File.Exists(Path.Combine(install, @"startup\lyj.men")), "startup\\lyj.men 存在");
            Check(File.Exists(Path.Combine(install, @"startup\rbn_lyj.rtb")), "LYJ 选项卡已打包");
            Check(File.Exists(Path.Combine(install, @"startup\rbn_lyj_import.grb")), "图档导入组已打包");
            Check(File.Exists(Path.Combine(install, @"startup\rbn_lyj_export.grb")), "图档导出组已打包");
            Check(File.Exists(Path.Combine(install, @"startup\rbn_lyj_cam.grb")), "加工组（加工助手）已打包");
            Check(File.Exists(Path.Combine(install, @"startup\rbn_lyj_color.grb")), "显示组（透明化 + 颜色）已打包");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_lyj_transparency.grb")), "旧的独立透明化组不再打包");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_nxprogramming.rtb")), "旧 NX-编程 选项卡不再打包");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_nxprogramming_tools.grb")), "旧 工具 组不再打包");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_lyj_systools.grb")), "旧 LYJ-系统工具组 不再打包");
            Check(!File.Exists(Path.Combine(install, @"startup\gen_cam_strategies.men")), "生成的工序菜单不打包（首次启动自生成）");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_nxprogramming_insert.grb")), "生成的插入工序组不打包（首次启动自生成）");
            Check(!File.Exists(Path.Combine(install, @"config\cam_template.txt")), "cam_template.txt 不打包（首次启动自生成）");
            Check(!File.Exists(Path.Combine(install, @"config\icon_map.txt")), "icon_map.txt 不打包（首次启动自生成）");
            Check(!File.Exists(Path.Combine(install, @"config\cam_strategies.txt")), "工序清单不打包（首次启动自生成）");
            Check(File.Exists(Path.Combine(install, @"config\测试.prt")), "加工模板 测试.prt 已打包");
            Check(!File.Exists(Path.Combine(install, @"config\MY_Plan.prt")), "旧模板 MY_Plan.prt 不再打包");
            Check(!Directory.Exists(Path.Combine(install, @"config\BMP")), "图标素材目录 config\\BMP 不再打包");
            Check(Directory.GetFiles(Path.Combine(install, "application"), "cam_op_*.bmp").Length == 0, "加工策略图标不打包");
            Check(Directory.GetFiles(Path.Combine(install, "application"), "cam_grp_*.bmp").Length == 0, "策略文件夹图标不打包");
            Check(Directory.GetFiles(Path.Combine(install, "application"), "lyj_*.bmp").Length == 19, "功能区按钮图标 19 个");
            Check(File.Exists(Path.Combine(install, @"application\lyj_import_auto.bmp")), "导入图档图标存在");
            Check(File.Exists(Path.Combine(install, @"application\lyj_export_auto.bmp")), "导出图档图标存在");
            Check(File.Exists(Path.Combine(install, @"application\lyj_transparency.bmp")), "透明化图标存在");
            for (int i = 1; i <= 10; i++)
                Check(File.Exists(Path.Combine(install, string.Format(@"application\lyj_color_{0:00}.bmp", i))),
                      "颜色图标 " + i + " 存在");
            Check(File.Exists(Path.Combine(install, @"application\lyj_color_config.bmp")), "配置按钮图标存在");
            Check(File.Exists(Path.Combine(install, @"application\LYJ_Transparency.dlx")), "透明化对话框定义已打包");
            Check(File.Exists(Path.Combine(install, @"application\LYJ_ColorConfig.dlx")), "显示组配置对话框已打包");
            Check(File.Exists(Path.Combine(install, @"application\LYJ_ColorPick.dlx")), "改颜色选择对话框已打包");

            // 显示组的内容：固定 12 个按钮（透明化 + 颜色 01~10 + 配置），全部「仅图标」中号按钮。
            // 固定结构是"改颜色不用重启 NX"的前提：规模不变，只换颜色值。
            string colorGrb = File.ReadAllText(
                Path.Combine(install, @"startup\rbn_lyj_color.grb"), Encoding.GetEncoding("GBK"));
            int iTrans = colorGrb.IndexOf("LYJ_APP__transparency", StringComparison.Ordinal);
            int iColor1 = colorGrb.IndexOf("LYJ_APP__color01", StringComparison.Ordinal);
            int iColor10 = colorGrb.IndexOf("LYJ_APP__color10", StringComparison.Ordinal);
            int iCfg = colorGrb.IndexOf("LYJ_APP__color_config", StringComparison.Ordinal);
            Check(iTrans > 0 && iColor1 > iTrans, "透明化排在显示组第一位");
            Check(iColor10 > iColor1 && iCfg > iColor10, "颜色 01~10 顺序正确、配置按钮排最后");
            Check(colorGrb.Split(new[] { "BUTTON " }, StringSplitOptions.None).Length - 1 == 12,
                  "显示组固定 12 个按钮");
            Check(colorGrb.Contains("RIBBON_STYLE MEDIUM_IMAGE"), "显示组按钮为中号样式");
            Check(!colorGrb.Contains("_AND_TEXT"), "显示组按钮只显示图标、不带文字标签");
            Check(!colorGrb.Contains("ALWAYS_LARGE_IMAGE"), "显示组没有大号按钮");

            // 菜单里必须为每个颜色按钮登记同名菜单项，否则功能区那个按钮不显示（NX 的硬要求）
            string men = File.ReadAllText(
                Path.Combine(install, @"startup\lyj.men"), Encoding.GetEncoding("GBK"));
            int menuHits = 0;
            for (int i = 1; i <= 10; i++)
                if (men.Contains("BUTTON " + string.Format("LYJ_APP__color{0:00}", i))
                    && men.Contains("ACTIONS " + string.Format("LYJ_APP__color{0:00}", i)))
                    menuHits++;
            Check(menuHits == 10, "菜单为 10 个颜色按钮登记了同名菜单项（按钮才能显示）");
            Check(!File.Exists(Path.Combine(install, @"config\cam_strategies.stamp")), "时间戳未打包（首次启动会重新生成）");
            Check(File.Exists(Path.Combine(install, "install_dir.txt")), "写入 install_dir.txt");

            // 改名前的 NX工具箱 遗留文件必须清掉，否则 NX 会加载出点不动的死菜单
            File.WriteAllText(Path.Combine(install, @"startup\nxtools.men"), "# stale");
            File.WriteAllText(Path.Combine(install, @"startup\NXTools.dll"), "stale");
            File.WriteAllText(Path.Combine(install, @"application\nxtools_import_stl.bmp"), "stale");
            // 旧版四张导入图标（已合并为 lyj_import_auto.bmp），重装时必须被清掉
            File.WriteAllText(Path.Combine(install, @"application\lyj_import_stl.bmp"), "stale");
            File.WriteAllText(Path.Combine(install, @"application\lyj_import_stp.bmp"), "stale");
            File.WriteAllText(Path.Combine(install, @"application\lyj_import_igs.bmp"), "stale");
            File.WriteAllText(Path.Combine(install, @"application\lyj_import_xt.bmp"), "stale");
            // 透明化已并入「显示」组，旧的独立组文件重装时必须被清掉
            File.WriteAllText(Path.Combine(install, @"startup\rbn_lyj_transparency.grb"), "stale");
            // 上一版写死名字的五个颜色图标，重装时必须被清掉（现在按槽位编号 lyj_color_01..10）
            foreach (string c in new[] { "blue", "green", "gray", "white", "black" })
                File.WriteAllText(Path.Combine(install, @"application\lyj_color_" + c + ".bmp"), "stale");
            SetupCore.ExtractPayload(install, log);
            Check(!File.Exists(Path.Combine(install, @"startup\nxtools.men")), "旧 nxtools.men 已清理");
            Check(!File.Exists(Path.Combine(install, @"startup\NXTools.dll")), "旧 NXTools.dll 已清理");
            Check(!File.Exists(Path.Combine(install, @"application\nxtools_import_stl.bmp")), "旧 nxtools_*.bmp 已清理");
            Check(!File.Exists(Path.Combine(install, @"startup\rbn_lyj_transparency.grb")), "旧透明化组已清理");
            Check(!File.Exists(Path.Combine(install, @"application\lyj_import_stl.bmp")), "旧导入图标 lyj_import_stl.bmp 已清理");
            Check(!File.Exists(Path.Combine(install, @"application\lyj_import_xt.bmp")), "旧导入图标 lyj_import_xt.bmp 已清理");
            Check(!File.Exists(Path.Combine(install, @"application\lyj_color_blue.bmp")), "旧颜色图标 lyj_color_blue.bmp 已清理");
            Check(File.Exists(Path.Combine(install, @"application\lyj_import_auto.bmp")), "新导入图标未被误删");
            Check(File.Exists(Path.Combine(install, @"application\lyj_export_auto.bmp")), "新导出图标未被误删");
            Check(File.Exists(Path.Combine(install, @"startup\LYJ.dll")), "清理后新文件仍在");

            Console.WriteLine("== 2. 登记 custom_dirs.dat ==");
            Check(SetupCore.RegisterCustomDir(nx, install, log), "登记成功");
            string txt = File.ReadAllText(customDirs, Encoding.Default);
            Check(txt.Contains("D:\\OtherPluginA"), "原有条目保留");
            Check(txt.Contains(install), "新增安装目录条目");
            Check(File.Exists(customDirs + ".lyj_bak"), "原文件已备份");
            // 编码保护：GBK 原文件的中文注释必须原样保留，不能被写成 UTF-8 或出现替换字符
            byte[] after = File.ReadAllBytes(customDirs);
            Check(!(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF), "未强行加 UTF-8 BOM");
            string gbkBack = Encoding.GetEncoding(936).GetString(after);
            Check(gbkBack.Contains("原有配置"), "GBK 中文注释保留（未被转码破坏）");
            Check(!gbkBack.Contains("\uFFFD"), "无替换字符（无信息丢失）");

            Console.WriteLine("== 3. 重复登记应幂等 ==");
            SetupCore.RegisterCustomDir(nx, install, log);
            string txt2 = File.ReadAllText(customDirs, Encoding.Default);
            int hits = 0;
            foreach (string line in txt2.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Equals(install.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) hits++;
            Check(hits == 1, "目录出现次数 = " + hits);

            Console.WriteLine("== 4. 注销 ==");
            Check(SetupCore.UnregisterCustomDir(nx, install, log), "注销成功");
            string txt3 = File.ReadAllText(customDirs, Encoding.Default);
            Check(!txt3.Contains(install), "安装目录条目已删除");
            Check(!txt3.Contains("LYJ 外挂目录"), "标记注释已删除");
            Check(txt3.Contains("D:\\OtherPluginA") && txt3.Contains("D:\\OtherPluginB"), "其它外挂条目不受影响");
            string gbkAfterUnreg = Encoding.GetEncoding(936).GetString(File.ReadAllBytes(customDirs));
            Check(gbkAfterUnreg.Contains("原有配置") && !gbkAfterUnreg.Contains("\uFFFD"), "注销后中文注释仍完好");

            Console.WriteLine("== 4.5 加工模板安装 / 卸载 ==");
            string tplDir = Path.Combine(fakeNx, @"MACH\resource\template_part\metric");
            Directory.CreateDirectory(tplDir);
            SetupCore.InstallTemplates(install, nx, log);
            string tplDest = Path.Combine(tplDir, "测试.prt");
            Check(File.Exists(tplDest), "模板已复制到 NX 加工模板目录");
            Check(File.ReadAllBytes(tplDest).Length == File.ReadAllBytes(Path.Combine(install, @"config\测试.prt")).Length, "模板内容一致");
            SetupCore.RemoveTemplates(install, nx, log);
            Check(!File.Exists(tplDest), "卸载时移除模板");
            // 用户自己的同名模板不能被误删
            File.WriteAllText(tplDest, "user-own-template");
            SetupCore.RemoveTemplates(install, nx, log);
            Check(File.Exists(tplDest), "用户自有同名模板不被删除");
            File.Delete(tplDest);

            Console.WriteLine("== 5. 检测逻辑（真机） ==");
            List<NxInstall> real = SetupCore.DetectNx();
            Console.WriteLine("  检测到 " + real.Count + " 个 NX");
            foreach (NxInstall i in real) Console.WriteLine("    " + i.VersionText + "  " + i.Root);
            Check(real.Count >= 1, "至少检测到一个 NX");

            Console.WriteLine("== 6. 中文路径兼容 ==");
            string cnInstall = Path.Combine(temp, "LYJ 测试");
            SetupCore.ExtractPayload(cnInstall, log);
            Check(File.Exists(Path.Combine(cnInstall, @"startup\LYJ.dll")), "中文安装目录可正常释放");

            Console.WriteLine("== 7. 界面可正常创建 ==");
            try
            {
                using (var f = new MainForm())
                {
                    Check(f.Text.Length > 0, "主窗口构造成功：" + f.Text);
                }
            }
            catch (Exception ex) { Check(false, "主窗口构造异常：" + ex.Message); }

            Console.WriteLine("== 8. 默认安装目录 ==");
            string defDir = SetupCore.DefaultInstallDir;
            Console.WriteLine("  默认安装目录：" + defDir);
            Check(defDir.EndsWith(@"\LYJ", StringComparison.OrdinalIgnoreCase), "目录名以 LYJ 结尾");
            bool hasD = false;
            foreach (DriveInfo di in DriveInfo.GetDrives())
                if (di.Name.StartsWith("D", StringComparison.OrdinalIgnoreCase) && di.DriveType == DriveType.Fixed && di.IsReady) { hasD = true; break; }
            Check(hasD ? defDir.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase) : true,
                hasD ? "有 D 盘时默认装 D 盘" : "无 D 盘（不适用）");
            Check(defDir == @"C:\LYJ" || char.IsLetter(defDir[0]) && defDir[1] == ':', "盘符合法");

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "全部通过" : ("失败 " + _fail + " 项"));
            Console.WriteLine("日志：");
            Console.WriteLine(log.ToString());
            try { Directory.Delete(temp, true); } catch { }
            return _fail == 0 ? 0 : 1;
        }
    }
}
