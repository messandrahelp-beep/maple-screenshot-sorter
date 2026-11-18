// 메이플 스샷 정리
// 게임이 저장한 스크린샷을 자동 촬영(Maple_A_...)과 직접 찍은 것(Maple_...)으로 나눠
// 정해 둔 폴더로 옮겨요. 알림 영역(시계 옆) 아이콘으로 계속 돌아가요.
// 만들기: 소스\만들기.ps1 (윈도우에 들어 있는 C# 컴파일러 사용, C# 5 문법)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("메이플 스샷 정리")]
[assembly: System.Reflection.AssemblyProduct("메이플 스샷 정리")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace MapleShotSorter
{
    class Settings
    {
        public string source;
        public string autoDir;
        public string manualDir;
        public bool byMonth;
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\MapleShotSorter", out created))
            {
                if (!created)
                {
                    MessageBox.Show("메이플 스샷 정리가 이미 켜져 있어요.\n시계 옆 아이콘을 눌러 주세요.", "메이플 스샷 정리");
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var app = new SorterApp();
                Application.Run();
                app.Dispose();
            }
        }
    }

    class SorterApp : IDisposable
    {
        const string Title = "메이플 스샷 정리";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "MapleShotSorter";

        static readonly Regex AutoName = new Regex(@"^Maple_A_\d", RegexOptions.IgnoreCase);
        static readonly Regex ManualName = new Regex(@"^Maple_\d", RegexOptions.IgnoreCase);
        static readonly Regex DateName = new Regex(@"^Maple_(?:A_)?(\d{2})(\d{2})\d{2}_", RegexOptions.IgnoreCase);
        static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp" };

        readonly string here;
        readonly string settingsPath;
        readonly string logPath;
        readonly string exePath;
        Settings cfg;
        bool firstRun;
        int moved;
        bool quitting;
        bool hintShown;

        Form form;
        TextBox tbSource, tbAuto, tbManual;
        CheckBox cbMonth, cbStart;
        Label status;
        NotifyIcon tray;
        Icon icon;
        FileSystemWatcher watcher;
        System.Windows.Forms.Timer kick;     // 알림 받은 뒤 1초마다, 다 옮기면 멈춤
        System.Windows.Forms.Timer fallback; // 알림을 놓쳤을 때를 위한 1분마다 확인

        public SorterApp()
        {
            exePath = Application.ExecutablePath;
            here = Path.GetDirectoryName(exePath);
            settingsPath = Path.Combine(here, "settings.json");
            logPath = Path.Combine(here, "옮긴 기록.txt");

            LoadSettings();
            icon = MakeIcon();
            BuildForm();
            BuildTray();
            MigrateOldStartup();

            kick = new System.Windows.Forms.Timer { Interval = 1000 };
            kick.Tick += delegate { SortNow(); if (!HasPending()) kick.Stop(); };
            fallback = new System.Windows.Forms.Timer { Interval = 60000 };
            fallback.Tick += delegate { SortNow(); };

            var h = form.Handle; // 알림을 이 창에서 받으려면 필요
            watcher = new FileSystemWatcher { Filter = "Maple_*", SynchronizingObject = form };
            watcher.Created += delegate { kick.Start(); };
            watcher.Renamed += delegate { kick.Start(); };

            UpdateStatus();
            if (firstRun)
            {
                status.Text = "폴더를 확인하고 저장을 눌러 주세요.";
                ShowForm();
            }
            else StartWatch();
        }

        // ---------- 설정 ----------
        void LoadSettings()
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            cfg = new Settings
            {
                source = @"C:\Nexon\Maple",
                autoDir = Path.Combine(pics, @"메이플 스샷\자동 촬영"),
                manualDir = Path.Combine(pics, @"메이플 스샷\직접 촬영"),
                byMonth = false
            };
            firstRun = !File.Exists(settingsPath);
            if (firstRun) return;
            try
            {
                var j = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(settingsPath, Encoding.UTF8));
                if (!string.IsNullOrWhiteSpace(j.source)) cfg.source = j.source;
                if (!string.IsNullOrWhiteSpace(j.autoDir)) cfg.autoDir = j.autoDir;
                if (!string.IsNullOrWhiteSpace(j.manualDir)) cfg.manualDir = j.manualDir;
                cfg.byMonth = j.byMonth;
            }
            catch { }
        }

        void SaveSettings()
        {
            File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(cfg), new UTF8Encoding(false));
        }

        bool StartupOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + exePath + "\"");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        // 예전 파워셸 판의 시작 프로그램 바로가기가 있으면 이 프로그램으로 바꿔요
        void MigrateOldStartup()
        {
            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "메이플 스샷 정리.lnk");
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                    SetStartup(true);
                    cbStart.Checked = true;
                }
            }
            catch { }
        }

        // ---------- 옮기기 ----------
        void Log(string text)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n", Encoding.UTF8); }
            catch { }
        }

        static bool IsShot(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return Array.IndexOf(Exts, ext) >= 0 && (AutoName.IsMatch(name) || ManualName.IsMatch(name));
        }

        IEnumerable<string> ShotFiles()
        {
            if (!Directory.Exists(cfg.source)) yield break;
            string[] files;
            try { files = Directory.GetFiles(cfg.source, "Maple_*"); }
            catch { yield break; }
            foreach (var f in files) if (IsShot(Path.GetFileName(f))) yield return f;
        }

        bool HasPending()
        {
            foreach (var f in ShotFiles()) return true;
            return false;
        }

        static bool IsReady(string path)
        {
            try
            {
                if ((DateTime.Now - File.GetLastWriteTime(path)).TotalSeconds < 1) return false;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch { return false; }
        }

        static string FreeName(string dir, string name)
        {
            string target = Path.Combine(dir, name);
            if (!File.Exists(target)) return target;
            string b = Path.GetFileNameWithoutExtension(name), e = Path.GetExtension(name);
            for (int n = 2; ; n++)
            {
                target = Path.Combine(dir, b + " (" + n + ")" + e);
                if (!File.Exists(target)) return target;
            }
        }

        int SortNow()
        {
            int count = 0;
            foreach (var f in new List<string>(ShotFiles()))
            {
                string name = Path.GetFileName(f);
                string dest = AutoName.IsMatch(name) ? cfg.autoDir : cfg.manualDir;
                if (string.IsNullOrWhiteSpace(dest)) continue;
                if (cfg.byMonth)
                {
                    var m = DateName.Match(name);
                    if (m.Success) dest = Path.Combine(dest, "20" + m.Groups[1].Value + "-" + m.Groups[2].Value);
                }
                if (!IsReady(f)) continue;
                try
                {
                    Directory.CreateDirectory(dest);
                    string target = FreeName(dest, name);
                    File.Move(f, target);
                    count++;
                    Log(name + " → " + target);
                }
                catch (Exception ex) { Log("옮기기 실패: " + name + " (" + ex.Message + ")"); }
            }
            if (count > 0) { moved += count; UpdateStatus(); }
            return count;
        }

        void StartWatch()
        {
            watcher.EnableRaisingEvents = false;
            if (Directory.Exists(cfg.source))
            {
                watcher.Path = cfg.source;
                watcher.EnableRaisingEvents = true;
            }
            fallback.Start();
            kick.Start();
        }

        // ---------- 화면 ----------
        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b1 = new SolidBrush(Color.FromArgb(74, 140, 128))) g.FillEllipse(b1, 1, 1, 30, 30);
                    g.FillEllipse(Brushes.White, 9, 9, 14, 14);
                    using (var b2 = new SolidBrush(Color.FromArgb(40, 80, 72))) g.FillEllipse(b2, 12, 12, 8, 8);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void BuildForm()
        {
            form = new Form
            {
                Text = Title,
                Icon = icon,
                Font = new Font("맑은 고딕", 10f),
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                ShowInTaskbar = true
            };
            float k;
            using (var g = form.CreateGraphics()) k = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * k);

            int y = 18;
            tbSource = AddRow("게임 스샷 저장 위치", cfg.source, ref y, S);
            tbAuto = AddRow("자동 촬영 옮길 폴더", cfg.autoDir, ref y, S);
            tbManual = AddRow("직접 촬영 옮길 폴더", cfg.manualDir, ref y, S);

            cbMonth = new CheckBox { Text = "달마다 폴더 나누기 (예: 2026-11)", AutoSize = true, Location = new Point(S(16), S(y + 2)), Checked = cfg.byMonth };
            cbStart = new CheckBox { Text = "컴퓨터 켤 때 자동으로 켜기", AutoSize = true, Location = new Point(S(16), S(y + 30)), Checked = StartupOn() };
            status = new Label { Location = new Point(S(16), S(y + 66)), Size = new Size(S(528), S(22)), ForeColor = Color.FromArgb(60, 110, 100) };
            form.Controls.AddRange(new Control[] { cbMonth, cbStart, status });

            var btLog = new Button { Text = "옮긴 기록 보기", Location = new Point(S(16), S(306)), Size = new Size(S(130), S(32)) };
            var btSave = new Button { Text = "저장", Location = new Point(S(344), S(306)), Size = new Size(S(96), S(32)) };
            var btHide = new Button { Text = "창 닫기", Location = new Point(S(448), S(306)), Size = new Size(S(96), S(32)) };
            form.Controls.AddRange(new Control[] { btLog, btSave, btHide });
            form.AcceptButton = btSave;
            form.ClientSize = new Size(S(560), S(352));

            btSave.Click += delegate { Save(); };
            btHide.Click += delegate { form.Hide(); };
            btLog.Click += delegate
            {
                if (!File.Exists(logPath)) File.WriteAllText(logPath, "", Encoding.UTF8);
                System.Diagnostics.Process.Start("notepad.exe", "\"" + logPath + "\"");
            };
            form.FormClosing += (s, e) =>
            {
                if (quitting || e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                form.Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    tray.ShowBalloonTip(3000, Title, "계속 켜져 있어요. 끄려면 아이콘 오른쪽 클릭 → 끝내기.", ToolTipIcon.Info);
                }
            };
        }

        TextBox AddRow(string label, string value, ref int y, Func<int, int> S)
        {
            var lb = new Label { Text = label, AutoSize = true, Location = new Point(S(16), S(y)) };
            var tb = new TextBox { Text = value, Location = new Point(S(16), S(y + 24)), Width = S(430) };
            var bt = new Button { Text = "찾아보기", Location = new Point(S(454), S(y + 22)), Size = new Size(S(90), S(30)) };
            bt.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = "폴더를 골라 주세요." })
                {
                    if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                    if (dlg.ShowDialog(form) == DialogResult.OK) tb.Text = dlg.SelectedPath;
                }
            };
            form.Controls.AddRange(new Control[] { lb, tb, bt });
            y += 64;
            return tb;
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("설정 열기", null, delegate { ShowForm(); });
            menu.Items.Add("지금 정리하기", null, delegate
            {
                int n = SortNow();
                tray.ShowBalloonTip(2000, Title, n + "장 옮겼어요.", ToolTipIcon.Info);
            });
            menu.Items.Add("자동 촬영 폴더 열기", null, delegate { OpenFolder(cfg.autoDir); });
            menu.Items.Add("직접 촬영 폴더 열기", null, delegate { OpenFolder(cfg.manualDir); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("끝내기", null, delegate { Quit(); });
            tray = new NotifyIcon { Icon = icon, Text = Title, Visible = true, ContextMenuStrip = menu };
            tray.MouseDoubleClick += delegate { ShowForm(); };
        }

        void UpdateStatus()
        {
            status.Text = "켜져 있어요. 이번에 옮긴 스샷: " + moved + "장";
            tray.Text = Title + " · " + moved + "장 옮김";
        }

        void ShowForm()
        {
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        static void OpenFolder(string p)
        {
            try
            {
                Directory.CreateDirectory(p);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\"");
            }
            catch { }
        }

        void Warn(string msg) { MessageBox.Show(form, msg, Title); }

        void Save()
        {
            string src = tbSource.Text.Trim(), a = tbAuto.Text.Trim(), m = tbManual.Text.Trim();
            if (!Directory.Exists(src)) { Warn("게임 스샷 저장 위치를 찾을 수 없어요."); return; }
            if (a == "" || m == "") { Warn("옮길 폴더를 둘 다 정해 주세요."); return; }
            string s0 = src.TrimEnd('\\');
            if (string.Equals(a.TrimEnd('\\'), s0, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.TrimEnd('\\'), s0, StringComparison.OrdinalIgnoreCase))
            { Warn("옮길 폴더는 게임 스샷 저장 위치와 달라야 해요."); return; }

            cfg.source = src; cfg.autoDir = a; cfg.manualDir = m; cfg.byMonth = cbMonth.Checked;
            try { SaveSettings(); } catch { Warn("설정을 저장하지 못했어요."); return; }
            try { SetStartup(cbStart.Checked); } catch { Warn("자동으로 켜기 설정을 못 했어요."); }
            firstRun = false;
            int n = SortNow();
            StartWatch();
            status.Text = "저장했어요. 지금 " + n + "장 옮겼어요.";
        }

        void Quit()
        {
            quitting = true;
            kick.Stop();
            fallback.Stop();
            watcher.EnableRaisingEvents = false;
            tray.Visible = false;
            form.Close();
            Application.Exit();
        }

        public void Dispose()
        {
            watcher.Dispose();
            tray.Dispose();
            form.Dispose();
        }
    }
}
