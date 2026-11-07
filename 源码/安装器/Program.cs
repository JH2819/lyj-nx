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
using System.Windows.Forms;

namespace NxToolboxSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var opt = Options.Parse(args);

            // 静默/命令行模式（供批量部署和自动化测试用）
            if (opt.Silent)
                return RunSilent(opt);

            Application.Run(new MainForm());
            return 0;
        }

        private static int RunSilent(Options opt)
        {
            var log = new StringBuilder();
            int code = 0;
            try
            {
                if (opt.ListOnly)
                {
                    List<NxInstall> all = SetupCore.DetectNx();
                    log.AppendLine("检测到 NX：" + all.Count + " 个");
                    foreach (NxInstall nx in all) log.AppendLine("  " + nx.VersionText + "  " + nx.Root);
                }
                else if (opt.Uninstall)
                {
                    var extra = new List<NxInstall>();
                    foreach (string r in opt.NxRoots) extra.Add(new NxInstall { Root = r, Major = 0 });
                    bool ok = SetupCore.Uninstall(opt.Dir, log, opt.DeleteFiles, extra);
                    code = ok ? 0 : 1;
                }
                else
                {
                    List<NxInstall> targets;
                    if (opt.NxRoots.Count > 0)
                    {
                        targets = new List<NxInstall>();
                        foreach (string r in opt.NxRoots)
                        {
                            int major = 0;
                            try
                            {
                                string nxopen = Path.Combine(r, @"NXBIN\managed\NXOpen.dll");
                                if (File.Exists(nxopen))
                                    major = System.Reflection.AssemblyName.GetAssemblyName(nxopen).Version.Major;
                            }
                            catch { }
                            targets.Add(new NxInstall { Root = r, Major = major });
                        }
                    }
                    else targets = SetupCore.DetectNx();

                    bool ok = SetupCore.Install(opt.Dir, targets, !opt.NoAcl, log);
                    code = ok ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("[异常] " + ex);
                code = 1;
            }

            string text = log.ToString();
            if (!string.IsNullOrEmpty(opt.LogFile))
            {
                try { File.WriteAllText(opt.LogFile, text, Encoding.UTF8); } catch { }
            }
            if (opt.QuietLog)
            {
                try
                {
                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        byte[] b = Encoding.UTF8.GetBytes(text);
                        stdout.Write(b, 0, b.Length);
                        stdout.Flush();
                    }
                }
                catch { }
            }
            // 卸载后需要删目录时，等父进程退出再删
            if (opt.Uninstall && opt.DeleteFiles && opt.AfterDelay && Directory.Exists(opt.Dir))
            {
                System.Threading.Thread.Sleep(600);
                SetupCore.DeleteDirectoryContent(opt.Dir);
            }
            return code;
        }

        /// <summary>命令行参数。</summary>
        private sealed class Options
        {
            public bool Silent;
            public bool Uninstall;
            public bool DeleteFiles;
            public bool AfterDelay;
            public bool NoAcl;
            public bool ListOnly;
            public bool QuietLog = true;
            public string Dir = SetupCore.DefaultInstallDir;
            public string LogFile;
            public List<string> NxRoots = new List<string>();

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    string up = a.ToUpperInvariant();
                    if (up == "/S" || up == "-S") { o.Silent = true; }
                    else if (up == "/U" || up == "-U") { o.Uninstall = true; }
                    else if (up == "/DELFILES") { o.DeleteFiles = true; }
                    else if (up == "/AFTER") { o.AfterDelay = true; }
                    else if (up == "/NOACL") { o.NoAcl = true; }
                    else if (up == "/LIST") { o.Silent = true; o.ListOnly = true; }
                    else if (up.StartsWith("/DIR=")) { o.Dir = a.Substring(5).Trim('"'); }
                    else if (up.StartsWith("/LOG=")) { o.LogFile = a.Substring(5).Trim('"'); }
                    else if (up.StartsWith("/NX="))
                    {
                        foreach (string p in a.Substring(4).Split(';'))
                            if (p.Trim().Length > 0) o.NxRoots.Add(p.Trim().Trim('"', '\\'));
                    }
                    // 兼容：/U 后面的裸目录参数
                    else if (o.Uninstall && Directory.Exists(a)) { o.Dir = a.TrimEnd('\\'); }
                }
                return o;
            }
        }
    }
}
