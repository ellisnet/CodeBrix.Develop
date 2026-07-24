//
// FrameBufferEmulatorWindow.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Emulation.FrameBuffer;

/// <summary>
/// The window a Linux Frame Buffer head is emulated in: a black screen
/// inside a thin bezel, proportioned to the emulated device's resolution and
/// orientation. It is a free-standing top-level window — not transient for
/// the workbench — that the user can move and resize independently.
/// <para>
/// It is created on the first Run or Debug of a frame-buffer head and then
/// STAYS OPEN, keeping the place the user put it, until they close it (Tools
/// ▸ Close Emulator, or the window manager) or the application exits.
/// Stopping only stops what is being emulated; the window does not move or
/// hide. That deliberately avoids ever needing to restore a window position,
/// which GTK 4 has no API for and Wayland forbids outright.
/// </para>
/// <para>
/// The orientation and resolution are read once, here, when the window is
/// created — changing them in Options takes effect the next time an emulator
/// window is opened.
/// </para>
/// </summary>
public sealed class FrameBufferEmulatorWindow : IDisposable
{
    /// <summary>The bezel's thickness around the emulated screen, in pixels.</summary>
    const int BezelThickness = 6;

    /// <summary>
    /// The height of the empty drag handle standing in for a title bar. It
    /// gives the window client-side decorations — resize edges without any
    /// title-bar buttons — and makes the top of the bezel draggable.
    /// </summary>
    const int TitleBarHeight = 6;

    /// <summary>
    /// How many consecutive unchanged frame-clock ticks count as "the resize
    /// has settled" before the window is snapped back to the exact screen
    /// proportions. GTK 4 dropped the geometry hints that used to constrain
    /// this in the toolkit, so the correction has to be applied afterwards.
    /// </summary>
    const int SettledTickCount = 8;

    const string BezelCssClass = "cb-framebuffer-bezel";
    const string ScreenCssClass = "cb-framebuffer-screen";
    const string TitleBarCssClass = "cb-framebuffer-titlebar";

    // The title-bar slot picks up the theme's title-bar metrics — around 46px
    // tall — unless they are explicitly zeroed, which would make the top of
    // the bezel many times thicker than its other three sides.
    const string BezelCss = """
        .cb-framebuffer-bezel { background-color: #141414; }
        .cb-framebuffer-screen { background-color: #000000; }
        .cb-framebuffer-titlebar {
            min-height: 0;
            padding: 0;
            border: none;
            box-shadow: none;
            background-image: none;
            background-color: #141414;
        }
        """;

    static Gtk.CssProvider? cssProvider;

    readonly Gtk.Window window;
    readonly Gtk.AspectFrame screenFrame;
    readonly Gtk.DrawingArea screen;

    readonly int rememberedWidth;
    readonly int rememberedHeight;

    FrameBufferOrientation orientation = FrameBufferOrientation.Portrait;
    FrameBufferResolutionInfo resolution =
        FrameBufferResolutionInfo.Get(FrameBufferResolution.SevenInch720x1280);

    // The emulated screen's size — what is remembered and re-fitted. The
    // window itself is this plus its chrome (bezel and drag handle).
    int screenWidth;
    int screenHeight;

    bool applyingSize;
    bool pendingIntendedSize;
    uint settleTickId;
    int lastTickWidth;
    int lastTickHeight;
    int settledTicks;
    bool disposed;

    // The screen's content until an emulated application is driving it.
    readonly FrameBufferTestPattern testPattern;

