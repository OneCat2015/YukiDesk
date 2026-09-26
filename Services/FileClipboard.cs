using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
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
        private static readonly object TemporaryGate = new object();
        private static readonly List<string> TemporaryClipboardPaths = new List<string>();

        /// <summary>在 UI 线程捕获的剪贴板快照；后台线程只消费路径，不再访问 WPF Clipboard。</summary>
        public sealed class PasteSnapshot
        {
            public IReadOnlyList<string> Paths { get; }
            public bool Move { get; }

            internal PasteSnapshot(IReadOnlyList<string> paths, bool move)
            {
                Paths = paths;
                Move = move;
            }
        }

        public sealed class PasteResult
        {
            public int Total { get; internal set; }
            public int Succeeded { get; internal set; }
            public int Failed { get; internal set; }
            public IReadOnlyList<string> FailedItems { get; internal set; }
            public bool SucceededFully => Total > 0 && Failed == 0 && Succeeded == Total;
        }

        public static void CopyFiles(IEnumerable<string> paths) => Write(paths, CopyEffect);
        public static void CutFiles(IEnumerable<string> paths) => Write(paths, MoveEffect);

        public static void Write(IEnumerable<string> paths, uint effect)
        {
            try
            {
                var list = new StringCollection();
                foreach (var p in paths ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
                if (list.Count == 0) return;
                SetClipboardData(list, effect, null);
            }
            catch (Exception ex) { App.Log("写入剪贴板失败: " + ex.Message); }
        }

        public static bool WriteTemporaryPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)
                    || (!File.Exists(path) && !Directory.Exists(path))) return false;
                var list = new StringCollection { path };
                return SetClipboardData(list, CopyEffect, new[] { path });
            }
            catch (Exception ex)
            {
                App.Log("写入临时剪贴板路径失败: " + ex.Message);
                return false;
            }
        }

        private static bool SetClipboardData(StringCollection list, uint effect,
            IEnumerable<string> temporaryPaths)
        {
            var data = new DataObject();
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", EffectStream(effect));
            Clipboard.SetDataObject(data, true);

            var old = new List<string>();
            lock (TemporaryGate)
            {
                old.AddRange(TemporaryClipboardPaths);
                TemporaryClipboardPaths.Clear();
                if (temporaryPaths != null)
                    TemporaryClipboardPaths.AddRange(temporaryPaths);
            }
            if (old.Count > 0)
            {
                var stale = old.ToArray();
                _ = Task.Run(() => DeleteTemporaryPaths(stale));
            }
            return true;
        }

        public static string CreateNamedTemporaryCopy(string source, string displayName)
        {
            string sourceFull = Path.GetFullPath(source ?? "");
            if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))
                throw new FileNotFoundException("回收站内容不存在", sourceFull);

            string root = Path.Combine(Path.GetTempPath(), "YukiDeskClipboard");
            Directory.CreateDirectory(root);
            string name = Path.GetFileName(displayName ?? "");
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(sourceFull);
            if (string.IsNullOrWhiteSpace(name)) name = "剪贴板项目";
            string destination = UniqueDest(name, root);
            try
            {
                CopyPath(sourceFull, destination);
                return destination;
            }
            catch
            {
                try { DeletePath(destination); } catch { }
                throw;
            }
        }

        public static void CleanupTemporaryClipboardPaths()
        {
            List<string> paths;
            lock (TemporaryGate)
            {
                paths = new List<string>(TemporaryClipboardPaths);
                TemporaryClipboardPaths.Clear();
            }
            DeleteTemporaryPaths(paths);
        }

        public static void CleanupTemporaryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            DeleteTemporaryPaths(new[] { path });
        }

        private static void DeleteTemporaryPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths ?? Array.Empty<string>())
            {
                try { DeletePath(path); } catch (Exception ex) { App.Log("清理临时剪贴板文件失败: " + ex.Message); }
            }
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

        /// <summary>在 UI 线程捕获剪贴板路径与剪切状态；失败返回 null。</summary>
        public static PasteSnapshot CapturePasteSnapshot()
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return null;
                var paths = new List<string>();
                foreach (string src in Clipboard.GetFileDropList())
                {
                    if (!string.IsNullOrWhiteSpace(src) && (File.Exists(src) || Directory.Exists(src)))
                        paths.Add(src);
                }
                return paths.Count == 0 ? null : new PasteSnapshot(paths, IsCutEffect());
            }
            catch (Exception ex) { App.Log("读取剪贴板失败: " + ex.Message); return null; }
        }

        /// <summary>把已捕获的剪贴板快照放入目标目录，后台线程可安全调用。</summary>
        public static PasteResult PasteSnapshotDetailed(PasteSnapshot snapshot, string destDir,
            Action<int, string, bool> progress = null)
        {
            var result = new PasteResult
            {
                Total = snapshot?.Paths?.Count ?? 0,
                FailedItems = new List<string>()
            };
            if (snapshot == null || snapshot.Paths == null) return result;
            for (int i = 0; i < snapshot.Paths.Count; i++)
            {
                string src = snapshot.Paths[i];
                bool placed = TryPlace(src, destDir, snapshot.Move);
                if (placed) result.Succeeded++;
                else
                {
                    result.Failed++;
                    ((List<string>)result.FailedItems).Add(Path.GetFileName(src));
                }
                progress?.Invoke(i + 1, Path.GetFileName(src), placed);
            }
            return result;
        }

        public static int PasteSnapshotInto(PasteSnapshot snapshot, string destDir, Action<int, string> progress = null)
        {
            var result = PasteSnapshotDetailed(snapshot, destDir,
                (completed, name, placed) => progress?.Invoke(completed, name));
            return result.Succeeded;
        }

        /// <summary>兼容旧调用：捕获剪贴板后同步粘贴。</summary>
        public static int PasteInto(string destDir)
        {
            try { return PasteSnapshotInto(CapturePasteSnapshot(), destDir); }
            catch (Exception ex) { App.Log("粘贴失败: " + ex.Message); return 0; }
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
            string dest = null;
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
                dest = UniqueDest(Path.GetFileName(sourceFull), destinationRoot);
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
                if (!string.IsNullOrEmpty(dest))
                {
                    try { DeletePath(dest); } catch { }
                }
                App.Log("移动/复制失败: " + src + " — " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 替换同名项目：先把新项目暂存到目标目录，再移除旧项目，最后提交到旧项目的路径。
        /// 同卷移动可在提交失败时恢复源路径；跨卷复制会保留源直到提交成功。
        /// </summary>
        public static bool TryReplace(string src, string replaceTarget,
            Func<bool> removeTarget, out string error)
        {
            error = null;
            string sourceFull = null;
            string temp = null;
            bool stagedByMove = false;
            try
            {
                sourceFull = Path.GetFullPath(src);
                string targetFull = Path.GetFullPath(replaceTarget);
                if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))
                {
                    error = "源项目已不存在。";
                    return false;
                }
                if (!File.Exists(targetFull) && !Directory.Exists(targetFull))
                {
                    error = "替换目标已不存在。";
                    return false;
                }
                if (Directory.Exists(sourceFull) &&
                    (IsReparsePoint(sourceFull) || IsInside(sourceFull, targetFull)))
                {
                    error = "源文件夹路径无效。";
                    return false;
                }

                string targetDir = Path.GetDirectoryName(targetFull);
                string name = Path.GetFileName(targetFull);
                temp = Path.Combine(targetDir, ".yukidesk-staging-"
                    + Guid.NewGuid().ToString("N") + "-" + name);
                bool sameVolume = SameVolume(sourceFull, targetDir);
                if (sameVolume)
                {
                    MovePath(sourceFull, temp);
                    stagedByMove = true;
                }
                else
                {
                    CopyPath(sourceFull, temp);
                }

                if (removeTarget == null || !removeTarget())
                {
                    error = "旧项目无法移入回收站。";
                    RestoreStagedSource(sourceFull, temp, stagedByMove);
                    return false;
                }

                try
                {
                    MovePath(temp, targetFull);
                    temp = null;
                }
                catch (Exception ex)
                {
                    error = "新项目提交失败；旧项目已移入回收站: " + ex.Message;
                    RestoreStagedSource(sourceFull, temp, stagedByMove);
                    return false;
                }

                if (!sameVolume)
                {
                    try { DeletePath(sourceFull); }
                    catch (Exception ex)
                    {
                        error = "目标已替换，但源项目清理失败: " + ex.Message;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                RestoreStagedSource(sourceFull, temp, stagedByMove);
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temp))
                {
                    try { DeletePath(temp); } catch { }
                }
            }
        }

        private static void MovePath(string source, string destination)
        {
            if (File.Exists(source)) File.Move(source, destination);
            else if (Directory.Exists(source)) Directory.Move(source, destination);
            else throw new FileNotFoundException("源项目不存在", source);
        }

        private static void DeletePath(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static void RestoreStagedSource(string source, string temp, bool stagedByMove)
        {
            if (!stagedByMove || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(temp)) return;
            try
            {
                if (!File.Exists(source) && !Directory.Exists(source)
                    && (File.Exists(temp) || Directory.Exists(temp)))
                    MovePath(temp, source);
            }
            catch (Exception ex) { App.Log("恢复暂存源失败: " + ex.Message); }
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
