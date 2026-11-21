using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sakshi.App.Tray;

public enum TrayIconState { Idle, Recording, RecordingDim, Busy, Transcribing }

/// <summary>
/// The tray icons, drawn pixel by pixel (no renderer, so it is deterministic and works headless):
/// idle = dim dot, recording = red dot (blinking against a dimmer red), starting/naming = amber ring,
/// transcribing = blue dot.
/// </summary>
internal static class Icons
{
    public const int Size = 32;

    public static TrayIconState StateFor(string status, bool blinkOn) => status switch
    {
        "recording" => blinkOn ? TrayIconState.Recording : TrayIconState.RecordingDim,
        "starting" or "naming" => TrayIconState.Busy,
        "transcribing" => TrayIconState.Transcribing,
        _ => TrayIconState.Idle,
    };

    /// <summary>BGRA, premultiplied alpha, <see cref="Size"/> x <see cref="Size"/>.</summary>
    public static byte[] Pixels(TrayIconState state)
    {
        var (r, g, b, outer, inner) = state switch
        {
            TrayIconState.Recording => (0xE5, 0x39, 0x35, 11.0, 0.0),
            TrayIconState.RecordingDim => (0x7A, 0x20, 0x20, 11.0, 0.0),
            TrayIconState.Busy => (0xFF, 0xB3, 0x00, 12.0, 8.0),
            TrayIconState.Transcribing => (0x2D, 0x8C, 0xF0, 11.0, 0.0),
            _ => (0x80, 0x80, 0x88, 8.0, 0.0),
        };
        var px = new byte[Size * Size * 4];
        const double centre = Size / 2.0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var d = Math.Sqrt((x + 0.5 - centre) * (x + 0.5 - centre) + (y + 0.5 - centre) * (y + 0.5 - centre));
                // Anti-aliased edges: full coverage inside, linear over one pixel at each edge.
                var cover = Math.Clamp(outer - d + 0.5, 0, 1);
                if (inner > 0) cover = Math.Min(cover, Math.Clamp(d - inner + 0.5, 0, 1));
                var o = (y * Size + x) * 4;
                px[o] = (byte)Math.Round(b * cover);
                px[o + 1] = (byte)Math.Round(g * cover);
                px[o + 2] = (byte)Math.Round(r * cover);
                px[o + 3] = (byte)Math.Round(255 * cover);
            }
        return px;
    }

    public static WriteableBitmap Render(TrayIconState state)
    {
        var bmp = new WriteableBitmap(new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = Pixels(state);
        using var fb = bmp.Lock();
        for (var y = 0; y < Size; y++)
            Marshal.Copy(px, y * Size * 4, fb.Address + y * fb.RowBytes, Size * 4);
        return bmp;
    }
}
