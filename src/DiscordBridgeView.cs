using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBridgePlugin;

/// <summary>
/// Avalonia settings panel for the Discord bridge. Pure-code construction
/// (no AXAML) so the plugin doesn't pull in the Avalonia AXAML compiler
/// at publish time — matches the FX-chain sampler editor pattern.
///
/// <para><b>What it shows:</b> bot token (masked), guild id, channel id,
/// bitrate, current status badge, Connect / Disconnect buttons. Edits
/// persist via the plugin's setter properties (which JSON-save on every
/// write).</para>
///
/// <para><b>Status binding.</b> Because we're not using the MVVM stack
/// here, status changes drive UI via the plugin's StatusChanged event
/// dispatched onto the UI thread.</para>
/// </summary>
internal static class DiscordBridgeView
{
    public static Control Build(DiscordBridgePlugin plugin)
    {
        // Row layout: a 2-column grid where the right column stretches.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("160, *"),
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, Auto, Auto, Auto"),
            Margin = new Thickness(0, 5, 0, 5),
        };

        AddRow(grid, 0, "Bot Token:", out var tokenBox, masked: true, plugin.BotToken,
            v => plugin.BotToken = v);
        AddRow(grid, 1, "Guild ID (Server):", out var guildBox, masked: false, plugin.GuildId,
            v => plugin.GuildId = v);
        AddRow(grid, 2, "Voice Channel ID:", out var channelBox, masked: false, plugin.ChannelId,
            v => plugin.ChannelId = v);
        AddRow(grid, 3, "Bitrate (bps):", out var bitrateBox, masked: false, plugin.Bitrate.ToString(),
            v =>
            {
                if (int.TryParse(v, out var n)) plugin.Bitrate = n;
            });

        // Status row — multi-line block so the long DAVE/4017/etc error
        // messages from Discord.Net don't get truncated.
        var statusLabel = new TextBlock
        {
            Text = "Status:",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 10, 8),
        };
        Grid.SetRow(statusLabel, 4);
        Grid.SetColumn(statusLabel, 0);
        grid.Children.Add(statusLabel);

        var statusText = new TextBlock
        {
            Text = StatusLine(plugin),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        };
        Grid.SetRow(statusText, 4);
        Grid.SetColumn(statusText, 1);
        grid.Children.Add(statusText);

        // Connect/Disconnect buttons in a horizontal stack on the right.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var disconnectBtn = new Button { Content = "Disconnect" };
        var connectBtn = new Button { Content = "Connect & Join" };
        buttons.Children.Add(disconnectBtn);
        buttons.Children.Add(connectBtn);
        Grid.SetRow(buttons, 5);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        // ── Event wiring ──────────────────────────────────────────────

        connectBtn.Click += async (_, _) =>
        {
            connectBtn.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await plugin.ConnectAsync(cts.Token);
            }
            catch (Exception ex)
            {
                statusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                connectBtn.IsEnabled = true;
            }
        };

        disconnectBtn.Click += async (_, _) =>
        {
            try { await plugin.DisconnectAsync(); }
            catch (Exception ex) { statusText.Text = $"Error: {ex.Message}"; }
        };

        // Live status updates. Discord.Net's threads fire StatusChanged;
        // marshal to the UI thread for the text refresh.
        EventHandler statusHandler = (_, _) =>
        {
            Dispatcher.UIThread.Post(() => statusText.Text = StatusLine(plugin));
        };
        plugin.StatusChanged += statusHandler;
        grid.DetachedFromVisualTree += (_, _) => plugin.StatusChanged -= statusHandler;

        return grid;
    }

    private static string StatusLine(DiscordBridgePlugin plugin)
    {
        var detail = plugin.StatusDetail;
        return string.IsNullOrEmpty(detail)
            ? plugin.Status.ToString()
            : $"{plugin.Status} — {detail}";
    }

    private static void AddRow(Grid grid, int row, string label, out TextBox box,
        bool masked, string initial, Action<string> onChanged)
    {
        var labelControl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 10, 8),
        };
        Grid.SetRow(labelControl, row);
        Grid.SetColumn(labelControl, 0);
        grid.Children.Add(labelControl);

        // Local so the TextChanged lambda can capture without hitting the
        // CS1628 "can't capture out parameter" rule.
        var input = new TextBox
        {
            Text = initial,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
        };
        if (masked) input.PasswordChar = '*';
        input.TextChanged += (_, _) => onChanged(input.Text ?? "");
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        box = input;
    }
}
