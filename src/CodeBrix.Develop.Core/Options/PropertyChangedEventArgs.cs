//
// PropertyChangedEventArgs.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (inspired by MonoDevelop.Core.PropertyChangedEventArgs, simplified
//      for CodeBrix.Develop)
// SPDX-License-Identifier: MIT
//

using System;

namespace CodeBrix.Develop.Core.Options;

/// <summary>
/// Event arguments describing a change to a single stored option value.
/// </summary>
public class PropertyChangedEventArgs : EventArgs
{
    /// <summary>The key of the option that changed.</summary>
    public string Key { get; }

    /// <summary>The previous value, or null when the option was not set before.</summary>
    public object OldValue { get; }

    /// <summary>The new value, or null when the option was removed.</summary>
    public object NewValue { get; }

    /// <summary>Creates event arguments for a changed option.</summary>
    public PropertyChangedEventArgs(string key, object oldValue, object newValue)
    {
        Key = key;
        OldValue = oldValue;
        NewValue = newValue;
    }
}
