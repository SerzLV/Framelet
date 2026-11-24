using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;

internal static partial class Program
{
    private static readonly OcrTemplate InvoiceTemplate = new("Invoice number", ["Invoice No.", "Rēķina Nr.", "Счёт №"],
        OcrTemplatePlacement.SameOrNextLine, OcrTemplateFormat.Code);

    private static void OcrTemplatesFindLabelledValues(string root)
    {
        var layout = TemplateLayout("Invoice No.: INV-2026/001\r\nRĒĶINA\u00A0Nr.\r\n  INV-2026/001\n"
            + "Счёт №: AB-002\nPreInvoice No.: FALSE\nInvoice No.\n\nFALSE\n"
            + "Tax: -1 234,56 EUR\nDelivery: 31.02.2026\nDelivery: 24.11.2026\n"
            + "Quantity: 12.75\nQuantity: 45\nMemo: Please retain a copy.  ");
        OcrTemplate[] templates =
        [
            InvoiceTemplate,
            new("Tax", ["Tax"], OcrTemplatePlacement.SameLine, OcrTemplateFormat.Amount),
            new("Delivery date", ["Delivery"], OcrTemplatePlacement.SameLine, OcrTemplateFormat.Date),
            new("Number of items", ["Quantity"], OcrTemplatePlacement.SameLine, OcrTemplateFormat.Digits),
            new("Memo", ["Memo"], OcrTemplatePlacement.SameLine, OcrTemplateFormat.Text)
        ];
        var fields = OcrService.ReadTemplateFields(layout, templates);
        Require(fields.Select(field => field.Value)
                .SequenceEqual(["INV-2026/001", "AB-002", "-1 234,56 EUR", "24.11.2026", "45", "Please retain a copy."]),
            "Templates match literal multilingual labels and enforce their chosen value format");
        Require(fields[0].Regions.Count == 2 && fields[0].Regions[0].Left > 50 && fields[0].Regions[1].Top >= 38,
            "A duplicate value highlights only its actual positions on the same and next lines");
        var next = InvoiceTemplate with { Placement = OcrTemplatePlacement.NextLine };
        var same = InvoiceTemplate with { Placement = OcrTemplatePlacement.SameLine };
        var alternatives = TemplateLayout("Invoice No.: ABC\nXYZ");
        Require(OcrService.ReadTemplateFields(alternatives, [next]).Single().Value == "XYZ"
            && OcrService.ReadTemplateFields(alternatives, [same]).Single().Value == "ABC",
            "Explicit placement picks the intended line even if both lines contain a value");
        var store = new HistoryStore(Directory.CreateDirectory(Path.Combine(root, "OcrTemplates")).FullName);
        store.SavePreferences(new Preferences(OcrTemplates: templates));
        var loaded = store.LoadPreferences().OcrTemplates;
        Require(loaded?.Length == templates.Length && loaded[0].Labels.SequenceEqual(InvoiceTemplate.Labels)
            && loaded[0].Placement == InvoiceTemplate.Placement && loaded[1].Format == OcrTemplateFormat.Amount,
            "OCR templates round-trip through preferences without changing labels, placement or format");
    }

    private static void OcrTemplateDialogPreviewsMatches()
    {
        var image = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 100 * 4], 200 * 4);
        var dialog = new OcrTemplatesWindow([InvoiceTemplate], TemplateLayout("Invoice No.: INV-001"), image);
        dialog.Loaded += (_, _) =>
        {
            Require(dialog.Background is SolidColorBrush background && background.Color == Color.FromRgb(16, 21, 28)
                && dialog.Foreground is SolidColorBrush foreground && foreground.Color == Color.FromRgb(241, 245, 250)
                && dialog.WindowStyle == WindowStyle.None, "The template window has a dark frame and readable foreground");
            var check = (Button)dialog.FindName("CheckButton");
            var results = (ListBox)dialog.FindName("MatchesList");
            var highlights = (OcrTextLayer)dialog.FindName("PreviewHighlights");
            var name = (TextBox)dialog.FindName("NameBox");
            Require(check.IsEnabled && name.Text == InvoiceTemplate.Name
                && ((ListBox)dialog.FindName("FormatBox")).SelectedValue is OcrTemplateFormat.Code,
                "The template window loads the saved rule and enables testing with an OCR snapshot");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(results.Items.Count == 1 && highlights.Visibility == Visibility.Visible,
                "Testing a template displays its result and highlights it in the preview");
            name.Text = "New name";
            Require(highlights.Visibility == Visibility.Collapsed && results.Items.Count == 0,
                "Editing a template clears the previous preview result");
            var placement = (ListBox)dialog.FindName("PlacementBox");
            placement.SelectedValue = OcrTemplatePlacement.NextLine;
            ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        Require(dialog.ShowDialog() == true && dialog.Templates.Single() is
            { Name: "New name", Placement: OcrTemplatePlacement.NextLine, Format: OcrTemplateFormat.Code },
            "Saving the template dialog returns the edited name and placement");
    }

    private static void OcrReadsGroupedIbanBelowItsLabel()
    {
        const string printedSample = "PT00 0000 000123456789101112";
        const string validExample = "PT50 0002 0123 1234 5678 9015 4";
        var layout = TemplateLayout("Comprovativo de IBAN / SWIFT\nCliente\nIBAN\n\nPTOO 0000 000123456789101112\nBIC/SWIFT\nTOTAPTPL");
        var template = new OcrTemplate("IBAN", ["IBAN"], OcrTemplatePlacement.SameOrNextLine, OcrTemplateFormat.Text);
        foreach (var format in new[] { OcrTemplateFormat.Code, OcrTemplateFormat.Text, OcrTemplateFormat.Iban })
        {
            var match = OcrService.ReadTemplateFields(layout, [template with { Format = format }]).Single();
            Require(match.Value == printedSample && match.Regions.Single().Top >= 78,
                $"IBAN templates skip the heading and highlight the complete printed account ({format})");
        }
        var alias = template with { Labels = ["Account"], Format = OcrTemplateFormat.Iban };
        var valid = TemplateLayout($"Account: {validExample} BIC: TOTAPTPL\n{printedSample}");
        Require(OcrService.ReadTemplateFields(valid, [alias]).Single().Value == validExample,
            "The IBAN format supports other labels and stops before the adjacent BIC field");
        var (fields, _) = OcrService.ReadFields(valid, "PT");
        Require(fields.Single(field => field.Kind == ContactKind.Iban).Value == "PT50000201231234567890154",
            "Built-in detection validates Portuguese IBANs and rejects the placeholder account");
    }

    private static OcrLayout TemplateLayout(string text)
    {
        var words = new List<OcrWord>();
        var offset = 0;
        var line = 0;
        foreach (var row in text.Split('\n'))
        {
            foreach (Match word in Regex.Matches(row, @"\S+"))
                words.Add(new OcrWord(word.Value, new Rect(word.Index * 5, line * 20, word.Length * 5, 12), line, offset + word.Index));
            offset += row.Length + 1;
            line++;
        }
        return new OcrLayout(text, words);
    }
}
