using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

namespace alpha_island.Core;

/// <summary>只读取当前拖放载荷里的真实文件列表，不用剪切板或文本猜测拖拽内容。</summary>
internal static class FileDropPayload
{
    public static bool TryRead(IDataObject data, out string[] paths)
    {
        paths = Array.Empty<string>();
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                || data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] candidates)
                return false;

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
                string path;
                try { path = Path.GetFullPath(candidate); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (File.Exists(path) || Directory.Exists(path)) result.Add(path);
            }
            paths = new string[result.Count];
            result.CopyTo(paths);
            return paths.Length > 0;
        }
        catch (Exception)
        {
            // 外部程序提供的 OLE 数据对象可能拒绝读取或已失效，按不可接收处理。
            return false;
        }
    }

    // 中转站暂存的是路径，尚未移动文件。向来源确认 Copy 可避免来源提前删除原文件；
    // 复制/剪切分区只决定稍后向真正目标拖出时的操作。
    public static DragDropEffects StagingEffect(DragDropEffects allowed)
        => (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
}
