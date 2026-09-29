using ArtNet.Desktop.Services;
using ArtNet.Networking;

namespace ArtNet.Desktop.Pages;

/// <summary>Draws 512 DMX levels as a 16-column grid with channel numbers and a level bar per cell.</summary>
public sealed class DmxGridDrawable : IDrawable
{
    public byte[] Levels { get; set; } = new byte[ArtNetConstants.DmxChannels];
    public bool Percent { get; set; }
    public int Columns { get; set; } = 16;
    public Color Accent { get; set; } = Color.FromArgb("#F5A524");
    public Color TextColor { get; set; } = Colors.White;
    public Color Dim { get; set; } = Color.FromArgb("#6E6E6E");

    public void Draw(ICanvas canvas, RectF rect)
    {
        int rows = (ArtNetConstants.DmxChannels + Columns - 1) / Columns;
        float w = rect.Width / Columns, h = rect.Height / rows;
        canvas.FontSize = Math.Clamp(h * 0.34f, 7, 14);
        for (int i = 0; i < ArtNetConstants.DmxChannels; i++)
        {
            float x = rect.X + (i % Columns) * w, y = rect.Y + (i / Columns) * h;
            byte v = i < Levels.Length ? Levels[i] : (byte)0;
            if (v > 0)
            {
                canvas.FillColor = Accent.WithAlpha(0.18f + 0.6f * v / 255f);
                canvas.FillRectangle(x + 1, y + 1 + (h - 2) * (1 - v / 255f), w - 2, (h - 2) * v / 255f);
            }
            canvas.StrokeColor = Dim.WithAlpha(0.35f);
            canvas.DrawRectangle(x, y, w, h);
            canvas.FontColor = Dim;
            canvas.DrawString((i + 1).ToString(), x + 2, y + 1, w - 4, h / 2, Microsoft.Maui.Graphics.HorizontalAlignment.Left, Microsoft.Maui.Graphics.VerticalAlignment.Top);
            canvas.FontColor = v > 0 ? TextColor : Dim;
            canvas.DrawString(Percent ? ArtNetText.Percent(v) : v.ToString(), x + 2, y + h / 2 - 1, w - 4, h / 2, Microsoft.Maui.Graphics.HorizontalAlignment.Right, Microsoft.Maui.Graphics.VerticalAlignment.Bottom);
        }
    }
}

/// <summary>DMX monitor (merged output of any universe) and DMX output (ArtDmx to subscribers, keep-alive, ArtSync).</summary>
public sealed class DmxPage : ContentPage
{
    private readonly ArtNetService _service;
    private readonly DmxGridDrawable _monitorDrawable = new();
    private readonly GraphicsView _monitorView;
    private readonly Label _monitorInfo = Ui.Caption("No data yet.");
    private readonly DmxGridDrawable _outputDrawable = new();
    private readonly GraphicsView _outputView;
    private readonly Label _outputInfo = Ui.Caption();
    private readonly byte[] _output = new byte[ArtNetConstants.DmxChannels];
    private readonly Entry _outUniverse = Ui.Entry("1", "universe", 120);
    private bool _syncAfterSend;
    private DateTime _lastSend;
    private bool _levelsDirty;   // slider levels changed since the last send started
    private bool _sendLoopRunning;

    public DmxPage(ArtNetService service)
    {
        _service = service;
        Title = "DMX";
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        foreach (var d in new[] { _monitorDrawable, _outputDrawable }) d.TextColor = dark ? Colors.White : Colors.Black;
        _monitorView = new GraphicsView { Drawable = _monitorDrawable, HeightRequest = 640 };
        _outputView = new GraphicsView { Drawable = _outputDrawable, HeightRequest = 640 };
        _outputDrawable.Levels = _output;

        service.MonitorFrame += OnMonitorFrame;

        Content = Ui.Page(MonitorCard(), OutputCard());
    }

    // ------------------------------------------------------------------ monitor

    private View MonitorCard()
    {
        var universe = Ui.Entry(_service.MonitorAddress.Value.ToString(), "universe", 120);
        var status = Ui.Caption();
        var subscribe = Ui.Switch("Subscribe (announce an output port so controllers unicast this universe to us)", _service.MonitorPort is not null, async on =>
        {
            try
            {
                var a = Ui.ParseUniverse(universe);
                _service.MonitorPort = on ? a : null; // added by the service whenever the node starts
                if (_service.Node is not { } node)
                {
                    status.Text = on ? $"Universe {a} will be announced when the node starts." : "Monitor port removed.";
                    return;
                }
                // Through the node: ArtAddress / ArtInput may change the ports on the receive thread.
                node.UpdatePorts(ports =>
                {
                    var list = ports.Where(p => p.Name != "Monitor").ToList();
                    if (on) list.Add(ArtNetPortConfig.Output(a, "Monitor"));
                    return list;
                });
                await node.NotifyChangedAsync();
                status.Text = on ? $"Announcing an output port for universe {a}." : "Monitor port removed.";
            }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
        });
        return Ui.Card("Monitor (received ArtDmx, merged)",
            Ui.Row(Ui.Field("Universe", universe),
                Ui.Button("Watch", () =>
                {
                    _service.MonitorAddress = Ui.ParseUniverse(universe);
                    Array.Clear(_monitorDrawable.Levels);
                    _monitorInfo.Text = $"Watching universe {_service.MonitorAddress} – waiting for data.";
                    _monitorView.Invalidate();
                }, status),
                Ui.Switch("Percent", false, on => { _monitorDrawable.Percent = on; _outputDrawable.Percent = on; _monitorView.Invalidate(); _outputView.Invalidate(); }),
                Ui.Button("Received universes", () =>
                {
                    var node = _service.RequireNode();
                    status.Text = node.Universes.Count == 0 ? "Nothing received yet."
                        : string.Join(Environment.NewLine, node.Universes.Select(u =>
                            $"{u.Address}: {u.PacketCount} packets, {u.DroppedCount} dropped, {u.LastLength} ch, sources {string.Join(" + ", u.Sources)}{(u.IsMerging ? " (merging " + u.MergeMode.ToDisplayName() + ")" : "")}{(u.IsActive ? "" : " – idle")}"));
                }, status)),
            subscribe,
            _monitorInfo,
            _monitorView,
            status);
    }

