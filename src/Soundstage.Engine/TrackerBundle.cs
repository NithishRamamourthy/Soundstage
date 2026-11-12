using System;
using System.IO;
using System.Reflection;

namespace Soundstage.Engine
{
    /// <summary>
    /// The bundled copy of sony-head-tracker.exe (MIT, see NOTICE.md). It is embedded in
    /// this assembly, so the single-file app carries it and nothing has to be downloaded
    /// separately. On first use it is unpacked to %LocalAppData%\Soundstage\tools.
    /// </summary>
    public static class TrackerBundle
    {
        const string ResourceName = "Soundstage.Engine.Assets.sony-head-tracker.exe";
        const string FileName = "sony-head-tracker.exe";

        static readonly object Gate = new object();
        static string _cached;

        /// <summary>Path to the bundled tracker, or null if it could not be unpacked.</summary>
        public static string Ensure()
        {
            lock (Gate)
            {
                if (_cached != null && File.Exists(_cached)) return _cached;
                try
                {
                    Assembly asm = typeof(TrackerBundle).Assembly;
                    using (Stream src = asm.GetManifestResourceStream(ResourceName))
                    {
                        if (src == null) return null;

                        string dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Soundstage", "tools");
                        Directory.CreateDirectory(dir);
                        string target = Path.Combine(dir, FileName);

                        if (!IsCurrent(src, target))
                        {
                            src.Position = 0;
                            string temp = target + ".tmp";
                            using (FileStream dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                src.CopyTo(dst);
                            }
                            File.Move(temp, target, true);
                        }

                        _cached = target;
                        return target;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        static bool IsCurrent(Stream src, string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                return fi.Exists && fi.Length == src.Length;
            }
            catch
            {
                return false;
            }
        }
    }
}
