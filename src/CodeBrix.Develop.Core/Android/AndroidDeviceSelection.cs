//
// AndroidDeviceSelection.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Collections.Generic;

namespace CodeBrix.Develop.Core.Android;

/// <summary>
/// Which attached device an Android launch should go to.
/// </summary>
public static class AndroidDeviceSelection
{
    /// <summary>
    /// The index of the device to select, or -1 when there are none.
    /// </summary>
    /// <param name="devices">The devices adb currently reports.</param>
    /// <param name="rememberedSerial">
    /// The serial the user deliberately chose in an earlier session, or "" if
    /// they never chose one.
    /// </param>
    /// <remarks>
    /// The rules, in order:
    /// <list type="number">
    /// <item>the remembered device, whenever it is attached — a deliberate
    /// choice outranks convenience, and stays the choice across sessions;</item>
    /// <item>otherwise the first READY device, so a working device is
    /// preferred over one that is merely plugged in;</item>
    /// <item>otherwise the first device of any kind, so an unauthorized phone
    /// is still selected and visible rather than silently ignored;</item>
    /// <item>otherwise nothing.</item>
    /// </list>
    /// A remembered device that is NOT attached is skipped without being
    /// forgotten: unplugging a phone must not permanently repoint the choice.
    /// </remarks>
    public static int PreferredIndex(IReadOnlyList<AndroidDevice> devices, string rememberedSerial)
    {
        if (devices == null || devices.Count == 0)
            return -1;

        if (!string.IsNullOrEmpty(rememberedSerial))
        {
            for (var i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].Serial, rememberedSerial, StringComparison.Ordinal))
                    return i;
            }
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i].IsReady)
                return i;
        }
        return 0;
    }
}
