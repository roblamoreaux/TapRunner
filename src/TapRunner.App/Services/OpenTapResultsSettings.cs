using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TapRunner.App.Services
{
    /// <summary>
    /// Reads/writes the database file of the SQLite result listener in OpenTAP's own results
    /// settings ("&lt;tap.exe folder&gt;\Settings\Results.xml"), so it can be changed from TapRunner's
    /// Settings. Only that one FilePath value is touched; the rest of the file is left as it is.
    /// </summary>
    public static class OpenTapResultsSettings
    {
        public static string GetResultsXmlPath(string tapExePath)
        {
            var tapDir = Path.GetDirectoryName(tapExePath ?? "");
            return string.IsNullOrEmpty(tapDir) ? "" : Path.Combine(tapDir, "Settings", "Results.xml");
        }

        /// <summary>
        /// The first SQLite listener's FilePath, or null (with the reason in <paramref name="problem"/>)
        /// if Results.xml or a SQLite listener in it can't be found.
        /// </summary>
        public static string ReadSqliteDatabasePath(string resultsXmlPath, out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(resultsXmlPath))
                {
                    problem = $"{resultsXmlPath} was not found.";
                    return null;
                }

                var filePath = FindSqliteFilePath(XDocument.Load(resultsXmlPath));
                if (filePath == null)
                    problem = "No SQLite result listener is set up in OpenTAP's Results settings - add one in OpenTAP first.";
                return filePath?.Value;
            }
            catch (Exception ex)
            {
                problem = $"Could not read {resultsXmlPath}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Sets the first SQLite listener's FilePath. Keeps a copy of the previous file as
        /// Results.xml.bak. Throws if the file or the listener can't be found or written.
        /// </summary>
        public static void WriteSqliteDatabasePath(string resultsXmlPath, string databasePath)
        {
            var doc = XDocument.Load(resultsXmlPath, LoadOptions.PreserveWhitespace);
            var filePath = FindSqliteFilePath(doc)
                ?? throw new InvalidOperationException("No SQLite result listener is set up in OpenTAP's Results settings.");
            filePath.Value = databasePath;

            File.Copy(resultsXmlPath, resultsXmlPath + ".bak", overwrite: true);

            // Same encoding (UTF-8 with BOM) and CRLF line endings OpenTAP writes it with.
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(true),
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
            };
            using var writer = XmlWriter.Create(resultsXmlPath, settings);
            doc.Save(writer);
        }

        static XElement FindSqliteFilePath(XDocument doc)
            => doc.Root?.Elements()
                .Where(e => ((string)e.Attribute("type") ?? "").EndsWith(".SQLiteDatabase", StringComparison.Ordinal))
                .Select(e => e.Element("FilePath"))
                .FirstOrDefault(e => e != null);
    }
}
