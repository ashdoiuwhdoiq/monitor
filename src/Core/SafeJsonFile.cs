using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 用户数据文件（设置、流量、硬件趋势）的安全读写。
    /// 1. 读：文件存在却读不出来时，先另存一份 *.unreadable-时间戳 再降级，原始数据永不被覆盖丢失；
    ///    连另存都失败（比如文件被占用），本次运行禁止写入该文件。
    /// 2. 写：先完整写入并刷盘临时文件，再备份旧文件，最后原子替换。
    /// </summary>
    public sealed class SafeJsonFile
    {
        private const int ReadRetries = 3;

        public string FilePath { get; }
        public string BackupPath { get; }
        public string TempPath { get; }

        /// <summary>读取阶段发现无法安全保护原文件时为 true，此后 Write 一律跳过。</summary>
        public bool SaveBlocked { get; private set; }

        public SafeJsonFile(string fileName)
        {
            FilePath = Path.Combine(AppContext.BaseDirectory, fileName);
            BackupPath = FilePath + ".bak";
            TempPath = FilePath + ".tmp";
        }

        /// <summary>依次尝试主文件和 .bak；都不可用时返回 null，由调用方决定默认值。</summary>
        public T? Load<T>(JsonSerializerOptions? options = null) where T : class
        {
            SaveBlocked = false;
            foreach (string path in new[] { FilePath, BackupPath })
            {
                if (!File.Exists(path)) continue;

                if (TryRead(path, options, out T? value)) return value;

                // 文件存在但读不出：先保住原始字节，再继续尝试下一个来源
                if (!Quarantine(path)) SaveBlocked = true;
            }
            return null;
        }

        public bool Write(string json)
        {
            if (SaveBlocked)
            {
                Debug.WriteLine($"[SafeJsonFile] 已阻止写入（原文件未能安全保存）：{FilePath}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(json)) return false;

            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, BackupPath, true); }
                catch (Exception ex) { Debug.WriteLine($"[SafeJsonFile] 写入备份失败: {ex.Message}"); }
            }

            File.Move(TempPath, FilePath, true);
            return true;
        }

        public void CleanupTemp()
        {
            try
            {
                if (File.Exists(TempPath)) File.Delete(TempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SafeJsonFile] 清理临时文件失败: {ex.Message}");
            }
        }

        private static bool TryRead<T>(string path, JsonSerializerOptions? options, out T? value) where T : class
        {
            value = null;
            for (int attempt = 1; attempt <= ReadRetries; attempt++)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    value = JsonSerializer.Deserialize<T>(json, options);
                    return value != null;
                }
                catch (IOException) when (attempt < ReadRetries)
                {
                    // 被杀软或同步盘短暂占用，稍等重试，避免误判为损坏
                    Thread.Sleep(150 * attempt);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SafeJsonFile] 读取失败: {path}, {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        private static bool Quarantine(string path)
        {
            try
            {
                string target = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                if (File.Exists(target)) target += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.Copy(path, target, false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SafeJsonFile] 另存损坏文件失败: {path}, {ex.Message}");
                return false;
            }
        }
    }
}
