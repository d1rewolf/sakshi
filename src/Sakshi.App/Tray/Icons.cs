using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sakshi.App.Tray;

public enum TrayIconState { Idle, Recording, RecordingDim, Busy, Transcribing }

/// <summary>
/// The tray icons: the Sakshi eye (packaging/sakshi.svg), drawn pixel by pixel (no renderer, so it is deterministic and
/// works headless). The outline is one mid grey that reads on dark and light bars; the pupil carries the state:
/// idle = grey, recording = red that blinks (the pupil disappears on the off beat), starting/naming = amber ring,
/// transcribing = blue. The tray drops the logo's iris ring, which does not fit at 32 px.
/// </summary>
internal static class Icons
{
    public const int Size = 32;

    private static readonly (int R, int G, int B) Grey = (0x8A, 0x93, 0x99);

    // The eye is a lens: the overlap of two circles of radius LidRadius, centred LidOffset above and below the middle.
    // Lid centreline half-width 13.5 px and half-height 7.5 px: the logo's eye (26 x 15 of 64), a little flatter so the
    // pupil fills more of the 32 px tray.
    private const double LidHalfHeight = 7.5;
    private const double LidRadius = (13.5 * 13.5 + LidHalfHeight * LidHalfHeight) / (2 * LidHalfHeight);
    private const double LidOffset = LidRadius - LidHalfHeight;
    private const double LidStroke = 2.2;
    private const double PupilRadius = 4.5;
    private const double RingInner = 2.6;

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
        (int R, int G, int B)? pupil = state switch
        {
            TrayIconState.Recording => (0xE5, 0x39, 0x35),
            TrayIconState.RecordingDim => null,
            TrayIconState.Busy => (0xFF, 0xB3, 0x00),
            TrayIconState.Transcribing => (0x2D, 0x8C, 0xF0),
            _ => Grey,
        };
        var ring = state == TrayIconState.Busy;
        var px = new byte[Size * Size * 4];
        const double centre = Size / 2.0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - centre, dy = y + 0.5 - centre;
                // Distance outside the lens (negative inside): the further of the two lid circles decides.
                var lens = Math.Max(Math.Sqrt(dx * dx + (dy - LidOffset) * (dy - LidOffset)),
                                    Math.Sqrt(dx * dx + (dy + LidOffset) * (dy + LidOffset))) - LidRadius;
                // Anti-aliased edges: full coverage inside, linear over one pixel at each edge.
                var lid = Math.Clamp(LidStroke / 2 - Math.Abs(lens) + 0.5, 0, 1);
                var d = Math.Sqrt(dx * dx + dy * dy);
                var dot = pupil is null ? 0 : Math.Clamp(PupilRadius - d + 0.5, 0, 1);
                if (ring) dot = Math.Min(dot, Math.Clamp(d - RingInner + 0.5, 0, 1));
                var (r, g, b) = pupil ?? Grey;
                // The lid and the pupil never touch, so adding the two premultiplied colours is exact.
                var o = (y * Size + x) * 4;
                px[o] = (byte)Math.Round(Grey.B * lid + b * dot);
                px[o + 1] = (byte)Math.Round(Grey.G * lid + g * dot);
                px[o + 2] = (byte)Math.Round(Grey.R * lid + r * dot);
                px[o + 3] = (byte)Math.Round(255 * Math.Min(1, lid + dot));
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
