using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopBar.Interop
{
    internal static class NativeMethods
    {
        // ---------- 桌面层 Z 序控制 ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
        private static extern IntPtr FindWindowW(string cls, string win);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cw, int ch);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

        /// <summary>给无边框窗口设置圆角(不依赖分层透明)。</summary>
        public static void SetRoundRegion(IntPtr hwnd, int w, int h, int radius)
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius);
            if (rgn != IntPtr.Zero && !SetWindowRgn(hwnd, rgn, true))
                DeleteObject(rgn);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public static IntPtr FindDesktopHost() => FindWindowW("Progman", null);

        /// <summary>加工具窗口 + 不激活扩展样式:点击顶栏不会抢焦点、不会跳到前台。</summary>
        public static void ApplyNoActivateToolStyle(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public static void EnsureShown(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int n);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

        private const uint GW_HWNDPREV = 3;
        private const int WS_EX_TOOLWINDOW_ = 0x80;
        private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

        /// <summary>该窗口是否属于"桌面装饰层"(壁纸引擎/桌面整理软件画的桌面内容)。</summary>
        private static bool IsDesktopDecoration(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, 128);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "SysListView32" || cls == "SHELLDLL_DefView"
                || cls.Contains("WinUIDesktopWin32WindowClass")) return true;
            int ex = GetWindowLong(h, -20);
            // 无重定向位图 + 工具窗 = DirectComposition 合成的透明桌面层(桌面挂件/壁纸)
            return (ex & WS_EX_NOREDIRECTIONBITMAP) != 0 && (ex & WS_EX_TOOLWINDOW_) != 0;
        }

        /// <summary>
        /// 把顶栏钉在"桌面层":越过壁纸/桌面整理软件画的装饰窗口,紧贴其上、
        /// 所有普通软件窗口之下。SWP_SHOWWINDOW 是关键:开机静默启动(不抢焦点)时
        /// 分层窗口的呈现表面可能一直不被合成,带此标志才能强制 DWM 开始显示。
        /// </summary>
        public static void PinAboveDesktop(IntPtr hwnd)
        {
            try
            {
                // 防御:上次浮出留下的置顶标志必须先撤掉,否则顶栏永远浮在所有软件之上,
                // 下面的"钉到桌面层"只是反复把装饰窗垫到它下面,看不到效果。
                if (IsTopmostFlagged(hwnd)) UnfloatTopmost(hwnd);
                const uint FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
                IntPtr progman = FindDesktopHost();
                if (progman == IntPtr.Zero)
                {
                    App.Log("钉底跳过:未找到桌面宿主窗口");
                    return;
                }
                IntPtr above = progman;
                if (progman != IntPtr.Zero)
                {
                    // 从桌面向上走,跳过所有"桌面装饰层"窗口,把顶栏插到它们的正上方。
                    // 注意:IsWindowVisible 对被 DWM cloak(隐藏)的窗口仍返回 true,
                    // 必须用 DWMWA_CLOAKED 跳过,否则会提前停下、被壁纸/挂件层盖住。
                    IntPtr h = GetWindow(progman, GW_HWNDPREV);
                    int guard = 0;
                    while (h != IntPtr.Zero && h != hwnd && guard++ < 64)
                    {
                        int cloak;
                        DwmGetWindowAttribute(h, 14, out cloak, 4);
                        if (cloak != 0) { h = GetWindow(h, GW_HWNDPREV); continue; }
                        if (IsWindowVisible(h) && IsDesktopDecoration(h)) above = h;
                        else if (IsWindowVisible(h)) break;
                        h = GetWindow(h, GW_HWNDPREV);
                    }
                }
                // SetWindowPos 语义:第一个参数(被移动窗口)会被插到第二个参数的正下方。
                // 因此要把"最上层的装饰窗口"移到顶栏正下方,顶栏才能贴着桌面层、盖过壁纸/图标/挂件。
                bool moved = SetWindowPos(hwnd, above, 0, 0, 0, 0, FLAGS);
                if (!moved)
                    App.Log("钉底失败: SetWindowPos 返回 false");
            }
            catch (Exception ex) { App.Log("钉底失败: " + ex.Message); }
        }

        /// <summary>窗口是否带 WS_EX_TOPMOST 标志。</summary>
        private static bool IsTopmostFlagged(IntPtr hwnd)
            => hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & 0x8) != 0;

        /// <summary>撤掉置顶标志落回普通层(钉底前的防御,防外部软件把顶栏置顶后赖在顶层)。</summary>
        private static void UnfloatTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // ---------- 媒体键 ----------
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        private const uint KEYEVENTF_KEYUP = 0x2;

        public const byte VK_MEDIA_PREV = 0xB1;
        public const byte VK_MEDIA_NEXT = 0xB0;
        public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        public static void SendMediaKey(byte vk)
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // ---------- 回收站 ----------
        private const uint SHERB_NOCONFIRMATION = 0x1;
        private const uint SHERB_NOPROGRESSUI = 0x2;
        private const uint SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        /// <summary>清空所有盘的回收站；确认与提示音由应用自行处理，成功返回 0。</summary>
        public static int EmptyRecycleBin()
            => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint flags);

        /// <summary>在资源管理器中打开一个 Shell 命名空间路径（如回收站 CLSID）；成功返回 0。</summary>
        public static int OpenShellNamespace(string parsingName)
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return unchecked((int)0x8000FFFF);
            try
            {
                return SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x4;
        private const ushort FOF_NOCONFIRMATION = 0x10;
        private const ushort FOF_ALLOWUNDO = 0x40;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint func;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        /// <summary>把文件/文件夹移入系统回收站（带撤销信息）；confirm 由应用自行弹出。</summary>
        public static bool DeleteToRecycleBin(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            var op = new SHFILEOPSTRUCT
            {
                func = FO_DELETE,
                // pFrom 必须以双 \0 结尾：每项由封送器补一个终结符，这里再显式补一个。
                pFrom = string.Join("\0", paths) + "\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT)
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

        /// <summary>为文件创建硬链接（同卷瞬时零拷贝）；失败返回 false（跨卷/不支持时）。</summary>
        public static bool CreateHardLink(string linkPath, string targetPath)
            => CreateHardLinkW(linkPath, targetPath, IntPtr.Zero);


        // ---------- Shell 图标 ----------
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_ICONLOCATION = 0x200;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO sfi, uint cb, uint flags);

        /// <summary>按指定尺寸提取图标(nIconSize 高16位=大图标)。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int iconIndex, uint flags,
            out IntPtr hiconLarge, out IntPtr hiconSmall, uint nIconSize);

        // ---------- Explorer 图标提取 ----------
        // 先从 Shell 的图像工厂读取桌面实际使用的高分辨率图标；失败时再走兼容回退。
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        private const uint SIIGBF_BIGGERSIZEOK = 0x1;
        private const uint SIIGBF_ICONONLY = 0x4;
        private const uint SIIGBF_SCALEUP = 0x100;
        private static readonly Guid ShellItemImageFactoryIid =
            new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            int GetImage(SIZE size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            IntPtr bindCtx,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public uint xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

        /// <summary>HICON → BitmapSource(绕开 .NET8 下 CreateBitmapSourceFromHIcon 的句柄校验 bug)。</summary>
        private static BitmapSource BitmapToSource(IntPtr hBitmap)
        {
            if (hBitmap == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DeleteObject(hBitmap); }
        }

        private static BitmapSource TryShellDesktopIcon(string path, int pixelSize)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path)) return null;
            IShellItemImageFactory factory = null;
            try
            {
                var iid = ShellItemImageFactoryIid;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                    return null;
                var size = new SIZE { cx = Math.Max(48, Math.Min(256, pixelSize)), cy = Math.Max(48, Math.Min(256, pixelSize)) };
                int hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY | SIIGBF_SCALEUP, out IntPtr bitmap);
                return hr == 0 ? BitmapToSource(bitmap) : null;
            }
            catch { return null; }
            finally { if (factory != null) Marshal.ReleaseComObject(factory); }
        }

        private static BitmapSource IconToSource(IntPtr hIcon)
        {
            if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;
            try
            {
                if (ii.hbmColor == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHBitmap(ii.hbmColor, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }

        /// <summary>验证 HICON 在本进程是否真的可用(此环境下 shell 工厂句柄可能无效)。</summary>
        private static bool IconUsable(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return false;
            bool ok = GetIconInfo(hIcon, out ICONINFO ii);
            if (ok)
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            return ok;
        }

        private static BitmapSource TryExtract(string iconFile, int index, int pixelSize)
        {
            try
            {
                pixelSize = Math.Max(48, Math.Min(256, pixelSize));
                uint iconSize = unchecked((uint)(pixelSize << 16));
                int hr = SHDefExtractIcon(iconFile, index, 0, out IntPtr big, out IntPtr small, iconSize);
                if ((hr != 0 && hr != 1) || big == IntPtr.Zero || !IconUsable(big))
                {
                    if (big != IntPtr.Zero) DestroyIcon(big);
                    if (small != IntPtr.Zero) DestroyIcon(small);
                    return null;
                }
                var src = IconToSource(big);
                DestroyIcon(big);
                if (small != IntPtr.Zero) DestroyIcon(small);
                return src;
            }
            catch { return null; }
        }

        /// <summary>提取 48px 系统(资源管理器)图标。本机 shell 的 IShellItemImageFactory /
        /// 部分图标 API 返回无效句柄,故只用 SHGetFileInfo 关联 + SHDefExtractIcon 直提。</summary>
        public static BitmapSource GetFileIcon(string path, bool isDir, int pixelSize = 96)
        {
            try
            {
                // 1) 优先读取 Explorer 桌面实际使用的高分辨率图标。
                var desktopIcon = TryShellDesktopIcon(path, pixelSize);
                if (desktopIcon != null) return desktopIcon;

                // 2) 兼容路径：按真实文件请求 Shell 图标，覆盖 .lnk/.exe/.msi 和普通文件。
                var sfi = new SHFILEINFO();
                uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                IntPtr r = SHGetFileInfo(path, attr, ref sfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICONLOCATION);
                if (r != IntPtr.Zero && !string.IsNullOrEmpty(sfi.szDisplayName))
                {
                    var src = TryExtract(sfi.szDisplayName, sfi.iIcon, pixelSize);
                    if (src != null) return src;
                }

                // 3) 直接取 Shell 返回的实际 HICON。
                sfi = new SHFILEINFO();
                r = SHGetFileInfo(path, attr, ref sfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
                if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        if (IconUsable(sfi.hIcon))
                        {
                            var src = IconToSource(sfi.hIcon);
                            if (src != null) return src;
                        }
                    }
                    finally { DestroyIcon(sfi.hIcon); }
                }

                // 3) 类型默认图标:文件夹 / 通用文件
                return GetSystemTypeIcon(isDir, pixelSize);
            }
            catch { return null; }
        }

        /// <summary>取 imageres.dll 的系统类型图标（文件夹/通用文件），始终是 Explorer 同款 Windows 图标。</summary>
        public static BitmapSource GetSystemTypeIcon(bool isDir, int pixelSize)
        {
            string sys = Path.Combine(Environment.SystemDirectory, "imageres.dll");
            var icon = TryExtract(sys, isDir ? 3 : 2, pixelSize);
            if (icon != null) return icon;
            string shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            return TryExtract(shell32, isDir ? 3 : 1, pixelSize);
        }

        // ---------- 桌面工作区预留 ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);
        [DllImport("user32.dll")]
        private static extern uint GetLastError();
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern uint SHAppBarMessage(uint message, ref APPBARDATA data);

        private const uint SPI_GETWORKAREA = 0x0030;
        private const uint SPI_SETWORKAREA = 0x002F;
        private const uint SPIF_SENDCHANGE = 0x02;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABM_QUERYPOS = 0x00000002;
        private const uint ABM_SETPOS = 0x00000003;
        private const uint ABE_TOP = 1;
        private static RECT _originalWorkArea;
        private static bool _workAreaCaptured;
        private static IntPtr _appBarWindow;
        private static bool _appBarRegistered;

        public static bool SetDesktopTopReserve(IntPtr hwnd, int height)
        {
            if (hwnd == IntPtr.Zero) return false;
            height = Math.Max(1, height);
            try
            {
                if (_appBarRegistered && _appBarWindow != hwnd)
                    RemoveDesktopAppBar();

                var data = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = hwnd,
                    uEdge = ABE_TOP,
                    rc = new RECT
                    {
                        Left = 0,
                        Top = 0,
                        Right = (int)Math.Ceiling(SystemParameters.PrimaryScreenWidth),
                        Bottom = height
                    }
                };

                if (!_appBarRegistered)
                {
                    uint added = SHAppBarMessage(ABM_NEW, ref data);
                    if (added == 0)
                    {
                        App.Log("注册桌面 AppBar 失败 error=" + Marshal.GetLastWin32Error());
                        return SetDesktopTopReserveFallback(height);
                    }
                    _appBarRegistered = true;
                    _appBarWindow = hwnd;
                }

                uint queried = SHAppBarMessage(ABM_QUERYPOS, ref data);
                if (queried == 0)
                {
                    App.Log("查询桌面 AppBar 位置失败 error=" + Marshal.GetLastWin32Error());
                    RemoveDesktopAppBar();
                    return SetDesktopTopReserveFallback(height);
                }
                data.rc.Bottom = data.rc.Top + height;
                uint positioned = SHAppBarMessage(ABM_SETPOS, ref data);
                if (positioned == 0)
                {
                    App.Log("设置桌面 AppBar 位置失败 error=" + Marshal.GetLastWin32Error());
                    RemoveDesktopAppBar();
                    return SetDesktopTopReserveFallback(height);
                }

                int width = Math.Max(1, data.rc.Right - data.rc.Left);
                int actualHeight = Math.Max(1, data.rc.Bottom - data.rc.Top);
                bool positionedWindow = SetWindowPos(hwnd, IntPtr.Zero, data.rc.Left, data.rc.Top, width, actualHeight,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                if (!positionedWindow)
                {
                    App.Log("定位桌面 AppBar 窗口失败 error=" + Marshal.GetLastWin32Error());
                    return false;
                }
                App.Log("桌面 AppBar 已注册 hwnd=" + hwnd + " rect=" +
                    data.rc.Left + "," + data.rc.Top + "," + data.rc.Right + "," + data.rc.Bottom);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("设置桌面 AppBar 失败: " + ex.Message);
                RemoveDesktopAppBar();
                return SetDesktopTopReserveFallback(height);
            }
        }

        private static bool SetDesktopTopReserveFallback(int height)
        {
            try
            {
                var r = new RECT();
                if (!_workAreaCaptured && SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0))
                {
                    _originalWorkArea = r;
                    _workAreaCaptured = true;
                }
                if (!_workAreaCaptured) return false;
                r = _originalWorkArea;
                r.Top = Math.Min(r.Bottom - 100, r.Top + Math.Max(0, height));
                bool ok = SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
                App.Log("桌面空间兼容回退 result=" + ok + " error=" + Marshal.GetLastWin32Error());
                return ok;
            }
            catch (Exception ex) { App.Log("设置桌面顶部空间失败: " + ex.Message); return false; }
        }

        public static void RemoveDesktopAppBar()
        {
            try
            {
                if (_appBarRegistered && _appBarWindow != IntPtr.Zero)
                {
                    var data = new APPBARDATA
                    {
                        cbSize = Marshal.SizeOf<APPBARDATA>(),
                        hWnd = _appBarWindow
                    };
                    uint removed = SHAppBarMessage(ABM_REMOVE, ref data);
                    App.Log("桌面 AppBar 已移除 result=" + removed);
                }
            }
            catch (Exception ex) { App.Log("移除桌面 AppBar 失败: " + ex.Message); }
            finally { _appBarRegistered = false; _appBarWindow = IntPtr.Zero; }
        }

        public static void RestoreDesktopWorkArea()
        {
            RemoveDesktopAppBar();
            try
            {
                if (_workAreaCaptured)
                {
                    var r = _originalWorkArea;
                    bool ok = SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
                    App.Log("恢复桌面工作区 result=" + ok + " error=" + Marshal.GetLastWin32Error());
                    _workAreaCaptured = false;
                }
            }
            catch (Exception ex) { App.Log("恢复桌面工作区失败: " + ex.Message); }
        }

        // ---------- 系统操作 ----------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ExitWindowsEx(uint flags, uint reason);
        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
        private const uint EWX_LOGOFF = 0x00000000;
        private const uint EWX_SHUTDOWN = 0x00000001;
        private const uint EWX_REBOOT = 0x00000002;
        private const uint EWX_FORCEIFHUNG = 0x00000010;
        public static void Shutdown() => ExitWindowsEx(EWX_SHUTDOWN | EWX_FORCEIFHUNG, 0);
        public static void Restart() => ExitWindowsEx(EWX_REBOOT | EWX_FORCEIFHUNG, 0);
        public static void Sleep() => SetSuspendState(false, false, false);
        public static void Lock() => LockWorkStation();
        public static void DisplayOff()
        {
            SendMessage((IntPtr)0xffff, 0x0112, (IntPtr)0xF170, (IntPtr)2);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
