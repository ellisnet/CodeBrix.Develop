//
// SignatureHelpPopover.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System;
using System.Text;
using CodeBrix.Develop.Core.TypeSystem;
using Gdk = CodeBrix.Develop.UI.Gdk;
using Gtk = CodeBrix.Develop.UI.Gtk;

namespace CodeBrix.Develop.Ide.Gui.Completion;

/// <summary>
/// The parameter-hint bubble shown above the caret while typing the
/// arguments of a call: the best overload with the active parameter in
/// bold, plus an overload counter. Never takes keyboard focus.
/// </summary>
public class SignatureHelpPopover
{
    readonly Gtk.Popover popover;
    readonly Gtk.Label label;

    /// <summary>The signature help currently displayed, or null.</summary>
    public SignatureHelpResult? Current { get; private set; }

    /// <summary>Whether the bubble is currently shown.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>Creates the popover, parented to the given editor widget.</summary>
    public SignatureHelpPopover(Gtk.Widget parent)
    {
        label = Gtk.Label.New(null);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetMaxWidthChars(110);
        label.SetMarginStart(8);
        label.SetMarginEnd(8);
        label.SetMarginTop(5);
        label.SetMarginBottom(5);

        popover = Gtk.Popover.New();
        popover.SetChild(label);
        popover.SetParent(parent);
        popover.SetAutohide(false); // never steal focus from the editor
        popover.SetHasArrow(false);
        popover.SetPosition(Gtk.PositionType.Top);
    }

    /// <summary>Shows (or updates) the bubble at the caret rectangle.</summary>
    public void Show(Gdk.Rectangle caret, SignatureHelpResult result)
    {
        Current = result;
        label.SetMarkup(BuildMarkup(result));
        popover.SetPointingTo(caret);
        if (!IsVisible)
            popover.Popup();
        IsVisible = true;
    }

    /// <summary>Hides the bubble.</summary>
    public void Hide()
    {
        Current = null;
        popover.Popdown();
        IsVisible = false;
    }

    static string BuildMarkup(SignatureHelpResult result)
    {
        var markup = new StringBuilder();
        var index = Math.Clamp(result.ActiveSignature, 0, result.Signatures.Count - 1);
        var signature = result.Signatures[index];

        if (result.Signatures.Count > 1)
            markup.Append($"<small>{index + 1} of {result.Signatures.Count}</small>  ");

        markup.Append("<tt>");
        markup.Append(Escape(signature.Prefix));
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            if (i > 0)
                markup.Append(", ");
            if (i == result.ActiveParameter
                || (i == signature.Parameters.Count - 1 && result.ActiveParameter >= signature.Parameters.Count))
                markup.Append("<b>").Append(Escape(signature.Parameters[i])).Append("</b>");
            else
                markup.Append(Escape(signature.Parameters[i]));
        }
        markup.Append(Escape(signature.Suffix));
        markup.Append("</tt>");
        return markup.ToString();
    }

    static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
