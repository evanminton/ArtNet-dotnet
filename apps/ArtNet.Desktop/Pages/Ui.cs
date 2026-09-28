namespace ArtNet.Desktop.Pages;

/// <summary>Small helpers for building pages in C#.</summary>
public static class Ui
{
    public static Style? Style(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;

    public static Label Heading(string text) => new() { Text = text, Style = Style("Heading") };
    public static Label Caption(string text = "") => new() { Text = text, Style = Style("Caption") };
    public static Label Mono(string text = "", double size = 13) => new() { Text = text, Style = Style("Mono"), FontSize = size };
    public static Label Text(string text = "") => new() { Text = text };

    /// <summary>A titled card.</summary>
    public static Border Card(string title, params IView[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(Heading(title));
        foreach (var c in children) stack.Children.Add(c);
        return new Border { Style = Style("Card"), Content = stack };
    }

    /// <summary>A caption above a control.</summary>
    public static VerticalStackLayout Field(string caption, View view) => new()
    {
        Spacing = 2,
        Children = { Caption(caption), view },
    };

    /// <summary>Views side by side, wrapping on narrow screens.</summary>
    public static FlexLayout Row(params View[] views)
    {
        var f = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
        foreach (var v in views)
        {
            v.Margin = new Thickness(0, 2, 8, 2);
            f.Children.Add(v);
        }
        return f;
    }

    public static Button Button(string text, Func<Task> action, Label? status = null)
    {
        var b = new Button { Text = text };
        b.Clicked += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                if (status is not null) status.Text = "⚠ " + ex.Message;
                else if (Application.Current?.Windows.FirstOrDefault()?.Page is { } page) await page.DisplayAlertAsync("Art-Net", ex.Message, "OK");
            }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Button(string text, Action action, Label? status = null) =>
        Button(text, () => { action(); return Task.CompletedTask; }, status);

    public static Entry Entry(string text = "", string placeholder = "", double width = 160, Keyboard? keyboard = null) => new()
    {
        Text = text,
        Placeholder = placeholder,
        WidthRequest = width,
        Keyboard = keyboard ?? Keyboard.Default,
    };

    /// <summary>A picker over option entries (shows the readable name).</summary>
    public static Picker Picker<T>(IReadOnlyList<T> items, int selected = 0, double width = 240)
    {
        var p = new Picker { WidthRequest = width, ItemsSource = items.ToList() };
        if (items.Count > 0) p.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        return p;
    }

    public static HorizontalStackLayout Switch(string text, bool on, Action<bool> changed)
    {
        var s = new Switch { IsToggled = on };
        s.Toggled += (_, e) => changed(e.Value);
        return new HorizontalStackLayout { Spacing = 4, Children = { s, new Label { Text = text, VerticalOptions = LayoutOptions.Center } } };
    }

    /// <summary>Readable fields grouped by section.</summary>
    public static View Fields(IEnumerable<ArtNetField> fields)
    {
        var stack = new VerticalStackLayout { Spacing = 2 };
        string? section = null;
        foreach (var f in fields)
        {
            if (f.Section != section)
            {
                section = f.Section;
                stack.Children.Add(new Label { Text = section, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 2) });
            }
            var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(new GridLength(200)), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
            grid.Add(Caption(f.Name), 0, 0);
            grid.Add(new Label { Text = f.Raw is null ? f.Value : $"{f.Value}   [{f.Raw}]", LineBreakMode = LineBreakMode.WordWrap }, 1, 0);
            stack.Children.Add(grid);
        }
        return stack;
    }

    public static PortAddress ParseUniverse(Entry entry) =>
        PortAddress.TryParse(entry.Text, out var a) ? a : throw new FormatException($"'{entry.Text}' is not a universe. Use 0-32767 or Net:Sub:Universe.");

    public static byte ParseByte(string? text, string what)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            byte.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var h)) return h;
        if (byte.TryParse(t, out var v)) return v;
        throw new FormatException($"{what} must be 0-255 (or 0x00-0xFF).");
    }

    public static byte[] ParseHex(string? text)
    {
        var t = (text ?? string.Empty).Replace(" ", "").Replace("-", "").Replace(":", "");
        return t.Length == 0 ? [] : Convert.FromHexString(t);
    }

    public static ScrollView Page(params IView[] children)
    {
        var stack = new VerticalStackLayout { Padding = 12, Spacing = 12, MaximumWidthRequest = 980 };
        foreach (var c in children) stack.Children.Add(c);
        return new ScrollView { Content = stack };
    }
}
