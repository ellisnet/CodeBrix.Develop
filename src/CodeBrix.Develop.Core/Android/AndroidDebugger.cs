//
// AndroidDebugger.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (where the on-device debugger lives on an Android device, how it is
//      placed there, and how it is started)
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeBrix.Develop.Core.Android;

/// <summary>
/// The rules of debugging a .NET app on an Android device: which payload an
/// ABI needs, where that payload goes inside the app's sandbox, the shell
/// commands that put it there and start it, and the parsers for what the
/// device answers with.
/// <para>
/// The debugger cannot run from /data/local/tmp — SELinux will not let the
/// app's uid execute anything there — so the payload is staged in
/// /data/local/tmp (which adb push can write) and then copied INTO the app's
/// own data directory by <c>run-as</c>, which runs as the app. Everything
/// afterwards happens under <c>run-as</c> for the same reason.
/// </para>
/// <para>
/// Everything here is a string, a path or a parser, so the rules are testable
/// without a device on the other end — the same shape as
/// <see cref="Remote.RemoteDebugger"/> for SSH devices.
/// </para>
/// </summary>
public static class AndroidDebugger
{
    /// <summary>The ABI of a 64-bit ARM device, as ro.product.cpu.abi reports it.</summary>
    public const string Arm64Abi = "arm64-v8a";

    /// <summary>The ABI of a 64-bit x86 device or emulator, as ro.product.cpu.abi reports it.</summary>
    public const string X64Abi = "x86_64";

    /// <summary>The debugger executable's name, in the payload and on the device.</summary>
    public const string DebuggerFileName = "netcoredbg";

    /// <summary>The file the on-device debugger's own output is redirected to.</summary>
    public const string OutputFileName = "ncdbg.out";

    /// <summary>The name of the payload stamp file kept beside the debugger on the device.</summary>
    public const string StampFileName = "payload.sha256";

    /// <summary>
    /// The world-writable folder adb pushes the payload to before
    /// <c>run-as</c> copies it into the app sandbox. adb cannot write into an
    /// app's data directory and the app cannot read the IDE's files, so this
    /// is the one place both sides can reach.
    /// </summary>
    public const string StagingDirectory = "/data/local/tmp/codebrix-ncdbg";

    /// <summary>
    /// The port the on-device debugger listens on by default. Nothing else in
    /// the app's namespace binds it, and <c>adb forward</c> maps it to
    /// whatever host port is free.
    /// </summary>
    public const int DefaultDevicePort = 4711;

    /// <summary>Whether an ABI reported by a device is one the IDE carries a debugger for.</summary>
    public static bool IsSupportedAbi(string abi) => PayloadFolderName(abi) != null;

    /// <summary>
    /// The IDE-output folder holding the debugger payload for the given ABI,
    /// or null for an ABI with no payload. These are siblings of the desktop
    /// debugger's own "netcoredbg" folder rather than children of it, because
    /// that folder is copied wholesale to SSH devices — an Android binary
    /// inside it would be deployed to every Linux device as well.
    /// </summary>
    public static string PayloadFolderName(string abi) => abi switch
    {
        Arm64Abi => "netcoredbg-android-arm64",
        X64Abi => "netcoredbg-android-x64",
        _ => null,
    };

    /// <summary>
    /// The full path of the payload folder for the given ABI in the IDE's own
    /// output, or null for an unsupported ABI.
    /// </summary>
    public static string LocalPayloadDirectory(string abi)
    {
        var folder = PayloadFolderName(abi);
        return folder == null ? null : Path.Combine(AppContext.BaseDirectory, folder);
    }

    /// <summary>
    /// The NuGet package that carries the payload for the given ABI. Named in
    /// the message shown when the payload is missing, because "install this
    /// package" is the only useful thing to say about it.
    /// </summary>
    public static string PayloadPackageName(string abi) => abi switch
    {
        Arm64Abi => "CodeBrix.Develop.Debug.AndroidArm64",
        X64Abi => "CodeBrix.Develop.Debug.AndroidX64",
        _ => null,
    };

    /// <summary>
    /// The explanation shown when the device's ABI has no debugger payload in
    /// the IDE's output — either because the ABI is not supported at all, or
    /// because the package that carries it is not installed.
    /// </summary>
    public static string PayloadMissingMessage(string abi)
    {
        var folder = PayloadFolderName(abi);
        if (folder == null)
        {
            return $"This device reports the ABI '{abi}', and Android debugging is available for " +
                $"{Arm64Abi} and {X64Abi} devices only.";
        }
        return $"The Android debugger for {abi} was not found in the IDE's own folder " +
            $"('{folder}'). It comes from the {PayloadPackageName(abi)} package — add it to " +
            "CodeBrix.Develop and rebuild the IDE.";
    }

