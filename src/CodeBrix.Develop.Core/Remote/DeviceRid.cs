//
// DeviceRid.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// Maps a device's reported architecture (systemd spelling, as captured into
/// <see cref="SshDeviceIdentity.Architecture"/>) to the .NET runtime
/// identifier the app must be published for. The RID tracks the DEVICE, not
/// the IDE host — so an x64 IDE deploying to an arm64 device publishes
/// linux-arm64, and vice versa.
/// </summary>
public static class DeviceRid
{
    /// <summary>
    /// The linux RID for the given architecture, or null when it is unknown
    /// or unrecognized (the caller then cannot deploy).
    /// </summary>
    public static string ForArchitecture(string architecture) => architecture switch
    {
        "x86-64" or "x86_64" or "amd64" => "linux-x64",
        "arm64" or "aarch64" => "linux-arm64",
        "arm" or "armv7l" or "armhf" => "linux-arm",
        "x86" or "i386" or "i686" => "linux-x86",
        _ => null,
    };
}
