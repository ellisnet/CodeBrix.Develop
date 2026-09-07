//
// AndroidDebugBridge.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.Develop.Core.Android;

/// <summary>
/// The little of the Android Debug Bridge (adb) the IDE needs: finding the
/// tool, working out which device a run went to, and stopping an app that is
/// running there.
/// </summary>
/// <remarks>
/// An Android app launched by "dotnet run" is NOT a child of that process —
/// the Android SDK's targets install and start it on the device, and the
/// local process merely follows logcat afterwards. Killing the local process
/// therefore leaves the app running on the device, which is why stopping a
/// run has to reach the device explicitly.
/// </remarks>
public static class AndroidDebugBridge
{
    /// <summary>The adb executable's path beneath an Android SDK folder.</summary>
    public const string AdbRelativePath = "platform-tools/adb";

    /// <summary>
    /// Locates the adb executable, or returns null when no Android SDK can be
    /// found. Deliberately does NOT search PATH: adb reaches PATH through a
    /// shell profile, which an IDE started from a desktop launcher never runs,
    /// so relying on it would work from a terminal and fail from the menu.
    /// </summary>
    public static string FindAdb() => FindAdb(DefaultSdkRoots());

    internal static string FindAdb(IEnumerable<string> sdkRoots)
    {
        if (sdkRoots == null)
            return null;
        foreach (var root in sdkRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var candidate = Path.Combine(root, "platform-tools", "adb");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// The Android SDK folders to search, most explicit first: the two
    /// environment variables the Android tooling defines, then the default
    /// location Android Studio installs to.
    /// </summary>
    internal static IEnumerable<string> DefaultSdkRoots()
    {
        yield return Environment.GetEnvironmentVariable("ANDROID_HOME");
        yield return Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, "Android", "Sdk");
    }

    /// <summary>
    /// Reads the device serial out of a build-output line, or returns null
    /// when the line says nothing about a device. The Android SDK's _Upload
    /// target announces its choice as "Found device: RFCRB0MPHYH", and that
    /// is the only authoritative statement of which device a run went to —
    /// asking adb afterwards would only guess, and would guess wrong whenever
    /// more than one device is attached.
    /// </summary>
    public static string ParseDeviceSerial(string outputLine)
    {
        if (string.IsNullOrEmpty(outputLine))
            return null;
        const string marker = "Found device:";
        var start = outputLine.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var serial = outputLine.Substring(start + marker.Length).Trim();
        return serial.Length == 0 ? null : serial;
    }

    /// <summary>
    /// Lists the devices adb can see, newest state each call. Returns an empty
    /// list when adb is missing or fails — "no Android SDK" and "no device
    /// plugged in" both mean the same thing to a caller deciding whether an
    /// app can be launched.
    /// </summary>
    public static async Task<IReadOnlyList<AndroidDevice>> ListDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var adb = FindAdb();
        if (adb == null)
            return Array.Empty<AndroidDevice>();

        var startInfo = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("devices");
        startInfo.ArgumentList.Add("-l");

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return Array.Empty<AndroidDevice>();
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return Array.Empty<AndroidDevice>();
            return await WithAvdNamesAsync(adb, ParseDevices(stdout), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Polling runs on a timer; a transient adb failure must not be
            // able to take the IDE down, and the next poll will say more.
            return Array.Empty<AndroidDevice>();
        }
    }

