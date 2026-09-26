using System;
using System.Runtime.InteropServices;

namespace DesktopBar.Services
{
    /// <summary>播放端点音量控制。不做设备选择，始终作用于系统当前默认输出设备。</summary>
    public sealed class VolumeService : IDisposable
    {
        private static readonly Guid EndpointVolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        private IMMDeviceEnumerator _enumerator;
        private IMMDevice _device;
        private IAudioEndpointVolume _endpoint;
        private string _selectedId = "";
        private bool _disposed;

        public float? GetVolume()
        {
            try
            {
                Ensure();
                Check(_endpoint.GetMasterVolumeLevelScalar(out float value), "读取系统音量");
                return Math.Max(0, Math.Min(1, value));
            }
            catch (Exception ex)
            {
                App.Log("读取系统音量失败(状态未知): " + ex.Message);
                InvalidateEndpoint();
                return null;
            }
        }

        public bool? GetMute()
        {
            try
            {
                Ensure();
                Check(_endpoint.GetMute(out bool mute), "读取静音状态");
                return mute;
            }
            catch (Exception ex)
            {
                App.Log("读取静音状态失败(状态未知): " + ex.Message);
                InvalidateEndpoint();
                return null;
            }
        }

        public bool SetVolume(float value)
        {
            try
            {
                Ensure();
                Check(_endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, value)), Guid.Empty), "设置系统音量");
                return true;
            }
            catch (Exception ex) { App.Log("设置系统音量失败: " + ex.Message); InvalidateEndpoint(); return false; }
        }

        public bool SetMute(bool mute)
        {
            try
            {
                Ensure();
                Check(_endpoint.SetMute(mute, Guid.Empty), "设置静音");
                return true;
            }
            catch (Exception ex) { App.Log("设置静音失败: " + ex.Message); InvalidateEndpoint(); return false; }
        }

        private void InvalidateEndpoint()
        {
            ReleaseEndpointOnly();
            Release(_device);
            _device = null;
        }

        /// <summary>跟随系统默认输出设备：默认端点变化时重绑，音量始终作用于当前设备。</summary>
        public bool SyncDefaultIfChanged()
        {
            try
            {
                Ensure();
                string defaultId = GetDefaultDeviceId();
                if (string.IsNullOrEmpty(defaultId) || DeviceIdsEqual(_selectedId, defaultId)) return false;
                return TryBindEndpoint(defaultId);
            }
            catch (Exception ex) { App.Log("跟随默认播放设备失败: " + ex.Message); return false; }
        }

        private void Ensure()
        {
            if (_endpoint != null) return;
            EnsureEnumerator();
            if (_device == null)
            {
                Check(_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out _device), "查找默认播放设备");
                _selectedId = GetDeviceId(_device);
            }
            ActivateEndpoint(_device);
        }

        private void EnsureEnumerator()
        {
            if (_enumerator == null)
                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass));
        }

        private void ActivateEndpoint(IMMDevice device)
        {
            ActivateEndpoint(device, out _endpoint);
        }

        private static void ActivateEndpoint(IMMDevice device, out IAudioEndpointVolume endpoint)
        {
            if (device == null) throw new InvalidOperationException("播放设备不可用");
            var iid = EndpointVolumeIid;
            Check(device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out endpoint), "连接播放设备");
            if (endpoint == null) throw new InvalidOperationException("播放设备音量接口不可用");
        }

        private static void Check(int hr, string operation)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        private bool TryBindEndpoint(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            IMMDevice nextDevice = null;
            IAudioEndpointVolume nextEndpoint = null;
            try
            {
                EnsureEnumerator();
                Check(_enumerator.GetDevice(id.Trim(), out nextDevice), "查找播放设备");
                if (nextDevice == null) return false;
                ActivateEndpoint(nextDevice, out nextEndpoint);
                if (nextEndpoint == null) return false;

                ReleaseEndpointOnly();
                Release(_device);
                _device = nextDevice;
                _endpoint = nextEndpoint;
                nextDevice = null;
                nextEndpoint = null;
                _selectedId = GetDeviceId(_device);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("绑定播放设备失败: " + ex.Message);
                return false;
            }
            finally
            {
                Release(nextEndpoint);
                Release(nextDevice);
            }
        }

        private string GetDefaultDeviceId()
        {
            IMMDevice d = null;
            try
            {
                Check(_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d), "查找默认播放设备");
                return NormalizeDeviceId(GetDeviceId(d));
            }
            catch (Exception ex)
            {
                App.Log("读取默认播放设备失败: " + ex.Message);
                return "";
            }
            finally { Release(d); }
        }

        private static string NormalizeDeviceId(string id)
        {
            return string.IsNullOrWhiteSpace(id) ? "" : id.Trim();
        }

        private static bool DeviceIdsEqual(string a, string b) =>
            string.Equals(NormalizeDeviceId(a), NormalizeDeviceId(b), StringComparison.OrdinalIgnoreCase);

        private static string GetDeviceId(IMMDevice device)
        {
            if (device == null) return "";
            IntPtr ptr = IntPtr.Zero;
            try
            {
                Check(device.GetId(out ptr), "读取播放设备 ID");
                return ptr == IntPtr.Zero ? "" : NormalizeDeviceId(Marshal.PtrToStringUni(ptr) ?? "");
            }
            catch (Exception ex)
            {
                App.Log("读取播放设备 ID 失败: " + ex.Message);
                return "";
            }
            finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
        }

        private void ReleaseEndpointOnly()
        {
            Release(_endpoint);
            _endpoint = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseEndpointOnly();
            Release(_device);
            Release(_enumerator);
            _device = null;
            _enumerator = null;
        }

        private static void Release(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); } catch { }
        }

        private static class CLSCTX { public const uint ALL = 0x17; }
        private enum EDataFlow { Render, Capture, All }
        private enum ERole { Console, Multimedia, Communications }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            // 保留 COM vtable 的完整方法槽位，否则后续方法会错位调用。
            int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, uint clsctx, IntPtr activationParams, out IAudioEndpointVolume endpoint);
            // 保留槽位；属性存储暂未使用。
            int OpenPropertyStore(uint access, out IntPtr store);
            int GetId(out IntPtr id);
            int GetState(out uint state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out uint channels);
            int SetMasterVolumeLevel(float level, Guid context);
            int SetMasterVolumeLevelScalar(float level, Guid context);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channel, float level, Guid context);
            int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
            int GetChannelVolumeLevel(uint channel, out float level);
            int GetChannelVolumeLevelScalar(uint channel, out float scalar);
            int SetMute(bool mute, Guid context);
            int GetMute(out bool mute);
            int GetVolumeStepInfo(out uint step, out uint stepCount);
            int VolumeStepUp(Guid context);
            int VolumeStepDown(Guid context);
            int QueryHardwareSupport(out uint mask);
            int GetHardwareVolumeStepInfo(out float min, out float max, out float step);
        }
    }
}