    /// <summary>
    /// Raised when the window is closing, from Tools ▸ Close Emulator or from
    /// the window manager. The size is still readable at this point, which is
    /// when the caller persists it; the window is gone afterwards and the
    /// caller must drop its reference.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Creates the emulator window for the given application, proportioned to
    /// the given screen and orientation. The remembered size is the screen
    /// size persisted from a previous window; pass 0 for either dimension to
    /// open at the screen's default size.
    /// </summary>
    public FrameBufferEmulatorWindow(Gtk.Application application, FrameBufferOrientation orientation,
        FrameBufferResolution resolution, int rememberedWidth, int rememberedHeight)
    {
        ArgumentNullException.ThrowIfNull(application);
        this.rememberedWidth = rememberedWidth;
        this.rememberedHeight = rememberedHeight;
        this.orientation = orientation;
        this.resolution = FrameBufferResolutionInfo.Get(resolution);

        window = Gtk.Window.New();
        application.AddWindow(window);
        window.SetTitle("Frame Buffer");
        EnsureCssInstalled();

        // An empty drag handle in the title-bar slot: client-side decorations
        // (so the window stays resizable from its edges) with no title-bar
        // buttons of its own — the emulator is closed from the Tools menu.
        var titleBar = Gtk.WindowHandle.New();
        titleBar.SetSizeRequest(-1, TitleBarHeight);
        titleBar.AddCssClass(BezelCssClass);
        titleBar.AddCssClass(TitleBarCssClass);
        window.SetTitlebar(titleBar);

        // The screen. AspectFrame keeps it EXACTLY proportional to the
        // emulated resolution at every instant, whatever the window is doing;
        // any leftover space becomes bezel. Its resize signal is also the
        // trigger for the snap below — the window's own default-size
        // properties do not track an interactive resize.
        screen = Gtk.DrawingArea.New();
        screen.AddCssClass(ScreenCssClass);
        screen.SetHexpand(true);
        screen.SetVexpand(true);
        screen.OnResize += (_, _) => WatchForSettledResize();

        screenFrame = Gtk.AspectFrame.New(0.5f, 0.5f, 1.0f, false);
        screenFrame.SetChild(screen);
        screenFrame.SetHexpand(true);
        screenFrame.SetVexpand(true);
        screenFrame.SetMarginStart(BezelThickness);
        screenFrame.SetMarginEnd(BezelThickness);
        // The drag handle above already supplies the top bezel, so no margin
        // here — that keeps the bezel the same thickness on all four sides.
        screenFrame.SetMarginTop(0);
        screenFrame.SetMarginBottom(BezelThickness);

        // Wrapping the content in a window handle makes the bezel itself the
        // drag surface, so the window moves without a title bar to grab.
        var bezel = Gtk.WindowHandle.New();
        bezel.AddCssClass(BezelCssClass);
        bezel.SetChild(screenFrame);
        window.SetChild(bezel);

        // Draws the no-signal screen at the device's exact resolution; the
        // emulated application's frames will take this same display path.
        testPattern = new FrameBufferTestPattern(screen,
            this.resolution.GetWidth(orientation), this.resolution.GetHeight(orientation));

        // Closing really closes — the window's place on screen goes with it,
        // which is the trade the user makes by closing rather than stopping.
        window.OnCloseRequest += (_, _) =>
        {
            OnClosing();
            return false; // let it be destroyed
        };

        // The screen size the window opens at, before it has been laid out.
        screenFrame.SetRatio((float) this.resolution.GetAspectRatio(orientation));
        (screenWidth, screenHeight) = rememberedWidth > 0 && rememberedHeight > 0
            ? this.resolution.GetSizeForWidth(rememberedWidth, orientation)
            : this.resolution.GetDefaultWindowSize(orientation);
        ApplySize(screenWidth + HorizontalChrome, screenHeight + VerticalChrome);
    }

    /// <summary>The emulated device's current orientation.</summary>
    public FrameBufferOrientation Orientation => orientation;

    /// <summary>The emulated device's current screen.</summary>
    public FrameBufferResolutionInfo Resolution => resolution;