    // An AVD id never changes while a device is attached, so it is asked for
    // once per serial and remembered. Without the cache every poll would pay
    // a shell round trip per device, several times a minute, forever.
    static readonly Dictionary<string, string> avdNameCache = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Fills in each device's AVD name, asking the device itself the first
    /// time it is seen. Devices that are not ready are left alone: a shell
    /// command cannot run on an unauthorized or offline device.
    /// </summary>
    static async Task<IReadOnlyList<AndroidDevice>> WithAvdNamesAsync(string adb,
        IReadOnlyList<AndroidDevice> devices, CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
            return devices;

        var named = new List<AndroidDevice>(devices.Count);
        foreach (var device in devices)
        {
            string avdName;
            var known = false;
            lock (avdNameCache)
                known = avdNameCache.TryGetValue(device.Serial, out avdName);

            if (!known)
            {
                avdName = device.IsReady
                    ? await ReadAvdNameAsync(adb, device.Serial, cancellationToken).ConfigureAwait(false)
                    : "";
                // A device that was not ready is NOT cached: once the user
                // authorizes it, the next poll should ask properly.
                if (device.IsReady)
                {
                    lock (avdNameCache)
                        avdNameCache[device.Serial] = avdName;
                }
            }

            named.Add(string.IsNullOrEmpty(avdName)
                ? device
                : new AndroidDevice(device.Serial, device.State, device.Model, device.Product, avdName));
        }

        // Forget devices that are no longer attached, so a serial reused by a
        // different AVD (emulator ports are recycled) is asked about afresh.
        lock (avdNameCache)
        {
            foreach (var stale in avdNameCache.Keys
                         .Where(serial => !devices.Any(d => string.Equals(d.Serial, serial, StringComparison.Ordinal)))
                         .ToList())
                avdNameCache.Remove(stale);
        }
        return named;
    }

