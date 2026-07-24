//
// FrameBufferTestPattern.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using Cairo = CodeBrix.Develop.UI.Cairo;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The emulator's own screen content: a test pattern drawn at exactly the
/// emulated device's resolution, scaled down (or up) into the window, with
/// pointer input reported back in device pixels.
/// <para>
/// It is what the emulator shows when no emulated application is driving it —
/// the no-signal screen — and it doubles as the reference implementation of
/// the display path every emulated frame will take: the frame is produced at
/// the device's exact resolution knowing nothing about the window, Skia writes
/// it straight into a Cairo surface's pixels with no intermediate copy, and
/// all scaling happens here on the IDE side.
/// </para>
/// </summary>
internal sealed class FrameBufferTestPattern : IDisposable
{
    // The emulated device's frame, at EXACTLY the configured resolution. It
    // never learns the window exists — the whole point of the design.
    readonly int deviceWidth;
    readonly int deviceHeight;
    readonly SKBitmap deviceBitmap;
    readonly SKSurface deviceSurface;

    // The presentation side: a Cairo image surface whose pixels a Skia surface
    // writes into directly, with no intermediate copy.
    Cairo.ImageSurface? presentSurface;
    SKSurface? presentSkia;
    int presentWidth;
    int presentHeight;

    // Input, recorded in DEVICE pixels.
    SKPoint? lastPress;
    SKPoint? lastRelease;
    readonly List<SKPoint> dragTrail = new();
    bool dragging;
    int rightClicks;

    // Frame timing.
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastFrameStamp;
    double framesPerSecond;
    double scaleFactor = 1;
    long frameCount;
    bool disposed;

    public FrameBufferTestPattern(Gtk.DrawingArea screen, int deviceWidth, int deviceHeight)
    {
        this.deviceWidth = deviceWidth;
        this.deviceHeight = deviceHeight;

        var info = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        deviceBitmap = new SKBitmap(info);
        deviceSurface = SKSurface.Create(info, deviceBitmap.GetPixels(), deviceBitmap.RowBytes)
            ?? throw new InvalidOperationException("Could not create the device-resolution Skia surface");

        screen.SetDrawFunc(OnDraw);
        InstallInput(screen);

        // Drive a continuous redraw so the frame rate is measurable.
        screen.AddTickCallback((widget, _) =>
        {
            widget.QueueDraw();
            return true;
        });
    }

    void InstallInput(Gtk.DrawingArea screen)
    {
        // One gesture covers the whole single-touch model: begin = finger
        // press, update = drag, end = lift. A tap is simply a drag that never
        // moved, so a separate click gesture would only fight this one.
        //
        // It runs in the CAPTURE phase and claims the sequence immediately.
        // Without both, the Gtk.WindowHandle wrapping the bezel takes the
        // press for a window-move and the canvas gets a begin with no updates.
        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        drag.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        drag.OnDragBegin += (sender, args) =>
        {
            sender.SetState(Gtk.EventSequenceState.Claimed);
            dragging = true;
            dragTrail.Clear();
            lastPress = ToDevice(args.StartX, args.StartY);
            dragTrail.Add(lastPress.Value);
        };
        drag.OnDragUpdate += (sender, args) =>
        {
            if (!sender.GetStartPoint(out var startX, out var startY))
                return;
            dragTrail.Add(ToDevice(startX + args.OffsetX, startY + args.OffsetY));
            if (dragTrail.Count > 400)
                dragTrail.RemoveAt(0);
        };
        drag.OnDragEnd += (sender, args) =>
        {
            dragging = false;
            if (sender.GetStartPoint(out var startX, out var startY))
                lastRelease = ToDevice(startX + args.OffsetX, startY + args.OffsetY);
        };
        screen.AddController(drag);

        // Right button: claimed and discarded, so it cannot reach the window
        // handle underneath and raise the window manager's menu.
        var rightClick = Gtk.GestureClick.New();
        rightClick.SetButton(3);
        rightClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        rightClick.OnPressed += (sender, _) =>
        {
            rightClicks++;
            sender.SetState(Gtk.EventSequenceState.Claimed);
        };
        screen.AddController(rightClick);
    }

    // Window pixels -> device pixels. One uniform scale, no letterbox offset,
    // because the canvas IS the screen (the AspectFrame guarantees it).
    SKPoint ToDevice(double x, double y) => new(
        (float) Math.Round(x * deviceWidth / Math.Max(1, presentWidth)),
        (float) Math.Round(y * deviceHeight / Math.Max(1, presentHeight)));

    void OnDraw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (disposed || width <= 0 || height <= 0)
            return;

        var now = clock.Elapsed.TotalSeconds;
        var delta = now - lastFrameStamp;
        if (delta > 0)
            framesPerSecond = 0.9 * framesPerSecond + 0.1 * (1.0 / delta);
        lastFrameStamp = now;
        frameCount++;

        EnsurePresentSurface(width, height);
        if (presentSurface == null || presentSkia == null)
            return;

        RenderDeviceFrame(now);

