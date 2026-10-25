// SQLite through winsqlite3.dll, the copy that ships in System32 on Windows 10 and 11 (signed by Microsoft).
// It uses the stdcall convention. Only plain SQL is used, nothing that needs a recent SQLite.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Hoodata
{
    public sealed class SqliteException : Exception
    {
        public readonly int Code;
        public SqliteException(int code, string message) : base("SQLite " + code + ": " + message) { Code = code; }
    }

    static class Native
    {
        const string Lib = "winsqlite3";
        const CallingConvention Cc = CallingConvention.StdCall;

        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
        [DllImport(Lib, CallingConvention = Cc)] public static extern void sqlite3_free(IntPtr p);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nbyte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_reset(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_clear_bindings(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_bind_double(IntPtr stmt, int i, double v);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_bind_null(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] v, int n, IntPtr destructor);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern double sqlite3_column_double(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = Cc)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)] public static extern int sqlite3_changes(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr sqlite3_libversion();

        public static readonly IntPtr Transient = new IntPtr(-1);

        public static byte[] Utf8z(string s)
        {
            int n = Encoding.UTF8.GetByteCount(s);
            var b = new byte[n + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
            return b;
        }

        public static string FromUtf8(IntPtr p, int n)
        {
            if (p == IntPtr.Zero) return null;
            if (n < 0)
            {
                n = 0;
                while (Marshal.ReadByte(p, n) != 0) n++;
            }
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }
    }

    public sealed class SqliteDb : IDisposable
    {
        IntPtr h;
        const int OpenReadOnly = 0x1, OpenReadWrite = 0x2, OpenCreate = 0x4, OpenFullMutex = 0x10000;

        public static string Version { get { return Native.FromUtf8(Native.sqlite3_libversion(), -1); } }

        public SqliteDb(string path, bool readOnly)
        {
            int flags = (readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate) | OpenFullMutex;
            int rc = Native.sqlite3_open_v2(Native.Utf8z(path), out h, flags, IntPtr.Zero);
            if (rc != 0)
            {
                string msg = h != IntPtr.Zero ? Native.FromUtf8(Native.sqlite3_errmsg(h), -1) : "cannot open";
                if (h != IntPtr.Zero) Native.sqlite3_close_v2(h);
                h = IntPtr.Zero;
                throw new SqliteException(rc, msg + " (" + path + ")");
            }
            Native.sqlite3_busy_timeout(h, 10000);
        }

        internal IntPtr Handle { get { return h; } }

        internal SqliteException Error(int rc)
        {
            return new SqliteException(rc, Native.FromUtf8(Native.sqlite3_errmsg(h), -1));
        }

        public void Exec(string sql)
        {
            IntPtr err;
            int rc = Native.sqlite3_exec(h, Native.Utf8z(sql), IntPtr.Zero, IntPtr.Zero, out err);
            if (rc != 0)
            {
                string msg = Native.FromUtf8(err, -1);
                if (err != IntPtr.Zero) Native.sqlite3_free(err);
                throw new SqliteException(rc, msg);
            }
        }

        public SqliteStmt Prepare(string sql) { return new SqliteStmt(this, sql); }

        public long LastInsertRowId { get { return Native.sqlite3_last_insert_rowid(h); } }
        public int Changes { get { return Native.sqlite3_changes(h); } }

        public long ScalarLong(string sql, long fallback)
        {
            using (var st = Prepare(sql))
                return st.Step() && !st.IsNull(0) ? st.GetLong(0) : fallback;
        }

        public string ScalarText(string sql)
        {
            using (var st = Prepare(sql))
                return st.Step() ? st.GetText(0) : null;
        }

        public void Dispose()
        {
            if (h != IntPtr.Zero) { Native.sqlite3_close_v2(h); h = IntPtr.Zero; }
        }
    }

    public sealed class SqliteStmt : IDisposable
    {
        readonly SqliteDb db;
        IntPtr st;
        const int Row = 100, Done = 101;

        internal SqliteStmt(SqliteDb db, string sql)
        {
            this.db = db;
            int rc = Native.sqlite3_prepare_v2(db.Handle, Native.Utf8z(sql), -1, out st, IntPtr.Zero);
            if (rc != 0) throw db.Error(rc);
        }

        void Check(int rc) { if (rc != 0) throw db.Error(rc); }

        // Parameters are numbered from 1, as in SQLite.
        public SqliteStmt Bind(int i, long v) { Check(Native.sqlite3_bind_int64(st, i, v)); return this; }

        public SqliteStmt Bind(int i, double v)
        {
            if (double.IsNaN(v)) return BindNull(i);
            Check(Native.sqlite3_bind_double(st, i, v));
            return this;
        }

        public SqliteStmt Bind(int i, double? v) { return v.HasValue ? Bind(i, v.Value) : BindNull(i); }
        public SqliteStmt Bind(int i, long? v) { return v.HasValue ? Bind(i, v.Value) : BindNull(i); }

        public SqliteStmt Bind(int i, string v)
        {
            if (v == null) return BindNull(i);
            byte[] b = Encoding.UTF8.GetBytes(v);
            Check(Native.sqlite3_bind_text(st, i, b, b.Length, Native.Transient));
            return this;
        }

        public SqliteStmt BindNull(int i) { Check(Native.sqlite3_bind_null(st, i)); return this; }

        // True while there is a row to read.
        public bool Step()
        {
            int rc = Native.sqlite3_step(st);
            if (rc == Row) return true;
            if (rc == Done) return false;
            throw db.Error(rc);
        }

        public void Run() { while (Step()) { } Reset(); }

        public void Reset()
        {
            Native.sqlite3_reset(st);
            Native.sqlite3_clear_bindings(st);
        }

        public bool IsNull(int i) { return Native.sqlite3_column_type(st, i) == 5; }
        public long GetLong(int i) { return Native.sqlite3_column_int64(st, i); }
        public double GetDouble(int i) { return Native.sqlite3_column_double(st, i); }
        public double? GetDoubleOrNull(int i) { return IsNull(i) ? (double?)null : GetDouble(i); }
        public long? GetLongOrNull(int i) { return IsNull(i) ? (long?)null : GetLong(i); }

        public string GetText(int i)
        {
            IntPtr p = Native.sqlite3_column_text(st, i);
            if (p == IntPtr.Zero) return null;
            return Native.FromUtf8(p, Native.sqlite3_column_bytes(st, i));
        }

        public void Dispose()
        {
            if (st != IntPtr.Zero) { Native.sqlite3_finalize(st); st = IntPtr.Zero; }
        }
    }
}