    /// <summary>
    /// The AVD id an emulator was started from, or "" for a physical device.
    /// Read from the ro.boot.qemu.avd_name property, which only an emulator
    /// sets — a plain shell property, so no emulator-console auth is needed.
    /// </summary>
    static async Task<string> ReadAvdNameAsync(string adb, string serial, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(serial);
        startInfo.ArgumentList.Add("shell");
        startInfo.ArgumentList.Add("getprop");
        startInfo.ArgumentList.Add("ro.boot.qemu.avd_name");

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return "";
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout.Trim() : "";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not knowing the AVD name costs a nicer label, nothing more.
            return "";
        }
    }

    /// <summary>
    /// Parses the output of "adb devices -l" — one device per line as
    /// "&lt;serial&gt; &lt;state&gt; key:value key:value …", after a header
    /// line. Lines adb writes about its own daemon are ignored.
    /// </summary>
    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidDevice>();
        if (string.IsNullOrWhiteSpace(output))
            return devices;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0
                || line.StartsWith("List of devices", StringComparison.Ordinal)
                || line.StartsWith("*", StringComparison.Ordinal)
                || line.StartsWith("adb server", StringComparison.Ordinal))
                continue;

            var fields = line.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                continue;

            string model = null;
            string product = null;
            for (var i = 2; i < fields.Length; i++)
            {
                var separator = fields[i].IndexOf(':');
                if (separator <= 0)
                    continue;
                var key = fields[i].Substring(0, separator);
                var value = fields[i].Substring(separator + 1);
                if (key == "model")
                    model = value;
                else if (key == "product")
                    product = value;
            }
            devices.Add(new AndroidDevice(fields[0], fields[1], model, product));
        }
        return devices;
    }

    /// <summary>
    /// Stops the given application on the device, returning whether adb
    /// reported success. A null or empty <paramref name="deviceSerial"/>
    /// leaves the device unspecified, which is correct when exactly one is
    /// attached and lets adb report the ambiguity itself when several are.
    /// Never throws for the ordinary failures (no SDK, no device, adb
    /// missing) — those come back as false with a message on
    /// <see cref="OutputReceived"/>.
    /// </summary>
    public static async Task<bool> ForceStopAsync(string applicationId, string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return false;

        var adb = FindAdb();
        if (adb == null)
        {
            OutputReceived?.Invoke(
                "Cannot stop the app on the device: no Android SDK found (looked at ANDROID_HOME, "
                + "ANDROID_SDK_ROOT and ~/Android/Sdk).");
            return false;
        }

        var startInfo = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(deviceSerial))
        {
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(deviceSerial);
        }
        startInfo.ArgumentList.Add("shell");
        startInfo.ArgumentList.Add("am");
        startInfo.ArgumentList.Add("force-stop");
        startInfo.ArgumentList.Add(applicationId);

        OutputReceived?.Invoke($"{adb} {string.Join(' ', startInfo.ArgumentList)}");
        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                return false;
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // "am force-stop" says nothing on success; anything it does say is
            // worth showing, and adb's own failures arrive on stderr.
            foreach (var text in new[] { stdout, stderr })
            {
                if (!string.IsNullOrWhiteSpace(text))
                    OutputReceived?.Invoke(text.TrimEnd());
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"Failed to stop the app on the device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Raised with the adb command line and anything adb reports, so the
    /// caller can show it in the same place as the rest of the run output.
    /// </summary>
    public static event Action<string> OutputReceived;

    /// <summary>
    /// The message of the exception the general-purpose runners throw when no
    /// Android SDK can be found. They cannot answer "false" like
    /// <see cref="ForceStopAsync"/> does: a debug session that cannot run adb
    /// has not failed to stop something, it cannot start at all.
    /// </summary>
    public const string NoSdkMessage =
        "no Android SDK found (looked at ANDROID_HOME, ANDROID_SDK_ROOT and ~/Android/Sdk)";

    /// <summary>
    /// Runs adb against one device and returns everything it said. The
    /// arguments are passed AFTER "-s &lt;serial&gt;", so they are the adb
    /// command itself ("shell", "push", "forward", …). Throws
    /// <see cref="InvalidOperationException"/> when adb cannot be found; a
    /// command that runs and fails comes back as a result with the exit code
    /// and the error text, because "the device said no" is an answer and not
    /// an exception.
    /// </summary>
    public static async Task<AdbResult> RunAsync(string serial, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments == null)
            throw new ArgumentNullException(nameof(arguments));

        var adb = FindAdb();
        if (adb == null)
            throw new InvalidOperationException($"Cannot run adb: {NoSdkMessage}.");

        var startInfo = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(serial))
        {
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(serial);
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        OutputReceived?.Invoke($"{adb} {string.Join(' ', startInfo.ArgumentList)}");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"adb could not be started ({adb}).");

        // Both streams are drained concurrently: a command that writes more
        // than a pipe buffer to one of them would block forever if the other
        // were read first.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new AdbResult(process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one shell command on the device. The command is passed as a
    /// SINGLE argument, so the device's own shell parses it — which is what
    /// lets a command carry redirections, pipes and quoting of its own.
    /// </summary>
    public static Task<AdbResult> ShellAsync(string serial, string command,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, new[] { "shell", command }, cancellationToken);

    /// <summary>Copies one local file to the device.</summary>
    public static Task<AdbResult> PushAsync(string serial, string localPath, string remotePath,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, new[] { "push", localPath, remotePath }, cancellationToken);

    /// <summary>
    /// Publishes a device port on this machine's loopback interface, so a
    /// server listening on the device can be connected to as though it were
    /// local.
    /// </summary>
    public static Task<AdbResult> ForwardAsync(string serial, int hostPort, int devicePort,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, new[] { "forward", $"tcp:{hostPort}", $"tcp:{devicePort}" }, cancellationToken);

    /// <summary>
    /// Withdraws a forward made by <see cref="ForwardAsync"/>. Forwards
    /// outlive the process that made them, so a session that ends without
    /// removing its own leaves the host port claimed until adb is restarted.
    /// </summary>
    public static Task<AdbResult> RemoveForwardAsync(string serial, int hostPort,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, new[] { "forward", "--remove", $"tcp:{hostPort}" }, cancellationToken);
}

/// <summary>
/// What one adb command said: its exit code and both of its output streams.
/// </summary>
public sealed class AdbResult
{
    /// <summary>Creates a result for a finished adb command.</summary>
    public AdbResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? "";
        StandardError = standardError ?? "";
    }

    /// <summary>adb's exit code; zero when the command succeeded.</summary>
    public int ExitCode { get; }

    /// <summary>Everything the command wrote to standard output.</summary>
    public string StandardOutput { get; }

    /// <summary>
    /// Everything the command wrote to standard error. adb reports its own
    /// failures here ("device not found", "more than one device"), while a
    /// shell command's failure text may arrive on either stream.
    /// </summary>
    public string StandardError { get; }

    /// <summary>Whether the command exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The output, with standard error appended when it said anything — what
    /// a caller wants to show a user, and what a parser wants to read when
    /// the interesting text could have arrived on either stream.
    /// </summary>
    public string CombinedOutput => StandardError.Trim().Length == 0
        ? StandardOutput
        : $"{StandardOutput}{StandardError}";
}
