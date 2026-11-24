using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Framelet;

internal partial class OcrTemplatesWindow
{
    private readonly ObservableCollection<Draft> _drafts;
    private readonly OcrLayout? _layout;
    private Draft? _selected;
    private RecognizedContact[] _matches = [];

    internal OcrTemplatesWindow(IEnumerable<OcrTemplate> templates, OcrLayout? layout, BitmapSource? image)
    {
        _drafts = new(templates.Where(template => template is not null).Select(template => new Draft(template)));
        _layout = layout;
        InitializeComponent();
        PlacementBox.ItemsSource = Choices<OcrTemplatePlacement>();
        FormatBox.ItemsSource = Choices<OcrTemplateFormat>();
        TemplateList.ItemsSource = _drafts;
        TemplateList.SelectedIndex = 0;
        UpdateDraftControls();
        PreviewHighlights.SetActive(false);
        if (image is not null)
        {
            PreviewPixels.Width = image.PixelWidth;
            PreviewPixels.Height = image.PixelHeight;
            PreviewImage.Source = image;
            PreviewUnavailable.Visibility = Visibility.Collapsed;
            PreviewViewbox.Visibility = Visibility.Visible;
        }
    }

    internal OcrTemplate[] Templates { get; private set; } = [];

    private static Choice<T>[] Choices<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => new Choice<T>(value, UiText.Get($"OcrTemplate{value}"))).ToArray();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var draft = new Draft(new OcrTemplate("", [], OcrTemplatePlacement.SameOrNextLine, OcrTemplateFormat.Code));
        _drafts.Add(draft);
        TemplateList.SelectedItem = draft;
        NameBox.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var index = TemplateList.SelectedIndex;
        _drafts.Remove(_selected);
        TemplateList.SelectedIndex = Math.Min(index, _drafts.Count - 1);
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is not null)
            _selected.PropertyChanged -= OnDraftChanged;
        _selected = TemplateList.SelectedItem as Draft;
        if (_selected is not null)
            _selected.PropertyChanged += OnDraftChanged;
        UpdateDraftControls();
        ClearCheck();
    }

    private void UpdateDraftControls()
    {
        EditorForm.DataContext = _selected;
        EditorScroll.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyFormHint.Visibility = _selected is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = _selected is not null;
        CheckButton.IsEnabled = _selected is not null && _layout is not null;
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        ClearCheck();
    }

    private void ClearCheck()
    {
        SaveStatus.Text = "";
        CheckStatus.Text = "";
        _matches = [];
        MatchesList.ItemsSource = null;
        MatchesList.Visibility = Visibility.Collapsed;
        PreviewHighlights.SetHighlightedRegions([]);
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || _selected is null)
            return;

        ClearCheck();
        if (_selected.Build() is not { } template)
        {
            CheckStatus.Text = UiText.Get("OcrTemplateIncomplete");
            return;
        }

        _matches = OcrService.ReadTemplateFields(_layout, [template]);
        CheckStatus.Text = _matches.Length == 0 ? UiText.Get("OcrTemplateNoMatches")
            : UiText.Format("OcrTemplateMatches", _matches.Length);
        MatchesList.ItemsSource = _matches;
        MatchesList.Visibility = _matches.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateHighlights();
    }

    private void MatchesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHighlights();

    private void UpdateHighlights() =>
        PreviewHighlights.SetHighlightedRegions(MatchesList.SelectedItem is RecognizedContact field
            ? field.Regions : _matches.SelectMany(match => match.Regions).Distinct().ToArray());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var templates = new List<OcrTemplate>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in _drafts)
        {
            var template = draft.Build();
            var failure = template is null ? "OcrTemplateIncomplete"
                : names.Add(template.Name) ? null : "OcrTemplateDuplicate";
            if (failure is not null)
            {
                TemplateList.SelectedItem = draft;
                SaveStatus.Text = UiText.Get(failure);
                NameBox.Focus();
                return;
            }
            templates.Add(template!);
        }
        Templates = templates.ToArray();
        DialogResult = true;
    }

    private sealed record Choice<T>(T Value, string Label);

    private sealed class Draft(OcrTemplate template) : INotifyPropertyChanged
    {
        private string _name = template.Name ?? "";
        private string _labels = string.Join(Environment.NewLine, template.Labels ?? []);
        private OcrTemplatePlacement _placement = template.Placement;
        private OcrTemplateFormat _format = template.Format;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name
        {
            get => _name;
            set
            {
                if (Set(ref _name, value, nameof(Name)))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? UiText.Get("OcrTemplateNew") : Name.Trim();
        public string Labels { get => _labels; set => Set(ref _labels, value, nameof(Labels)); }
        public OcrTemplatePlacement Placement { get => _placement; set => Set(ref _placement, value, nameof(Placement)); }
        public OcrTemplateFormat Format { get => _format; set => Set(ref _format, value, nameof(Format)); }

        internal OcrTemplate? Build()
        {
            var labels = Labels.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return string.IsNullOrWhiteSpace(Name) || labels.Length == 0 ? null : new OcrTemplate(Name.Trim(), labels, Placement, Format);
        }

        private bool Set<T>(ref T field, T value, string property)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            return true;
        }
    }
}
