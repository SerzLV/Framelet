using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;
using Rect = System.Windows.Rect;

namespace Framelet;

internal sealed record RecognizedImage(string Text, IReadOnlyList<RecognizedContact> Contacts, OcrLayout Layout,
    IReadOnlyList<Rect> ContactRegions);

internal static partial class OcrService
{
    internal const string AutomaticMode = "auto";
    private const string MixedLanguages = "rus+lav+eng";
    private static readonly SemaphoreSlim Worker = new(1);
    private static TesseractEngine? _engine;
    private static string? _engineLanguages;
    private static volatile bool _shuttingDown;

    internal static async Task<RecognizedImage> RecognizeAsync(BitmapSource image, string language, CancellationToken token)
    {
        var layout = await RecognizeLayoutAsync(image, language, includeWords: true, token).ConfigureAwait(false);
        var (fields, redactions) = ReadFields(layout, RegionInfo.CurrentRegion.TwoLetterISORegionName);
        return new RecognizedImage(NormalizeShortcuts(layout.RawText), fields, layout, redactions);
    }

    internal static async Task<string> RecognizeTextAsync(BitmapSource image, CancellationToken token)
    {
        var layout = await RecognizeLayoutAsync(image, AutomaticMode, includeWords: false, token).ConfigureAwait(false);
        return NormalizeShortcuts(layout.RawText);
    }

