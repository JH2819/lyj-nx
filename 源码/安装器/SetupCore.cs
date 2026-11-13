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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace NxToolboxSetup
{
    /// <summary>检测到的一个 NX 安装。</summary>
    public class NxInstall
    {
        public string Root;      // NX 安装目录（UGII_BASE_DIR）
        public int Major;        // 主版本号：12 / 1847 / 2606 …
        public string VersionText { get { return Major >= 1000 ? "NX " + Major : "NX " + Major + ".0"; } }
        /// <summary>是否是本外挂适配的版本（仅 NX 2606）。</summary>
        public bool Supported { get { return Major == SetupCore.TargetMajor; } }
        public string Display
        {
            get { return VersionText + "    " + Root + (Supported ? "" : "    [未适配]"); }
        }
        public override string ToString() { return Display; }
    }

    /// <summary>安装/卸载核心逻辑（与界面解耦，便于命令行与自动化测试）。</summary>
    public static class SetupCore
    {
        public const string ProductName = "LYJ";
        /// <summary>本外挂适配的 NX 主版本（只在这一个版本上验证过，界面上按此标注）。</summary>
        public const int TargetMajor = 2606;
        /// <summary>发布版本号（窗口标题 / 卸载信息用）：打包脚本通过 /p:Version= 注入，
        /// 形如 1.0.261105-1431（大版本.小版本.打包时间）；没注入时退回程序集版本。</summary>
        public static readonly string ProductVersion = ReadProductVersion();
        public const string PayloadResource = "payload.zip";           // 与 EmbeddedResource 名称一致
        public const string MarkerLine = "# LYJ 外挂目录（安装程序写入，卸载时会自动删除）";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LYJ";

        private static string ReadProductVersion()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                object[] attrs = asm.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs != null && attrs.Length > 0)
                {
                    string v = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0])
                        .InformationalVersion;
                    if (!string.IsNullOrEmpty(v)) return v.Trim();
                }
                return asm.GetName().Version.ToString(3);
            }
            catch { return "1.0"; }
        }

        /// <summary>
        /// 默认安装目录（纯英文路径，避免 NX 读取 custom_dirs.dat 时的编码问题）。
        /// 优先 D 盘（不占系统盘）；D 盘不可用时依次试 E/F/G，最后回落 C 盘。
        /// </summary>
        public static string DefaultInstallDir
        {
            get
            {
                if (HasFixedDrive('D')) return @"D:\LYJ";
                foreach (char c in new char[] { 'E', 'F', 'G' })
                    if (HasFixedDrive(c)) return c + @":\LYJ";
                return @"C:\LYJ";
            }
        }

        /// <summary>该盘符是否是可用（就绪）的本地固定磁盘。</summary>
        private static bool HasFixedDrive(char letter)
        {
            try
            {
                char want = char.ToUpperInvariant(letter);
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.Name.Length < 2) continue;
                    if (char.ToUpperInvariant(d.Name[0]) != want) continue;
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ==================== NX 检测 ====================

        /// <summary>检测本机所有 NX（NX12 及以上、带 .NET 接口）。走注册表 + 常见目录扫描。
        /// 注意：这里只是「列出来」，是否适配由 TargetMajor 决定，界面上非 2606 的会标 [未适配]。</summary>
        public static List<NxInstall> DetectNx()
        {
            var roots = new List<string>();
            Action<string> add = delegate (string d)
            {
                if (string.IsNullOrEmpty(d)) return;
                d = ToLongPath(d.Trim().TrimEnd('\\', '/'));
                foreach (string r in roots)
                    if (string.Equals(r, d, StringComparison.OrdinalIgnoreCase)) return;
                roots.Add(d);
            };

            // 1) 注册表：HKLM/HKCU \ SOFTWARE[\WOW6432Node]\Siemens\Designcenter <版本>
            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string sub in new[] { @"SOFTWARE\Siemens", @"SOFTWARE\WOW6432Node\Siemens" })
                {
                    try
                    {
                        using (RegistryKey k = hive.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            foreach (string name in k.GetSubKeyNames())
                            {
                                if (name.IndexOf("Designcenter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                using (RegistryKey sk = k.OpenSubKey(name))
                                {
                                    if (sk == null) continue;
                                    object v = sk.GetValue("UGII_BASE_DIR") ?? sk.GetValue("INSTALLDIR");
                                    if (v != null) add(v.ToString());
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2) 环境变量 UGII_BASE_DIR
            try { add(Environment.GetEnvironmentVariable("UGII_BASE_DIR")); } catch { }

            // 3) 常见目录：<ProgramFiles>\Siemens\*、各盘符 \Program Files\Siemens\*
            var candidates = new List<string>();
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Siemens"));
            }
            foreach (DriveInfo di in SafeDrives())
            {
                candidates.Add(Path.Combine(di.RootDirectory.FullName, @"Program Files\Siemens"));
                candidates.Add(Path.Combine(di.RootDirectory.FullName, "Siemens"));
            }
            foreach (string c in candidates)
            {
                try
                {
                    if (!Directory.Exists(c)) continue;
                    foreach (string d in Directory.GetDirectories(c)) add(d);
                }
                catch { }
            }

            // 过滤：必须有 NXBIN\managed\NXOpen.dll（.NET 接口），且主版本 >= 12
            var result = new List<NxInstall>();
            foreach (string root in roots)
            {
                string nxopen = Path.Combine(root, @"NXBIN\managed\NXOpen.dll");
                if (!File.Exists(nxopen)) continue;
                int major = ReadMajor(nxopen);
                if (major <= 0) continue;
                if (major < 12 && major >= 1000) continue;   // 理论上不会出现
                if (major < 12) continue;                    // NX11 及更早不支持
                result.Add(new NxInstall { Root = root, Major = major });
            }
            result.Sort(delegate (NxInstall a, NxInstall b) { return a.Major.CompareTo(b.Major); });
            return result;
        }

        private static DriveInfo[] SafeDrives()
        {
            try { return DriveInfo.GetDrives(); } catch { return new DriveInfo[0]; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);

        /// <summary>把 8.3 短路径（如 C:\Users\ADMINI~1\…）还原为完整长路径，写进配置文件更可读。</summary>
        public static string ToLongPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return path;
                var sb = new StringBuilder(1024);
                int n = GetLongPathName(path, sb, sb.Capacity);
                if (n > 0 && n <= sb.Capacity) return sb.ToString(0, n);
            }
            catch { }
            return path;
        }

        /// <summary>从 NXOpen.dll 的 AssemblyVersion 读 NX 主版本号。</summary>
        private static int ReadMajor(string nxopenPath)
        {
            try
            {
                AssemblyName an = AssemblyName.GetAssemblyName(nxopenPath);
                if (an != null && an.Version != null) return an.Version.Major;
            }
            catch { }
            return 0;
        }

        // ==================== custom_dirs.dat 注册 ====================

        /// <summary>确定该 NX 的 custom_dirs.dat 路径（优先已存在的文件）。</summary>
        public static string CustomDirsFile(string nxRoot, bool createIfMissing)
        {
            string a = Path.Combine(nxRoot, @"UGII\menus\custom_dirs.dat");
            string b = Path.Combine(nxRoot, @"UGII\custom_dirs.dat");
            if (File.Exists(a)) return a;
            if (File.Exists(b)) return b;
            if (!createIfMissing) return null;
            try { Directory.CreateDirectory(Path.GetDirectoryName(a)); } catch { }
            return a;
        }

        /// <summary>把外挂目录登记到指定 NX 的 custom_dirs.dat（幂等，自动备份）。</summary>
        public static bool RegisterCustomDir(NxInstall nx, string installDir, StringBuilder log)
        {
            try
            {
                string dirLong = ToLongPath(installDir.TrimEnd('\\'));
                string file = CustomDirsFile(nx.Root, true);
                if (file == null) { log.AppendLine("  [失败] 无法定位 " + nx.VersionText + " 的 custom_dirs.dat"); return false; }

                string text = "";
                TextEncodingKind kind = TextEncodingKind.Utf8NoBom;
                if (File.Exists(file)) text = ReadAllTextAuto(file, out kind);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                bool already = false;
                foreach (string l in lines)
                {
                    string t = l.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    if (string.Equals(t.TrimEnd('\\'), dirLong, StringComparison.OrdinalIgnoreCase))
                    { already = true; break; }
                }

                if (!already)
                {
                    // 首次写入前备份原文件
                    string bak = file + ".lyj_bak";
                    if (File.Exists(file) && !File.Exists(bak)) File.Copy(file, bak, true);

                    var sb = new StringBuilder();
                    sb.Append(text.TrimEnd('\r', '\n'));
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append(MarkerLine).Append("\r\n");
                    sb.Append(dirLong).Append("\r\n");
                    WriteAllTextAuto(file, sb.ToString(), kind);
                    log.AppendLine("  登记到 " + nx.VersionText + "：" + file);
                }
                else
                {
                    log.AppendLine("  " + nx.VersionText + " 已登记，跳过");
                }
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine("  [失败] " + nx.VersionText + " 登记出错：" + ex.Message);
                return false;
            }
        }

        /// <summary>从指定 NX 的 custom_dirs.dat 里去掉外挂目录（卸载用）。</summary>
        public static bool UnregisterCustomDir(NxInstall nx, string installDir, StringBuilder log)
        {
            try
            {
                string dirLong = ToLongPath(installDir.TrimEnd('\\'));
                string file = CustomDirsFile(nx.Root, false);
                if (file == null || !File.Exists(file)) return true;

                TextEncodingKind kind;
                string text = ReadAllTextAuto(file, out kind);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                var keep = new List<string>();
                bool changed = false;
                foreach (string l in lines)
                {
                    string t = l.Trim();
                    if (t.Equals(MarkerLine, StringComparison.OrdinalIgnoreCase)) { changed = true; continue; }
                    if (t.Length > 0 && !t.StartsWith("#") &&
                        string.Equals(t.TrimEnd('\\'), dirLong, StringComparison.OrdinalIgnoreCase))
                    { changed = true; continue; }
                    keep.Add(l);
                }
                if (changed)
                {
                    WriteAllTextAuto(file, string.Join("\r\n", keep).TrimEnd('\r', '\n') + "\r\n", kind);
                    log.AppendLine("  从 " + nx.VersionText + " 移除登记：" + file);
                }
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine("  [失败] " + nx.VersionText + " 移除登记出错：" + ex.Message);
                return false;
            }
        }

        /// <summary>cn/dat 文件的原编码类型。写回时必须与读入时一致，否则会破坏原有中文注释。</summary>
        public enum TextEncodingKind { Utf8NoBom, Utf8Bom, Gbk }

        /// <summary>探测编码：UTF-8 BOM → 严格 UTF-8 → 本地代码页（中文系统 GBK）。</summary>
        public static TextEncodingKind DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return TextEncodingKind.Utf8Bom;
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);   // 纯 ASCII / 合法 UTF-8
                return TextEncodingKind.Utf8NoBom;
            }
            catch { return TextEncodingKind.Gbk; }
        }

        /// <summary>读文本：严格解码，绝不用替换字符，避免把 GBK 中文读成 ?? 后再写坏原文件。</summary>
        public static string ReadAllTextAuto(string path, out TextEncodingKind kind)
        {
            byte[] bytes = File.ReadAllBytes(path);
            kind = DetectEncoding(bytes);
            if (kind == TextEncodingKind.Utf8Bom)
            {
                try { return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
                catch { kind = TextEncodingKind.Gbk; }     // BOM 下却是 GBK 字节的异常文件
            }
            if (kind == TextEncodingKind.Utf8NoBom)
                return new UTF8Encoding(false, true).GetString(bytes);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.Default.GetString(bytes);       // GBK
        }

        /// <summary>按原编码写回（UTF-8 时不加 BOM，除非原文件本来就有 BOM）。</summary>
        public static void WriteAllTextAuto(string path, string text, TextEncodingKind kind)
        {
            Encoding enc;
            if (kind == TextEncodingKind.Gbk) enc = Encoding.Default;
            else if (kind == TextEncodingKind.Utf8Bom) enc = new UTF8Encoding(true);
            else enc = new UTF8Encoding(false);
            File.WriteAllText(path, text, enc);
        }

        // ==================== 文件部署 ====================

        /// <summary>把内嵌的 payload.zip 解压到安装目录。</summary>
        public static int ExtractPayload(string installDir, StringBuilder log)
        {
            Directory.CreateDirectory(installDir);
            int count = 0;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (s == null)
                    throw new InvalidOperationException("安装包损坏：缺少内嵌数据 payload.zip");
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项
                        string dest = Path.Combine(installDir, e.FullName.Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        count++;
                    }
                }
            }
            log.AppendLine("  已释放 " + count + " 个文件 → " + installDir);

            // 清掉改名前的遗留文件。NX 会扫描 startup\ 下所有 .men/.rtb，
            // 旧菜单留着只会变成点不动的死按钮，装新版时顺手删掉。
            string[] stale =
            {
                @"startup\nxtools.men", @"startup\rbn_nxtools.rtb",
                @"startup\rbn_nxtools_import.grb", @"startup\rbn_nxtools_export.grb",
                @"startup\NXTools.dll", @"startup\rbn_nxprogramming.rtb",
                @"startup\rbn_nxprogramming_insert.grb", @"startup\rbn_nxprogramming_tools.grb",
                @"startup\rbn_lyj_systools.grb", @"startup\gen_cam_strategies.men",
                @"startup\rbn_lyj_transparency.grb",     // 透明化已并入「显示」组（rbn_lyj_color.grb）
                // 上一版写死名字的颜色图标：现在按槽位编号 lyj_color_01..10 由程序现画
                @"application\lyj_color_blue.bmp", @"application\lyj_color_green.bmp",
                @"application\lyj_color_gray.bmp", @"application\lyj_color_white.bmp",
                @"application\lyj_color_black.bmp",
            };
            foreach (string rel in stale)
            {
                string p = Path.Combine(installDir, rel);
                try
                {
                    if (!File.Exists(p)) continue;
                    File.Delete(p);
                    log.AppendLine("  已清理旧版本文件：" + rel);
                }
                catch { }
            }
            try
            {
                string appDir = Path.Combine(installDir, "application");
                if (Directory.Exists(appDir))
                {
                    foreach (string f in Directory.GetFiles(appDir, "nxtools_*.bmp"))
                        try { File.Delete(f); } catch { }
                    // 导入按钮已合并为 lyj_import_auto.bmp，旧的四张导入图标不再被引用。
                    // 这里必须逐个精确删，不能用 lyj_import_*.bmp 通配（会把新图标一起删掉）。
                    foreach (string old in new[] { "lyj_import_stl.bmp", "lyj_import_stp.bmp",
                                                   "lyj_import_igs.bmp", "lyj_import_xt.bmp" })
                    {
                        string p = Path.Combine(appDir, old);
                        try
                        {
                            if (!File.Exists(p)) continue;
                            File.Delete(p);
                            log.AppendLine("  已清理旧版导入图标：" + old);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 删除模板时间戳：让 NX 首次启动按本机模板重新生成工序清单与图标
            string stamp = Path.Combine(installDir, @"config\cam_strategies.stamp");
            try { if (File.Exists(stamp)) { File.Delete(stamp); log.AppendLine("  已重置模板时间戳（首次启动 NX 时自动同步工序清单）"); } }
            catch { }

            // 记录安装目录，供外挂运行时定位（DLL 自身也能推断，这里是双保险）
            try { File.WriteAllText(Path.Combine(installDir, "install_dir.txt"), installDir, Encoding.UTF8); } catch { }
            return count;
        }

        /// <summary>给安装目录授予普通用户修改权限（NX 以内嵌用户身份运行，需要写配置/图标）。</summary>
        public static void GrantUsersModify(string dir, StringBuilder log)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                DirectorySecurity sec = di.GetAccessControl();
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
                log.AppendLine("  已授权 Users 修改权限");
            }
            catch (Exception ex)
            {
                log.AppendLine("  [提示] 设置目录权限失败（可手动给目录加写权限）：" + ex.Message);
            }
        }

        // ==================== 卸载信息 ====================

        public static void WriteUninstallKey(string installDir, string uninstallerExe, StringBuilder log)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", ProductName);
                    k.SetValue("DisplayVersion", ProductVersion);
                    k.SetValue("Publisher", "LYJ");
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("DisplayIcon", uninstallerExe);
                    k.SetValue("UninstallString", "\"" + uninstallerExe + "\" /U");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 4096, RegistryValueKind.DWord);
                }
                log.AppendLine("  已写入控制面板卸载信息");
            }
            catch (Exception ex) { log.AppendLine("  [提示] 写卸载信息失败：" + ex.Message); }
        }

        public static void RemoveUninstallKey()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
        }

        /// <summary>把自身复制成安装目录里的卸载程序（安装包删掉也能卸载）。</summary>
        public static string CopySelfAsUninstaller(string installDir, StringBuilder log)
        {
            try
            {
                string self = ToLongPath(Assembly.GetExecutingAssembly().Location);
                string dest = Path.Combine(installDir, "卸载" + ProductName + ".exe");
                if (!string.Equals(self, dest, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, dest, true);
                log.AppendLine("  卸载程序：" + dest);
                return dest;
            }
            catch (Exception ex)
            {
                log.AppendLine("  [提示] 生成卸载程序失败：" + ex.Message);
                return Path.Combine(installDir, "卸载" + ProductName + ".exe");
            }
        }

        // ==================== 加工模板安装 ====================

        /// <summary>NX 的加工模板目录（MACH\resource\template_part\metric）。
        /// CAMOperationCollection.CreateWithUserName 只认安装到这里的模板部件。</summary>
        public static string TemplatePartDir(NxInstall nx)
        {
            if (nx == null || string.IsNullOrEmpty(nx.Root)) return null;
            string baseDir = Path.Combine(nx.Root, @"MACH\resource\template_part");
            if (!Directory.Exists(baseDir)) return null;
            string metric = Path.Combine(baseDir, "metric");
            if (Directory.Exists(metric)) return metric;
            string english = Path.Combine(baseDir, "english");
            if (Directory.Exists(english)) return english;
            return metric;   // 都不存在时按 metric 创建
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
                using (var ha = System.Security.Cryptography.MD5.Create())
                using (var sa = File.OpenRead(a))
                {
                    byte[] da = ha.ComputeHash(sa);
                    using (var sb = File.OpenRead(b))
                    {
                        byte[] db = ha.ComputeHash(sb);
                        if (da.Length != db.Length) return false;
                        for (int i = 0; i < da.Length; i++) if (da[i] != db[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>把安装目录 config\*.prt 的加工模板复制到各 NX 的加工模板目录（已存在且一致则跳过）。</summary>
        public static void InstallTemplates(string installDir, NxInstall nx, StringBuilder log)
        {
            try
            {
                string cfg = Path.Combine(installDir, "config");
                if (!Directory.Exists(cfg)) return;
                string[] prts = Directory.GetFiles(cfg, "*.prt", SearchOption.TopDirectoryOnly);
                if (prts.Length == 0) return;
                string destDir = TemplatePartDir(nx);
                if (destDir == null)
                {
                    log.AppendLine("  [提示] " + nx.VersionText + " 没有加工模板目录，跳过模板安装");
                    return;
                }
                Directory.CreateDirectory(destDir);
                foreach (string src in prts)
                {
                    string dest = Path.Combine(destDir, Path.GetFileName(src));
                    if (File.Exists(dest) && SameFile(src, dest))
                    {
                        log.AppendLine("  模板已就位：" + Path.GetFileName(src));
                        continue;
                    }
                    if (File.Exists(dest))
                    {
                        string bak = dest + ".lyj_bak";
                        if (!File.Exists(bak)) { try { File.Copy(dest, bak, true); } catch { } }
                    }
                    File.Copy(src, dest, true);
                    log.AppendLine("  模板已安装到 " + nx.VersionText + "：" + dest);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  [提示] 模板安装失败（可手动复制）：" + ex.Message);
            }
        }

        /// <summary>卸载时移除安装的加工模板（只删与本包一致的那份，用户改过的不动；有备份则还原）。</summary>
        public static void RemoveTemplates(string installDir, NxInstall nx, StringBuilder log)
        {
            try
            {
                string cfg = Path.Combine(installDir, "config");
                if (!Directory.Exists(cfg)) return;
                string destDir = TemplatePartDir(nx);
                if (destDir == null) return;
                foreach (string src in Directory.GetFiles(cfg, "*.prt", SearchOption.TopDirectoryOnly))
                {
                    string dest = Path.Combine(destDir, Path.GetFileName(src));
                    if (!File.Exists(dest)) continue;
                    bool ours = SameFile(src, dest);
                    string bak = dest + ".lyj_bak";
                    if (!ours && !File.Exists(bak)) continue;      // 用户自己的同名模板，不动
                    try
                    {
                        if (File.Exists(bak)) { File.Copy(bak, dest, true); File.Delete(bak); log.AppendLine("  已还原原模板：" + dest); }
                        else { File.Delete(dest); log.AppendLine("  已移除模板：" + dest); }
                    }
                    catch (Exception ex) { log.AppendLine("  [提示] 模板清理失败：" + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  [提示] 模板清理出错：" + ex.Message);
            }
        }

        // ==================== 组合流程 ====================

        /// <summary>执行安装：释放文件 → 授权 → 登记到各 NX → 写卸载信息。</summary>
        public static bool Install(string installDir, List<NxInstall> targets, bool grantAcl, StringBuilder log)
        {
            try
            {
                installDir = ToLongPath(installDir.TrimEnd('\\'));
                log.AppendLine("安装目录：" + installDir);
                ExtractPayload(installDir, log);
                installDir = ToLongPath(installDir);      // 目录已建好，短路径可还原为长路径
                if (grantAcl) GrantUsersModify(installDir, log);

                log.AppendLine("登记到 NX（共 " + targets.Count + " 个）：");
                if (targets.Count == 0)
                    log.AppendLine("  [注意] 没有选中任何 NX，稍后可重新运行安装程序再登记");
                foreach (NxInstall nx in targets)
                    RegisterCustomDir(nx, installDir, log);

                log.AppendLine("安装加工模板（供加工助手插入工序）：");
                foreach (NxInstall nx in targets)
                    InstallTemplates(installDir, nx, log);

                string un = CopySelfAsUninstaller(installDir, log);
                WriteUninstallKey(installDir, un, log);
                log.AppendLine("安装完成。请重启 NX：功能区会多出“LYJ”选项卡，菜单栏会多出“LYJ”菜单。");
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine("[错误] " + ex.Message);
                return false;
            }
        }

        /// <summary>执行卸载：从各 NX 注销 → 删卸载信息 → 删目录（自身延时删除）。</summary>
        public static bool Uninstall(string installDir, StringBuilder log, bool deleteFiles, List<NxInstall> extraNx = null)
        {
            try
            {
                installDir = ToLongPath(installDir.TrimEnd('\\'));
                log.AppendLine("卸载目录：" + installDir);
                List<NxInstall> all = DetectNx();
                if (extraNx != null)
                {
                    foreach (NxInstall e in extraNx)
                    {
                        bool dup = false;
                        foreach (NxInstall x in all)
                            if (string.Equals(x.Root, e.Root, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (!dup) all.Add(e);
                    }
                }
                log.AppendLine("从 NX 注销（共 " + all.Count + " 个）：");
                foreach (NxInstall nx in all)
                    UnregisterCustomDir(nx, installDir, log);

                log.AppendLine("移除安装时复制到 NX 的加工模板：");
                foreach (NxInstall nx in all)
                    RemoveTemplates(installDir, nx, log);

                RemoveUninstallKey();
                log.AppendLine("  已移除控制面板卸载信息");

                if (deleteFiles && Directory.Exists(installDir))
                {
                    // 自身通常就在安装目录里（卸载程序是安装包里的一份副本），
                    // 所以先删能删的，剩下的目录交给 cmd 在进程退出后删除。
                    DeleteDirectoryContent(installDir);

                    string self = ToLongPath(Assembly.GetExecutingAssembly().Location);
                    bool inside = self.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);
                    if (inside)
                    {
                        try
                        {
                            string args = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + installDir + "\"";
                            var psi = new ProcessStartInfo("cmd.exe", args);
                            psi.UseShellExecute = false;
                            psi.CreateNoWindow = true;
                            psi.WindowStyle = ProcessWindowStyle.Hidden;
                            Process.Start(psi);
                            log.AppendLine("  目录由后台任务稍后删除：" + installDir);
                        }
                        catch (Exception ex) { log.AppendLine("  [提示] 清理目录失败，可手动删除：" + ex.Message); }
                    }
                    else
                    {
                        try { Directory.Delete(installDir, false); log.AppendLine("  已删除：" + installDir); }
                        catch { log.AppendLine("  已清空：" + installDir); }
                    }
                }
                log.AppendLine("卸载完成。");
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine("[错误] " + ex.Message);
                return false;
            }
        }

        /// <summary>删除目录内容（跳过本程序自身）。</summary>
        public static void DeleteDirectoryContent(string dir)
        {
            string self = ToLongPath(Assembly.GetExecutingAssembly().Location);
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch { }
            }
            foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                try { Directory.Delete(d, false); } catch { }
            }
            try { Directory.Delete(dir, false); } catch { }
        }
    }
}
