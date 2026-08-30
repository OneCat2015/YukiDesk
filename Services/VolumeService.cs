using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace DesktopBar.Services
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; }
        public string Name { get; }
        public bool IsDefault { get; }

        public AudioDeviceInfo(string id, string name, bool isDefault)
        {
            Id = id ?? "";
            Name = string.IsNullOrWhiteSpace(name) ? "播放设备" : name;
            IsDefault = isDefault;
        }

        public override string ToString() => IsDefault ? Name + "（默认）" : Name;
    }

    /// <summary>播放端点音量与设备选择。选择设备会同时把 Windows 默认输出路由切到该端点。</summary>
    public sealed class VolumeService : IDisposable
    {
        private static readonly Guid EndpointVolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        // PolicyConfigClient + IPolicyConfig 是 Windows 内部接口，SoundSwitch/AudioSwitcher
        // 等同样通过它切换系统默认输出设备。
        private static readonly Guid PolicyConfigClass = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

        private IMMDeviceEnumerator _enumerator;
        private IMMDevice _device;
        private IAudioEndpointVolume _endpoint;
        private IPolicyConfig _policyConfig;
        private string _selectedId = "";
        private string _deviceName = "默认播放设备";
        private bool _explicitSelection;
        private bool _disposed;

        public string CurrentDeviceId => _selectedId;
        public string DeviceName => _deviceName;

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

        public async Task<List<AudioDeviceInfo>> RefreshDevicesAsync()
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                EnsureEnumerator();
                Ensure();
                // WinRT 枚举提供友好名称；其设备 ID(\\?\SWD#MMDEVAPI#{端点ID}#{角色GUID})
                // 中段才是 MMDevice 端点 ID。控件与 COM 端点全部使用提取后的中段 ID，
                // 否则 WinRT ID 与 COM ID 永不相等，当前/默认设备永远探测不到。
                string defaultId = GetDefaultDeviceId();
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                foreach (var device in devices)
                {
                    if (device == null || string.IsNullOrWhiteSpace(device.Id)) continue;
                    string id = ExtractEndpointId(device.Id);
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new AudioDeviceInfo(id, device.Name, DeviceIdsEqual(id, defaultId)));
                }

                if (result.Count == 0)
                {
                    _deviceName = "默认播放设备";
                    return result;
                }

                AudioDeviceInfo selected = null;
                if (_explicitSelection && !string.IsNullOrEmpty(_selectedId))
                    selected = result.Find(x => DeviceIdsEqual(x.Id, _selectedId));
                selected ??= result.Find(x => x.IsDefault) ?? result[0];

                bool bound = true;
                if (!_explicitSelection && !string.IsNullOrEmpty(defaultId)
                    && !DeviceIdsEqual(_selectedId, defaultId))
                {
                    // Windows 默认播放设备可能在程序运行期间改变；跟随默认时重绑真实端点。
                    bound = TryBindEndpoint(defaultId, false);
                }
                else if (_explicitSelection && !DeviceIdsEqual(_selectedId, selected.Id))
                {
                    // 用户选中的设备被移除时回退到列表中的默认设备。
                    bound = TryBindEndpoint(selected.Id, false);
                }

                if (bound)
                {
                    var actual = result.Find(x => DeviceIdsEqual(x.Id, _selectedId));
                    _deviceName = (actual ?? selected).ToString();
                }
                else
                {
                    App.Log("播放设备绑定失败，保留当前设备状态");
                }
            }
            catch (Exception ex) { App.Log("枚举播放设备失败: " + ex.Message); }
            return result;
        }

        private static string ExtractEndpointId(string winrtId)
        {
            if (string.IsNullOrWhiteSpace(winrtId)) return "";
            string id = winrtId.Trim();
            int marker = id.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                int start = marker + "MMDEVAPI#".Length;
                int end = id.IndexOf('#', start);
                if (end > start) return id.Substring(start, end - start);
            }
            // 已经是 MMDevice 端点 ID({0.0...}.{guid})或其他可直传格式。
            return id;
        }

        public bool SelectDevice(string id, string name = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            IMMDevice nextDevice = null;
            IAudioEndpointVolume nextEndpoint = null;
            try
            {
                EnsureEnumerator();
                Check(_enumerator.GetDevice(ToMmDeviceId(id), out nextDevice), "查找播放设备");
                if (nextDevice == null) return false;
                ActivateEndpoint(nextDevice, out nextEndpoint);
                if (nextEndpoint == null) return false;

                // 系统默认输出路由一并切换；失败时仍保留对本端点的控制。
                bool defaultChanged = SetSystemDefaultEndpoint(id);

                ReleaseEndpointOnly();
                Release(_device);
                _device = nextDevice;
                _endpoint = nextEndpoint;
                nextDevice = null;
                nextEndpoint = null;
                _selectedId = GetDeviceId(_device);
                // 切换成功后跟随系统默认（此后 Windows 侧改动默认设备时同步跟随）；
                // 失败则锁定用户所选端点。
                _explicitSelection = !defaultChanged;
                _deviceName = string.IsNullOrWhiteSpace(name) ? "播放设备" : name;
                return true;
            }
            catch (Exception ex)
            {
                App.Log("切换播放设备失败: " + ex.Message);
                return false;
            }
            finally
            {
                Release(nextEndpoint);
                Release(nextDevice);
            }
        }

        private bool SetSystemDefaultEndpoint(string id)
        {
            string mmId = ToMmDeviceId(id);
            try
            {
                if (_policyConfig == null)
                    _policyConfig = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClass));
                // 三个角色全部切换，Windows 声音设置里的“默认输出设备”才会完整指向新端点。
                Check(_policyConfig.SetDefaultEndpoint(mmId, ERole.Console), "设置默认输出设备");
                Check(_policyConfig.SetDefaultEndpoint(mmId, ERole.Multimedia), "设置默认输出设备");
                Check(_policyConfig.SetDefaultEndpoint(mmId, ERole.Communications), "设置默认输出设备");
                return true;
            }
            catch (Exception ex)
            {
                App.Log("切换系统默认输出设备失败: " + ex.Message);
                return false;
            }
        }

        private void Ensure()
        {
            if (_endpoint != null) return;
            EnsureEnumerator();
            if (_device == null)
            {
                if (_explicitSelection && !string.IsNullOrEmpty(_selectedId))
                {
                    try
                    {
                        Check(_enumerator.GetDevice(ToMmDeviceId(_selectedId), out _device), "查找已选播放设备");
                    }
                    catch { _device = null; }
                }
                if (_device == null)
                {
                    Check(_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out _device), "查找默认播放设备");
                    _selectedId = GetDeviceId(_device);
                    // 友好名称由 RefreshDevices 通过 WinRT 枚举补齐。
                    _explicitSelection = false;
                }
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

        private bool TryBindEndpoint(string id, bool explicitSelection)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            IMMDevice nextDevice = null;
            IAudioEndpointVolume nextEndpoint = null;
            try
            {
                EnsureEnumerator();
                Check(_enumerator.GetDevice(ToMmDeviceId(id), out nextDevice), "查找播放设备");
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
                _explicitSelection = explicitSelection;
                // 友好名称由 RefreshDevices 通过 WinRT 枚举补齐。
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

        private static string ToMmDeviceId(string id)
        {
            // API 边界统一接受 WinRT 的 MMDEVAPI 包装 ID 或 MMDevice 原始 ID。
            return ExtractEndpointId(id);
        }

        private static bool DeviceIdsEqual(string a, string b) =>
            string.Equals(ToMmDeviceId(a), ToMmDeviceId(b), StringComparison.OrdinalIgnoreCase);

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
            Release(_policyConfig);
            _device = null;
            _enumerator = null;
            _policyConfig = null;
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
            // 保留槽位；友好名称经由 WinRT DeviceInformation 读取。
            int OpenPropertyStore(uint access, out IntPtr store);
            int GetId(out IntPtr id);
            int GetState(out uint state);
        }

        // Windows 内部接口，方法槽位必须与 mmdeviceapi/PolicyConfig 定义完全一致。
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
            int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr newFormat, IntPtr oldFormat);
            int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, IntPtr outDefaultPeriod, IntPtr outMinPeriod);
            int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
            int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
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
            int GetChannelVolumeLevelScalar(uint channel, out float level);
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
