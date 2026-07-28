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

    // The gesture's own start point, captured at drag-begin. GestureDrag's
    // GetStartPoint() only answers while the gesture is still active, and at
    // drag-end it can already have been reset — so asking it there is a path
    // that silently drops the finger-up. Remembering it costs two ints.
    double dragStartX;
    double dragStartY;

    // True between a Press we sent and its Release. Guarantees exactly one
    // Release per Press: a head that never sees the finger lift keeps the
    // pointer captured by whatever was pressed, so every later tap is re-routed
    // to that stale target instead of what the user actually touched.
    bool touchDown;

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

        // Drive a continuous redraw WHILE AN APP IS ATTACHED; each draw polls
        // the frame source, so this is also the frame-consumption tick,
        // deliberately decoupled from the app's own render pacing. With no
        // source the screen is a static black — powered off — and repainting
        // it would be pure waste (GTK still repaints it on resize by itself).
        screen.AddTickCallback((widget, _) =>
        {
            if (frameSource != null)
                widget.QueueDraw();
            return true;
        });
    }

    /// <summary>
    /// How far the device has been turned counter-clockwise from the orientation
    /// it was created in, in quarter turns (0-3). Applied to every frame drawn and
    /// undone on every touch reported, UNCONDITIONALLY — whether the application
    /// honored the turn only decides what is inside the frame, never how the
    /// device itself is being held. GTK main thread only.
    /// </summary>
    public int QuarterTurns { get; set; }

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
            // A begin without an end for the previous gesture would leave the
            // device holding a finger down; lift it before pressing again.
            ReleaseTouch(lastSentX, lastSentY);
            dragStartX = args.StartX;
            dragStartY = args.StartY;
            var (x, y) = ToDevice(args.StartX, args.StartY);
            lastSentX = x;
            lastSentY = y;
            touchDown = true;
            Touch?.Invoke(FrameBufferTouchKind.Press, x, y);
        };
        drag.OnDragUpdate += (_, args) =>
        {
            if (!touchDown)
                return;
            var (x, y) = ToDevice(dragStartX + args.OffsetX, dragStartY + args.OffsetY);
            if (x == lastSentX && y == lastSentY)
                return;
            lastSentX = x;
            lastSentY = y;
            Touch?.Invoke(FrameBufferTouchKind.Move, x, y);
        };
        drag.OnDragEnd += (_, args) =>
        {
            var (x, y) = ToDevice(dragStartX + args.OffsetX, dragStartY + args.OffsetY);
            ReleaseTouch(x, y);
        };
        // A gesture can also END without drag-end: another widget claiming the
        // sequence, the pointer leaving, the window losing the grab. Every one
        // of those must still lift the finger, at wherever it last was.
        drag.OnCancel += (_, _) => ReleaseTouch(lastSentX, lastSentY);
        drag.OnEnd += (_, _) => ReleaseTouch(lastSentX, lastSentY);
        screen.AddController(drag);

        // Right button: claimed and discarded — on the emulated device it does
        // not exist, and letting it through would raise the WM's window menu.
        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        rightClick.OnPressed += (sender, _) => sender.SetState(Gtk.EventSequenceState.Claimed);
        screen.AddController(rightClick);
    }

    // Sends Release exactly once per Press, from whichever path first notices
    // the gesture is over. Doing nothing when no finger is down is what makes
    // it safe to call from all of them.
    void ReleaseTouch(int x, int y)
    {
        if (!touchDown)
            return;
        touchDown = false;
        Touch?.Invoke(FrameBufferTouchKind.Release, x, y);
    }

    // Window pixels -> device pixels: the exact inverse of the transform OnDraw
    // applies, so a finger always lands on the frame-buffer pixel it is pointing
    // at however the device is turned. No letterbox offset, because the canvas IS
    // the screen; clamped so the far edges land on the last device pixel rather
    // than one past it.
    (int X, int Y) ToDevice(double x, double y)
    {
        var width = (double) Math.Max(1, presentWidth);
        var height = (double) Math.Max(1, presentHeight);
        // Normalized position within the UNROTATED frame.
        var (normalizedX, normalizedY) = QuarterTurns switch
        {
            1 => ((height - y) / height, x / width),
            2 => ((width - x) / width, (height - y) / height),
            3 => (y / height, (width - x) / width),
            _ => (x / width, y / height),
        };
        return (
            Math.Clamp((int) Math.Round(normalizedX * deviceWidth), 0, deviceWidth - 1),
            Math.Clamp((int) Math.Round(normalizedY * deviceHeight), 0, deviceHeight - 1));
    }

    void OnDraw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        // The finally-Dispose is LOAD-BEARING: the binding's draw-func handler
        // takes its own reference on the frame's cairo_t (a GBoxed copy) and
        // otherwise releases it only at FINALIZATION. Each frame context pins
        // megabytes of render-target state, and this draw runs at frame-clock
        // rate — undisposed, the process grows by hundreds of MB/s of native
        // memory the GC cannot see, until the kernel OOM-kills the IDE.
        try
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
                // Turning the device turns what is on it. The frame buffer never
                // changes shape, so an odd number of quarter turns draws it into a
                // transposed rectangle — which is exactly the canvas's shape,
                // because the window was reshaped to match.
                var (degrees, translateX, translateY, targetWidth, targetHeight) = QuarterTurns switch
                {
                    1 => (-90f, 0f, (float) height, height, width),
                    2 => (180f, (float) width, (float) height, width, height),
                    3 => (90f, (float) width, 0f, height, width),
                    _ => (0f, 0f, 0f, width, height),
                };
                canvas.Save();
                canvas.Translate(translateX, translateY);
                canvas.RotateDegrees(degrees);
                canvas.DrawImage(frame, SKRect.Create(0, 0, targetWidth, targetHeight),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Restore();
            }
            canvas.Flush();
            presentSurface.MarkDirty();
            cr.SetSourceSurface(presentSurface, 0, 0);
            cr.Paint();
        }
        finally
        {
            cr.Dispose();
        }
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
