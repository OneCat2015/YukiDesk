using System;
using System.Diagnostics;
using System.IO;

namespace DesktopBar.UI
{
    /// <summary>用系统默认程序打开文件/文件夹(无 shell 命令拼接,使用参数化 API)。</summary>
    public static class ShellOpen
    {
        public static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                bool isFile = File.Exists(path);
                bool isDir = Directory.Exists(path);
                if (!isFile && !isDir) return;

                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = isDir ? "open" : null
                };
                Process.Start(psi);
            }
            catch (Exception ex) { App.Log("打开失败: " + path + " — " + ex.Message); }
        }
    }
}
