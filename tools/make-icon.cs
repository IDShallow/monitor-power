// Generates dist\app.ico (16/32/48/256 px) - a dark rounded square with a monitor
// outline and a green power glyph. Build: csc -target:exe tools\make-icon.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class MakeIcon
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = new int[] { 16, 32, 48, 256 };
        List<byte[]> pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap bmp = Render(s))
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }

        using (FileStream fs = new FileStream(outPath, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type = icon
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));   // width  (0 = 256)
                w.Write((byte)(s >= 256 ? 0 : s));   // height
                w.Write((byte)0);                    // palette
                w.Write((byte)0);                    // reserved
                w.Write((ushort)1);                  // planes
                w.Write((ushort)32);                 // bpp
                w.Write((uint)pngs[i].Length);
                w.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }
        Console.WriteLine("icon written: " + outPath);
    }

    static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = size / 256f;

            using (GraphicsPath bg = RoundRect(new RectangleF(6 * k, 6 * k, 244 * k, 244 * k), 56 * k))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 26, 32, 44)))
                g.FillPath(b, bg);

            RectangleF screen = new RectangleF(40 * k, 52 * k, 176 * k, 112 * k);
            float penW = Math.Max(1.4f, 12 * k);
            using (GraphicsPath sp = RoundRect(screen, 14 * k))
            using (Pen pen = new Pen(Color.FromArgb(255, 226, 233, 244), penW))
                g.DrawPath(pen, sp);

            using (Pen pen = new Pen(Color.FromArgb(255, 226, 233, 244), penW))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 128 * k, 168 * k, 128 * k, 192 * k);
                g.DrawLine(pen, 94 * k, 198 * k, 162 * k, 198 * k);
            }

            using (Pen pen = new Pen(Color.FromArgb(255, 64, 200, 120), Math.Max(1.4f, 11 * k)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, 104 * k, 78 * k, 48 * k, 48 * k, -55f, 290f);
                g.DrawLine(pen, 128 * k, 82 * k, 128 * k, 104 * k);
            }
        }
        return bmp;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
