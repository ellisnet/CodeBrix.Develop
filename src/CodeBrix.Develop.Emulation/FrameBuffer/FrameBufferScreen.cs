//
// FrameBufferScreen.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using SkiaSharp;
using Cairo = CodeBrix.Develop.UI.Cairo;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The emulator's live screen: displays the frames an emulated application
/// publishes through an <see cref="IFrameBufferFrameSource"/>, and reports
/// single-touch input back in device pixels. With no source attached — or a
/// source that has produced no frame yet — the screen is black, exactly like a
/// device with no power.
/// <para>
/// The display path is the one proven by <see cref="FrameBufferTestPattern"/>
/// (which remains the reference implementation and diagnostic screen): the
/// frame exists at the device's exact resolution knowing nothing about the
/// window, Skia writes the scaled result straight into a Cairo surface's
/// pixels with no intermediate copy, and all scaling happens here on the IDE
/// side. Everything runs on the GTK main thread.
/// </para>
/// </summary>
internal sealed class FrameBufferScreen : IDisposable
{
    // The emulated device's frame, at EXACTLY the configured resolution.
    readonly int deviceWidth;
    readonly int deviceHeight;
    readonly SKImageInfo deviceInfo;
    readonly SKBitmap deviceBitmap;
    readonly int deviceFrameBytes;

    readonly Gtk.DrawingArea screen;

    // The presentation side: a Cairo image surface whose pixels a Skia surface
    // writes into directly, with no intermediate copy.
    Cairo.ImageSurface? presentSurface;
    SKSurface? presentSkia;
    int presentWidth;
    int presentHeight;

    IFrameBufferFrameSource? frameSource;
    long lastSequence;
    bool hasFrame;
    bool disposed;

    // Move events are deduplicated per device pixel, so holding the mouse
    // still does not stream identical coordinates at the app.
    int lastSentX = -1;
    int lastSentY = -1;

    /// <summary>
    /// Raised on the GTK main thread for each touch, in DEVICE pixels:
    /// press, move (only while pressed), release.
    /// </summary>
    public event Action<FrameBufferTouchKind, int, int>? Touch;

    public FrameBufferScreen(Gtk.DrawingArea screen, int deviceWidth, int deviceHeight)
    {
        this.screen = screen;
        this.deviceWidth = deviceWidth;
        this.deviceHeight = deviceHeight;
        deviceFrameBytes = deviceWidth * 4 * deviceHeight;

        deviceInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        deviceBitmap = new SKBitmap(deviceInfo);

        screen.SetDrawFunc(OnDraw);
        InstallInput(screen);

        // Drive a continuous redraw; each draw polls the frame source, so this
        // is also the frame-consumption tick. Deliberately decoupled from the
        // app's own render pacing.
        screen.AddTickCallback((widget, _) =>
        {
            widget.QueueDraw();
            return true;
        });
    }

    /// <summary>
    /// Attaches the frames of a running emulated application, or detaches with
    /// null — power off — which blanks the screen. GTK main thread only.
    /// </summary>
    public void SetFrameSource(IFrameBufferFrameSource? source)
    {
        frameSource = source;
        lastSequence = 0;
        hasFrame = false;
        screen.QueueDraw();
    }

    void InstallInput(Gtk.DrawingArea screen)
    {
        // One gesture covers the whole single-touch model: begin = finger
        // press, update = drag, end = lift. Capture phase + an immediate claim,
        // or the Gtk.WindowHandle wrapping the bezel steals the press for a
        // window-move.
        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        drag.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        drag.OnDragBegin += (sender, args) =>
        {
            sender.SetState(Gtk.EventSequenceState.Claimed);
            var (x, y) = ToDevice(args.StartX, args.StartY);
            lastSentX = x;
            lastSentY = y;
            Touch?.Invoke(FrameBufferTouchKind.Press, x, y);
        };
        drag.OnDragUpdate += (sender, args) =>
        {
            if (!sender.GetStartPoint(out var startX, out var startY))
                return;
            var (x, y) = ToDevice(startX + args.OffsetX, startY + args.OffsetY);
            if (x == lastSentX && y == lastSentY)
                return;
            lastSentX = x;
            lastSentY = y;
            Touch?.Invoke(FrameBufferTouchKind.Move, x, y);
        };
        drag.OnDragEnd += (sender, args) =>
        {
            if (!sender.GetStartPoint(out var startX, out var startY))
                return;
            var (x, y) = ToDevice(startX + args.OffsetX, startY + args.OffsetY);
            Touch?.Invoke(FrameBufferTouchKind.Release, x, y);
        };
        screen.AddController(drag);

        // Right button: claimed and discarded — on the emulated device it does
        // not exist, and letting it through would raise the WM's window menu.
        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        rightClick.OnPressed += (sender, _) => sender.SetState(Gtk.EventSequenceState.Claimed);
        screen.AddController(rightClick);
    }

    // Window pixels -> device pixels. One uniform scale, no letterbox offset,
    // because the canvas IS the screen; clamped so the screen's far edges land
    // on the last device pixel rather than one past it.
    (int X, int Y) ToDevice(double x, double y) => (
        Math.Clamp((int) Math.Round(x * deviceWidth / Math.Max(1, presentWidth)), 0, deviceWidth - 1),
        Math.Clamp((int) Math.Round(y * deviceHeight / Math.Max(1, presentHeight)), 0, deviceHeight - 1));

    void OnDraw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (disposed || width <= 0 || height <= 0)
            return;

        EnsurePresentSurface(width, height);
        if (presentSurface == null || presentSkia == null)
            return;

        // Pull the newest complete frame, if one arrived since last tick.
        if (frameSource is { } source
            && source.TryCopyLatestFrame(deviceBitmap.GetPixels(), deviceFrameBytes, ref lastSequence))
        {
            hasFrame = true;
        }

        // Cairo's contract for writing to a surface's pixels behind its back:
        // flush BEFORE touching them and mark dirty AFTER.
        presentSurface.Flush();
        var canvas = presentSkia.Canvas;
        canvas.Clear(SKColors.Black);
        if (hasFrame)
        {
            using var frame = SKImage.FromPixels(deviceInfo, deviceBitmap.GetPixels(), deviceBitmap.RowBytes);
            canvas.DrawImage(frame, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        canvas.Flush();
        presentSurface.MarkDirty();
        cr.SetSourceSurface(presentSurface, 0, 0);
        cr.Paint();
    }

    void EnsurePresentSurface(int width, int height)
    {
        if (presentSurface != null && presentWidth == width && presentHeight == height)
            return;

        presentSkia?.Dispose();
        presentSurface?.Dispose();

        presentSurface = new Cairo.ImageSurface(Cairo.Format.Argb32, width, height);
        presentWidth = width;
        presentHeight = height;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        presentSkia = SKSurface.Create(info, FrameBufferTestPattern.GetPixelPointer(presentSurface),
            presentSurface.Stride);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        presentSkia?.Dispose();
        presentSurface?.Dispose();
        deviceBitmap.Dispose();
    }
}
