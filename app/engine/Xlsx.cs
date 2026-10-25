// Excel workbooks (.xlsx, Office Open XML): the first worksheet is turned into a CSV file, which the CSV import then
// reads like any other. Cells with a date format become "yyyy-MM-dd HH:mm:ss" (Excel keeps them as day numbers).
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

        static XmlReader Open(ZipArchive zip, string path)
        {
            ZipArchiveEntry e = zip.GetEntry(path);
            if (e == null) return null;
            return XmlReader.Create(e.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false });
        }

        // Writes the first worksheet of `xlsx` to `csv`; returns the number of rows.
        public static int ToCsv(string xlsx, string csv)
        {
            using (var zip = ZipFile.OpenRead(xlsx))
            {
                string sheetPath = FirstSheet(zip);
                bool date1904;
                ReadWorkbookFlags(zip, out date1904);
                List<string> shared = SharedStrings(zip);
                List<bool> dateStyles = DateStyles(zip);
                using (XmlReader r = Open(zip, sheetPath))
                using (var w = new StreamWriter(csv, false, new UTF8Encoding(false)))
                {
                    if (r == null) throw new InvalidDataException("The workbook has no worksheet.");
                    return WriteRows(r, w, shared, dateStyles, date1904);
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
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet") { rid = r.GetAttribute("id", Rel); break; }
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

        // Read node by node: ReadElementContentAsString moves past the element, and the next Read would skip a sibling.
        static List<string> SharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            using (XmlReader r = Open(zip, "xl/sharedStrings.xml"))
            {
                if (r == null) return list;
                StringBuilder sb = null;
                bool inT = false;
                int skip = 0;   // inside a phonetic guide (rPh), whose text is not part of the string
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        if (r.LocalName == "si" && !r.IsEmptyElement) sb = new StringBuilder();
                        else if (r.LocalName == "si") list.Add("");
                        else if (r.LocalName == "rPh" && !r.IsEmptyElement) skip++;
                        else if (r.LocalName == "t" && !r.IsEmptyElement) inT = true;
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        if (r.LocalName == "t") inT = false;
                        else if (r.LocalName == "rPh") skip--;
                        else if (r.LocalName == "si" && sb != null) { list.Add(sb.ToString()); sb = null; }
                    }
                    else if (inT && skip == 0 && sb != null && IsText(r)) sb.Append(r.Value);
                }
            }
            return list;
        }

        // Built-in number formats that are dates or times, including the ones Korean, Japanese and Chinese Excel use.
        static bool BuiltInDate(int id) { return (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58); }

        static readonly Regex Quoted = new Regex("\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", RegexOptions.CultureInvariant);

        public static bool IsDateFormat(string code)
        {
            string c = Quoted.Replace(code ?? "", "").ToLowerInvariant();
            if (c.Contains("general")) return false;
            if (c.IndexOfAny(new[] { 'y', 'd', 'h', 's' }) >= 0) return true;
            return c.Contains("m") && c.IndexOfAny(new[] { '0', '#' }) < 0;
        }

        static List<bool> DateStyles(ZipArchive zip)
        {
            var custom = new Dictionary<int, bool>();
            var styles = new List<bool>();
            using (XmlReader r = Open(zip, "xl/styles.xml"))
            {
                if (r == null) return styles;
                bool inXfs = false;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "numFmt")
                    {
                        int id;
                        if (int.TryParse(r.GetAttribute("numFmtId"), out id)) custom[id] = IsDateFormat(r.GetAttribute("formatCode"));
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "cellXfs") inXfs = !r.IsEmptyElement;
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs") inXfs = false;
                    else if (inXfs && r.NodeType == XmlNodeType.Element && r.LocalName == "xf")
                    {
                        int id;
                        int.TryParse(r.GetAttribute("numFmtId"), out id);
                        bool date;
                        styles.Add(custom.TryGetValue(id, out date) ? date : BuiltInDate(id));
                    }
                }
            }
            return styles;
        }

        static int ColumnIndex(string cellRef)
        {
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (ch < 'A' || ch > 'Z') break;
                col = col * 26 + (ch - 'A' + 1);
            }
            return col - 1;
        }

        static string DateText(double serial, bool date1904)
        {
            DateTime b = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
            DateTime d = b.AddMilliseconds(Math.Round(serial * 86400000.0));
            return d.ToString(d.Millisecond != 0 ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        static int WriteRows(XmlReader r, StreamWriter w, List<string> shared, List<bool> dateStyles, bool date1904)
        {
            int rows = 0;
            var cells = new List<string>();
            int col = 0;
            string type = null, style = null;
            var value = new StringBuilder();
            bool inValue = false, inCell = false;
            int skip = 0, width = 0;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "dimension":
                            string dim = r.GetAttribute("ref") ?? "";
                            int colon = dim.IndexOf(':');
                            if (colon > 0) width = ColumnIndex(dim.Substring(colon + 1)) + 1;
                            break;
                        case "row":
                            cells.Clear();
                            if (r.IsEmptyElement) { WriteCsv(w, cells); rows++; }
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
                        Put(cells, col, CellText(value.ToString(), type, style, shared, dateStyles, date1904));
                        inCell = false;
                    }
                    else if (r.LocalName == "row")
                    {
                        while (cells.Count < width) cells.Add("");
                        WriteCsv(w, cells);
                        rows++;
                    }
                }
                else if (inValue && skip == 0 && IsText(r)) value.Append(r.Value);
            }
            return rows;
        }

        static void Put(List<string> cells, int col, string text)
        {
            while (cells.Count < col) cells.Add("");
            if (col == cells.Count) cells.Add(text); else if (col < cells.Count) cells[col] = text;
        }

        static string CellText(string value, string type, string style, List<string> shared, List<bool> dateStyles, bool date1904)
        {
            if (type == "s") { int i; return int.TryParse(value, out i) && i >= 0 && i < shared.Count ? shared[i] : ""; }
            if (type == "inlineStr" || type == "str") return value;
            if (type == "b") return value == "1" ? "1" : "0";
            if (type == "e") return "";
            int si;
            double d;
            bool isDate = int.TryParse(style, out si) && si >= 0 && si < dateStyles.Count && dateStyles[si];
            if (value.Length > 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return isDate ? DateText(d, date1904) : d.ToString("R", CultureInfo.InvariantCulture);
            return value;
        }

        static void WriteCsv(StreamWriter w, List<string> cells)
        {
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