        // Scale the device frame into the window-sized surface. This is the
        // production data flow: the head renders at device resolution, the IDE
        // does all the scaling.
        scaleFactor = (double) width / deviceWidth;

        // Cairo's contract for writing to a surface's pixels behind its back:
        // flush BEFORE touching them (which also detaches the snapshot Cairo
        // took when the surface was last used as a paint source — without
        // this, mark_dirty asserts) and mark dirty AFTER.
        presentSurface.Flush();
        using (var frame = deviceSurface.Snapshot())
        {
            var canvas = presentSkia.Canvas;
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(frame, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
        }
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
        presentSkia = SKSurface.Create(info, GetPixelPointer(presentSurface), presentSurface.Stride);
    }

    static IntPtr GetPixelPointer(Cairo.ImageSurface surface)
    {
        var data = surface.GetData();
        unsafe
        {
            return (IntPtr) Unsafe.AsPointer(ref MemoryMarshal.GetReference(data));
        }
    }

    // The test pattern standing in for the emulated application's output.
    void RenderDeviceFrame(double seconds)
    {
        var canvas = deviceSurface.Canvas;
        canvas.Clear(new SKColor(0x10, 0x14, 0x1c));

        using var paint = new SKPaint { IsAntialias = true };
        using var font = new SKFont { Size = Math.Max(16f, Math.Min(deviceWidth, deviceHeight) / 26f) };
        using var smallFont = new SKFont { Size = Math.Max(12f, Math.Min(deviceWidth, deviceHeight) / 40f) };

        // A 40-device-pixel grid: makes the scale factor legible at a glance.
        paint.Color = new SKColor(0x28, 0x30, 0x3c);
        paint.StrokeWidth = 1;
        for (var x = 0; x <= deviceWidth; x += 40)
            canvas.DrawLine(x, 0, x, deviceHeight, paint);
        for (var y = 0; y <= deviceHeight; y += 40)
            canvas.DrawLine(0, y, deviceWidth, y, paint);

        // Pure R/G/B swatches: a channel-order mistake is instantly visible.
        var swatch = deviceWidth / 8f;
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White };
        var labels = new[] { "R", "G", "B", "W" };
        for (var i = 0; i < colors.Length; i++)
        {
            paint.Color = colors[i];
            canvas.DrawRect(SKRect.Create(20 + i * (swatch + 8), 20, swatch, swatch), paint);
            paint.Color = SKColors.Black;
            canvas.DrawText(labels[i], 20 + i * (swatch + 8) + swatch / 2 - 6,
                20 + swatch / 2 + 8, SKTextAlign.Left, smallFont, paint);
        }

        // A one-device-pixel border, to prove nothing is cropped.
        paint.Color = SKColors.Yellow;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        canvas.DrawRect(SKRect.Create(0.5f, 0.5f, deviceWidth - 1, deviceHeight - 1), paint);
        paint.Style = SKPaintStyle.Fill;

        // Something moving, so a frozen frame is obvious.
        var cx = deviceWidth / 2f + (float) Math.Sin(seconds * 1.6) * (deviceWidth / 3f);
        var cy = deviceHeight * 0.42f + (float) Math.Cos(seconds * 1.1) * (deviceHeight / 8f);
        paint.Color = new SKColor(0x4f, 0xc3, 0xf7);
        canvas.DrawCircle(cx, cy, deviceWidth / 14f, paint);

        // Readouts.
        paint.Color = SKColors.White;
        var line = deviceHeight * 0.42f;
        void Write(string text)
        {
            canvas.DrawText(text, 20, line, SKTextAlign.Left, font, paint);
            line += font.Size * 1.35f;
        }

        Write($"device {deviceWidth} x {deviceHeight}");
        Write($"window {presentWidth} x {presentHeight}  (x{scaleFactor:F3})");
        Write($"{framesPerSecond:F1} fps   frame {frameCount}");
        Write(lastPress is { } p ? $"press   {p.X:F0}, {p.Y:F0}" : "press   —");
        Write(lastRelease is { } r ? $"release {r.X:F0}, {r.Y:F0}" : "release —");
        Write($"drag {(dragging ? "DOWN" : "up")}  points {dragTrail.Count}");
        Write($"right-clicks swallowed: {rightClicks}");

        // The drag trail, in device space.
        if (dragTrail.Count > 1)
        {
            paint.Color = SKColors.Orange;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 3;
            // Polygon point mode joins the points into a polyline, which
            // avoids SKPath's obsolete MoveTo/LineTo overloads.
            canvas.DrawPoints(SKPointMode.Polygon, dragTrail.ToArray(), paint);
            paint.Style = SKPaintStyle.Fill;
        }

        // Crosshair at the last press: this is the coordinate-accuracy check —
        // it must appear exactly under the pointer.
        if (lastPress is { } press)
        {
            paint.Color = SKColors.Orange;
            canvas.DrawCircle(press.X, press.Y, 14, paint);
            paint.Color = SKColors.Black;
            canvas.DrawCircle(press.X, press.Y, 4, paint);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        presentSkia?.Dispose();
        presentSurface?.Dispose();
        deviceSurface.Dispose();
        deviceBitmap.Dispose();
    }
}
