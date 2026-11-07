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
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NxToolboxSetup
{
    /// <summary>安装向导主窗口（单页：选 NX → 选目录 → 安装）。</summary>
    public class MainForm : Form
    {
        private CheckedListBox _nxList;
        private TextBox _dirBox;
        private CheckBox _aclBox;
        private TextBox _log;
        private Button _installBtn;
        private Button _uninstallBtn;
        private Button _closeBtn;
        private Button _refreshBtn;
        private Label _hint;
        private bool _busy;

        public MainForm()
        {
            Text = "LYJ 安装程序 v" + SetupCore.ProductVersion;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(660, 560);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var title = new Label
            {
                Text = "LYJ · 安装向导",
                Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 14)
            };
            Controls.Add(title);

            var sub = new Label
            {
                Text = "仅适配 NX 2606（其他版本未适配，不保证可用）。勾选需要启用外挂的 NX 版本，安装后重启 NX 生效。",
                AutoSize = false,
                Size = new Size(620, 34),
                Location = new Point(20, 46),
                ForeColor = Color.DimGray
            };
            Controls.Add(sub);

            var gbNx = new GroupBox
            {
                Text = "检测到的 NX（可多选）",
                Location = new Point(20, 84),
                Size = new Size(620, 150)
            };
            Controls.Add(gbNx);

            _nxList = new CheckedListBox
            {
                Location = new Point(12, 22),
                Size = new Size(488, 116),
                CheckOnClick = true,
                IntegralHeight = false
            };
            gbNx.Controls.Add(_nxList);

            _refreshBtn = new Button { Text = "重新检测", Location = new Point(512, 20), Size = new Size(96, 28) };
            _refreshBtn.Click += delegate { ReloadNx(); };
            gbNx.Controls.Add(_refreshBtn);

            var lblDir = new Label { Text = "安装目录：", AutoSize = true, Location = new Point(20, 248) };
            Controls.Add(lblDir);

            _dirBox = new TextBox { Location = new Point(100, 245), Size = new Size(430, 25), Text = SetupCore.DefaultInstallDir };
            Controls.Add(_dirBox);

            var browse = new Button { Text = "浏览…", Location = new Point(538, 244), Size = new Size(100, 26) };
            browse.Click += delegate
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择外挂安装目录（建议用纯英文路径）";
                    fbd.SelectedPath = _dirBox.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK) _dirBox.Text = fbd.SelectedPath;
                }
            };
            Controls.Add(browse);

            _aclBox = new CheckBox
            {
                Text = "给安装目录授予普通用户写权限（NX 运行时需要写配置和图标，推荐勾选）",
                AutoSize = true,
                Checked = true,
                Location = new Point(100, 276)
            };
            Controls.Add(_aclBox);

            _hint = new Label
            {
                Text = "提示：安装前请关闭所有 NX 窗口，否则新菜单要等下次启动才出现。",
                AutoSize = false,
                Size = new Size(620, 20),
                Location = new Point(20, 304),
                ForeColor = Color.FromArgb(180, 90, 0)
            };
            Controls.Add(_hint);

            _installBtn = new Button { Text = "开始安装", Location = new Point(360, 330), Size = new Size(130, 34) };
            _installBtn.Click += delegate { DoInstall(); };
            Controls.Add(_installBtn);

            _uninstallBtn = new Button { Text = "卸载", Location = new Point(500, 330), Size = new Size(140, 34) };
            _uninstallBtn.Click += delegate { DoUninstall(); };
            Controls.Add(_uninstallBtn);

            _closeBtn = new Button { Text = "退出", Location = new Point(20, 330), Size = new Size(100, 34) };
            _closeBtn.Click += delegate { Close(); };
            Controls.Add(_closeBtn);

            _log = new TextBox
            {
                Location = new Point(20, 376),
                Size = new Size(620, 168),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 9f)
            };
            Controls.Add(_log);

            ReloadNx();
            Log("准备就绪。选择目标 NX 后点“开始安装”。");
        }

        private void ReloadNx()
        {
            _nxList.Items.Clear();
            List<NxInstall> list;
            try
            {
                list = SetupCore.DetectNx();
            }
            catch (Exception ex)
            {
                // 注册表/进程枚举异常时不让未处理异常弹 .NET 错误框，落日志即可
                Log("检测 NX 失败：" + ex.Message + "\r\n可稍后点“刷新”重试，或直接手动填写安装目录。");
                _nxList.Items.Add("（检测失败：" + ex.Message + "）");
                return;
            }
            foreach (NxInstall nx in list)
            {
                int i = _nxList.Items.Add(nx);
                _nxList.SetItemChecked(i, true);
            }
            if (list.Count == 0)
            {
                _nxList.Items.Add("（未检测到 NX：可先安装 NX，或手动把外挂目录登记到 custom_dirs.dat）");
                Log("未检测到 NX。若 NX 装在非默认目录，请确认已运行过一次 NX（注册表里会留安装路径）。");
            }
            else
            {
                Log("检测到 " + list.Count + " 个 NX：");
                foreach (NxInstall nx in list) Log("  " + nx.VersionText + "  " + nx.Root);
            }
        }

        private void Log(string s)
        {
            if (_log.InvokeRequired) { _log.BeginInvoke(new Action<string>(Log), s); return; }
            _log.AppendText(s + "\r\n");
        }

        private List<NxInstall> CheckedNx()
        {
            var list = new List<NxInstall>();
            foreach (object o in _nxList.CheckedItems)
            {
                var nx = o as NxInstall;
                if (nx != null) list.Add(nx);
            }
            return list;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _installBtn.Enabled = !busy;
            _uninstallBtn.Enabled = !busy;
            _refreshBtn.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void DoInstall()
        {
            if (_busy) return;
            string dir = (_dirBox.Text ?? "").Trim().TrimEnd('\\');
            if (dir.Length == 0) { MessageBox.Show(this, "请填写安装目录。", "提示"); return; }

            List<NxInstall> targets = CheckedNx();
            if (targets.Count == 0)
            {
                if (MessageBox.Show(this, "没有勾选任何 NX，仍要只释放文件吗？\r\n（之后可重新运行安装程序登记到 NX）",
                        "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
            }

            if (IsNxRunning() &&
                MessageBox.Show(this, "检测到 NX 正在运行。\r\n继续安装不会损坏数据，但新菜单要重启 NX 后才出现。\r\n\r\n继续吗？",
                    "NX 正在运行", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            SetBusy(true);
            var log = new StringBuilder();
            bool ok = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { ok = SetupCore.Install(dir, targets, _aclBox.Checked, log); }
                catch (Exception ex) { log.AppendLine("[错误] " + ex.Message); }
                finally
                {
                    BeginInvoke(new Action(delegate
                    {
                        Log(log.ToString().TrimEnd());
                        SetBusy(false);
                        if (ok)
                            MessageBox.Show(this,
                                "安装完成！\r\n\r\n安装目录：" + dir + "\r\n\r\n请重启 NX 后使用：\r\n" +
                                "· 功能区「LYJ」选项卡（图档导入 / 图档导出 / 加工）\r\n" +
                                "· 菜单栏「LYJ」菜单（同上三组）",
                                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
            });
        }

        private void DoUninstall()
        {
            if (_busy) return;
            string dir = (_dirBox.Text ?? "").Trim().TrimEnd('\\');
            if (MessageBox.Show(this, "确定卸载 LYJ？\r\n\r\n会从所有检测到的 NX 里移除登记，并删除目录：\r\n" + dir,
                    "卸载确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            SetBusy(true);
            var log = new StringBuilder();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { SetupCore.Uninstall(dir, log, true); }
                catch (Exception ex) { log.AppendLine("[错误] " + ex.Message); }
                finally
                {
                    BeginInvoke(new Action(delegate
                    {
                        Log(log.ToString().TrimEnd());
                        SetBusy(false);
                        MessageBox.Show(this, "卸载完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
            });
        }

        private static bool IsNxRunning()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string n = p.ProcessName.ToLowerInvariant();
                        if (n == "ugraf" || n == "ugraf.exe" || n.StartsWith("ugraf")) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }
    }
}