    /// <summary>The debugger's folder inside the app's data directory.</summary>
    public static string DebuggerDirectory(string package) => $"/data/data/{package}/ncdbg";

    /// <summary>The debugger executable's full path inside the app's data directory.</summary>
    public static string DebuggerPath(string package) => $"{DebuggerDirectory(package)}/{DebuggerFileName}";

    /// <summary>
    /// The app's loose assemblies and PDBs, deployed there by a Debug build's
    /// fast deployment. The debugger reads its symbols from this folder.
    /// </summary>
    public static string AssemblyDirectory(string package, string abi) =>
        $"/data/data/{package}/files/.__override__/{abi}";

    /// <summary>
    /// The app's cache directory, used as TMPDIR: the debugger and the
    /// runtime both want somewhere writable, and an app may not write
    /// anywhere outside its own sandbox.
    /// </summary>
    public static string CacheDirectory(string package) => $"/data/data/{package}/cache";

    /// <summary>The stamp file recording which payload version is on the device.</summary>
    public static string StampPath(string package) => $"{DebuggerDirectory(package)}/{StampFileName}";

    /// <summary>
    /// Marks the app as the one ActivityManager will hold for a debugger.
    /// MUST be applied BEFORE the app is launched: applied to an app that is
    /// already running it RESTARTS the process, orphaning the process id the
    /// session was about to attach to. It also stops Android reporting the
    /// app as not responding while it sits at a breakpoint.
    /// </summary>
    public static string SetDebugAppCommand(string package) =>
        $"am set-debug-app --persistent {Validated(package, nameof(package))}";

    /// <summary>
    /// Undoes <see cref="SetDebugAppCommand"/>. Left in place, the persistent
    /// debug-app marking outlives the IDE and changes how the app behaves the
    /// next time the user starts it by hand.
    /// </summary>
    public const string ClearDebugAppCommand = "am clear-debug-app";

    /// <summary>Stops the app and every process of it.</summary>
    public static string ForceStopCommand(string package) =>
        $"am force-stop {Validated(package, nameof(package))}";

    /// <summary>
    /// Asks the package manager which activity a launch of the app would
    /// start. The app's launcher activity is not something the IDE can
    /// assume: it is whatever the manifest declares.
    /// </summary>
    public static string ResolveActivityCommand(string package) =>
        $"cmd package resolve-activity --brief {Validated(package, nameof(package))}";

    /// <summary>Starts the given "package/activity" component.</summary>
    public static string StartActivityCommand(string component) =>
        $"am start -n {Validated(component, nameof(component))}";

    /// <summary>Reports the process id(s) of the running app, or nothing when it is not running.</summary>
    public static string PidOfCommand(string package) =>
        $"pidof {Validated(package, nameof(package))}";

    /// <summary>
    /// Reads the process's memory map, which is where the app's native
    /// library directory under /data/app is discovered. Only the app itself
    /// may read its own /proc entry, hence <c>run-as</c>.
    /// </summary>
    public static string ReadMapsCommand(string package, int pid) =>
        $"run-as {Validated(package, nameof(package))} cat /proc/{pid}/maps";

    /// <summary>
    /// Reads the payload stamp already on the device. Fails harmlessly (with
    /// no output) the first time, when nothing has been placed yet.
    /// </summary>
    public static string ReadStampCommand(string package) =>
        $"run-as {Validated(package, nameof(package))} cat {StampPath(package)}";

    /// <summary>
    /// Copies the staged payload into the app's data directory and makes the
    /// debugger executable — as the app, which is the only uid that may write
    /// there and the only one that may execute from there.
    /// </summary>
    public static string InstallPayloadCommand(string package)
    {
        var validated = Validated(package, nameof(package));
        var directory = DebuggerDirectory(validated);
        return $"run-as {validated} sh -c 'mkdir -p {directory} && cp {StagingDirectory}/* {directory}/ " +
            $"&& chmod 755 {directory}/{DebuggerFileName}'";
    }

    /// <summary>
    /// Records which payload is now on the device, so the next debug session
    /// can skip the push entirely when nothing has changed.
    /// </summary>
    public static string WriteStampCommand(string package, string stamp)
    {
        var validated = Validated(package, nameof(package));
        return $"run-as {validated} sh -c 'printf %s {Validated(stamp, nameof(stamp))} > {StampPath(validated)}'";
    }

    /// <summary>
    /// Kills any debugger left running in the app's sandbox — belt and braces
    /// after a DAP disconnect, and the way a debugger orphaned by a crashed
    /// session is cleared before the next one starts.
    /// </summary>
    public static string KillDebuggerCommand(string package) =>
        $"run-as {Validated(package, nameof(package))} pkill -9 {DebuggerFileName}";

