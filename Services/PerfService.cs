using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace DesktopBar.Services
{
    /// <summary>一块显卡的信息(DXGI 枚举)。属性绑定用于设置页下拉框。</summary>
    public sealed class GpuAdapterInfo
    {
        public string Name { get; set; }
        /// <summary>LUID "高32位:低32位",与 GPU 引擎计数器实例里的 luid 对应。</summary>
        public string Luid { get; set; }
        public double UtilPercent { get; set; } = -1;
        public override string ToString() => Name ?? "";
    }

    /// <summary>一次性能采样快照。数值 -1 表示暂时未知(首个采样周期/计数器不可用)。</summary>
    public sealed class PerfSample
    {
        public double CpuPercent;
        public ulong MemUsedBytes;
        public ulong MemTotalBytes;
        public double NetUpBps;
        public double NetDownBps;
        /// <summary>显卡列表(含各自占用率);元素只读替换,勿跨线程修改。</summary>
        public List<GpuAdapterInfo> Gpus;
    }

    /// <summary>
    /// 性能监测采样:CPU(GetSystemTimes)、内存(GlobalMemoryStatusEx)、
    /// GPU(DXGI 枚举 + GPU 引擎 PDH 通配符计数器)、网络(网络接口 PDH 通配符计数器)。
    /// 通配符路径一次采集覆盖全部实例,实例随进程/接口增删自动跟随。
    /// 引用计数模式:任一胶囊可见时以 1 秒周期采样,全部隐藏后线程退出。
    /// </summary>
    public sealed class PerfService
    {
        private static PerfService _instance;
        public static PerfService Instance => _instance ??= new PerfService();

        private readonly object _gate = new object();
        private int _users;
        private Thread _thread;
        private CancellationTokenSource _cancellation;
        private PerfSample _latest;

        /// <summary>最近一次采样快照(采样线程写入,读取方只读)。</summary>
        public PerfSample Latest => _latest;

        /// <summary>每次采样后触发;处理器运行在采样线程,UI 使用前需自行调度。</summary>
        public event Action<PerfSample> Updated;

        private PerfService() { }

        public void AddRef()
        {
            lock (_gate)
            {
                _users++;
                StartWorkerLocked();
            }
        }

        public void ReleaseRef()
        {
            CancellationTokenSource cancellation = null;
            lock (_gate)
            {
                _users = Math.Max(0, _users - 1);
                if (_users == 0) cancellation = _cancellation;
            }
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void StartWorkerLocked()
        {
            if (_users <= 0 || _thread != null) return;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var cancellation = _cancellation;
            _thread = new Thread(() => RunLoop(cancellation))
            {
                IsBackground = true,
                Name = "PerfMonitor"
            };
            _thread.Start();
        }

        private void RunLoop(CancellationTokenSource runCancellation)
        {
            var token = runCancellation.Token;
            try
            {
                using var cpu = new CpuSampler();
                using var mem = new MemorySampler();
                using var pdh = new PdhHost();
                while (!token.IsCancellationRequested)
                {
                    var sample = new PerfSample
                    {
                        CpuPercent = cpu.Next(),
                        NetUpBps = -1,
                        NetDownBps = -1
                    };
                    (sample.MemTotalBytes, sample.MemUsedBytes) = mem.Next();
                    pdh.Collect();
                    if (pdh.NetAvailable)
                        (sample.NetUpBps, sample.NetDownBps) = pdh.ReadNetTotals();

                    sample.Gpus = AdaptersSnapshot();
                    if (sample.Gpus.Count > 0 && pdh.GpuAvailable)
                    {
                        var sums = pdh.ReadGpuSums();
                        foreach (var gpu in sample.Gpus)
                            gpu.UtilPercent = sums.TryGetValue(gpu.Luid, out double v)
                                ? Math.Min(100.0, v)
                                : 0; // 计数器可用但无引擎实例 = 显卡完全空闲
                    }

                    _latest = sample;
                    Updated?.Invoke(sample);
                    if (token.WaitHandle.WaitOne(1000)) break;
                }
            }
            catch (Exception ex)
            {
                App.Log("性能监测线程异常: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cancellation, runCancellation))
                    {
                        _thread = null;
                        _cancellation = null;
                        if (_users > 0) StartWorkerLocked();
                    }
                }
                runCancellation.Dispose();
            }
        }

        // 显卡名单每 30 秒重枚举一次;初值取负数,保证启动后第一次比较就能触发刷新。
        private List<GpuAdapterInfo> _adapters = new List<GpuAdapterInfo>();
        private readonly object _adaptersGate = new object();
        private long _adaptersTick = -30000;

        private List<GpuAdapterInfo> AdaptersSnapshot()
        {
            long now = Environment.TickCount64;
            lock (_adaptersGate)
            {
                if (now - _adaptersTick > 30000)
                {
                    _adapters = EnumerateAdapters();
                    _adaptersTick = now;
                }
                return new List<GpuAdapterInfo>(_adapters);
            }
        }

        /// <summary>枚举显卡(DXGI);失败返回空列表。供设置页下拉框使用。</summary>
        public static List<GpuAdapterInfo> EnumerateGpus() => EnumerateAdapters();

        // ---------- 采样器 ----------

        private sealed class CpuSampler : IDisposable
        {
            private ulong _idle, _kernel, _user;
            private bool _primed;

            public double Next()
            {
                if (!PerfNative.GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
                ulong i = PerfNative.FileTimeValue(idle);
                ulong k = PerfNative.FileTimeValue(kernel);
                ulong u = PerfNative.FileTimeValue(user);
                if (!_primed)
                {
                    _idle = i; _kernel = k; _user = u;
                    _primed = true;
                    return -1;
                }
                ulong di = i - _idle, dk = k - _kernel, du = u - _user;
                _idle = i; _kernel = k; _user = u;
                ulong total = dk + du; // kernel 时间已含 idle,不再重复计入
                if (total == 0) return -1;
                return Math.Clamp(100.0 * (dk + du - di) / total, 0, 100);
            }

            public void Dispose() { }
        }

        private sealed class MemorySampler : IDisposable
        {
            public (ulong total, ulong used) Next()
            {
                if (!PerfNative.TryGetMemoryStatus(out ulong total, out ulong avail)) return (0, 0);
                return (total, total > avail ? total - avail : 0);
            }

            public void Dispose() { }
        }

        /// <summary>GPU 引擎 + 网络接口的 PDH 查询:通配符计数器,每次采集一次性覆盖所有实例。</summary>
        private sealed class PdhHost : IDisposable
        {
            private IntPtr _query = IntPtr.Zero;
            private IntPtr _gpuUtil = IntPtr.Zero;
            private IntPtr _netRecv = IntPtr.Zero;
            private IntPtr _netSent = IntPtr.Zero;
            private bool _primed;

            public bool GpuAvailable => _gpuUtil != IntPtr.Zero;
            public bool NetAvailable => _netRecv != IntPtr.Zero && _netSent != IntPtr.Zero;

            public PdhHost()
            {
                try
                {
                    if (PerfNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
                    if (PerfNative.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuUtil) != 0)
                        _gpuUtil = IntPtr.Zero;
                    if (PerfNative.PdhAddEnglishCounterW(_query, @"\Network Interface(*)\Bytes Received/sec", IntPtr.Zero, out _netRecv) != 0)
                        _netRecv = IntPtr.Zero;
                    if (PerfNative.PdhAddEnglishCounterW(_query, @"\Network Interface(*)\Bytes Sent/sec", IntPtr.Zero, out _netSent) != 0)
                        _netSent = IntPtr.Zero;
                    if (_query == IntPtr.Zero) return;
                    // 速率类计数器需要两次采集才有有效数据。
                    PerfNative.PdhCollectQueryData(_query);
                    PerfNative.PdhCollectQueryData(_query);
                    _primed = true;
                }
                catch (Exception ex)
                {
                    App.Log("性能计数器初始化失败: " + ex.Message);
                    _gpuUtil = _netRecv = _netSent = IntPtr.Zero;
                }
            }

            public void Collect()
            {
                if (!_primed) return;
                PerfNative.PdhCollectQueryData(_query);
            }

            /// <summary>按 LUID 键聚合 GPU 引擎占用率;无实例/读取失败返回空字典(视为 0%)。</summary>
            public Dictionary<string, double> ReadGpuSums()
            {
                var sums = new Dictionary<string, double>();
                if (_gpuUtil == IntPtr.Zero) return sums;
                if (!ReadArray(_gpuUtil, out var items)) return sums;
                foreach (var (name, value) in items)
                {
                    string luid = GpuLuid.Parse(name);
                    if (luid == null) continue;
                    sums[luid] = (sums.TryGetValue(luid, out double s) ? s : 0) + value;
                }
                return sums;
            }

            /// <summary>聚合所有网络接口的收发字节速率;读取失败返回 (-1, -1)。</summary>
            public (double up, double down) ReadNetTotals()
            {
                if (!ReadArray(_netRecv, out var recv)) return (-1, -1);
                if (!ReadArray(_netSent, out var sent)) return (-1, -1);
                double up = 0, down = 0;
                foreach (var (_, value) in recv) down += value;
                foreach (var (_, value) in sent) up += value;
                return (up, down);
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W(x64): { LPWSTR szName; PDH_FMT_COUNTERVALUE(16B) } = 24B,
            // 其中 PDH_FMT_COUNTERVALUE = { DWORD CStatus; double 值(union) }。
            private const int ItemSize = 24;
            private const int MaxBufferSize = 16 * 1024 * 1024;

            private bool ReadArray(IntPtr counter, out List<(string name, double value)> items)
            {
                items = new List<(string name, double value)>();
                try
                {
                    int size = 64 * 1024;
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        int itemCount = 0;
                        int hr = PerfNative.PdhGetFormattedCounterArrayW(counter, PerfNative.PDH_FMT_DOUBLE,
                            ref size, ref itemCount, buf);
                        if (hr == PerfNative.PDH_MORE_DATA)
                        {
                            if (size <= 0 || size > MaxBufferSize) return false;
                            Marshal.FreeHGlobal(buf);
                            buf = Marshal.AllocHGlobal(size);
                            itemCount = 0;
                            hr = PerfNative.PdhGetFormattedCounterArrayW(counter, PerfNative.PDH_FMT_DOUBLE,
                                ref size, ref itemCount, buf);
                        }
                        // PDH_INVALID_DATA(速率未就绪)/PDH_NO_DATA(实例为空)按无数据处理。
                        if (hr != 0) return false;

                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr item = buf + i * ItemSize;
                            IntPtr namePtr = Marshal.ReadIntPtr(item);
                            if (namePtr == IntPtr.Zero) continue;
                            if (Marshal.ReadInt32(item, 8) != 0) continue; // CStatus 非零 = 该实例数据无效
                            double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                            string name = Marshal.PtrToStringUni(namePtr);
                            if (name != null) items.Add((name, value));
                        }
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                if (_query != IntPtr.Zero)
                {
                    try { PerfNative.PdhCloseQuery(_query); } catch { }
                    _query = IntPtr.Zero;
                }
            }
        }

        /// <summary>GPU 引擎实例名解析:pid_10052_luid_0x00000000_0x00017171_phys_0_... → "00000000:00017171"。</summary>
        internal static class GpuLuid
        {
            private static readonly Regex LuidRegex =
                new Regex(@"luid_0x(?<high>[0-9A-Fa-f]{8})_0x(?<low>[0-9A-Fa-f]{8})", RegexOptions.Compiled);

            public static string Parse(string instanceName)
            {
                if (string.IsNullOrEmpty(instanceName)) return null;
                var m = LuidRegex.Match(instanceName);
                if (!m.Success) return null;
                return ulong.Parse(m.Groups["high"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    .ToString("X8", CultureInfo.InvariantCulture)
                    + ":" + ulong.Parse(m.Groups["low"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    .ToString("X8", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>枚举物理显卡(过滤 WARP 软件渲染与同名驱动节点重复项)。实测部分系统上
        /// EnumAdapters 返回的适配器不支持 QI IDXGIAdapter1,故走经典 IDXGIAdapter + GetDesc 路径。</summary>
        internal static List<GpuAdapterInfo> EnumerateAdapters()
        {
            var list = new List<GpuAdapterInfo>();
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if (PerfNative.CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null)
                    return list;

                uint index = 0;
                while (factory.EnumAdapters(index++, out var adapter) == 0)
                {
                    if (adapter == null) break;
                    try
                    {
                        if (adapter.GetDesc(out var desc) != 0) continue;
                        string name = desc.Description?.Trim();
                        // "Microsoft Basic Render Driver" 是 WARP 软件渲染,不计入显卡列表。
                        if (string.IsNullOrWhiteSpace(name)
                            || name.Equals("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string luid = ((uint)desc.LuidHigh).ToString("X8", CultureInfo.InvariantCulture)
                            + ":" + desc.LuidLow.ToString("X8", CultureInfo.InvariantCulture);
                        // 同名驱动节点(不同 LUID)只保留第一个,下拉框不出现重复项。
                        if (list.Exists(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                        list.Add(new GpuAdapterInfo { Name = name, Luid = luid, UtilPercent = -1 });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("枚举显卡失败: " + ex.Message);
            }
            return list;
        }
    }

    /// <summary>性能监测所需 Win32/DXGI/PDH 入口,仅服务内部使用。</summary>
    internal static class PerfNative
    {
        // ---------- CPU / 内存 ----------

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint LowPart;
            public uint HighPart;
        }

        internal static ulong FileTimeValue(FILETIME t) => ((ulong)t.HighPart << 32) | t.LowPart;

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        internal static bool TryGetMemoryStatus(out ulong totalPhys, out ulong availPhys)
        {
            totalPhys = 0;
            availPhys = 0;
            try
            {
                var status = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(status)) return false;
                totalPhys = status.ullTotalPhys;
                availPhys = status.ullAvailPhys;
                return totalPhys > 0;
            }
            catch { return false; }
        }

        // ---------- PDH ----------

        internal const uint PDH_FMT_DOUBLE = 0x00000200;
        internal const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        internal static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref int bufferSize, ref int itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        internal static extern int PdhCloseQuery(IntPtr query);

        // ---------- DXGI 显卡枚举 ----------

        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        /// <summary>只调用 EnumAdapters/GetDesc;其余方法仅用于占住 COM 虚表槽位(含 IDXGIObject 继承的 4 个)。</summary>
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(IntPtr name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(IntPtr name, IntPtr data);
            [PreserveSig] int GetPrivateData(IntPtr name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIFactory
            [PreserveSig] int EnumAdapters(uint index, out IDXGIFactoryAdapter adapter);
            [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        }

        [ComImport, Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0")] // IDXGIAdapter
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDXGIFactoryAdapter
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(IntPtr name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(IntPtr name, IntPtr data);
            [PreserveSig] int GetPrivateData(IntPtr name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(out DXGIAdapterDesc desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long version);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DXGIAdapterDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public ulong DedicatedVideoMemory;
            public ulong DedicatedSystemMemory;
            public ulong SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }
    }
}
