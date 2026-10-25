// Excel workbooks (.xlsx, Office Open XML): the first worksheet is turned into a CSV file, which the CSV import then
// reads like any other. Cells with a date format become "yyyy-MM-dd HH:mm:ss" (Excel keeps them as day numbers).
// A workbook is a ZIP file: what it unpacks to is limited, so a small crafted file cannot fill the disk or memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Hoodata
{
    public static class Xlsx
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string StrictRel = "http://purl.oclc.org/ooxml/officeDocument/relationships";   // "Strict Open XML" saves

        static XmlReader Open(ZipArchive zip, string path)
        {
            ZipArchiveEntry e = zip.GetEntry(path);
            if (e == null) return null;
            return XmlReader.Create(e.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false });
        }

        // The CSV may be this much larger than the workbook (real sheets give a few times its size), and the shared
        // strings this long in all, each counting 16 characters more for itself; a cell holds at most what Excel
        // allows (32767 characters), and a workbook has at most as many cell formats as Excel's limit.
        const long CsvBase = 64L * 1024 * 1024, CsvPerByte = 40;
        const long SharedBase = 16L * 1024 * 1024, SharedPerByte = 20, MaxSharedChars = 256L * 1024 * 1024;
        const int MaxCellChars = 32767, MaxStyles = 65536;

        // Writes the first worksheet of `xlsx` to `csv`; returns the number of rows.
        public static int ToCsv(string xlsx, string csv)
        {
            long size = new FileInfo(xlsx).Length;
            long limit = CsvBase + CsvPerByte * size;
            using (var zip = ZipFile.OpenRead(xlsx))
            {
                string sheetPath = FirstSheet(zip);
                bool date1904;
                ReadWorkbookFlags(zip, out date1904);
                List<string> shared = SharedStrings(zip, Math.Min(MaxSharedChars, SharedBase + SharedPerByte * size));
                List<int> dateStyles = DateStyles(zip);
                using (XmlReader r = Open(zip, sheetPath))
                using (var w = new StreamWriter(csv, false, new UTF8Encoding(false)))
                {
                    if (r == null) throw new InvalidDataException("The workbook has no worksheet.");
                    return WriteRows(r, w, shared, dateStyles, date1904, limit);
                }
            }
        }

        static string FirstSheet(ZipArchive zip)
        {
            string rid = null;
            using (XmlReader r = Open(zip, "xl/workbook.xml"))
            {
                if (r == null) throw new InvalidDataException("Not an Excel workbook.");
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet") { rid = r.GetAttribute("id", Rel) ?? r.GetAttribute("id", StrictRel); break; }
            }
            if (rid == null) throw new InvalidDataException("The workbook has no worksheet.");
            using (XmlReader r = Open(zip, "xl/_rels/workbook.xml.rels"))
            {
                while (r != null && r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") == rid)
                    {
                        string target = r.GetAttribute("Target").Replace('\\', '/');
                        return target.StartsWith("/") ? target.Substring(1) : "xl/" + target;
                    }
            }
            return "xl/worksheets/sheet1.xml";
        }

        static void ReadWorkbookFlags(ZipArchive zip, out bool date1904)
        {
            date1904 = false;
            using (XmlReader r = Open(zip, "xl/workbook.xml"))
                while (r != null && r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "workbookPr")
                    {
                        string v = r.GetAttribute("date1904");
                        date1904 = v == "1" || v == "true";
                        return;
                    }
        }

        static bool IsText(XmlReader r)
        {
            return r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA || r.NodeType == XmlNodeType.SignificantWhitespace || r.NodeType == XmlNodeType.Whitespace;
        }

        // Adds the text node's characters, up to a cell's limit, without holding a longer one in memory first.
        static void AppendText(XmlReader r, StringBuilder sb, char[] buf)
        {
            if (!r.CanReadValueChunk)
            {
                string v = r.Value;
                if (sb.Length < MaxCellChars) sb.Append(v, 0, Math.Min(v.Length, MaxCellChars - sb.Length));
                return;
            }
            int k;
            while ((k = r.ReadValueChunk(buf, 0, buf.Length)) > 0)
                if (sb.Length < MaxCellChars) sb.Append(buf, 0, Math.Min(k, MaxCellChars - sb.Length));
        }

        // Read node by node: ReadElementContentAsString moves past the element, and the next Read would skip a sibling.
        static List<string> SharedStrings(ZipArchive zip, long maxChars)
        {
            var list = new List<string>();
            using (XmlReader r = Open(zip, "xl/sharedStrings.xml"))
            {
                if (r == null) return list;
                StringBuilder sb = null;
                bool inT = false;
                int skip = 0;   // inside a phonetic guide (rPh), whose text is not part of the string
                long total = 0;
                var buf = new char[4096];
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        if (r.LocalName == "si" && !r.IsEmptyElement) sb = new StringBuilder();
                        else if (r.LocalName == "si")
                        {
                            if ((total += 16) > maxChars) throw new HttpError(400, "too_large");
                            list.Add("");
                        }
                        else if (r.LocalName == "rPh" && !r.IsEmptyElement) skip++;
                        else if (r.LocalName == "t" && !r.IsEmptyElement) inT = true;
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        if (r.LocalName == "t") inT = false;
                        else if (r.LocalName == "rPh") skip--;
                        else if (r.LocalName == "si" && sb != null)
                        {
                            string text = sb.ToString();
                            if ((total += text.Length + 16) > maxChars) throw new HttpError(400, "too_large");
                            list.Add(text);
                            sb = null;
                        }
                    }
                    else if (inT && skip == 0 && sb != null && IsText(r)) AppendText(r, sb, buf);
                }
            }
            return list;
        }

        // What a number format shows: 0 a number, 1 a date, 2 a time of day, 3 a date and time, 4 a date or a time
        // (told by the value).
        public const int NotDate = 0, DateOnly = 1, TimeOnly = 2, DateAndTime = 3, DateOrTime = 4;

        // Built-in formats. 27-36 and 50-58 are the Korean, Japanese and Chinese ones, and each language has its own
        // meaning for them (34 is a date in Korean, a time in Chinese): the value tells.
        static int BuiltInKind(int id)
        {
            if (id >= 14 && id <= 17) return DateOnly;
            if ((id >= 18 && id <= 21) || (id >= 45 && id <= 47)) return TimeOnly;
            if (id == 22) return DateAndTime;
            if ((id >= 27 && id <= 36) || (id >= 50 && id <= 58)) return DateOrTime;
            return NotDate;
        }

        static readonly Regex Quoted = new Regex("\"[^\"]*\"|\\\\.|\\[\\$[^\\]]*\\]|\\[(?![hms]+\\])[^\\]]*\\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static int FormatKind(string code)
        {
            string c = Quoted.Replace(code ?? "", "").ToLowerInvariant();
            if (c.Contains("general")) return NotDate;
            bool time = c.IndexOfAny(new[] { 'h', 's' }) >= 0 || c.Contains("am/pm") || c.Contains("a/p");
            bool date = c.IndexOfAny(new[] { 'y', 'd' }) >= 0 || c.Contains("mmm") || (c.Contains("m") && !time && c.IndexOfAny(new[] { '0', '#' }) < 0);
            return date && time ? DateAndTime : date ? DateOnly : time ? TimeOnly : NotDate;
        }

        public static bool IsDateFormat(string code) { return FormatKind(code) != NotDate; }

        static List<int> DateStyles(ZipArchive zip)
        {
            var custom = new Dictionary<int, int>();
            var styles = new List<int>();
            using (XmlReader r = Open(zip, "xl/styles.xml"))
            {
                if (r == null) return styles;
                bool inXfs = false;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "numFmt")
                    {
                        int id;
                        if (int.TryParse(r.GetAttribute("numFmtId"), out id)) custom[id] = FormatKind(r.GetAttribute("formatCode"));
                        if (custom.Count > MaxStyles) throw new HttpError(400, "too_large");
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "cellXfs") inXfs = !r.IsEmptyElement;
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs") inXfs = false;
                    else if (inXfs && r.NodeType == XmlNodeType.Element && r.LocalName == "xf")
                    {
                        int id;
                        int.TryParse(r.GetAttribute("numFmtId"), out id);
                        int kind;
                        styles.Add(custom.TryGetValue(id, out kind) ? kind : BuiltInKind(id));
                        if (styles.Count > MaxStyles) throw new HttpError(400, "too_large");
                    }
                }
            }
            return styles;
        }

        // The column of a cell reference (A1 -> 0). Excel's last column is XFD (16384); beyond that is not a sheet.
        static int ColumnIndex(string cellRef)
        {
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (ch < 'A' || ch > 'Z') break;
                col = col * 26 + (ch - 'A' + 1);
                if (col > 16384) throw new InvalidDataException("A cell lies beyond Excel's last column.");
            }
            return col - 1;
        }

        // A date as 2026-10-25, a time of day as 13:05:00, both as 2026-10-25 13:05:00: so a sheet with separate date
        // and time columns gives the CSV import a date column and a time column, which it joins. What the format
        // shows is not always all the cell holds (a full time shown as a date): then both are written.
        static string DateText(double serial, bool date1904, int kind)
        {
            long ms = (long)Math.Round(serial * 86400000.0);
            bool timeOfDay = ms >= 0 && ms < 86400000L, wholeDay = ms % 86400000L == 0;
            if ((kind == TimeOnly || kind == DateOrTime) && timeOfDay)
            {
                DateTime t = new DateTime(2000, 1, 1).AddMilliseconds(ms);
                return t.ToString(t.Millisecond != 0 ? "HH:mm:ss.fff" : "HH:mm:ss", CultureInfo.InvariantCulture);
            }
            DateTime b = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
            DateTime d = b.AddMilliseconds(ms);
            if ((kind == DateOnly || kind == DateOrTime) && wholeDay) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return d.ToString(d.Millisecond != 0 ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // A cell of type "d" (Strict Open XML, or a date written as text): ISO 8601, a date, a time or both, with up to
        // 20 decimals. As a day number from 1899-12-30, or NaN.
        static readonly Regex IsoDate = new Regex(@"^(?:(\d{4})-(\d{2})-(\d{2}))?T?(?:(\d{2}):(\d{2})(?::(\d{2})(\.\d+)?)?)?(?:Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant);

        static double IsoSerial(string text)
        {
            Match m = IsoDate.Match(text.Trim());
            if (!m.Success || (!m.Groups[1].Success && !m.Groups[4].Success)) return double.NaN;
            double days = 0;
            if (m.Groups[1].Success)
            {
                DateTime d;
                if (!DateTime.TryParseExact(m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return double.NaN;
                // A time of day alone is dated 1899-12-30 or 1899-12-31 (Excel's day 0).
                if (d.Year >= 1900) days = (d - new DateTime(1899, 12, 30)).TotalDays;
            }
            if (m.Groups[4].Success)
            {
                double sec = int.Parse(m.Groups[4].Value) * 3600 + int.Parse(m.Groups[5].Value) * 60;
                if (m.Groups[6].Success) sec += int.Parse(m.Groups[6].Value);
                if (m.Groups[7].Success) sec += Math.Round(double.Parse("0" + m.Groups[7].Value, CultureInfo.InvariantCulture), 3);
                days += sec / 86400.0;
            }
            return days;
        }

        static int WriteRows(XmlReader r, StreamWriter w, List<string> shared, List<int> dateStyles, bool date1904, long limit)
        {
            int rows = 0;
            var cells = new List<string>();
            int col = 0;
            string type = null, style = null;
            var value = new StringBuilder();
            var buf = new char[4096];
            bool inValue = false, inCell = false;
            int skip = 0, width = 0;
            long rowChars = 0;   // in the row so far: the limit holds within a row too
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "dimension":
                            string dim = r.GetAttribute("ref") ?? "";
                            int colon = dim.IndexOf(':');
                            // Padding to the sheet's width, unless it is wider than any table HOODATA takes (formatting
                            // alone can make a sheet's dimension reach the last column).
                            if (colon > 0)
                            {
                                int wd = ColumnIndex(dim.Substring(colon + 1)) + 1;
                                width = wd <= CsvAnalyzer.MaxColumns ? wd : 0;
                            }
                            break;
                        case "row":
                            cells.Clear();
                            rowChars = 0;
                            if (r.IsEmptyElement) { WriteCsv(w, cells, limit); rows++; }
                            break;
                        case "c":
                            string reference = r.GetAttribute("r");
                            col = reference != null ? ColumnIndex(reference) : cells.Count;
                            type = r.GetAttribute("t");
                            style = r.GetAttribute("s");
                            value.Length = 0;
                            inCell = !r.IsEmptyElement;
                            if (!inCell) Put(cells, col, "");
                            break;
                        case "v":
                        case "t":
                            inValue = inCell && !r.IsEmptyElement;
                            break;
                        case "rPh":
                            if (!r.IsEmptyElement) skip++;   // a phonetic guide inside an inline string
                            break;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "v" || r.LocalName == "t") inValue = false;
                    else if (r.LocalName == "rPh") skip--;
                    else if (r.LocalName == "c" && inCell)
                    {
                        string text = CellText(value.ToString(), type, style, shared, dateStyles, date1904);
                        if (w.BaseStream.Position + (rowChars += text.Length + 1) > limit) throw new HttpError(400, "too_large");
                        Put(cells, col, text);
                        inCell = false;
                    }
                    else if (r.LocalName == "row")
                    {
                        while (cells.Count < width) cells.Add("");
                        WriteCsv(w, cells, limit);
                        rows++;
                    }
                }
                else if (inValue && skip == 0 && IsText(r)) AppendText(r, value, buf);
            }
            return rows;
        }

        static void Put(List<string> cells, int col, string text)
        {
            if (col >= CsvAnalyzer.MaxColumns)
            {
                if (text.Length == 0) return;
                throw new HttpError(400, "too_many_columns");
            }
            while (cells.Count < col) cells.Add("");
            if (col == cells.Count) cells.Add(text); else if (col < cells.Count) cells[col] = text;
        }

        static string CellText(string value, string type, string style, List<string> shared, List<int> dateStyles, bool date1904)
        {
            if (type == "s") { int i; return int.TryParse(value, out i) && i >= 0 && i < shared.Count ? shared[i] : ""; }
            if (type == "inlineStr" || type == "str") return value;
            if (type == "b") return value == "1" ? "1" : "0";
            if (type == "e") return "";
            int si;
            double d;
            int kind = int.TryParse(style, out si) && si >= 0 && si < dateStyles.Count ? dateStyles[si] : NotDate;
            if (type == "d")
            {
                d = IsoSerial(value);
                return double.IsNaN(d) ? value : DateText(d, false, kind == NotDate ? DateOrTime : kind);
            }
            if (value.Length > 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return kind != NotDate ? DateText(d, date1904, kind) : d.ToString("R", CultureInfo.InvariantCulture);
            return value;
        }

        static void WriteCsv(StreamWriter w, List<string> cells, long limit)
        {
            if (w.BaseStream.Position > limit) throw new HttpError(400, "too_large");
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) w.Write(',');
                string c = cells[i];
                if (c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) c = "\"" + c.Replace("\"", "\"\"") + "\"";
                w.Write(c);
            }
            w.Write('\n');
        }
    }
}
