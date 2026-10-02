using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBar.Services
{
    /// <summary>
    /// PawnIO 内核驱动通道:读取 AMD Zen 原生 CPU 温度(Tctl,SMN THM_TCON_CUR_TMP)。
    /// PawnIO(namazso,GPL-2.0)安装一次后普通权限即可使用;设备 \\?\GLOBALROOT\Device\PawnIO
    /// 经两次 IOCTL 完成加载与执行。温度解码与 Linux k10temp / LibreHardwareMonitor 一致:
    /// (raw &gt;&gt; 21) × 0.125 °C,TJ_SEL/RANGE_SEL 置位时再减 49。
    /// 模块二进制 AMDFamily17.bin 取自 LibreHardwareMonitor 资源(模块源码 LGPL-2.1,由 PawnIO 官方签名)。
    /// 仅采样线程调用 ReadCpuTempC;设置页通过 IsAvailable / InvalidateProbe 交互。
    /// </summary>
    internal static class PawnIoTctl
    {
        private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
        // CTL_CODE(41394 = 0xA1B2, 0x821 / 0x841, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint IOCTL_LOAD_BINARY = 0xA1B22084;
        private const uint IOCTL_EXECUTE_FN = 0xA1B22104;
        private const int FnNameLength = 32;
        private const uint SmnThmTconCurTmp = 0x00059800;
        private const uint TempRangeSelMask = 0x80000;
        private const uint TempTjSelMask = 0x30000;

        private static readonly object _gate = new object();
        private static readonly byte[] ModuleBinary = LoadModuleBinary();
        private static IntPtr _device = new IntPtr(-1);
        private static bool _loadFailed;
        private static long _probeNotBefore;
        private static bool _logged;
        private static double _lastC = -1;

        /// <summary>原生 Tctl 是否已可用(首次成功读取后为真)。</summary>
        public static bool IsAvailable { get; private set; }

        private static byte[] LoadModuleBinary()
        {
            try
            {
                using var stream = typeof(PawnIoTctl).Assembly
                    .GetManifestResourceStream("DesktopBar.Resources.PawnIo.AMDFamily17.bin");
                if (stream == null) return null;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch { return null; }
        }

        /// <summary>外部(设置页)通知状态可能已变化,立即重新探测。</summary>
        public static void InvalidateProbe()
        {
            lock (_gate)
            {
                _probeNotBefore = 0;
            }
        }

        /// <summary>读取 Tctl(°C);不可用或读数异常返回 -1。1 秒级调用,内部自带重试节流。</summary>
        public static double ReadCpuTempC()
        {
            lock (_gate)
            {
                if (!EnsureOpenLocked()) return _lastC;
                try
                {
                    // 输入缓冲:32 字节 ASCII 函数名 + ULONG64 参数(SMN 地址)。
                    var input = new byte[FnNameLength + 8];
                    Encoding_ASCII_GetBytes("ioctl_read_smn", input);
                    ulong addr = SmnThmTconCurTmp;
                    Array.Copy(BitConverter.GetBytes(addr), 0, input, FnNameLength, 8);
                    var output = new byte[8];
                    if (!DeviceIoControl(_device, IOCTL_EXECUTE_FN, input, (uint)input.Length,
                            output, (uint)output.Length, out uint read, IntPtr.Zero) || read < 8)
                        return _lastC;
                    uint raw = (uint)BitConverter.ToUInt64(output, 0);
                    double t = ((raw >> 21) & 0x7FF) * 0.125;
                    bool adjust = (raw & TempRangeSelMask) != 0 || (raw & TempTjSelMask) == TempTjSelMask;
                    if (adjust) t -= 49.0;
                    if (t < -5 || t > 125) return _lastC; // 异常读数(非 AMD 平台等)不采信
                    _lastC = t;
                    if (!IsAvailable)
                    {
                        IsAvailable = true;
                        if (!_logged)
                        {
                            _logged = true;
                            App.Log("PawnIO 已连接,原生 CPU 温度(Tctl)可用");
                        }
                    }
                    return t;
                }
                catch
                {
                    return _lastC;
                }
            }
        }

        private static bool EnsureOpenLocked()
        {
            if (_device != new IntPtr(-1)) return true;
            long now = Environment.TickCount64;
            if (_loadFailed && now < _probeNotBefore) return false;
            if (ModuleBinary == null)
            {
                _loadFailed = true;
                _probeNotBefore = long.MaxValue; // 资源缺失不会自愈,不再重试
                return false;
            }
            // 未装 PawnIO 时每 30 秒重试一次打开,覆盖"刚装完驱动"的场景。
            IntPtr h = CreateFileW(DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1) || h == IntPtr.Zero)
            {
                _loadFailed = true;
                _probeNotBefore = now + 30000;
                return false;
            }
            if (!DeviceIoControl(h, IOCTL_LOAD_BINARY, ModuleBinary, (uint)ModuleBinary.Length,
                    null, 0, out _, IntPtr.Zero))
            {
                CloseHandle(h);
                _loadFailed = true;
                _probeNotBefore = now + 30000;
                return false;
            }
            _device = h;
            _loadFailed = false;
            return true;
        }

        private static void Encoding_ASCII_GetBytes(string s, byte[] buffer)
        {
            for (int i = 0; i < s.Length && i < FnNameLength - 1; i++)
                buffer[i] = (byte)s[i];
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr device, uint code,
            byte[] inBuffer, uint inSize, byte[] outBuffer, uint outSize,
            out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);
    }
}