    private static async Task<OcrLayout> RecognizeLayoutAsync(BitmapSource image, string language, bool includeWords, CancellationToken token)
    {
        await Worker.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Recognize(image, language, includeWords, token), token).ConfigureAwait(false);
        }
        finally
        {
            Worker.Release();
            if (_shuttingDown)
                ReleaseResources();
        }
    }

    private static OcrLayout Recognize(BitmapSource image, string language, bool includeWords, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var automatic = language == AutomaticMode;
        if (_shuttingDown)
            throw new OperationCanceledException();
        var engine = GetEngine(automatic ? MixedLanguages : language);
        return image.PixelHeight > 4700 && image.PixelHeight > image.PixelWidth * 2L
            ? RecognizeTallImage(engine, image, includeWords, automatic, token)
            : RecognizePage(engine, image, includeWords, automatic, token);
    }

    private static TesseractEngine GetEngine(string languages)
    {
        if (_engine is not null && _engineLanguages == languages)
            return _engine;

        _engine?.Dispose();
        _engine = null;
        _engineLanguages = null;
        var directory = Path.GetDirectoryName(typeof(OcrService).Assembly.Location) ?? AppContext.BaseDirectory;
        var engine = new TesseractEngine(Path.Combine(directory, "Assets", "Ocr"), languages, EngineMode.LstmOnly);
        try
        {
            engine.SetVariable("user_defined_dpi", "144");
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        _engine = engine;
        _engineLanguages = languages;
        return engine;
    }

    internal static void Shutdown()
    {
        _shuttingDown = true;
        ReleaseResources();
    }

    private static void ReleaseResources()
    {
        if (!Worker.Wait(0))
            return;
        try
        {
            _engine?.Dispose();
            _engine = null;
            _engineLanguages = null;
        }
        finally
        {
            Worker.Release();
        }
    }

    private static OcrLayout RecognizePage(TesseractEngine engine, BitmapSource image, bool includeWords, bool automatic, CancellationToken token)
    {
        var longestSide = Math.Max(image.PixelWidth, image.PixelHeight);
        var scale = longestSide > 4700 ? 4700d / longestSide : Math.Clamp(2400d / longestSide, 1, 2.5);
        // Small differences in resampling can change which model wins on a short word.
        if (automatic && scale > 1)
            scale = Math.Round(scale * 2, MidpointRounding.AwayFromZero) / 2;
        var input = scale == 1 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale));
        token.ThrowIfCancellationRequested();

        using var pix = OcrInput.Create(input);
        var scaleX = (double)image.PixelWidth / pix.Width;
        var scaleY = (double)image.PixelHeight / pix.Height;
        var layout = ReadLayout(engine, pix, PageSegMode.Auto, scaleX, scaleY, includeWords);
        if (layout.RawText.Length == 0)
            layout = ReadLayout(engine, pix, PageSegMode.SparseText, scaleX, scaleY, includeWords);

        token.ThrowIfCancellationRequested();
        return layout;
    }

    private static OcrLayout RecognizeTallImage(TesseractEngine engine, BitmapSource image, bool includeWords, bool automatic, CancellationToken token)
    {
        const int pageHeight = 2600;
        const int overlap = 160;
        var text = new StringBuilder();
        var words = new List<OcrWord>();
        var lineNumber = 0;
        for (var start = 0; start < image.PixelHeight; start += pageHeight)
        {
            var end = Math.Min(start + pageHeight, image.PixelHeight);
            var cropStart = Math.Max(0, start - overlap);
            var cropEnd = Math.Min(end + overlap, image.PixelHeight);
            var crop = new CroppedBitmap(image, new System.Windows.Int32Rect(0, cropStart, image.PixelWidth, cropEnd - cropStart));
            var layout = RecognizePage(engine, crop, includeWords: true, automatic, token);
            foreach (var line in layout.Words.GroupBy(word => word.Line))
            {
                var center = cropStart + line.Average(word => word.Bounds.Top + word.Bounds.Height / 2);
                if (center < start || center >= end)
                    continue;

                lineNumber++;
                if (text.Length > 0)
                    text.Append('\n');
                var first = line.First();
                var last = line.Last();
                if (first.Offset >= 0 && last.Offset >= first.Offset && line.All(word => word.Offset >= first.Offset))
                {
                    var offset = text.Length - first.Offset;
                    text.Append(layout.RawText.AsSpan(first.Offset, last.Offset + last.Text.Length - first.Offset));
                    if (includeWords)
                        foreach (var word in line)
                            AppendWord(word, offset + word.Offset);
                }
                else
                {
                    var join = "";
                    foreach (var word in line)
                    {
                        text.Append(join);
                        if (includeWords)
                            AppendWord(word, text.Length);
                        text.Append(word.Text);
                        join = " ";
                    }
                }
            }

            void AppendWord(OcrWord word, int offset)
            {
                var bounds = word.Bounds;
                bounds.Offset(0, cropStart);
                words.Add(word with { Bounds = bounds, Offset = offset, Line = lineNumber });
            }
        }
        return new OcrLayout(text.ToString(), words);
    }

    internal static (RecognizedContact[] Fields, Rect[] Redactions) ReadFields(OcrLayout layout, string region)
    {
        var financial = FinancialFieldExtractor.Find(layout.RawText);
        var matches = ContactExtractor.Find(layout.RawText, region)
            .Where(contact => contact.Contact.Kind != ContactKind.Phone || !financial.Any(field =>
                contact.Start < field.Start + field.Length && field.Start < contact.Start + contact.Length))
            .Concat(financial);
        var fields = new List<RecognizedContact>();
        var redactions = new HashSet<Rect>();
        foreach (var group in matches.GroupBy(match => (match.Contact.Kind, match.Identity)))
        {
            var regions = group.SelectMany(match => MatchRegions(layout, match)).Distinct().ToArray();
            fields.Add(group.First().Contact with { Regions = regions });
            if (group.Key.Kind is ContactKind.Email or ContactKind.Phone or ContactKind.Address or ContactKind.Location)
                redactions.UnionWith(regions);
        }
        return (fields.ToArray(), redactions.ToArray());
    }

    internal static RecognizedContact[] ReadTemplateFields(OcrLayout layout, IEnumerable<OcrTemplate> templates) =>
        OcrTemplateMatcher.Find(layout.RawText, templates)
            .GroupBy(match => (match.Contact.TemplateName, match.Identity))
            .Select(group => group.First().Contact with
            {
                Regions = group.SelectMany(match => MatchRegions(layout, match)).Distinct().ToArray()
            }).ToArray();

    private static IEnumerable<Rect> MatchRegions(OcrLayout layout, ContactMatch match)
    {
        var end = match.Start + match.Length;
        var words = layout.Words.Where(word => word.Offset >= 0 && word.Offset < end
            && word.Offset + word.Text.Length > match.Start);
        foreach (var line in words.GroupBy(word => word.Line))
        {
            var bounds = line.Select(word => word.Bounds).Aggregate(Rect.Union);
            var padding = Math.Clamp(bounds.Height * 0.16, 2, 8);
            bounds.Inflate(padding, padding);
            yield return bounds;
        }
    }

    internal static string NormalizeShortcuts(string text)
    {
        return ShortcutPattern().Replace(text, match =>
        {
            var keys = match.Groups["keys"].Value.Split('+', StringSplitOptions.TrimEntries);
            for (var index = 0; index < keys.Length - 1; index++)
                if (keys[index].Equals("СШ", StringComparison.OrdinalIgnoreCase) || keys[index].Equals("CШ", StringComparison.OrdinalIgnoreCase))
                    keys[index] = "Ctrl";
            if (keys[^1].Length == 1)
            {
                const string cyrillic = "АВСЕНКМОРТХУ";
                const string latin =    "ABCEHKMOPTXY";
                var index = cyrillic.IndexOf(char.ToUpperInvariant(keys[^1][0]));
                if (index >= 0)
                    keys[^1] = latin[index].ToString();
            }

            var dash = match.Groups["dash"];
            var bullet = match.Groups["bullet"].Value;
            if (dash.Success && bullet.Length > 0)
                bullet = "• ";

            var conjunction = match.Groups["joined"];
            return bullet + string.Join(" + ", keys) + (conjunction.Success ? $" {conjunction.Value}" : "")
                + (dash.Success ? $" {dash.Value} " : "");
        }).TrimEnd();
    }

    private static OcrLayout ReadLayout(TesseractEngine engine, Pix image, PageSegMode segmentation,
        double scaleX, double scaleY, bool includeWords)
    {
        using var page = engine.Process(image, segmentation);
        var text = page.GetText().Trim();
        if (text.Length == 0 || !includeWords)
            return new OcrLayout(text, []);

        using var iterator = page.GetIterator();
        iterator.Begin();
        var words = new List<OcrWord>();
        var line = 0;
        var textOffset = 0;
        do
        {
            if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine))
                line++;
            var word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrEmpty(word))
                continue;

            var offset = text.IndexOf(word, textOffset, StringComparison.Ordinal);
            if (offset >= 0)
                textOffset = offset + word.Length;
            if (iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box) && box.Width > 0 && box.Height > 0)
                words.Add(new OcrWord(word, new System.Windows.Rect(box.X1 * scaleX, box.Y1 * scaleY,
                    box.Width * scaleX, box.Height * scaleY), line, offset));
        } while (iterator.Next(PageIteratorLevel.Word));
        return new OcrLayout(text, words);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?<bullet>^[©®*][ \t]*)?(?<keys>(?:(?:Ctrl|Control|[CС]Ш|Alt|Shift|Win|Windows|Cmd|Command|Option|Meta)[ \t]*\+[ \t]*)+(?:F(?:2[0-4]|1[0-9]|[1-9])|Enter|Escape|Esc|Space|Tab|Backspace|Delete|Home|End|Print[ \t]*Screen|PrtSc|[A-ZАВСЕНКМОРТХУ0-9]))(?<joined>и(?=[ \t]+\p{IsCyrillic}))?(?![\p{L}\p{N}_])(?:[ \t]*(?<dash>[—–])[ \t]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, 250)]
    private static partial Regex ShortcutPattern();
}
