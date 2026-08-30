using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace DesktopBar.Services
{
    /// <summary>
    /// 文件复制/剪切/粘贴：基于系统剪贴板（CF_HDROP + Preferred DropEffect），
    /// 与资源管理器互相兼容——这里复制的内容可粘到 Explorer，反之亦然。
    /// 粘贴语义：剪切(Preferred DropEffect 含 MOVE)为移动，否则为复制。
    /// </summary>
    public static class FileClipboard
    {
        // Explorer 写入的 Preferred DropEffect：复制=5(COPY|LINK)，剪切=2(MOVE)。
        public const uint CopyEffect = 5;
        public const uint MoveEffect = 2;

        public static void CopyFiles(IEnumerable<string> paths) => Write(paths, CopyEffect);
        public static void CutFiles(IEnumerable<string> paths) => Write(paths, MoveEffect);

        public static void Write(IEnumerable<string> paths, uint effect)
        {
            try
            {
                var list = new StringCollection();
                foreach (var p in paths)
                    if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
                if (list.Count == 0) return;
                var data = new DataObject();
                data.SetFileDropList(list);
                data.SetData("Preferred DropEffect", EffectStream(effect));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex) { App.Log("写入剪贴板失败: " + ex.Message); }
        }

        /// <summary>构造 4 字节的 Preferred DropEffect 流。</summary>
        public static MemoryStream EffectStream(uint effect)
        {
            var ms = new MemoryStream(4);
            ms.Write(BitConverter.GetBytes(effect), 0, 4);
            ms.Position = 0;
            return ms;
        }

        public static bool HasPasteableFiles
        {
            get { try { return Clipboard.ContainsFileDropList(); } catch { return false; } }
        }

        /// <summary>把系统剪贴板里的文件粘贴到 destDir（剪切为移动，复制为复制），返回处理条数。</summary>
        public static int PasteInto(string destDir)
        {
            int count = 0;
            try
            {
                if (!Clipboard.ContainsFileDropList()) return 0;
                bool move = IsCutEffect();
                foreach (string src in Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    if (!File.Exists(src) && !Directory.Exists(src)) continue;
                    if (TryPlace(src, destDir, move)) count++;
                }
            }
            catch (Exception ex) { App.Log("粘贴失败: " + ex.Message); }
            return count;
        }

        private static bool IsCutEffect()
        {
            try
            {
                if (Clipboard.GetDataObject()?.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var buf = new byte[4];
                    ms.Position = 0;
                    if (ms.Read(buf, 0, 4) == 4)
                        return (BitConverter.ToUInt32(buf, 0) & MoveEffect) != 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>把 src 放入 destDir：move 时移动（跨卷目录自动递归复制后删除），否则复制。重名自动加序号。</summary>
        public static bool TryPlace(string src, string destDir, bool move)
        {
            try
            {
                string sourceFull = Path.GetFullPath(src);
                string destinationRoot = Path.GetFullPath(destDir);
                if (!Directory.Exists(destinationRoot)) return false;
                if (Directory.Exists(sourceFull) && (IsReparsePoint(sourceFull) || IsInside(sourceFull, destinationRoot)))
                {
                    App.Log("已阻止把文件夹复制或移动到自身内部: " + sourceFull);
                    return false;
                }
                string dest = UniqueDest(Path.GetFileName(sourceFull), destinationRoot);
                if (!move)
                {
                    CopyPath(src, dest);
                    return true;
                }
                if (File.Exists(sourceFull)) File.Move(sourceFull, dest);
                else if (Directory.Exists(sourceFull))
                {
                    if (SameVolume(sourceFull, destinationRoot)) Directory.Move(sourceFull, dest);
                    else
                    {
                        CopyDir(sourceFull, dest);
                        Directory.Delete(sourceFull, true);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Log("移动/复制失败: " + src + " — " + ex.Message);
                return false;
            }
        }

        public static string UniqueDest(string fileName, string destDir)
        {
            string dest = Path.Combine(destDir, fileName);
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            string ext = Path.GetExtension(fileName);
            string stem = fileName.Substring(0, fileName.Length - ext.Length);
            int i = 1;
            while (true)
            {
                dest = Path.Combine(destDir, stem + " (" + i++ + ")" + ext);
                if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            }
        }

        public static void CopyPath(string src, string dest)
        {
            if (File.Exists(src)) File.Copy(src, dest);
            else CopyDir(src, dest);
        }

        public static void CopyDir(string srcDir, string dstDir)
        {
            if (IsInside(srcDir, dstDir))
                throw new IOException("目标目录位于源目录内部。");
            var pending = new Stack<(string Src, string Dst)>();
            pending.Push((srcDir, dstDir));
            while (pending.Count > 0)
            {
                var (src, dst) = pending.Pop();
                if (IsReparsePoint(src)) continue;
                Directory.CreateDirectory(dst);
                foreach (var file in Directory.EnumerateFiles(src))
                {
                    if (!IsReparsePoint(file))
                        File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
                }
                foreach (var sub in Directory.EnumerateDirectories(src))
                {
                    if (!IsReparsePoint(sub))
                        pending.Push((sub, Path.Combine(dst, Path.GetFileName(sub))));
                }
            }
        }

        private static bool IsInside(string source, string candidate)
        {
            try
            {
                string sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(sourceFull, candidateFull, StringComparison.OrdinalIgnoreCase)
                    || candidateFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static bool SameVolume(string src, string destDir)
        {
            try
            {
                string a = Path.GetPathRoot(Path.GetFullPath(src));
                string b = Path.GetPathRoot(Path.GetFullPath(destDir));
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
