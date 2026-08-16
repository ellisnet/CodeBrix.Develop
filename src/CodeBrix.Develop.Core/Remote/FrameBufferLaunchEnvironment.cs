//
// FrameBufferLaunchEnvironment.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// The environment a LinuxFrameBuffer application is launched with on an SSH
/// device — the one place the set is written down, because it is handed over
/// two different ways: a plain run puts it in front of the launch command,
/// while a debug run gives it to the debug adapter (which passes it to the
/// process it starts). A variable added for one has to reach the other, and
/// keeping the list here is what makes that automatic.
/// </summary>
public static class FrameBufferLaunchEnvironment
{
    /// <summary>The runtime location: .NET lives in the device user's home, not on the system path.</summary>
    public const string DotnetRootVariable = "DOTNET_ROOT";

    /// <summary>Forces the software /dev/fb0 renderer; DRM master is never available over SSH.</summary>
    public const string UseDrmVariable = "CODEBRIX_FRAMEBUFFER_USE_DRM";

    /// <summary>Where the head takes device-orientation instructions from.</summary>
    public const string OrientationSourceVariable = "CODEBRIX_FRAMEBUFFER_ORIENTATION_SOURCE";

    /// <summary>Corrects a touch digitizer mounted upside-down (180), when the device needs it.</summary>
    public const string TouchRotationVariable = "CODEBRIX_FRAMEBUFFER_TOUCH_ROTATION";

    /// <summary>
    /// The launch environment, in a fixed order.
    /// <para>
    /// <paramref name="home"/> is the device user's home directory. A plain run
    /// passes the literal <c>$HOME</c> and lets the device's shell expand it; a
    /// debug run passes the resolved path, because the debug adapter hands the
    /// values to the process it starts without a shell in between.
    /// </para>
    /// <para>
    /// <paramref name="orientationEnabled"/> reflects the connect dialog's
    /// "Enable Orientation Changes": enabled, the app listens for the IDE's
    /// instructions; disabled, it opens no orientation source at all. Either
    /// way the device's own accelerometer is ignored during testing, even for
    /// an app that declared UseOrientationSensor.
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Create(
        string home, int touchRotationDegrees, bool orientationEnabled)
    {
        if (string.IsNullOrEmpty(home))
            throw new ArgumentException("The device home directory is required.", nameof(home));

        var environment = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(DotnetRootVariable, $"{home}/.dotnet"),
            new KeyValuePair<string, string>(UseDrmVariable, "0"),
            new KeyValuePair<string, string>(OrientationSourceVariable, orientationEnabled ? "develop" : "none"),
        };
        // Only a device that actually needs the correction gets the variable:
        // the head warns about values it does not honor, and an absent
        // variable is the normal case.
        if (touchRotationDegrees != 0)
        {
            environment.Add(new KeyValuePair<string, string>(TouchRotationVariable,
                touchRotationDegrees.ToString(CultureInfo.InvariantCulture)));
        }
        return environment;
    }

    /// <summary>
    /// The same set as a dictionary, for the debug adapter's launch request.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CreateMap(
        string home, int touchRotationDegrees, bool orientationEnabled)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in Create(home, touchRotationDegrees, orientationEnabled))
            map[variable.Key] = variable.Value;
        return map;
    }

    /// <summary>
    /// The variables formatted for a POSIX shell command line, values quoted:
    /// <c>KEY="value" KEY="value"</c>.
    /// </summary>
    public static string ToShellAssignments(IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var text = new StringBuilder();
        foreach (var variable in environment)
        {
            if (text.Length > 0)
                text.Append(' ');
            text.Append(variable.Key).Append("=\"").Append(variable.Value).Append('"');
        }
        return text.ToString();
    }
}
