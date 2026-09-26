using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    /// <summary>一条可用更新:版本号、发布说明和匹配当前架构/运行时类型的安装包。</summary>
    public sealed class UpdateInfo
    {
        public string Version { get; init; }
        public string Notes { get; init; }
        public string DownloadUrl { get; init; }
        public string FileName { get; init; }
    }

    /// <summary>
    /// 通过 GitHub Releases 实现的更新检查与下载。
    /// 检查走 api.github.com,下载走 release 资源;HttpClient 默认读取系统代理
    /// (WinINET/WinHTTP,含 PAC 自动发现),用户开了系统代理就会自动走代理,
    /// 这里只为需要认证的系统代理补上当前用户凭据。
    /// </summary>
    public static class UpdateService
    {
        private const string Repo = "Akiyuki1130/YukiDesk";
        private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

        /// <summary>当前程序版本(取程序集版本,只保留前三段)。必须先于 Http 初始化。</summary>
        public static Version CurrentVersion { get; } = Normalize(
            typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

        /// <summary>是否为自带 .NET 运行时的发布(full);优先读取打包标记，兼容旧包再检查 hostfxr.dll。</summary>
        public static bool IsSelfContained => DetectSelfContained();
        private const string RuntimeMarker = "yukidesk.runtime";

        private static bool DetectSelfContained()
        {
            try
            {
                string marker = Path.Combine(AppContext.BaseDirectory, RuntimeMarker);
                if (File.Exists(marker))
                {
                    string value = File.ReadAllText(marker).Trim();
                    if (string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));
        }

        /// <summary>当前设备架构标签(更新资源按架构命名)。</summary>
        public static string ArchTag =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        private static readonly HttpClient Http = CreateHttp();
        private static DispatcherTimer _autoTimer;
        private static int _busy;
        private static int _autoCheckRunning;

        /// <summary>是否正在进行检查/下载。</summary>
        public static bool Busy => Volatile.Read(ref _busy) != 0;

        private static Version Normalize(Version v)
            => new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build < 0 ? 0 : v.Build, 0));

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler();
            try
            {
                // 默认 UseProxy=true 且 Proxy=系统代理;这里只补凭据,不改变代理来源。
                if (handler.Proxy != null) handler.Proxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
            }
            catch { }
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YukiDesk/" + CurrentVersion.ToString(3));
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        // ---------- 检查 ----------

        /// <summary>查询最新 Release;无更新返回 null,网络/解析失败抛异常。</summary>
        public static async Task<UpdateInfo> CheckLatestAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await Http.GetAsync(ApiUrl, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean())
                return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= CurrentVersion) return null;

            string version = latest.ToString(3);
            string flavor = IsSelfContained ? "full" : "slim";
            var asset = FindAsset(root, ArchTag, flavor, version);
            if (asset == null && !IsSelfContained)
            {
                // slim 版没有专用安装包时退回同版本 full 版（自带运行时）。
                asset = FindAsset(root, ArchTag, "full", version);
            }
            if (asset == null)
                throw new InvalidOperationException("发行版中没有适用于 " + ArchTag + " / " + flavor + " / v" + version + " 的安装包");

            return new UpdateInfo
            {
                Version = version,
                Notes = root.GetProperty("body").GetString() ?? "",
                DownloadUrl = asset.Value.Url,
                FileName = asset.Value.Name
            };
        }

        private static (string Name, string Url)? FindAsset(JsonElement root,
            string arch, string flavor, string version)
        {
            string prefix = "YukiDesk-Setup-" + arch + "-" + flavor + "-";
            string expectedShort = prefix + version + ".exe";
            string expectedAssembly = prefix + version + ".0.exe";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                bool versionMatch = string.Equals(name, expectedShort, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, expectedAssembly, StringComparison.OrdinalIgnoreCase);
                if (versionMatch
                    && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    return (name, url);
            }
            return null;
        }

        // ---------- 下载 ----------

        /// <summary>下载更新安装包到临时目录,返回文件完整路径。</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "YukiDeskUpdate");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, info.FileName);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            long readTotal = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, cts.Token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                readTotal += read;
                if (progress != null && total > 0) progress.Report(readTotal * 100.0 / total.Value);
            }
            return target;
        }

        // ---------- 安装 ----------

        /// <summary>启动已下载的安装程序并退出 YukiDesk(每用户安装,无需管理员)。</summary>
        public static bool InstallAndRestart(string setupPath)
        {
            if (string.IsNullOrEmpty(setupPath) || !File.Exists(setupPath)) return false;
            App.Log("更新: 启动安装程序 " + setupPath);
            if (!Interop.NativeMethods.ShellExecuteOpen(setupPath))
            {
                App.Log("更新: 安装程序启动失败");
                return false;
            }
            // 给安装进程一点启动时间再退出;安装器对运行中的程序有自己的关闭提示兜底。
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Application.Current?.Shutdown();
            };
            t.Start();
            return true;
        }

        // ---------- 高层入口 ----------

        /// <summary>
        /// 一次完整的"检查 → 询问 → 下载 → 询问安装"流程,状态文本通过 status 回调上屏。
        /// autoDownload=true 时跳过第一步询问(用于"自动下载更新"开关)。
        /// </summary>
        public static async Task RunCheckAsync(Action<string> status, bool autoDownload, UpdateInfo preChecked = null)
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;
            try
            {
                UpdateInfo info = preChecked;
                if (info == null)
                {
                    status?.Invoke("正在检查更新…");
                    try { info = await CheckLatestAsync(CancellationToken.None); }
                    catch (Exception ex) { status?.Invoke("检查更新失败: " + ex.Message); return; }
                }

                if (info == null) { status?.Invoke("当前已是最新版本 (v" + CurrentVersion.ToString(3) + ")"); return; }
                status?.Invoke("发现新版本 v" + info.Version);

                if (!autoDownload)
                {
                    if (!ConfirmDialog.Show("YukiDesk 更新",
                        "发现新版本 v" + info.Version + "(当前 v" + CurrentVersion.ToString(3) + ")。\n\n是否立即下载并安装?", "下载并安装"))
                    {
                        status?.Invoke("已取消。");
                        return;
                    }
                }

                string path;
                try
                {
                    var progress = new Progress<double>(p => status?.Invoke("正在下载 v" + info.Version + "… " + Math.Round(p) + "%"));
                    path = await DownloadAsync(info, progress, CancellationToken.None);
                }
                catch (Exception ex) { status?.Invoke("下载失败: " + ex.Message); return; }

                status?.Invoke("下载完成,可以安装。");
                if (!ConfirmDialog.Show("YukiDesk 更新",
                    "新版本 v" + info.Version + " 已下载完成。\n\n立即安装吗?安装时会先退出 YukiDesk。", "立即安装")) return;
                if (!InstallAndRestart(path))
                    ConfirmDialog.Notice("更新失败", "安装程序未能启动，YukiDesk 将继续运行。");
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>启动自动检查:开机 25 秒后首次检查,之后每 24 小时一次。</summary>
        public static void StartAutoCheck()
        {
            if (_autoTimer != null) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _autoTimer = timer;
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                try
                {
                    if (App.Settings.AutoCheckUpdate
                        && Interlocked.Exchange(ref _autoCheckRunning, 1) == 0)
                    {
                        try
                        {
                            var info = await CheckLatestAsync(CancellationToken.None);
                            if (info != null)
                            {
                                App.Log("更新: 发现新版本 v" + info.Version);
                                if (App.Settings.AutoUpdate)
                                    await RunCheckAsync(null, autoDownload: true, info);
                                else
                                    App.ShowTrayBalloon("YukiDesk 有新版本",
                                        "v" + info.Version + " 已发布,可在 设置 → 关于 中手动更新。");
                            }
                        }
                        finally { Volatile.Write(ref _autoCheckRunning, 0); }
                    }
                }
                catch (Exception ex) { App.Log("更新: 自动检查失败 " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(_autoTimer, timer)
                        && Application.Current != null
                        && !Application.Current.Dispatcher.HasShutdownStarted)
                    {
                        timer.Interval = TimeSpan.FromHours(24);
                        timer.Start();
                    }
                }
            };
            timer.Start();
        }

        public static void StopAutoCheck()
        {
            var timer = _autoTimer;
            _autoTimer = null;
            timer?.Stop();
        }
    }
}
