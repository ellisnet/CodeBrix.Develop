//
// ThemeCssGenerator.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Text;

namespace CodeBrix.Develop.Ide.Themes;

/// <summary>
/// Translates a VS Code theme's workbench colors into the GTK 4 CSS that
/// styles the whole CodeBrix.Develop chrome: menus, toolbar, pads, tabs,
/// status bar, dialogs, and common widgets. Colors the theme does not
/// define fall back to VS Code's own dark/light defaults.
/// </summary>
static class ThemeCssGenerator
{
    /// <summary>Generates the application CSS for the given theme.</summary>
    public static string Generate(VSCodeTheme theme)
    {
        var dark = theme.Info.IsDark;
        string Col(string darkFallback, string lightFallback, params string[] keys) =>
            theme.GetColor(dark ? darkFallback : lightFallback, keys);

        var fg = Col("#CCCCCC", "#333333", "foreground", "editor.foreground");
        var editorBg = Col("#1E1E1E", "#FFFFFF", "editor.background");
        var sidebarBg = Col("#252526", "#F3F3F3", "sideBar.background");
        var sidebarFg = Col("#CCCCCC", "#333333", "sideBar.foreground", "foreground", "editor.foreground");
        var titlebarBg = Col("#3C3C3C", "#DDDDDD", "titleBar.activeBackground", "sideBar.background");
        var titlebarFg = Col("#CCCCCC", "#333333", "titleBar.activeForeground", "sideBar.foreground", "editor.foreground");
        var menuBg = Col("#252526", "#FFFFFF", "menu.background", "dropdown.background", "sideBar.background");
        var menuFg = Col("#CCCCCC", "#333333", "menu.foreground", "dropdown.foreground", "editor.foreground");
        var menuSelBg = Col("#04395E", "#0060C0", "menu.selectionBackground", "list.activeSelectionBackground");
        var menuSelFg = Col("#FFFFFF", "#FFFFFF", "menu.selectionForeground", "list.activeSelectionForeground");
        var listSelBg = Col("#094771", "#0060C0", "list.activeSelectionBackground");
        var listSelFg = Col("#FFFFFF", "#FFFFFF", "list.activeSelectionForeground");
        var listHovBg = Col("#2A2D2E", "#E8E8E8", "list.hoverBackground");
        var tabsBg = Col("#252526", "#F3F3F3", "editorGroupHeader.tabsBackground", "sideBar.background");
        var tabActiveBg = Col("#1E1E1E", "#FFFFFF", "tab.activeBackground", "editor.background");
        var tabActiveFg = Col("#FFFFFF", "#333333", "tab.activeForeground", "editor.foreground");
        var tabInactiveBg = Col("#2D2D2D", "#ECECEC", "tab.inactiveBackground", "editorGroupHeader.tabsBackground");
        var tabInactiveFg = Col("rgba(255, 255, 255, 0.5)", "rgba(51, 51, 51, 0.7)", "tab.inactiveForeground");
        var statusBg = Col("#007ACC", "#007ACC", "statusBar.background", "titleBar.activeBackground");
        var statusFg = Col("#FFFFFF", "#FFFFFF", "statusBar.foreground");
        var panelBg = Col("#1E1E1E", "#FFFFFF", "panel.background", "editor.background");
        var border = Col("#454545", "#C8C8C8", "panel.border", "editorGroup.border", "widget.border", "contrastBorder");
        var inputBg = Col("#3C3C3C", "#FFFFFF", "input.background");
        var inputFg = Col("#CCCCCC", "#333333", "input.foreground", "editor.foreground");
        var buttonBg = Col("#0E639C", "#007ACC", "button.background");
        var buttonFg = Col("#FFFFFF", "#FFFFFF", "button.foreground");
        var buttonHovBg = Col("#1177BB", "#0062A3", "button.hoverBackground", "button.background");
        var dropdownBg = Col("#3C3C3C", "#FFFFFF", "dropdown.background", "input.background");
        var dropdownFg = Col("#F0F0F0", "#333333", "dropdown.foreground", "editor.foreground");
        var scrollSlider = Col("rgba(121, 121, 121, 0.4)", "rgba(100, 100, 100, 0.4)", "scrollbarSlider.background");
        var scrollSliderHov = Col("rgba(100, 100, 100, 0.7)", "rgba(100, 100, 100, 0.7)", "scrollbarSlider.hoverBackground");
        var selectionBg = Col("#264F78", "#ADD6FF", "editor.selectionBackground");
        var focusBorder = Col("#007FD4", "#0090F1", "focusBorder");

        var css = new StringBuilder();
        css.AppendLine($$"""
            window {
                background-color: {{sidebarBg}};
                color: {{fg}};
            }

            menubar {
                background-color: {{titlebarBg}};
                color: {{titlebarFg}};
            }
            menubar > item:hover, menubar > item:selected {
                background-color: {{menuSelBg}};
                color: {{menuSelFg}};
            }
            popover > contents {
                background-color: {{menuBg}};
                color: {{menuFg}};
            }
            popover.menu > contents {
                border: 1px solid {{border}};
            }
            popover.menu modelbutton:hover, popover.menu modelbutton:selected {
                background-color: {{menuSelBg}};
                color: {{menuSelFg}};
            }

            .toolbar {
                background-color: {{titlebarBg}};
                color: {{titlebarFg}};
            }
            .toolbar button {
                background-image: none;
                background-color: transparent;
                color: {{titlebarFg}};
            }
            .toolbar button:hover {
                background-color: alpha({{titlebarFg}}, 0.15);
            }

            .cb-statusbar {
                background-color: {{statusBg}};
                color: {{statusFg}};
            }

            .cb-sidebar, .cb-sidebar listview {
                background-color: {{sidebarBg}};
                color: {{sidebarFg}};
            }
            listview > row:hover {
                background-color: {{listHovBg}};
            }
            listview > row:selected, listbox > row:selected {
                background-color: {{listSelBg}};
                color: {{listSelFg}};
            }
            listbox > row:hover:not(:selected) {
                background-color: {{listHovBg}};
            }
            listbox {
                background-color: transparent;
            }

            notebook > header {
                background-color: {{tabsBg}};
            }
            notebook > header > tabs > tab {
                background-color: {{tabInactiveBg}};
                color: {{tabInactiveFg}};
            }
            notebook > header > tabs > tab:checked {
                background-color: {{tabActiveBg}};
                color: {{tabActiveFg}};
            }
            notebook > stack {
                background-color: {{editorBg}};
            }

            .cb-output textview, .cb-output textview > text {
                background-color: {{panelBg}};
                color: {{fg}};
            }
            .cb-output textview > text selection {
                background-color: {{selectionBg}};
            }

            paned > separator, separator {
                background-color: {{border}};
            }

            entry, entry > text, text > text {
                background-color: {{inputBg}};
                color: {{inputFg}};
            }

            button {
                background-image: none;
                background-color: {{dropdownBg}};
                color: {{dropdownFg}};
                border: 1px solid {{border}};
                box-shadow: none;
                text-shadow: none;
            }
            button:hover {
                background-color: alpha({{fg}}, 0.1);
            }
            button:focus-visible, dropdown:focus-within > button {
                outline-color: {{focusBorder}};
            }
            button.suggested-action {
                background-color: {{buttonBg}};
                color: {{buttonFg}};
                border-color: transparent;
            }
            button.suggested-action:hover {
                background-color: {{buttonHovBg}};
            }

            dropdown > button, dropdown listview {
                background-color: {{dropdownBg}};
                color: {{dropdownFg}};
            }

            scrollbar > range > trough > slider {
                background-color: {{scrollSlider}};
            }
            scrollbar > range > trough > slider:hover {
                background-color: {{scrollSliderHov}};
            }

            tooltip, tooltip.background {
                background-color: {{menuBg}};
                color: {{menuFg}};
                border: 1px solid {{border}};
            }
            """);
        return css.ToString();
    }
}
