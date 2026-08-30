using System;
using System.Windows.Interop;
using DesktopBar.Interop;
using DesktopBar.Models;

namespace DesktopBar.Services
{
    public static class DesktopSpaceService
    {
        public static void Apply(AppSettings s, IntPtr hwnd)
        {
            if (s == null) return;
            if (s.ReserveDesktopSpace)
                NativeMethods.SetDesktopTopReserve(hwnd, s.DesktopSpaceHeight);
            else
                NativeMethods.RestoreDesktopWorkArea();
        }

        public static void Apply(AppSettings s)
        {
            if (s == null) return;
            if (s.ReserveDesktopSpace)
            {
                var hwnd = App.Bar == null ? IntPtr.Zero : new WindowInteropHelper(App.Bar).Handle;
                NativeMethods.SetDesktopTopReserve(hwnd, s.DesktopSpaceHeight);
            }
            else
            {
                NativeMethods.RestoreDesktopWorkArea();
            }
        }

        public static void Restore() => NativeMethods.RestoreDesktopWorkArea();
    }
}
