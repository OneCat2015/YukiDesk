using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DesktopBar.Services
{
    public sealed class RecycleItem
    {
        public string MetaFile { get; set; }
        public string DataPath { get; set; }
        public string OriginalPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime DeletedAt { get; set; }

        public string Name => Path.GetFileName(OriginalPath ?? "");
    }

    /// <summary>
    /// 回收站访问：直接解析各盘 $Recycle.Bin\当前用户SID 下的 $I 元数据文件，
    /// 得到原始路径/大小/删除时间；$R 文件即实际内容。只枚举当前用户的项目
    /// （其他用户的回收目录需要管理员权限）。
    /// </summary>
    public static class RecycleBin
    {
        public static List<RecycleItem> Enumerate()
        {
            var result = new List<RecycleItem>();
            try
            {
                string sid = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(sid)) return result;
                foreach (var drive in DriveInfo.GetDrives())
                {
                    // 可移动盘也有自己的 $Recycle.Bin:列表看不到它们,但"清空回收站"却会清到,
                    // 列表与破坏性操作范围必须一致,所以一并纳入(IsReady 挡掉未插入的盘)。
                    if ((drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                        || !drive.IsReady) continue;
                    string userBin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                    if (!Directory.Exists(userBin)) continue;
                    string[] metaFiles;
                    try { metaFiles = Directory.GetFiles(userBin, "$I*"); }
                    catch (Exception ex) { App.Log("枚举回收站目录失败(" + drive.Name + "): " + ex.Message); continue; }
                    foreach (var meta in metaFiles)
                    {
                        if (TryReadMeta(meta, out var item)) result.Add(item);
                    }
                }
                result.Sort((a, b) => b.DeletedAt.CompareTo(a.DeletedAt));
            }
            catch (Exception ex) { App.Log("枚举回收站失败: " + ex.Message); }
            return result;
        }

        private static bool TryReadMeta(string metaFile, out RecycleItem item)
        {
            item = null;
            try
            {
                var bytes = File.ReadAllBytes(metaFile);
                if (bytes.Length < 24) return false;
                long version = BitConverter.ToInt64(bytes, 0);
                int pathOffset, pathChars;
                if (version == 2)
                {
                    // v2: 24-27 为原始路径字符数，其后紧跟 UTF-16 路径。
                    pathChars = BitConverter.ToInt32(bytes, 24);
                    pathOffset = 28;
                }
                else
                {
                    // v1: 路径固定 520 字节 UTF-16。
                    pathChars = 260;
                    pathOffset = 24;
                }
                if (pathChars <= 0 || bytes.Length < pathOffset + pathChars * 2) return false;
                string original = Encoding.Unicode.GetString(bytes, pathOffset, pathChars * 2);
                int zero = original.IndexOf('\0');
                if (zero >= 0) original = original.Substring(0, zero);
                if (string.IsNullOrEmpty(original)) return false;

                string name = Path.GetFileName(metaFile);
                string dataPath = Path.Combine(Path.GetDirectoryName(metaFile) ?? "", "$R" + name.Substring(2));
                bool isDir = Directory.Exists(dataPath);
                if (!isDir && !File.Exists(dataPath)) return false; // $R 已被手动清理

                item = new RecycleItem
                {
                    MetaFile = metaFile,
                    DataPath = dataPath,
                    OriginalPath = original,
                    IsDirectory = isDir,
                    Size = BitConverter.ToInt64(bytes, 8),
                    DeletedAt = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16)).ToLocalTime()
                };
                return true;
            }
            catch (Exception ex)
            {
                App.Log("读取回收站元数据失败: " + ex.Message);
                return false;
            }
        }

                /// <summary>把项目移回原始位置；原位置被占用时自动加序号后缀。</summary>
        public static bool Restore(RecycleItem item)
        {
            try
            {
                string target = item.OriginalPath;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    string dir = Path.GetDirectoryName(target) ?? "";
                    string name = Path.GetFileName(target);
                    string ext = Path.GetExtension(name);
                    string stem = name.Substring(0, name.Length - ext.Length);
                    int i = 1;
                    do { target = Path.Combine(dir, stem + " (" + i++ + ")" + ext); }
                    while (File.Exists(target) || Directory.Exists(target));
                }
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (item.IsDirectory) Directory.Move(item.DataPath, target);
                else File.Move(item.DataPath, target);
                try
                {
                    File.Delete(item.MetaFile);
                }
                catch (Exception ex)
                {
                    // 数据已还原成功;元数据删除失败只造成幻影条目(下次枚举自然消失),
                    // 不能把它报成"还原失败"让用户误以为文件没还原。
                    App.Log("还原成功但删除回收站元数据失败: " + ex.Message);
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Log("还原回收站项目失败: " + ex.Message);
                return false;
            }
        }
    }
}
