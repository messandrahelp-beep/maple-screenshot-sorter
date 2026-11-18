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
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace MapleShotSorter
{
    class Settings
    {
        public string source;
        public string autoDir;
        public string manualDir;
        public bool byMonth;
        public bool? capture;      // 키로 메이플 창 찍기 (없으면 켬)
        public string captureKey;  // Pause / Scroll Lock / Print Screen
        public bool? captureSound; // 찍을 때 소리 (없으면 켬)
        public string captureName; // 찍은 파일 이름 앞부분 (없으면 messandra)
    }

    // 윈도우에 단축키를 등록하고, 눌리면 알려 주는 보이지 않는 창
    class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, int msg, uint action, IntPtr info);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_NOREPEAT = 0x4000;
        // 이미 켜져 있을 때 exe를 또 누르면 이 신호로 켜진 쪽의 설정 창을 띄워요
        public static readonly int ShowMsg = RegisterWindowMessage("MapleShotSorter.Show");
        bool registered;
        public event EventHandler Pressed;
        public event EventHandler ShowRequested;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
            // 관리자 권한으로 켜져 있어도 일반 권한 쪽 신호를 받을 수 있게
            try { ChangeWindowMessageFilterEx(Handle, ShowMsg, 1, IntPtr.Zero); } catch { }
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        public static void AskRunningToShow()
        {
            try { AllowSetForegroundWindow(-1); } catch { } // 켜진 쪽 창이 맨 앞으로 올 수 있게
            PostMessage((IntPtr)0xFFFF, ShowMsg, IntPtr.Zero, IntPtr.Zero);
        }

        public bool Register(uint vk)
        {
            Unregister();
            registered = RegisterHotKey(Handle, 1, MOD_NOREPEAT, vk);
            return registered;
        }

        public void Unregister()
        {
            if (registered) UnregisterHotKey(Handle, 1);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Pressed != null) Pressed(this, EventArgs.Empty);
            if (m.Msg == ShowMsg && ShowRequested != null) ShowRequested(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose() { Unregister(); DestroyHandle(); }
    }

    // 메이플 창의 게임 화면 부분만 찍기 (윈도우 캡처 도구의 창 찍기와 같은 방식: 화면에 보이는 그대로 복사)
    static class MapleCapture
    {
        public static string WindowClass = "MapleStoryClass";
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        static IntPtr FindMapleWindow()
        {
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(256);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                sb.Length = 0;
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == WindowClass) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        // 성공하면 그림, 아니면 null 과 이유
        public static Bitmap Take(out string error)
        {
            error = null;
            // 메이플은 관리자 권한으로 돌고 보안 프로그램이 창 이름 찾기(FindWindow)를 막아 둬요.
            // 그래서 화면의 창 목록을 하나씩 훑어 창 종류 이름이 같은 것을 찾아요.
            IntPtr h = FindMapleWindow();
            if (h == IntPtr.Zero) { error = "메이플 창을 찾지 못했어요."; return null; }
            if (IsIconic(h)) { error = "메이플 창이 내려가 있어요."; return null; }
            if (GetForegroundWindow() != h) { error = "메이플 창이 맨 앞에 있을 때 찍을 수 있어요."; return null; }

            RECT rc;
            if (!GetClientRect(h, out rc) || rc.R - rc.L < 16 || rc.B - rc.T < 16) { error = "메이플 창 크기를 읽지 못했어요."; return null; }
            var pt = new POINT();
            ClientToScreen(h, ref pt);

            var bmp = new Bitmap(rc.R - rc.L, rc.B - rc.T, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(pt.X, pt.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);

            if (IsAllBlack(bmp))
            {
                bmp.Dispose();
                error = "전체화면 모드에서는 찍을 수 없어요. 창 모드나 테두리 없는 전체화면으로 바꾸거나 게임 안 스크린샷을 써 주세요.";
                return null;
            }
            return bmp;
        }

        // 진짜 전체화면은 바깥에서 찍으면 까맣게 나와요
        static bool IsAllBlack(Bitmap b)
        {
            for (int yi = 1; yi < 12; yi++)
                for (int xi = 1; xi < 12; xi++)
                {
                    var c = b.GetPixel(b.Width * xi / 12, b.Height * yi / 12);
                    if (c.R > 8 || c.G > 8 || c.B > 8) return false;
                }
            return true;
        }

        // 짧은 찰칵 소리 (파일 없이 만들어서 재생)
        static byte[] shutter;
        public static void PlayShutter()
        {
            try
            {
                if (shutter == null) shutter = MakeShutter();
                using (var ms = new MemoryStream(shutter))
                using (var sp = new System.Media.SoundPlayer(ms)) sp.Play();
            }
            catch { }
        }

        static byte[] MakeShutter()
        {
            const int rate = 22050;
            int n = rate * 70 / 1000;
            var rnd = new Random(7);
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double env = Math.Exp(-t * 70);
                double s = (rnd.NextDouble() * 2 - 1) * 0.5 + Math.Sin(2 * Math.PI * 1800 * t) * 0.5;
                w.Write((short)(s * env * 9000));
            }
            return ms.ToArray();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        const string MutexName = @"Local\MapleShotSorter";
        public static Mutex Lock;

        // 컴퓨터 켤 때 자동으로 켜진 것 (창 없이 아이콘으로만)
        public static bool Background;

        static void AlreadyRunning()
        {
            // 이미 켜져 있으면 그쪽 설정 창을 띄워요 (자동 켜기로 두 번 켜진 경우는 조용히 끝)
            if (!Background) HotkeyWindow.AskRunningToShow();
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool relaunched = Array.IndexOf(args, "--elevated") >= 0;
            Background = Array.IndexOf(args, "--background") >= 0;
            bool created;
            try { Lock = new Mutex(true, MutexName, out created); }
            catch (UnauthorizedAccessException) { AlreadyRunning(); return; } // 관리자 권한으로 켜진 것이 이미 있음
            if (!created && relaunched)
            {
                // 관리자 권한으로 다시 켤 때는 앞의 것이 꺼질 때까지 잠깐 기다려요
                try { created = Lock.WaitOne(5000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created) { AlreadyRunning(); return; }

            // 키로 찍기를 켜 뒀으면 관리자 권한으로 다시 켜요 (메이플이 관리자 권한이라 그래야 키가 들어와요)
            if (!SorterApp.IsAdmin && SorterApp.WantsElevation())
            {
                if (SorterApp.RelaunchElevated()) return;
                SorterApp.ElevationDeclined = true;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var app = new SorterApp();
            Application.Run();
            app.Dispose();
            try { Lock.ReleaseMutex(); } catch { }
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

        // ---------- 관리자 권한 ----------
        public static readonly bool IsAdmin =
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        public static bool ElevationDeclined;
        const string TaskName = "MapleShotSorter";

        public static bool WantsElevation()
        {
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.json");
                if (!File.Exists(p)) return false;
                var j = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(p, Encoding.UTF8));
                return j.capture != false;
            }
            catch { return false; }
        }

        // 자물쇠를 풀고 관리자 권한으로 다시 켜요. 허용 창에서 "아니요"를 누르면 false.
        public static bool RelaunchElevated()
        {
            try { Program.Lock.ReleaseMutex(); } catch { }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, Program.Background ? "--elevated --background" : "--elevated") { UseShellExecute = true, Verb = "runas" };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch
            {
                try { Program.Lock.WaitOne(0); } catch (AbandonedMutexException) { }
                return false;
            }
        }

        static int Schtasks(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
            using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(10000); return p.ExitCode; }
        }

        static bool TaskExists() { try { return Schtasks("/Query /TN " + TaskName) == 0; } catch { return false; } }

        // 관리자 권한 프로그램은 시작 프로그램 목록으로는 안 켜져서, 작업 스케줄러에 "로그인할 때 가장 높은 권한으로" 올려요
        void CreateTask()
        {
            string user = System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            string exe = System.Security.SecurityElement.Escape(exePath);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>메이플 스샷 정리</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--background</Arguments></Exec></Actions></Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "MapleShotSorterTask.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = Schtasks("/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            if (code != 0) throw new Exception("작업 스케줄러 등록 실패 (" + code + ")");
        }

        static readonly string[] KeyNames = { "Pause", "Scroll Lock", "Print Screen" };
        static readonly uint[] KeyCodes = { 0x13, 0x91, 0x2C };
        HotkeyWindow hotkey;
        int shots;

        Form form;
        TextBox tbSource, tbAuto, tbManual;
        CheckBox cbMonth, cbStart, cbCapture, cbSound;
        ComboBox cmbKey;
        TextBox tbName;
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

            hotkey = new HotkeyWindow();
            hotkey.Pressed += delegate { CaptureNow(); };
            hotkey.ShowRequested += delegate { ShowForm(); };

            UpdateStatus();
            if (firstRun)
            {
                status.Text = "폴더를 확인하고 저장을 눌러 주세요.";
                ShowForm();
            }
            else
            {
                StartWatch();
                ApplyHotkey();
                // 직접 켜면 설정 창을 띄우고, 컴퓨터 켤 때 자동으로 켜졌으면 아이콘으로만
                if (!Program.Background) ShowForm();
            }
        }

        // ---------- 메이플 창 찍기 ----------
        string hotkeyError;

        void ApplyHotkey()
        {
            hotkeyError = null;
            hotkey.Unregister();
            if (cfg.capture == false) { UpdateStatus(); return; }
            int i = Math.Max(0, Array.IndexOf(KeyNames, cfg.captureKey));
            if (!hotkey.Register(KeyCodes[i]))
                hotkeyError = KeyNames[i] + " 키를 다른 프로그램이 쓰고 있어요. 다른 키를 골라 주세요.";
            Log(KeyNames[i] + " 키 등록 " + (hotkeyError == null ? "됨" : "안 됨"));
            UpdateStatus();
        }

        const string DefaultShotName = "messandra";

        // 파일 이름에 못 쓰는 글자는 빼요. 비면 기본값.
        static string CleanName(string s)
        {
            s = (s ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            s = s.Trim(' ', '.');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s == "" ? DefaultShotName : s;
        }

        static string ShotName(string prefix, DateTime t)
        {
            return CleanName(prefix) + "_" + t.ToString("yyMMdd_HHmmss") + ".png";
        }

        void CaptureNow()
        {
            string err;
            using (var bmp = MapleCapture.Take(out err))
            {
                if (bmp == null)
                {
                    // 게임 중에는 윈도우가 알림을 숨겨서 소리로도 알려요
                    Log("찍지 못함: " + err);
                    if (cfg.captureSound != false) System.Media.SystemSounds.Exclamation.Play();
                    tray.ShowBalloonTip(3000, Title, err, ToolTipIcon.Warning);
                    return;
                }
                try
                {
                    var now = DateTime.Now;
                    string name = ShotName(cfg.captureName, now);
                    string dest = cfg.manualDir;
                    if (cfg.byMonth) dest = Path.Combine(dest, now.ToString("yyyy-MM"));
                    Directory.CreateDirectory(dest);
                    string target = FreeName(dest, name);
                    bmp.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                    if (cfg.captureSound != false) MapleCapture.PlayShutter();
                    shots++;
                    Log("찍음 → " + target);
                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    Log("찍기 실패 (" + ex.Message + ")");
                    tray.ShowBalloonTip(3000, Title, "스샷을 저장하지 못했어요.", ToolTipIcon.Warning);
                }
            }
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
                cfg.capture = j.capture;
                cfg.captureKey = j.captureKey;
                cfg.captureSound = j.captureSound;
                cfg.captureName = j.captureName;
            }
            catch { }
        }

        void SaveSettings()
        {
            File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(cfg), new UTF8Encoding(false));
        }

        static bool RunKeyOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetRunKey(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --background");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        bool StartupOn() { return RunKeyOn() || TaskExists(); }

        // 관리자 권한으로 켜져 있으면 작업 스케줄러, 아니면 시작 프로그램 목록
        void SetStartup(bool on)
        {
            if (on && IsAdmin) { CreateTask(); SetRunKey(false); }
            else if (on) SetRunKey(true);
            else
            {
                SetRunKey(false);
                if (TaskExists() && Schtasks("/Delete /TN " + TaskName + " /F") != 0) throw new Exception("작업 삭제 실패");
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
            // 관리자 권한으로 켜졌는데 자동 켜기가 시작 프로그램 목록에 있으면 작업 스케줄러로 옮겨요 (목록으로는 안 켜져서)
            try
            {
                if (IsAdmin && RunKeyOn()) { CreateTask(); SetRunKey(false); Log("자동 켜기를 작업 스케줄러로 옮김"); }
                // 1.1.0 전에 등록한 자동 켜기에는 "창 없이 켜기" 표시가 없어서 다시 등록해요
                else if (RunKeyOn()) SetRunKey(true);
                else if (IsAdmin && !Program.Background && TaskExists()) CreateTask();
            }
            catch (Exception ex) { Log("자동 켜기 옮기기 실패 (" + ex.Message + ")"); }
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
            cbCapture = new CheckBox { Text = "키로 메이플 창 찍기 (켜면 관리자 권한으로 실행돼요)", AutoSize = true, Location = new Point(S(16), S(y + 58)), Checked = cfg.capture != false };
            cmbKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(40), S(y + 84)), Width = S(150) };
            cmbKey.Items.AddRange(KeyNames);
            cmbKey.SelectedIndex = Math.Max(0, Array.IndexOf(KeyNames, cfg.captureKey));
            cbSound = new CheckBox { Text = "찍을 때 소리", AutoSize = true, Location = new Point(S(206), S(y + 86)), Checked = cfg.captureSound != false };
            var lbName = new Label { Text = "파일 이름", AutoSize = true, Location = new Point(S(40), S(y + 120)) };
            tbName = new TextBox { Text = CleanName(cfg.captureName), Location = new Point(S(120), S(y + 117)), Width = S(150), MaxLength = 40 };
            var lbPreview = new Label { AutoSize = true, Location = new Point(S(278), S(y + 120)), ForeColor = Color.FromArgb(98, 114, 106) };
            EventHandler preview = delegate { lbPreview.Text = "→ " + ShotName(tbName.Text, new DateTime(2026, 11, 18, 13, 33, 41)); };
            tbName.TextChanged += preview;
            preview(null, EventArgs.Empty);
            cbCapture.CheckedChanged += delegate { cmbKey.Enabled = cbSound.Enabled = tbName.Enabled = cbCapture.Checked; };
            cmbKey.Enabled = cbSound.Enabled = tbName.Enabled = cbCapture.Checked;
            status = new Label { Location = new Point(S(16), S(y + 156)), Size = new Size(S(528), S(40)), ForeColor = Color.FromArgb(60, 110, 100) };
            form.Controls.AddRange(new Control[] { cbMonth, cbStart, cbCapture, cmbKey, cbSound, lbName, tbName, lbPreview, status });

            var btLog = new Button { Text = "기록 보기", Location = new Point(S(16), S(412)), Size = new Size(S(110), S(32)) };
            var btSave = new Button { Text = "저장", Location = new Point(S(344), S(412)), Size = new Size(S(96), S(32)) };
            var btHide = new Button { Text = "창 닫기", Location = new Point(S(448), S(412)), Size = new Size(S(96), S(32)) };
            form.Controls.AddRange(new Control[] { btLog, btSave, btHide });
            form.AcceptButton = btSave;
            form.ClientSize = new Size(S(560), S(458));

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
            var tb = new TextBox { Text = value, Location = new Point(S(16), S(y + 24)), Width = S(352) };
            var bt = new Button { Text = "찾아보기", Location = new Point(S(376), S(y + 22)), Size = new Size(S(90), S(30)) };
            var btOpen = new Button { Text = "열기", Location = new Point(S(472), S(y + 22)), Size = new Size(S(72), S(30)) };
            bt.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = "폴더를 골라 주세요." })
                {
                    if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                    if (dlg.ShowDialog(form) == DialogResult.OK) tb.Text = dlg.SelectedPath;
                }
            };
            btOpen.Click += delegate
            {
                string p = tb.Text.Trim();
                if (p == "") { Warn("폴더를 먼저 정해 주세요."); return; }
                OpenFolder(p);
            };
            form.Controls.AddRange(new Control[] { lb, tb, bt, btOpen });
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
            if (hotkeyError != null) { status.ForeColor = Color.FromArgb(194, 65, 47); status.Text = hotkeyError; }
            else if (ElevationDeclined && cfg.capture != false && !firstRun)
            {
                status.ForeColor = Color.FromArgb(194, 65, 47);
                status.Text = "관리자 권한을 허용하지 않아서 메이플 안에서는 키가 안 들어와요. 저장을 누르면 다시 물어봐요.";
            }
            else
            {
                status.ForeColor = Color.FromArgb(60, 110, 100);
                status.Text = "켜져 있어요. 이번에 옮긴 스샷 " + moved + "장" + (shots > 0 ? ", 찍은 스샷 " + shots + "장" : "");
            }
            if (tray != null) tray.Text = Title + " · " + moved + "장 옮김";
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
            cfg.capture = cbCapture.Checked;
            cfg.captureKey = (string)cmbKey.SelectedItem;
            cfg.captureSound = cbSound.Checked;
            cfg.captureName = CleanName(tbName.Text);
            tbName.Text = cfg.captureName;
            try { SaveSettings(); } catch { Warn("설정을 저장하지 못했어요."); return; }
            try { SetStartup(cbStart.Checked); } catch { Warn("자동으로 켜기 설정을 못 했어요."); }
            firstRun = false;
            int n = SortNow();
            StartWatch();

            // 키로 찍기는 관리자 권한이 있어야 메이플 안에서 키가 들어와요 → 다시 켜기
            if (cfg.capture == true && !IsAdmin)
            {
                Log("관리자 권한으로 다시 켜기");
                if (RelaunchElevated()) { Quit(); return; }
                ElevationDeclined = true;
            }
            ApplyHotkey();
            if (hotkeyError == null && !(ElevationDeclined && cfg.capture == true)) status.Text = "저장했어요. 지금 " + n + "장 옮겼어요.";
        }

        void Quit()
        {
            quitting = true;
            kick.Stop();
            fallback.Stop();
            watcher.EnableRaisingEvents = false;
            hotkey.Unregister();
            tray.Visible = false;
            form.Close();
            Application.Exit();
        }

        public void Dispose()
        {
            watcher.Dispose();
            hotkey.Dispose();
            tray.Dispose();
            form.Dispose();
        }
    }
}
