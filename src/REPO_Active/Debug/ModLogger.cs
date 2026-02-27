using System;
#if WITH_FILE_LOG
using System.IO;
using System.Text;
using System.Linq;
#endif
using BepInEx;
using BepInEx.Logging;

namespace REPO_Active.Debug
{
    public sealed class ModLogger
    {
#if WITH_FILE_LOG
        private const int MaxLogFiles = 20;
        private readonly object _lock = new object();
        private string _filePath = "";
        private string _sessionId = "NA";
#endif

        public bool Enabled { get; set; }

        public ModLogger(ManualLogSource log, bool enabled)
        {
            Enabled = enabled;
#if WITH_FILE_LOG
            if (Enabled) InitFile();
#endif
        }

        public void SetSession(string sessionId)
        {
#if WITH_FILE_LOG
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _sessionId = "NA";
                return;
            }

            _sessionId = sessionId;
#else
            _ = sessionId;
#endif
        }

#if WITH_FILE_LOG
        private void InitFile()
        {
            try
            {
                string dir = Path.Combine(Paths.ConfigPath, "REPO_Active", "logs");
                Directory.CreateDirectory(dir);

                string name = $"REPO_Active_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                _filePath = Path.Combine(dir, name);

                lock (_lock)
                {
                    File.AppendAllText(_filePath, $"[FileLog] {_filePath}{Environment.NewLine}", Encoding.UTF8);
                }

                CleanupOldLogs(dir, _filePath);
            }
            catch
            {
                // file logging is optional; don't crash if it fails
            }
        }

        private static void CleanupOldLogs(string dir, string currentFilePath)
        {
            try
            {
                var files = new DirectoryInfo(dir)
                    .GetFiles("REPO_Active_*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();

                for (int i = MaxLogFiles; i < files.Length; i++)
                {
                    var file = files[i];
                    if (string.Equals(file.FullName, currentFilePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    file.Delete();
                }
            }
            catch
            {
                // cleanup failure should not affect gameplay
            }
        }
#endif

        public void Log(string message)
        {
#if WITH_FILE_LOG
            if (!Enabled) return;
            if (string.IsNullOrEmpty(_filePath))
            {
                InitFile();
                if (string.IsNullOrEmpty(_filePath)) return;
            }
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_filePath, $"[S:{_sessionId}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // swallow logging errors to avoid breaking gameplay
            }
#else
            _ = message;
#endif
        }
    }
}