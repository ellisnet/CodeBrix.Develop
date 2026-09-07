//
// AndroidDeviceMonitor.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Android;

/// <summary>
/// Watches which Android devices are attached, by asking adb on a timer.
/// </summary>
/// <remarks>
/// adb offers no push notification a process can subscribe to short of
/// parsing "adb track-devices", so polling is the honest mechanism. It only
/// runs while an Android project is the startup project, so the cost is
/// bounded to the sessions that care.
/// <para>
/// <see cref="DevicesChanged"/> is raised only when the set of devices
/// actually differs from the previous poll, so a UI can bind to it without
/// being rebuilt every couple of seconds.
/// </para>
/// </remarks>
public sealed class AndroidDeviceMonitor : IDisposable
{
    /// <summary>How long to wait between polls when none is requested sooner.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    readonly TimeSpan interval;
    readonly Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> listDevices;
    readonly object gate = new object();

    CancellationTokenSource cancellation;
    Task pollingTask;
    IReadOnlyList<AndroidDevice> devices = Array.Empty<AndroidDevice>();

    /// <summary>Creates a monitor polling adb at the default interval.</summary>
    public AndroidDeviceMonitor()
        : this(DefaultInterval, AndroidDebugBridge.ListDevicesAsync)
    {
    }

    /// <summary>
    /// Creates a monitor with an explicit interval and device source. The
    /// source is injectable so the polling behaviour can be tested without
    /// an Android SDK or a device.
    /// </summary>
    public AndroidDeviceMonitor(TimeSpan interval,
        Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> listDevices)
    {
        this.interval = interval;
        this.listDevices = listDevices ?? throw new ArgumentNullException(nameof(listDevices));
    }

    /// <summary>
    /// Raised when the attached devices differ from the previous poll,
    /// including the first poll and the drop to none. Raised on a background
    /// thread: a UI listener must marshal to its own thread.
    /// </summary>
    public event Action<IReadOnlyList<AndroidDevice>> DevicesChanged;

    /// <summary>The devices seen at the most recent poll.</summary>
    public IReadOnlyList<AndroidDevice> Devices
    {
        get
        {
            lock (gate)
                return devices;
        }
    }

    /// <summary>Whether the monitor is currently polling.</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
                return cancellation != null;
        }
    }

    /// <summary>
    /// Starts polling, or does nothing when already started. The first poll
    /// happens immediately, so a device already attached shows up at once
    /// rather than after the first interval.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (cancellation != null)
                return;
            cancellation = new CancellationTokenSource();
            pollingTask = PollAsync(cancellation.Token);
        }
    }

    /// <summary>
    /// Stops polling and forgets the devices, so a later Start reports
    /// whatever is attached then as a change. Safe to call when not started.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource stopping;
        lock (gate)
        {
            stopping = cancellation;
            cancellation = null;
            pollingTask = null;
            devices = Array.Empty<AndroidDevice>();
        }
        if (stopping == null)
            return;
        stopping.Cancel();
        stopping.Dispose();
    }

    async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var polled = await listDevices(cancellationToken).ConfigureAwait(false);
                Publish(polled ?? (IReadOnlyList<AndroidDevice>) Array.Empty<AndroidDevice>());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a polling failure end the loop: the device may
                // come back, and an IDE that quietly stopped watching would
                // then never notice.
                LoggingService.LogWarning($"Polling for Android devices failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    void Publish(IReadOnlyList<AndroidDevice> polled)
    {
        lock (gate)
        {
            if (SameDevices(devices, polled))
                return;
            devices = polled;
        }
        DevicesChanged?.Invoke(polled);
    }

    /// <summary>
    /// Whether two polls describe the same devices — same serials, in the
    /// same order, in the same state. State is part of it: a phone going from
    /// "unauthorized" to "device" is exactly the change a caller waits for.
    /// </summary>
    internal static bool SameDevices(IReadOnlyList<AndroidDevice> first, IReadOnlyList<AndroidDevice> second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first == null || second == null || first.Count != second.Count)
            return false;
        return !first.Where((device, index) =>
            !string.Equals(device.Serial, second[index].Serial, StringComparison.Ordinal)
            || !string.Equals(device.State, second[index].State, StringComparison.Ordinal)).Any();
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