    /// <summary>
    /// Shows the window if it is not already showing, and raises it to the
    /// front. The window is never moved or resized by this — it stays exactly
    /// where the user put it.
    /// </summary>
    public void Present(string title)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        window.SetTitle(title);
        var firstShow = !window.GetVisible();
        window.Present();
        if (!firstShow)
            return;
        // The chrome is only measurable once the window is laid out, so the
        // size applied in the constructor was built on an estimate. Re-apply
        // it against the real measurements as soon as the layout settles.
        pendingIntendedSize = true;
        WatchForSettledResize();
    }

    /// <summary>
    /// Closes the window for good, raising <see cref="Closed"/>. The next
    /// emulator is a new window, which the window manager places wherever it
    /// likes — only the size is carried over.
    /// </summary>
    public void Close()
    {
        if (disposed)
            return;
        window.Close(); // routed through OnCloseRequest, as a manual close is
    }

    /// <summary>
    /// The emulated screen's current size, for persisting. False when no size
    /// is available yet, which is the case before it has ever been shown.
    /// </summary>
    public bool TryGetSize(out int width, out int height)
    {
        // Deliberately readable after closing: the Closed handler is exactly
        // where the size gets persisted.
        width = screenWidth;
        height = screenHeight;
        return width > 0 && height > 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        StopWatchingForSettledResize();
        testPattern.Dispose();
        window.Destroy();
    }

    // The window's size minus the emulated screen's available area: the bezel
    // margins, plus the drag handle standing in for a title bar. Measured
    // rather than assumed, and estimated from the constants until the window
    // has been laid out at least once.
    int HorizontalChrome => screenFrame.GetWidth() > 0
        ? window.GetWidth() - screenFrame.GetWidth()
        : BezelThickness * 2;

    int VerticalChrome => screenFrame.GetHeight() > 0
        ? window.GetHeight() - screenFrame.GetHeight()
        : TitleBarHeight + BezelThickness;

    // The window is about to be destroyed: stop watching it, and let the
    // caller persist the size while it is still readable.
    void OnClosing()
    {
        if (disposed)
            return;
        disposed = true;
        StopWatchingForSettledResize();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    // GTK 4 has no aspect-ratio geometry hint, so the window outline is
    // corrected after the fact: watch the frame clock while the size is
    // changing and snap once it holds still.
    void WatchForSettledResize()
    {
        if (disposed || applyingSize)
            return;
        settledTicks = 0;
        if (settleTickId != 0)
            return;
        lastTickWidth = window.GetWidth();
        lastTickHeight = window.GetHeight();
        settleTickId = window.AddTickCallback((_, _) => OnSettleTick());
    }

    void StopWatchingForSettledResize()
    {
        if (settleTickId == 0)
            return;
        window.RemoveTickCallback(settleTickId);
        settleTickId = 0;
    }

    bool OnSettleTick()
    {
        var width = window.GetWidth();
        var height = window.GetHeight();
        if (width != lastTickWidth || height != lastTickHeight)
        {
            lastTickWidth = width;
            lastTickHeight = height;
            settledTicks = 0;
            return true; // still moving
        }
        if (++settledTicks < SettledTickCount)
            return true;

        settleTickId = 0; // returning false removes the callback
        if (pendingIntendedSize)
        {
            pendingIntendedSize = false;
            ApplyIntendedSize();
        }
        else
        {
            SnapToScreenProportions(width, height);
        }
        return false;
    }

    // Re-applies the screen size Present asked for, now that the chrome
    // around it can actually be measured.
    void ApplyIntendedSize()
    {
        var targetWidth = screenWidth + HorizontalChrome;
        var targetHeight = screenHeight + VerticalChrome;
        if (targetWidth != window.GetWidth() || targetHeight != window.GetHeight())
            ApplySize(targetWidth, targetHeight);
    }

    // Constrains the SCREEN — not the window — to the emulated proportions,
    // then gives the window back its chrome, so the bezel stays even on all
    // four sides.
    void SnapToScreenProportions(int windowWidth, int windowHeight)
    {
        var horizontalChrome = HorizontalChrome;
        var verticalChrome = VerticalChrome;
        if (windowWidth <= horizontalChrome || windowHeight <= verticalChrome)
            return;

        (screenWidth, screenHeight) = resolution.SnapToAspectRatio(
            windowWidth - horizontalChrome, windowHeight - verticalChrome, orientation);

        var targetWidth = screenWidth + horizontalChrome;
        var targetHeight = screenHeight + verticalChrome;
        if (targetWidth != windowWidth || targetHeight != windowHeight)
            ApplySize(targetWidth, targetHeight);
    }

    void ApplySize(int width, int height)
    {
        applyingSize = true;
        try
        {
            window.SetDefaultSize(width, height);
        }
        finally
        {
            applyingSize = false;
        }
    }

    static void EnsureCssInstalled()
    {
        if (cssProvider != null)
            return;
        if (Gdk.Display.GetDefault() is not { } display)
            return;
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(BezelCss);
        // Application priority, matching the theme's own provider; the class
        // names are unique to the emulator so the two never collide.
        Gtk.StyleContext.AddProviderForDisplay(display, provider, 600);
        cssProvider = provider;
    }
}