    /// <summary>
    /// Reports the process id of the on-device debugger, so a launch can wait
    /// for it to be up before connecting. Run as the app, whose sandbox the
    /// debugger runs in; an empty answer means it is not running.
    /// </summary>
    public static string PidOfDebuggerCommand(string package) =>
        $"run-as {Validated(package, nameof(package))} pidof {DebuggerFileName}";

    /// <summary>
    /// The kernel's socket tables, read to learn whether the debugger's port
    /// is listening yet. Necessary because <c>adb forward</c> ACCEPTS a host
    /// connection before it tries the device end: a connect to the forwarded
    /// port succeeds even when nothing listens on the device, and the first
    /// message then finds the connection closed. IPv6 is included because a
    /// listener bound to any address can appear in either table.
    /// </summary>
    public const string ListeningSocketsCommand = "cat /proc/net/tcp /proc/net/tcp6";

    /// <summary>
    /// Reads the debugger's own output file (stdout and stderr), the place a
    /// debugger that failed to start explains itself.
    /// </summary>
    public static string ReadDebuggerOutputCommand(string package) =>
        $"run-as {Validated(package, nameof(package))} cat {DebuggerDirectory(package)}/{OutputFileName}";

    /// <summary>
    /// The environment the on-device debugger is started with. The debugger
    /// carries an Android compatibility layer that reads exactly these
    /// variables: where the app's assemblies were deployed, where its CoreCLR
    /// lives, and where it may write temporary files. The command timeout is
    /// raised because attaching to an app on a phone takes several seconds,
    /// comfortably more than the default allows.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ServerEnvironment(string package, string abi,
        string libraryDirectory)
    {
        var validatedPackage = Validated(package, nameof(package));
        return new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("NETCOREDBG_ANDROID_ASSEMBLY_DIR",
                AssemblyDirectory(validatedPackage, Validated(abi, nameof(abi)))),
            new KeyValuePair<string, string>("NETCOREDBG_ANDROID_CLR_DIR",
                Validated(libraryDirectory, nameof(libraryDirectory))),
            new KeyValuePair<string, string>("LD_LIBRARY_PATH", Validated(libraryDirectory, nameof(libraryDirectory))),
            new KeyValuePair<string, string>("TMPDIR", CacheDirectory(validatedPackage)),
            new KeyValuePair<string, string>("NETCOREDBG_COMMAND_TIMEOUT_MS", "60000"),
        };
    }

    /// <summary>
    /// Starts the on-device debugger in DAP server mode, in the background,
    /// with its own output captured to a file in the app's sandbox.
    /// <para>
    /// THE QUOTING RULE: the whole command after <c>sh -c</c> is wrapped in
    /// SINGLE quotes, which the device's shell strips before <c>run-as</c>
    /// hands the rest to the app's shell. Nothing inside may therefore
    /// contain a quote of its own — there is no escaping level left to spend
    /// — so every value that goes into the line (the package, the ABI, the
    /// library directory, and any extra environment) is validated and a value
    /// carrying a quote is refused rather than silently mangled. Every value
    /// the pipeline actually produces is a path or an identifier, so this
    /// costs nothing and closes the injection.
    /// </para>
    /// <paramref name="extraEnvironment"/> is appended after the standard
    /// variables in ordinal name order, so the command line is deterministic
    /// whatever order the caller's dictionary enumerates in.
    /// </summary>
    public static string StartServerCommand(string package, string abi, string libraryDirectory, int devicePort,
        IReadOnlyDictionary<string, string> extraEnvironment = null)
    {
        var validatedPackage = Validated(package, nameof(package));
        var assignments = new List<string>();
        foreach (var variable in ServerEnvironment(validatedPackage, abi, libraryDirectory))
            assignments.Add($"{variable.Key}={variable.Value}");
        if (extraEnvironment != null)
        {
            foreach (var variable in extraEnvironment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                assignments.Add($"{Validated(variable.Key, nameof(extraEnvironment))}=" +
                    $"{Validated(variable.Value, nameof(extraEnvironment))}");
            }
        }

        var directory = DebuggerDirectory(validatedPackage);
        return $"run-as {validatedPackage} sh -c 'env {string.Join(" ", assignments)} " +
            $"{directory}/{DebuggerFileName} --interpreter=vscode --server={devicePort} " +
            $"> {directory}/{OutputFileName} 2>&1 &'";
    }

    // The quoting rule of StartServerCommand, applied to every value that can
    // reach a command line. A quote would break out of the single-quoted
    // shell word; refusing beats producing a command that means something
    // other than what it says.
    static string Validated(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A device command needs a value here, and none was given.", parameterName);
        if (value.IndexOf('\'') >= 0 || value.IndexOf('"') >= 0)
        {
            throw new ArgumentException(
                $"'{value}' contains a quote, which cannot appear in a device command line.", parameterName);
        }
        return value;
    }

    /// <summary>
    /// The device's primary ABI, from the output of
    /// <c>getprop ro.product.cpu.abi</c>. adb's shell hands back CRLF line
    /// endings, which have to come off before the value is compared with
    /// anything.
    /// </summary>
    public static string ParseAbi(string getpropOutput)
    {
        var abi = (getpropOutput ?? "").Trim().Trim('\r', '\n').Trim();
        return abi.Length == 0 ? null : abi;
    }

    /// <summary>
    /// Whether the text of /proc/net/tcp (and /proc/net/tcp6) shows a socket
    /// LISTENING on the given port. Each data line is
    /// <c>sl local_address rem_address st ...</c>, with the address as
    /// hex <c>ip:port</c> and the state as two hex digits; 0A is LISTEN.
    /// </summary>
    /// <param name="socketTableText">The concatenated tables as the device printed them.</param>
    /// <param name="port">The TCP port, host byte order.</param>
    /// <returns>True when a listener on <paramref name="port"/> is present.</returns>
    public static bool IsPortListening(string socketTableText, int port)
    {
        if (string.IsNullOrEmpty(socketTableText))
            return false;
        var wanted = ":" + port.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var rawLine in socketTableText.Split('\n'))
        {
            var fields = rawLine.Trim().Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !fields[0].EndsWith(":", StringComparison.Ordinal))
                continue; // the header line, or nothing
            if (fields[1].EndsWith(wanted, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fields[3], "0A", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The app's process id from the output of <c>pidof</c>, or null when the
    /// app is not running. pidof lists every process of the package; the
    /// first is the main one, which is the process a debugger attaches to.
    /// </summary>
    public static int? ParsePid(string pidofOutput)
    {
        if (string.IsNullOrWhiteSpace(pidofOutput))
            return null;
        var first = pidofOutput.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null && int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : (int?) null;
    }

    // The app's native library directory under /data/app. The install path
    // carries randomized segments ("~~<random>==/<package>-<random>=="), so it
    // can only be discovered, never constructed.
    static readonly Regex LibraryDirectoryPattern =
        new Regex(@"/data/app/[^\s]*/lib/[A-Za-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The directory holding the app's libcoreclr.so, read out of a running
    /// process's /proc/&lt;pid&gt;/maps. It is where the debugger's
    /// compatibility layer is pointed for the runtime's own libraries, and
    /// there is nowhere else to learn it: the path under /data/app contains
    /// randomized segments the installer chooses. Null when the process maps
    /// no CoreCLR — which is what a MonoVM app looks like.
    /// </summary>
    public static string ParseLibraryDirectory(string mapsText)
    {
        if (string.IsNullOrEmpty(mapsText))
            return null;

        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in mapsText.Split('\n'))
        {
            if (line.IndexOf("libcoreclr.so", StringComparison.Ordinal) < 0)
                continue;
            var match = LibraryDirectoryPattern.Match(line);
            if (match.Success)
                directories.Add(match.Value);
        }
        return directories.Count == 0 ? null : directories.First();
    }

    /// <summary>
    /// The "package/activity" component from the output of
    /// <c>cmd package resolve-activity --brief</c>, whose answer is its LAST
    /// line (the lines before it describe the resolution). Null when the
    /// output names no component — an app with no launcher activity, or a
    /// package the device does not have.
    /// </summary>
    public static string ParseResolvedActivity(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var last = output
            .Split('\n')
            .Select(line => line.Trim().Trim('\r').Trim())
            .LastOrDefault(line => line.Length > 0);
        return last != null && last.IndexOf('/') > 0 ? last : null;
    }

    /// <summary>
    /// A fingerprint of the payload folder: SHA-256 over each file's name and
    /// contents, in ordinal name order, as lowercase hex. Comparing it with
    /// the stamp on the device is what makes "push the debugger" a one-time
    /// cost per device instead of a per-session one — and what makes a
    /// REBUILT debugger reach the device without anyone having to remember to
    /// clear anything.
    /// </summary>
    public static string ComputePayloadStamp(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The debugger payload folder was not found: {directory}");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.GetFiles(directory)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hash.AppendData(new byte[] { 0 });
            using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// A free TCP port on the loopback interface, for the host end of the
    /// adb forward. Asking the operating system for port 0 and releasing it
    /// is the only reliable way: any port picked from a range might be taken
    /// by the time it is used, and this narrows that window to nothing worth
    /// worrying about.
    /// </summary>
    public static int FindFreeHostPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint) listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
