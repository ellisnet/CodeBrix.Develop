//
// AndroidDevice.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core.Android;

/// <summary>
/// One entry from "adb devices -l": a phone, tablet or emulator adb can see.
/// </summary>
public sealed class AndroidDevice
{
    /// <summary>
    /// Creates a device entry. The serial is adb's own identifier for it —
    /// a hardware serial for a physical device, "emulator-5554" and the like
    /// for an emulator.
    /// </summary>
    public AndroidDevice(string serial, string state, string model, string product, string avdName = null)
    {
        Serial = serial ?? "";
        State = state ?? "";
        Model = model ?? "";
        Product = product ?? "";
        AvdName = avdName ?? "";
    }

    /// <summary>adb's identifier for the device, unique while it is attached.</summary>
    public string Serial { get; }

    /// <summary>
    /// What adb says about the device: "device" when it is usable, and
    /// otherwise a reason it is not — "offline", "unauthorized" (the user has
    /// not accepted this computer's debugging key yet), "no permissions".
    /// </summary>
    public string State { get; }

    /// <summary>The model as reported by adb, e.g. "SM_G781U1"; "" when unknown.</summary>
    public string Model { get; }

    /// <summary>The product as reported by adb, e.g. "r8quex"; "" when unknown.</summary>
    public string Product { get; }

    /// <summary>
    /// The AVD id of an emulator, e.g. "15-inch_HD_Device"; "" for a physical
    /// device. An emulator's adb model is the generic system image name
    /// ("sdk_gphone64_x86_64"), which is the same for every emulator the user
    /// has ever defined — the AVD id is the only thing that tells them apart.
    /// </summary>
    public string AvdName { get; }

    /// <summary>Whether this is an emulator rather than a physical device.</summary>
    public bool IsEmulator => AvdName.Length > 0;

    /// <summary>
    /// Whether the device is in a state an app can actually be deployed to.
    /// An attached-but-unauthorized phone is listed and NOT ready: showing it
    /// explains why nothing can be run on it, which an empty list would not.
    /// </summary>
    public bool IsReady => string.Equals(State, "device", StringComparison.Ordinal);

    /// <summary>
    /// How the device is named in the UI: "{name} - {serial}", where the name
    /// is the AVD id for an emulator and the model for a physical device, in
    /// both cases with underscores turned into spaces. Falls back to the
    /// serial alone when neither is known. A device that is not ready says so,
    /// because the name is the only place the user can find out.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // An emulator's adb model is the generic system-image name, the
            // same for every AVD the user has ever defined, so the AVD id is
            // what distinguishes it. Underscores are a transport artefact in
            // both; the casing is left exactly as reported.
            var name = Prettify(IsEmulator ? AvdName : Model);

            var label = name.Length > 0 ? $"{name} - {Serial}" : Serial;
            return IsReady ? label : $"{label} ({State})";
        }
    }

    /// <summary>
    /// Turns an adb-reported identifier into something readable: underscores
    /// become spaces. Nothing else — the casing an emulator or a phone reports
    /// is the casing the user chose or the manufacturer set.
    /// </summary>
    internal static string Prettify(string value)
        => string.IsNullOrEmpty(value) ? "" : value.Replace('_', ' ');

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}