    private void OnMonitorFrame(object? sender, ArtNetUniverseEventArgs e)
    {
        _monitorDrawable.Levels = e.Data;
        var u = _service.Node?.GetUniverse(e.Address);
        _monitorInfo.Text = $"Universe {e.Address} · from {string.Join(" + ", e.Sources)}" +
                            (e.Merging ? $" · MERGING ({u?.MergeMode.ToDisplayName()})" : "") +
                            (e.Synchronous ? " · synchronous (ArtSync)" : "") +
                            $" · {u?.PacketCount} packets · {u?.DroppedCount} dropped · {e.Data.Count(b => b != 0)} non-zero";
        _monitorView.Invalidate();
    }

    // ------------------------------------------------------------------ output

    private View OutputCard()
    {
        var status = Ui.Caption();
        var channel = Ui.Entry("1", "channel(s) e.g. 1-12", 140);
        var levelLabel = Ui.Text("0");
        var slider = new Slider { Minimum = 0, Maximum = 255, WidthRequest = 320 };
        var physical = Ui.Entry("0", "physical", 80, Keyboard.Numeric);

        (int First, int Last) Channels()
        {
            var parts = (channel.Text ?? "1").Split('-', 2, StringSplitOptions.TrimEntries);
            int first = int.Parse(parts[0]);
            int last = parts.Length == 2 ? int.Parse(parts[1]) : first;
            if (first < 1 || last > 512 || last < first) throw new FormatException("Channels must be within 1-512.");
            return (first, last);
        }

        slider.ValueChanged += async (_, e) =>
        {
            byte v = (byte)Math.Round(e.NewValue);
            var percent = ArtNetText.Percent(v);
            levelLabel.Text = v == 255 ? $"{v} ({percent})" : $"{v} ({percent}%)";
            try
            {
                var (first, last) = Channels();
                for (int c = first; c <= last; c++) _output[c - 1] = v;
                _outputView.Invalidate();
                // One send loop at a time, at most every 22 ms, running until no change is left unsent:
                // a change made while a send is in flight is picked up by the next pass, so the final
                // position of a drag is always sent.
                _levelsDirty = true;
                if (_sendLoopRunning) return;
                _sendLoopRunning = true;
                try
                {
                    while (_levelsDirty)
                    {
                        var wait = TimeSpan.FromMilliseconds(22) - (DateTime.UtcNow - _lastSend);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait);
                        _levelsDirty = false;
                        await SendAsync(status, physical);
                    }
                }
                finally { _sendLoopRunning = false; }
            }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
        };

        async Task SetAll(byte v)
        {
            Array.Fill(_output, v);
            await SendAsync(status, physical);
            _outputView.Invalidate();
        }

        return Ui.Card("Output (ArtDmx)",
            Ui.Caption("ArtDmx is unicast to every node subscribed to the universe (listed in its ArtPollReply) and re-sent about once a second. " +
                       "Enable 'Broadcast without subscribers' in Settings for legacy devices."),
            Ui.Row(Ui.Field("Universe", _outUniverse), Ui.Field("Channels", channel), Ui.Field("Level", slider), levelLabel, Ui.Field("Physical", physical)),
            Ui.Row(
                Ui.Button("Send", () => SendAsync(status, physical), status),
                Ui.Button("All full", () => SetAll(255), status),
                Ui.Button("All 50 %", () => SetAll(128), status),
                Ui.Button("Blackout", () => SetAll(0), status),
                Ui.Button("ArtSync", async () => { await _service.RequireNode().SendSyncAsync(); status.Text = "ArtSync broadcast."; }, status),
                Ui.Switch("ArtSync after each send", false, on => _syncAfterSend = on),
                Ui.Button("Stop output", () =>
                {
                    _service.RequireNode().StopOutput(Ui.ParseUniverse(_outUniverse));
                    status.Text = "Stopped transmitting (keep-alive off for this universe).";
                }, status)),
            _outputInfo,
            _outputView,
            status);
    }

    private async Task SendAsync(Label status, Entry physical)
    {
        var node = _service.RequireNode();
        var a = Ui.ParseUniverse(_outUniverse);
        _lastSend = DateTime.UtcNow;
        int targets = await node.SendDmxAsync(a, _output, Ui.ParseByte(physical.Text, "Physical"));
        if (_syncAfterSend) await node.SendSyncAsync();
        var subs = node.SubscribersOf(a);
        _outputInfo.Text = targets == 0
            ? $"Universe {a}: no subscribers – nothing sent (Art-Net 4 forbids broadcast ArtDmx)."
            : $"Universe {a} → {targets} destination(s): {string.Join(", ", subs.Select(s => s.DisplayName + " " + s.AddressText))}";
        status.Text = string.Empty;
    }
}
