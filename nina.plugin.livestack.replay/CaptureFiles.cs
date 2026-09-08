using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Plugin.Livestack.Replay {
    public static class CaptureFiles {
        private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}[_T]\d{2}[-:]\d{2}[-:]\d{2}", RegexOptions.Compiled);

        public static string[] Find(IEnumerable<string> paths) {
            return paths.SelectMany(Expand).Where(IsCapture).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(CaptureTime)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IEnumerable<string> Expand(string path) {
            path = path.Trim().Trim('"');
            if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", new EnumerationOptions {
                RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
            });
            if (File.Exists(path)) return new[] { path };
            throw new FileNotFoundException("Capture path does not exist or is inaccessible.", path);
        }

        private static bool IsCapture(string path) {
            return path.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fit", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fits.fz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime CaptureTime(string path) {
            string timestamp = Timestamp.Match(Path.GetFileName(path)).Value;
            return DateTime.TryParseExact(timestamp, new[] { "yyyy-MM-dd_HH-mm-ss", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime time) ? time : File.GetLastWriteTime(path);
        }
    }
}
