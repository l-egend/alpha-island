using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace alpha_island.Interop
{
    /// <summary>
    /// 只读查询 Windows 自带的 <c>winsqlite3.dll</c>（Win10 17063+ / Win11 的 System32 里就有），
    /// 用途是读 Chrome / Edge 的 <c>History</c>（下载表 <c>downloads</c>）。
    ///
    /// 为什么不用 NuGet 的 Microsoft.Data.Sqlite：只为读 5 个字段就拖进一整套原生库
    /// （SQLitePCLRaw + e_sqlite3.dll，多平台分发）不划算，而 winsqlite3 是系统自带、免分发。
    /// 代价：它是**没有文档保证的私有导出**，所以这里做两件事自我保护：
    /// ① 加载失败 / 查询失败一律返回 false + 错误字符串，调用方降级（不显示浏览器下载），绝不抛到界面；
    /// ② 只调最老、最稳定的那批 C API（open_v2 / prepare_v2 / step / column_* / finalize / close）。
    /// </summary>
    internal static class WinSqlite
    {
        private const string Dll = "winsqlite3.dll";

        private const int Ok = 0;
        private const int Row = 100;

        /// <summary>SQLITE_OPEN_READONLY。只读打开，绝不改别人的库。</summary>
        private const int OpenReadOnly = 0x0000_0001;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

        /// <summary>
        /// 只读执行一条查询。成功时 <paramref name="rows"/> 是「每行 = 各列文本」的列表
        /// （NULL 用 null 表示），失败时返回 false 并给出 <paramref name="error"/>。
        /// </summary>
        public static bool TryQuery(string dbPath, string sql, int maxRows, out List<string?[]> rows, out string? error)
        {
            rows = new List<string?[]>();
            error = null;

            if (string.IsNullOrWhiteSpace(dbPath) || !System.IO.File.Exists(dbPath))
            {
                error = "数据库文件不存在";
                return false;
            }

            IntPtr db = IntPtr.Zero;
            IntPtr stmt = IntPtr.Zero;
            try
            {
                int rc = sqlite3_open_v2(Utf8Z(dbPath), out db, OpenReadOnly, IntPtr.Zero);
                if (rc != Ok)
                {
                    error = $"open rc={rc} {Err(db)}";
                    return false;
                }

                // 浏览器正在跑的时候库被它独占，等一下再放弃（拿不到就由调用方去读副本）。
                // 只等 60ms：prepared 拿不到锁时 prepare 与 step 各要等一次，300ms 就是每拍 ~600ms
                // 的纯等待（alpha_74 实测 downloads 采样因此要 812–843ms/拍）；副本兜底已经足够可靠，
                // 没有必要在这里干等。
                sqlite3_busy_timeout(db, 60);

                rc = sqlite3_prepare_v2(db, Utf8Z(sql), -1, out stmt, IntPtr.Zero);
                if (rc != Ok)
                {
                    error = $"prepare rc={rc} {Err(db)}";
                    return false;
                }

                int guard = Math.Max(1, maxRows);
                while (sqlite3_step(stmt) == Row && rows.Count < guard)
                {
                    var cols = new string?[16];
                    for (int i = 0; i < cols.Length; i++) cols[i] = ColumnText(stmt, i);
                    rows.Add(cols);
                }

                return true;
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException 都会落到这里。
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        /// <summary>这一列是不是数值类型（INTEGER）。文本列取文本、数值列取 int64。</summary>
        public static long ColumnInt64(string?[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return 0;
            string? text = row[index];
            if (string.IsNullOrEmpty(text)) return 0;
            return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long value)
                ? value
                : 0;
        }

        public static string ColumnText(string?[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return string.Empty;
            return row[index] ?? string.Empty;
        }

        private static string? ColumnText(IntPtr stmt, int index)
        {
            IntPtr ptr = sqlite3_column_text(stmt, index);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        }

        private static string Err(IntPtr db)
        {
            if (db == IntPtr.Zero) return "(no db)";
            IntPtr ptr = sqlite3_errmsg(db);
            return ptr == IntPtr.Zero ? "(no msg)" : (Marshal.PtrToStringUTF8(ptr) ?? "(no msg)");
        }

        private static byte[] Utf8Z(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var z = new byte[bytes.Length + 1];
            Array.Copy(bytes, z, bytes.Length);
            return z;
        }
    }
}
