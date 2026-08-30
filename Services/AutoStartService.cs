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

        public static void Set(bool enabled)
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
            }
            catch (Exception ex) { App.Log("设置自启失败: " + ex.Message); }
        }
    }
}
