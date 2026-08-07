using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Parser for the community-translation locale files (Task113, "Locales/en.txt" scheme).
    ///
    /// File format (UTF-8 text, one entry per line):
    ///   key = value
    /// - Whitespace around the key and value is trimmed.
    /// - Blank lines and lines starting with '#' are comments.
    /// - The value may contain "\n" escapes for line breaks (written back by the template
    ///   generator the same way, so a file round-trips).
    /// - The first '=' splits key from value ('=' may appear freely inside the value).
    /// - Later entries for the same key win (last one wins; simplest rule for hand-edited files).
    ///
    /// Pure logic, no file IO (reading is the Game layer's responsibility: LocaleLoader), no
    /// UnityEngine dependency, deterministic.
    /// </summary>
    public static class LocaleFileParser
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue; // no separator, or empty key: ignore the line

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                result[key] = Unescape(value);
            }
            return result;
        }

        /// <summary>Turns the "\n" escape into a real newline ("\\n" stays a literal backslash-n).</summary>
        public static string Unescape(string value)
        {
            if (value == null || value.IndexOf('\\') < 0) return value;

            var sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == 'n') { sb.Append('\n'); i++; continue; }
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The inverse of Unescape, used by the template generator (LocaleLoader.WriteTemplate)
        /// so that multi-line defaults survive the one-entry-per-line format.</summary>
        public static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\r\n", "\\n").Replace("\n", "\\n");
        }
    }
}
