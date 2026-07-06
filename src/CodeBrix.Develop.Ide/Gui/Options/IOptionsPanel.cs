//
// IOptionsPanel.cs
//
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
// Copyright (c) 2026 Jeremy Ellis and contributors
//     (adapted from MonoDevelop for CodeBrix.Develop: GTK 4 widgets, plus a
//      CancelChanges hook for live-preview panels)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Options; //was previously: MonoDevelop.Ide.Gui.Dialogs

/// <summary>
/// One page of the Options dialog. Panels are created lazily when their
/// section is first visited; ApplyChanges runs for every instantiated panel
/// when the dialog is confirmed with OK.
/// </summary>
public interface IOptionsPanel
{
    /// <summary>Called once, before <see cref="CreatePanelWidget"/>.</summary>
    void Initialize(OptionsDialog dialog);

    /// <summary>Creates the panel's widget (placed inside a scrollable page).</summary>
    Gtk.Widget CreatePanelWidget();

    /// <summary>Whether the panel applies to the current context.</summary>
    bool IsVisible();

    /// <summary>Validates pending changes; returning false keeps the dialog open.</summary>
    bool ValidateChanges();

    /// <summary>
    /// Whether the panel currently holds changes that have not been applied
    /// (used, for example, to offer saving before an application restart).
    /// </summary>
    bool HasUnsavedChanges();

    /// <summary>Commits pending changes (the dialog was confirmed with OK).</summary>
    void ApplyChanges();

    /// <summary>
    /// Reverts any live-preview effects (the dialog was dismissed with
    /// Cancel or closed without OK).
    /// </summary>
    void CancelChanges();
}
