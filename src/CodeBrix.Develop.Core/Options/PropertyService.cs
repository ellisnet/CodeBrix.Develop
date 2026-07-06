//
// PropertyService.cs
//
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (C) 2007 Novell, Inc (http://www.novell.com)
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop for CodeBrix.Develop: SQLite-backed
//      OptionsStore instead of the MonoDevelopProperties.xml file)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;

namespace CodeBrix.Develop.Core.Options; //was previously: MonoDevelop.Core

/// <summary>
/// The static facade over the application's single <see cref="OptionsStore"/>:
/// every configurable value in CodeBrix.Develop is read and written through
/// this service, so the whole configuration lives in one portable
/// options.sqlite file.
/// </summary>
public static class PropertyService
{
    static OptionsStore store;

    /// <summary>Whether <see cref="Initialize()"/> has been called.</summary>
    public static bool IsInitialized => store != null;

    /// <summary>
    /// The options store; only available after <see cref="Initialize()"/>.
    /// </summary>
    public static OptionsStore Store =>
        store ?? throw new InvalidOperationException("PropertyService.Initialize must be called first");

    /// <summary>
    /// The default options folder: the "options" subfolder of the
    /// application's per-user configuration folder (on Linux
    /// ~/.config/CodeBrix.Develop/options).
    /// </summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeBrix.Develop", "options");

    /// <summary>
    /// Opens the options store in the default folder, running the startup
    /// auto-backup and pruning sequence. Call once, before any UI renders.
    /// </summary>
    public static void Initialize() => Initialize(DefaultDirectory);

    /// <summary>Opens the options store in the given folder.</summary>
    public static void Initialize(string directoryPath)
    {
        if (store != null)
            throw new InvalidOperationException("PropertyService is already initialized");
        store = new OptionsStore(directoryPath);
    }

    /// <summary>Wraps an option in a typed <see cref="ConfigurationProperty{T}"/> handle.</summary>
    public static ConfigurationProperty<T> Wrap<T>(string property, T defaultValue) =>
        ConfigurationProperty.Create(property, defaultValue);

    /// <summary>Whether a value is stored for the given key.</summary>
    public static bool HasValue(string property) => Store.HasValue(property);

    /// <summary>Returns the stored value for the key, or the given default when not set.</summary>
    public static T Get<T>(string property, T defaultValue) => Store.Get(property, defaultValue);

    /// <summary>Returns the stored value for the key, or the type's default when not set.</summary>
    public static T Get<T>(string property) => Store.Get<T>(property);

    /// <summary>Stores a value for the key; a null value removes the key.</summary>
    public static void Set(string key, object val) => Store.Set(key, val);

    /// <summary>Registers a handler raised when the given key's value changes.</summary>
    public static void AddPropertyHandler(string propertyName, EventHandler<PropertyChangedEventArgs> handler) =>
        Store.AddPropertyHandler(propertyName, handler);

    /// <summary>Removes a handler previously added with <see cref="AddPropertyHandler"/>.</summary>
    public static void RemovePropertyHandler(string propertyName, EventHandler<PropertyChangedEventArgs> handler) =>
        Store.RemovePropertyHandler(propertyName, handler);
}
