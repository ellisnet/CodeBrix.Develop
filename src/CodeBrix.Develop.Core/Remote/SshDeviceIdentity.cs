//
// SshDeviceIdentity.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text.Json;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// What an SSH-connected device says about itself, parsed from the outputs
/// of the identification commands: the DMI vendor and model files,
/// "hostnamectl --json=short", and "uname -m". Every value that could not
/// be captured is the literal <see cref="UnknownValue"/> — never blank,
/// never null.
/// </summary>
public sealed class SshDeviceIdentity
{
    /// <summary>The stored stand-in for a value the device did not yield.</summary>
    public const string UnknownValue = "unknown";

    SshDeviceIdentity(string vendor, string model, string operatingSystem, string kernel, string architecture)
    {
        Vendor = vendor;
        Model = model;
        OperatingSystem = operatingSystem;
        Kernel = kernel;
        Architecture = architecture;
    }

    /// <summary>The hardware vendor (DMI sys_vendor), e.g. "WinBook".</summary>
    public string Vendor { get; }

    /// <summary>The hardware model (DMI product_name), e.g. "TW700".</summary>
    public string Model { get; }

    /// <summary>The OS pretty name, e.g. "Debian GNU/Linux 13 (trixie)".</summary>
    public string OperatingSystem { get; }

    /// <summary>The kernel name and release, e.g. "Linux 6.12.101+deb13-amd64".</summary>
    public string Kernel { get; }

    /// <summary>The architecture in systemd spelling, e.g. "x86-64".</summary>
    public string Architecture { get; }

    /// <summary>
    /// Builds an identity from the raw command outputs. Any output may be
    /// null, empty, or malformed — the corresponding values parse to
    /// <see cref="UnknownValue"/>.
    /// </summary>
    public static SshDeviceIdentity Create(
        string vendorOutput, string modelOutput, string hostnamectlJson, string unameOutput)
    {
        var (operatingSystem, kernel) = ParseHostnamectl(hostnamectlJson);
        return new SshDeviceIdentity(
            Normalize(vendorOutput),
            Normalize(modelOutput),
            operatingSystem,
            kernel,
            NormalizeArchitecture(unameOutput));
    }

    /// <summary>
    /// Trims a raw command output (the DMI files end with a newline);
    /// blank or null becomes <see cref="UnknownValue"/>.
    /// </summary>
    public static string Normalize(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? UnknownValue : raw.Trim();

    // "hostnamectl --json=short" carries the OS pretty name and the kernel
    // name + release under stable keys. It does NOT carry an architecture
    // key — the text renderer computes that line itself — which is why the
    // architecture comes from uname instead.
    static (string OperatingSystem, string Kernel) ParseHostnamectl(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (UnknownValue, UnknownValue);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var operatingSystem = Normalize(GetString(root, "OperatingSystemPrettyName"));

            var kernelName = GetString(root, "KernelName");
            var kernelRelease = GetString(root, "KernelRelease");
            var kernel = string.IsNullOrWhiteSpace(kernelName) && string.IsNullOrWhiteSpace(kernelRelease)
                ? UnknownValue
                : Normalize($"{kernelName} {kernelRelease}".Trim());

            return (operatingSystem, kernel);
        }
        catch (JsonException)
        {
            return (UnknownValue, UnknownValue);
        }
    }

    static string GetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;

    // uname spells machine types its own way; the stored value uses the
    // systemd spelling ("x86-64", the one hostnamectl's text view shows) for
    // the machines a CodeBrix device could plausibly be. An unrecognized
    // machine type passes through as uname said it.
    static string NormalizeArchitecture(string unameOutput)
    {
        var machine = Normalize(unameOutput);
        return machine switch
        {
            "x86_64" => "x86-64",
            "aarch64" => "arm64",
            "armv7l" => "arm",
            "i386" or "i486" or "i586" or "i686" => "x86",
            _ => machine,
        };
    }
}
