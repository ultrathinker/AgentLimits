using System.Windows;
using System.Windows.Input;

namespace AgentLimits;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        AboutText.Text = BuildAboutText();
        GuideText.Text = PluginGuide.Text;
        PreviewMouseWheel += HelpWindow_PreviewMouseWheel;
    }

    /// <summary>Ctrl + mouse wheel — font size, step 1px, range 8..64.</summary>
    private void HelpWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        // Apply to whichever tab is active (TabControl switches the visual tree on tab change).
        if (MainTabs.SelectedIndex == 0)
            ScaleFont(AboutText, e.Delta);
        else
            ScaleFont(GuideText, e.Delta);
        e.Handled = true;
    }

    private static void ScaleFont(System.Windows.Controls.TextBox tb, int delta)
    {
        var next = tb.FontSize + (delta > 0 ? 1 : -1);
        if (next < 8) next = 8;
        if (next > 64) next = 64;
        tb.FontSize = next;
    }

    /// <summary>Show centered on the primary monitor at half-width / two-thirds-height.
    /// If already maximised, leave as-is.</summary>
    public void ShowCentered()
    {
        if (WindowState == WindowState.Maximized)
        {
            Show();
            Activate();
            return;
        }

        WindowState = WindowState.Normal;
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        var w = screenW / 2;
        var h = screenH * 2 / 3;

        Width = w;
        Height = h;
        Left = (screenW - w) / 2;
        Top = (screenH - h) / 2;

        Show();
        Activate();
    }

    private void CopyGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PluginGuide.Text);
            CopyGuideButton.Content = "Copied!";
        }
        catch
        {
            CopyGuideButton.Content = "Copy failed";
        }
    }

    private static string BuildAboutText() => string.Join("\n\n",
        "# About AgentLimits",

        "AgentLimits is a small Windows tray app that shows the remaining quota " +
        "for all your AI coding assistants in one place. It polls each service " +
        "in the background and displays the result in a compact overlay window.",

        "## What it shows",

        "• 5-hour and 7-day quota windows for each configured source.",
        "• Time until the next reset.",
        "• A status indicator that shifts colour as the quota gets lower " +
        "(green → yellow → red).",
        "• Stale rows when a source failed to respond (kept visible with the last " +
        "known value, dimmed).",

        "## How it works",

        "The app polls each AI service using only their read-only quota APIs. " +
        "None of these requests consume your AI quota — it's just status checking.",

        "A scheduler wakes up every minute for fast sources and every few " +
        "minutes for slow ones. A local MCP server also exposes the same data " +
        "to other AI agents on http://localhost:8765/mcp so they can check your " +
        "headroom before running a long task.",

        "## Adding a new source",

        "Want to add a model that isn't in the default list? You don't need to " +
        "know how to code in C#. Drop a small Python script into the plugins/ " +
        "folder and the app picks it up.",

        "If you don't know Python or HTTP APIs: open the **Plugin Authoring " +
        "Guide** tab, click **Copy to clipboard**, paste it into your AI " +
        "assistant (Claude, Codex, Cursor, etc.) and ask: *\"Add a block for " +
        "&lt;service name&gt;\"*. Your AI will read the guide, search for the right " +
        "endpoint, and write the plugin for you.",

        "## Tray menu",

        "Right-click the tray icon:",
        "• Show / hide — toggle the overlay window",
        "• Refresh now — poll every source immediately",
        "• Snap to top-right — re-anchor the window",
        "• Show used instead of remaining — flip the display mode",
        "• Approve new plugins — one-click approval for newly added plugins",
        "• Open plugins folder — opens the user-side plugins directory",
        "• Copy plugin authoring guide — same text as the next tab",
        "• Start with Windows — toggle autostart at login",
        "• Copy MCP endpoint — copies the local MCP URL",
        "• Exit — quit the app",

        "## Header buttons",

        "Top-right corner of the overlay:",
        "• ↻ — refresh now (blurs content briefly, shows a spinner)",
        "• ⇄ — switch between remaining / used display",
        "• ? — open this window",
        "• ✕ — hide the overlay (the app keeps running in the tray)",

        "Double-click anywhere on the overlay to refresh. Drag the overlay by " +
        "its body to move it. Drag any group header to reorder groups."
    );
}