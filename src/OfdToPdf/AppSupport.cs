using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OfdToPdf
{
    /// <summary>Where the app keeps fonts, settings and its log.</summary>
    public static class AppPaths
    {
        /// <summary>%LocalAppData%\OfdToPdf — always writable, unlike Program Files.</summary>
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfdToPdf");

        /// <summary>Portable fonts folder shipped next to the exe in the release zip.</summary>
        public static readonly string ExeFontDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");

        public static readonly string UserFontDir = Path.Combine(DataDir, "fonts");
        public static readonly string LogFile = Path.Combine(DataDir, "log.txt");
        public static readonly string SettingsFile = Path.Combine(DataDir, "settings.ini");

        public static IEnumerable<string> ExistingFontDirs()
        {
            return new[] { ExeFontDir, UserFontDir }.Where(Directory.Exists);
        }

        /// <summary>
        /// The fonts folder to show the user: the one next to the exe if it exists and is writable,
        /// otherwise the per-user one (created on demand).
        /// </summary>
        public static string FontDirForUser()
        {
            if (Directory.Exists(ExeFontDir) && IsWritable(ExeFontDir)) return ExeFontDir;
            Directory.CreateDirectory(UserFontDir);
            return UserFontDir;
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public enum ConflictMode { Rename, Overwrite, Skip }

    /// <summary>User preferences, remembered between runs in a small key=value file.</summary>
    public sealed class AppSettings
    {
        public bool SaveNextToSource = true;
        public string OutputDir = "";
        public ConflictMode Conflict = ConflictMode.Rename;
        public bool OpenFolderWhenDone = false;
        public int WindowWidth = 0;
        public int WindowHeight = 0;

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return s;
                foreach (string line in File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    bool b; int n; ConflictMode c;
                    switch (key)
                    {
                        case "SaveNextToSource": if (bool.TryParse(value, out b)) s.SaveNextToSource = b; break;
                        case "OutputDir": s.OutputDir = value; break;
                        case "Conflict": if (Enum.TryParse(value, out c)) s.Conflict = c; break;
                        case "OpenFolderWhenDone": if (bool.TryParse(value, out b)) s.OpenFolderWhenDone = b; break;
                        case "WindowWidth": if (int.TryParse(value, out n)) s.WindowWidth = n; break;
                        case "WindowHeight": if (int.TryParse(value, out n)) s.WindowHeight = n; break;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable settings are not worth bothering the user about; use defaults.
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllLines(AppPaths.SettingsFile, new[]
                {
                    "SaveNextToSource=" + SaveNextToSource,
                    "OutputDir=" + OutputDir,
                    "Conflict=" + Conflict,
                    "OpenFolderWhenDone=" + OpenFolderWhenDone,
                    "WindowWidth=" + WindowWidth,
                    "WindowHeight=" + WindowHeight,
                }, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Local, append-only record of conversions and errors (never sent anywhere).</summary>
    public static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = Path.Combine(AppPaths.DataDir, "log.old.txt");
                        File.Delete(old);
                        File.Move(AppPaths.LogFile, old);
                    }
                    File.AppendAllText(AppPaths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception)
                {
                    // Logging must never break a conversion.
                }
            }
        }
    }
}
