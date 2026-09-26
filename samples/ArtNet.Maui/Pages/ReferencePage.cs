namespace ArtNet.Maui.Pages;

/// <summary>One option of the reference list.</summary>
public sealed record ReferenceRow(string Group, string Name, string Raw, string Description);

/// <summary>Searchable catalog of every protocol option (names, raw values, descriptions) and RDM parameter names.</summary>
public sealed class ReferencePage : ContentPage
{
    private static readonly IReadOnlyList<ReferenceRow> AllRows =
        ArtNetOptionCatalog.All.SelectMany(g => g.Options.Select(o => new ReferenceRow(g.Title, o.Name, o.RawText, o.Description)))
            .Concat(RdmText.Parameters.OrderBy(k => k.Key).Select(k => new ReferenceRow("RDM Parameters", k.Value, $"0x{k.Key:X4}", "ANSI E1.20 parameter ID")))
            .ToArray();

    public ReferencePage()
    {
        Title = "Reference";
        var groups = new List<string> { "All groups" };
        groups.AddRange(ArtNetOptionCatalog.All.Select(g => g.Title));
        groups.Add("RDM Parameters");
        var picker = Ui.Picker(groups, 0, 260);
        var search = new SearchBar { Placeholder = "Search names and descriptions", WidthRequest = 320 };
        var groupDescription = Ui.Caption();

        var list = new CollectionView
        {
            ItemTemplate = new DataTemplate(() =>
            {
                var name = new Label { FontAttributes = FontAttributes.Bold };
                name.SetBinding(Label.TextProperty, static (ReferenceRow r) => r.Name);
                var raw = Ui.Mono("", 12);
                raw.SetBinding(Label.TextProperty, static (ReferenceRow r) => r.Raw);
                var group = Ui.Caption();
                group.SetBinding(Label.TextProperty, static (ReferenceRow r) => r.Group);
                var desc = new Label { LineBreakMode = LineBreakMode.WordWrap };
                desc.SetBinding(Label.TextProperty, static (ReferenceRow r) => r.Description);
                var g = new Grid
                {
                    ColumnDefinitions = [new(new GridLength(220)), new(new GridLength(80)), new(GridLength.Star)],
                    RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
                    ColumnSpacing = 8,
                    Padding = new Thickness(0, 4),
                };
                g.Add(name, 0, 0);
                g.Add(group, 0, 1);
                g.Add(raw, 1, 0);
                g.Add(desc, 2, 0);
                Grid.SetRowSpan(desc, 2);
                return g;
            }),
        };

        void Apply()
        {
            string g = picker.SelectedItem as string ?? "All groups";
            string q = search.Text?.Trim() ?? string.Empty;
            list.ItemsSource = AllRows.Where(r => (g == "All groups" || r.Group == g) &&
                (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Raw.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            groupDescription.Text = ArtNetOptionCatalog.All.FirstOrDefault(x => x.Title == g)?.Description ?? "";
        }

        picker.SelectedIndexChanged += (_, _) => Apply();
        search.TextChanged += (_, _) => Apply();
        Apply();

        var root = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)], Padding = 12, RowSpacing = 8 };
        root.Add(Ui.Row(picker, search), 0, 0);
        root.Add(groupDescription, 0, 1);
        root.Add(list, 0, 2);
        Content = root;
    }
}
