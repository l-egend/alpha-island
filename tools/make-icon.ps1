# Alpha Island 图标生成器（C 组①）
#
# 生成 Assets\alpha-island.ico：内置一套默认图标，用户可以直接换文件（或换 %LOCALAPPDATA%\alpha-island\config\icon.ico）。
#   尺寸：16 / 24 / 32 / 48 / 64 / 256
#   格式：每帧都是 32bpp DIB（BITMAPINFOHEADER + BGRA + AND 掩码）—— 故意不用 PNG 压缩帧：
#         System.Drawing.Icon 与 csc 的 /win32icon 对 PNG 帧的支持在不同版本上不稳，DIB 是最保险的。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
#   可选参数：-Preview <png 路径> 额外导出一张 256x256 预览图。

param(
    [string]$Output = "$PSScriptRoot\..\Assets\alpha-island.ico",
    [string]$Preview = "$PSScriptRoot\..\Assets\alpha-island-256.png",
    [string]$Source = "$PSScriptRoot\..\Assets\alpha-island-source.png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class AlphaIslandIcon
{
    public static string SourcePath;
    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.5f, radius * 2f);
        var p = new GraphicsPath();
        if (d >= r.Width || d >= r.Height)
        {
            p.AddEllipse(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (!string.IsNullOrEmpty(SourcePath) && File.Exists(SourcePath))
            {
                using (var original = Image.FromFile(SourcePath))
                    g.DrawImage(original, new Rectangle(0, 0, size, size));
                return bmp;
            }

            float s = size;
            float capsuleH = s * 0.44f;
            float inset = s * 0.07f;
            var capsule = new RectangleF(inset, (s - capsuleH) / 2f, s - inset * 2f, capsuleH);
            float r = capsule.Height / 2f;

            // 外发光（几层低透明度放大胶囊，小尺寸下几乎看不见，无所谓）
            for (int i = 3; i >= 1; i--)
            {
                float grow = s * 0.045f * i;
                var glow = RectangleF.Inflate(capsule, grow, grow);
                using (var path = RoundRect(glow, r + grow))
                using (var brush = new SolidBrush(Color.FromArgb(22 - i * 6, 34, 211, 238)))
                    g.FillPath(brush, path);
            }

            // 主体：青 → 紫 横向渐变（与岛体的品牌色一致：#22D3EE / #8A5CFF）
            using (var path = RoundRect(capsule, r))
            using (var brush = new LinearGradientBrush(capsule,
                       Color.FromArgb(255, 34, 211, 238), Color.FromArgb(255, 138, 92, 255), LinearGradientMode.Horizontal))
                g.FillPath(brush, path);

            if (size >= 32)
            {
                // 左上高光
                float hh = capsule.Height * 0.32f;
                var hi = new RectangleF(capsule.X + capsule.Width * 0.10f,
                                        capsule.Y + capsule.Height * 0.17f,
                                        capsule.Width * 0.32f, hh);
                using (var hp = RoundRect(hi, hh / 2f))
                using (var hb = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g.FillPath(hb, hp);

                // 右侧状态点
                float d = capsule.Height * 0.24f;
                using (var db = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
                    g.FillEllipse(db, capsule.Right - capsule.Width * 0.15f - d / 2f,
                                  capsule.Y + capsule.Height / 2f - d / 2f, d, d);
            }
        }
        return bmp;
    }

    // 单帧 -> ICO 内的 DIB 数据（BITMAPINFOHEADER + 自下而上的 BGRA + AND 掩码）
    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorStride = w * 4;
        int andStride = ((w + 31) / 32) * 4;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        bw.Write(40);                 // biSize
        bw.Write(w);                  // biWidth
        bw.Write(h * 2);              // biHeight（高度写两倍：XOR + AND）
        bw.Write((short)1);           // biPlanes
        bw.Write((short)32);          // biBitCount
        bw.Write(0);                  // biCompression = BI_RGB
        bw.Write(xorStride * h + andStride * h); // biSizeImage
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[xorStride];
            for (int y = h - 1; y >= 0; y--)   // 自下而上
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, xorStride);
                bw.Write(row);
            }
        }
        finally { bmp.UnlockBits(data); }

        // Mirror fully transparent pixels in the legacy AND mask as well as BGRA alpha.
        var mask = new byte[andStride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, h - 1 - y).A == 0)
                    mask[y * andStride + x / 8] |= (byte)(0x80 >> (x % 8));
        bw.Write(mask);
        bw.Flush();
        return ms.ToArray();
    }

    public static void Build(string icoPath, string previewPath)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var frames = new List<byte[]>();
        var dims = new List<int>();
        foreach (int size in sizes)
        {
            using (var bmp = Render(size))
            {
                frames.Add(ToDib(bmp));
                dims.Add(size);
                if (size == 256 && !string.IsNullOrEmpty(previewPath))
                {
                    var dir = Path.GetDirectoryName(previewPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(previewPath, ImageFormat.Png);
                }
            }
        }

        var outDir = Path.GetDirectoryName(icoPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using (var fs = File.Create(icoPath))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);              // reserved
            bw.Write((short)1);              // type = icon
            bw.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                bw.Write((byte)(dims[i] >= 256 ? 0 : dims[i])); // 256 写 0
                bw.Write((byte)(dims[i] >= 256 ? 0 : dims[i]));
                bw.Write((byte)0);           // 调色板数
                bw.Write((byte)0);           // reserved
                bw.Write((short)1);          // planes
                bw.Write((short)32);         // bit count
                bw.Write(frames[i].Length);
                bw.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) bw.Write(f);
        }
    }
}
'@

[AlphaIslandIcon]::SourcePath = $Source
[AlphaIslandIcon]::Build($Output, $Preview)

$full = [System.IO.Path]::GetFullPath($Output)
$len = (Get-Item $full).Length
"生成: $full ($([math]::Round($len / 1KB, 1)) KB)"
if (Test-Path $Preview) { "预览: $([System.IO.Path]::GetFullPath($Preview))" }
