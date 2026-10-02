using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Models;
using DesktopBar.Services;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopBar
{
    public partial class App : System.Windows.Application
    {
        private static Mutex _mutex;
        public static AppSettings Settings;
        public static MainWindow Bar;
        private static SettingsWindow _settingsWin;
        private static readonly object LogGate = new object();
        // 主进程与温度从属进程并发写同一 log.txt,用命名互斥体把"轮转+追加"包成跨进程原子操作。
        private static Mutex _logMutex;
        private Forms.NotifyIcon _tray;
        private Icon _trayIcon;

        public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
        public static void Log(string msg)
        {
            try
            {
                if (_logMutex == null) Interlocked.CompareExchange(ref _logMutex, new Mutex(false, "Local\\YukiDesk_Log_Mutex"), null);
                bool held = false;
                try { held = _logMutex.WaitOne(800); } catch (AbandonedMutexException) { held = true; }
                try
                {
                    lock (LogGate)
                    {
                        Directory.CreateDirectory(AppDataDir);
                        string logPath = Path.Combine(AppDataDir, "log.txt");
                        try
                        {
                            if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                            {
                                string backup = logPath + ".1";
                                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                                try { File.Move(logPath, backup); } catch { }
                            }
                        }
                        catch { }
                        File.AppendAllText(logPath,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                    }
                }
                finally { if (held) try { _logMutex.ReleaseMutex(); } catch { } }
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 全局异常兜底必须在 --tempslave 分支之前注册:从属进程没有任何 UI,漏注册就完全失声。
            DispatcherUnhandledException += (s, args) =>
            {
                Log("未处理异常: " + args.Exception);
                // 启动完成前(主窗/托盘还没建)吞异常会留下"无窗口无托盘却占着单实例互斥体"的僵尸,
                // 此时必须让进程退出;启动完成后才吞掉以保住顶栏。
                if (Bar == null) { args.Handled = false; Shutdown(1); }
                else args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log("后台线程致命异常: " + args.ExceptionObject);
            // 温度从属模式:计划任务以最高权限启动,读原生 Tctl 写共享内存,不建互斥体/托盘/主窗。
            if (e.Args != null && Array.IndexOf(e.Args, "--tempslave") >= 0)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Services.TemperatureSlave.Run(() => Dispatcher.BeginInvoke(new Action(Shutdown)));
                return;
            }
            _mutex = new Mutex(true, "YukiDesk_SingleInstance_Mutex", out bool created);
            if (!created) { Shutdown(); return; }
            Settings = SettingsService.Load(out bool firstRun);
            // 配置是用户的明确偏好；不要用一次可能失败的注册表读取反向覆盖它。
            AutoStartService.Set(Settings.AutoStart);
            // 上次会话残留在 %TEMP%\YukiDeskClipboard 的临时副本:剪贴板仍引用的保留,其余清掉。
            try { FileClipboard.CleanupStaleTempCopies(); } catch { }
            try { Directory.CreateDirectory(Settings.StorageRoot); foreach (var n in Settings.Folders) Directory.CreateDirectory(Path.Combine(Settings.StorageRoot, n)); } catch { }
            // 配置文件损坏时不要用默认值立即覆写(原始内容已在 .corrupt-* 隔离副本里),等用户显式保存。
            if (Settings.Folders.Count == 0 && !SettingsService.CorruptConfigDetected)
            {
                Settings.Folders.Add("常用");
                try { Directory.CreateDirectory(Path.Combine(Settings.StorageRoot, "常用")); } catch { }
                Save();
            }
            if (firstRun && !SettingsService.CorruptConfigDetected) Save();
            Log("YukiDesk 启动配置已加载");
            ThemeService.Start();
            Log("YukiDesk 主题服务已启动");
            Bar = new MainWindow();
            Log("YukiDesk 主窗口已创建");
            Bar.Show();
            Log("YukiDesk 主窗口已显示");
            BuildTray();
            Log("YukiDesk 托盘已创建");
            // GitHub 更新检查(设置-关于里可开关):首次延迟触发,避免拖慢启动。
            UpdateService.StartAutoCheck();
            // 登录温度任务存在但从属不在运行(例如本会话手动重启过应用)时主动拉起一次;
            // 只在任务路径与当前 exe 一致时执行,查询较重,放后台线程。
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var (exists, pathOk) = SettingsWindow.QuerySlaveTask();
                    if (exists && pathOk && !TemperatureSlave.IsFresh)
                    {
                        Log("检测到登录温度任务存在但从属未运行,主动拉起");
                        SettingsWindow.RunSlaveTaskOnce();
                    }
                }
                catch { }
            });
        }

        private void BuildTray()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    _trayIcon = Icon.ExtractAssociatedIcon(exe);
            }
            catch { _trayIcon = null; }
            _tray = new Forms.NotifyIcon
            {
                Text = "YukiDesk",
                Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
                Visible = true
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开设置", null, (s, e) => Dispatcher.BeginInvoke(new Action(ShowSettings)));
            menu.Items.Add("显示/隐藏顶栏", null, (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Bar.Visibility == Visibility.Visible) Bar.HideWithPanels();
                else Bar.Show();
            })));
            menu.Items.Add("打开数据目录", null, (s, e) => Dispatcher.BeginInvoke(new Action(() => UI.ShellOpen.OpenPath(Settings.StorageRoot))));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 YukiDesk", null, (s, e) => Dispatcher.BeginInvoke(new Action(Shutdown)));
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(ShowSettings)); };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 只阻止新操作;已经在跑的后台复制/移动必须等它收尾,否则进程退出会留下半份文件。
            try { UI.OperationProgressDialog.BeginShutdown(); } catch { }
            try { UI.OperationProgressDialog.WaitForIdle(5000); } catch { }
            try { Services.UpdateService.StopAutoCheck(); } catch { }
            try { Services.ThemeService.Stop(); } catch { }
            try { DesktopSpaceService.Restore(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { _trayIcon?.Dispose(); } catch { }
            try { Bar?.ClosePanels(); } catch { }
            try { Bar?.Close(); } catch { }
            try { SpectrumService.Instance.Stop(); } catch { }
            try { _logMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }

        public static void Save() => SettingsService.Save(Settings);
        public static void ApplyAndSave() { Save(); Bar?.ApplySettings(); }

        /// <summary>托盘气泡通知(更新提醒等);托盘未创建或已退出时静默。</summary>
        public static void ShowTrayBalloon(string title, string text)
        {
            var app = Application.Current as App;
            try { app?._tray?.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info); } catch { }
        }

        /// <summary>配置写盘失败的用户可见反馈(磁盘满/ACL/杀软锁定),否则用户以为已保存、重启后回滚。</summary>
        public static void ShowConfigSaveFailure(Exception ex)
        {
            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        UI.ConfirmDialog.Notice("保存设置失败",
                            "设置未能写入磁盘：" + ex.Message
                            + "\n请检查磁盘空间或文件权限,否则部分修改可能在重启后丢失。");
                    }
                    catch { }
                }));
            }
            catch { }
        }
        public static void ShowSettings()
        {
            Bar?.ClosePanels();
            if (_settingsWin != null)
            {
                _settingsWin.WindowState = WindowState.Normal;
                // 呼出已有设置窗口时强制弹到所有窗口之上，避免被别的软件压住找不到。
                _settingsWin.Topmost = true;
                _settingsWin.Activate();
                _settingsWin.Topmost = false;
                return;
            }
            var w = new SettingsWindow(); _settingsWin = w; w.Closed += (s, e) => _settingsWin = null;
            // 首帧前整窗透明,避免"直接闪出来"。
            w.Opacity = 0;
            // Topmost 短暂置顶把窗口带到最前，随后恢复普通层级（点别处可正常被遮挡）。
            w.Topmost = true;
            w.Show();
            w.Activate();
            w.Topmost = false;
            PlayOpenAnimation(w);
        }

        /// <summary>设置窗口打开动画:整窗淡入 + 上浮归位 + 内容轻微缩放。</summary>
        private static void PlayOpenAnimation(SettingsWindow w)
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(240);

                // 整窗淡入
                var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
                fade.Completed += (s, e) => { w.Opacity = 1; w.BeginAnimation(Window.OpacityProperty, null); };
                w.BeginAnimation(Window.OpacityProperty, fade);

                // 上浮:从下方 18px 滑到目标位置
                double top = w.Top;
                var rise = new DoubleAnimation(top + 18, top, duration) { EasingFunction = ease };
                rise.Completed += (s, e) => { w.Top = top; w.BeginAnimation(Window.TopProperty, null); };
                w.BeginAnimation(Window.TopProperty, rise);

                // 内容轻微缩放(0.96→1)
                var transform = new ScaleTransform(0.96, 0.96);
                w.WindowRoot.RenderTransform = transform;
                w.WindowRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                var scaleX = new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease };
                var scaleY = new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease };
                scaleX.Completed += (s, e) => { w.WindowRoot.RenderTransform = null; };
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            }
            catch { }
        }
    }
}
