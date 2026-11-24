using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;

internal static partial class Program
{
    private static void OcrFieldsKeepTheirImageRegions()
    {
        const string text = "Address: 10 North Street\nRīga LV-1050\nhello@example.com\nhttps://example.com\nhello@example.com\n"
            + "IBAN: lv80 BANK 0000 4351 9500 1\nDE89 3704 0044 0532 0130 00\nLV81BANK0000435195001\n"
            + "Line amount €42,50\nKopā: 1\u202F234,56 EUR\nИтого:\n12.50\nSubtotal 98.00\nRef 2026-11-24";
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

        var (fields, redactions) = OcrService.ReadFields(new OcrLayout(text, words), "LV");
        var address = fields.Single(field => field.Kind == ContactKind.Address);
        Require(address.Regions.Count == 2 && address.Regions[0].Left >= 40 && address.Regions[1].Top > address.Regions[0].Bottom,
            "A multiline address highlights each line and excludes its label");
        Require(fields.Single(field => field.Kind == ContactKind.Email).Regions.Count == 2,
            "A deduplicated address in the OCR list highlights all of its occurrences");
        Require(fields.Where(field => field.Kind == ContactKind.Iban).Select(field => field.Value)
                .SequenceEqual(["LV80BANK0000435195001", "DE89370400440532013000"]),
            "OCR recognizes valid grouped IBANs and rejects a failed checksum");
        Require(fields.Single(field => field.Kind == ContactKind.Amount).Value == "€42,50"
            && fields.Where(field => field.Kind == ContactKind.TotalAmount).Select(field => field.Value).SequenceEqual(["1\u202F234,56 EUR", "12.50"]),
            "OCR recognizes currency amounts and labelled totals while skipping unrelated numbers");
        Require(fields.Where(field => field.Kind is ContactKind.Link or ContactKind.Iban or ContactKind.Amount or ContactKind.TotalAmount)
                .All(field => field.Regions.Count > 0 && field.Regions.All(region => !redactions.Contains(region))),
            "Links and financial fields highlight without being added to contact redaction");

        var layer = new OcrTextLayer();
        layer.SetActive(false);
        layer.SetHighlightedRegions(address.Regions);
        Require(layer.Visibility == Visibility.Visible && !layer.IsHitTestVisible && !layer.IsActive,
            "Hover highlights work independently of image text selection and pass through the mouse");
        layer.Measure(new Size(200, 100));
        layer.Arrange(new Rect(0, 0, 200, 100));
        var pixels = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        pixels.Render(layer);
        Require(ToPixels(pixels).Any(value => value != 0), "The OCR field overlay renders its highlight");
        layer.SetLayout(null);
        Require(layer.Visibility == Visibility.Collapsed, "Clearing OCR removes any visible field highlight");
    }

    private static void OcrFindsNamesForRedaction()
    {
        var layout = TemplateLayout("Full name: Jean-Luc O’Neill\nCompany name: North Studio\n"
            + "Ф.И.О.:\nИван Сергеев\nVārds, uzvārds: Līga Bērziņa\n"
            + "_______ /А.А. Сергеев/\n/Петрова М. В./\nEmail: example@example.com");
        var (fields, contacts) = OcrService.ReadFields(layout, "LV");
        var names = fields.Where(field => field.Kind == ContactKind.PersonName).ToArray();
        Require(names.Select(field => field.Value)
            .SequenceEqual(["Jean-Luc O’Neill", "Иван Сергеев", "Līga Bērziņa", "А.А. Сергеев", "Петрова М. В."]),
            "Name detection recognizes labelled names and signatures in English, Russian and Latvian");
        Require(names.All(field => field.Regions.Count == 1)
            && names[0].Regions[0].Left > 45 && names[3].Regions[0].Left >= 38,
            "Name regions follow the name spans and leave the labels and signature line visible");
        Require(contacts.Count() == 1 && names.All(field => !contacts.Contains(field.Regions[0])),
            "Explicit name detection leaves the quick editor's default contact redaction unchanged");
    }
}
