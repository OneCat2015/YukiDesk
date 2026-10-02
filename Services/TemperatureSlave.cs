using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DesktopBar.Services
{
    /// <summary>
    /// 原生 CPU 温度从属模式:计划任务(RunLevel=HIGHEST)静默启动 "YukiDesk.exe --tempslave"。
    /// PawnIO 设备 DACL 仅授予 SYSTEM/Administrators,普通权限打不开,因此由这个高权限从属进程
    /// 经 PawnIoTctl 读取 Tctl,写入全局共享内存 Global\YukiDeskTctl 供普通权限主进程读取。
    /// 主进程退出(单实例互斥体消失)后从属进程自动退出;布局:
    /// [0]=int 魔数 0x544C4339,[4]=float 温度°C(-1 无效),[8]=long UTC ticks。
    /// </summary>
    internal static class TemperatureSlave
    {
        private const string MapName = @"Global\YukiDeskTctl";
        private const string MasterMutexName = "YukiDesk_SingleInstance_Mutex";
        private const int Magic = 0x544C4339;   // 小端内存中即 "9CLT"(非字符串语义,仅作魔数)
        private const int Capacity = 16;
        private const long StaleTicks = 60_000_000; // 6 秒(TimeSpan.FromSeconds(6).Ticks)

        /// <summary>从属模式主循环:阻塞直到主进程退出。runnning 在 Dispatcher 线程上触发退出。</summary>
        public static void Run(Action finished)
        {
            var thread = new Thread(() => Loop(finished)) { IsBackground = true, Name = "TctlSlave" };
            thread.Start();
        }

        private static void Loop(Action finished)
        {
            // 任何退出路径(主进程消失/异常)都必须调 finished() 结束 Dispatcher,
            // 否则这个提权进程会以 OnExplicitShutdown 状态永久滞留成僵尸。
            try
            {
                RunLoop(finished);
            }
            catch (Exception ex)
            {
                App.Log("温度从属进程异常退出: " + ex);
            }
            finally
            {
                finished();
            }
        }

        private static void RunLoop(Action finished)
        {
            using var mmf = MemoryMappedFile.CreateOrOpen(MapName, Capacity);
            using var view = mmf.CreateViewAccessor();
            int missed = 0;
            for (int i = 0; ; i++)
            {
                double t = PawnIoTctl.ReadCpuTempC();
                try
                {
                    view.Write(0, Magic);
                    view.Write(4, (float)t);
                    view.Write(8, DateTime.UtcNow.Ticks);
                }
                catch { }
                // 主进程互斥体探测:前 2 分钟每 30 秒一次(容忍 .NET 冷启动慢于从属),
                // 之后每 60 秒一次;连续 3 次不存在才退出,给"用户关自启后手动启动"留活路。
                if (i % (i < 4 ? 30 : 60) == 0)
                {
                    try
                    {
                        using (var m = Mutex.OpenExisting(MasterMutexName))
                        {
                        }
                        missed = 0;
                    }
                    catch
                    {
                        if (++missed >= 3)
                        {
                            // finally 会统一调 finished(),这里直接返回即可。
                            return;
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }

        /// <summary>读取从属进程发布的 Tctl(°C);无数据/过期/异常返回 -1。供采样线程调用。</summary>
        public static double ReadTctl()
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read);
                if (view.ReadInt32(0) != Magic) return -1;
                long ticks = view.ReadInt64(8);
                if (Math.Abs(DateTime.UtcNow.Ticks - ticks) > StaleTicks) return -1;
                double v = view.ReadSingle(4);
                return v >= -5 && v <= 125 ? v : -1;
            }
            catch { return -1; }
        }

        /// <summary>从属进程是否正在提供数据(设置页状态展示用)。</summary>
        public static bool IsFresh => ReadTctl() >= 0;
    }
}
