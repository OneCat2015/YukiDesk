using System;
using Microsoft.Win32;

namespace DesktopBar.Services
{
    public static class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "YukiDesk";

        public static bool IsEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) != null || k?.GetValue("DesktopBar") != null;
            }
            catch { return false; }
        }

        /// <summary>写入/删除 HKCU 自启项;返回是否与目标状态一致(供 UI 回读校正开关)。</summary>
        public static bool Set(bool enabled)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled)
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        k.SetValue(ValueName, $"\"{exe}\"");
                        k.DeleteValue("DesktopBar", false);
                    }
                }
                else
                {
                    k.DeleteValue(ValueName, false);
                    k.DeleteValue("DesktopBar", false);
                }
                return IsEnabled() == enabled;
            }
            catch (Exception ex) { App.Log("设置自启失败: " + ex.Message); return false; }
        }
    }
}
