using System.Globalization;
using System.Text.RegularExpressions;

namespace Framelet;

internal enum OcrTemplatePlacement { SameLine, NextLine, SameOrNextLine }
internal enum OcrTemplateFormat { Code, Digits, Amount, Date, Text, Iban }

internal sealed record OcrTemplate(string Name, string[] Labels, OcrTemplatePlacement Placement, OcrTemplateFormat Format);

internal static partial class OcrTemplateMatcher
{
    private static readonly char[] LabelSeparators = [' ', '\t', '\u00A0', '\u202F', ':', '=', '–', '—'];
    private static readonly char[] LineBreaks = ['\r', '\n'];
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d.M.yyyy", "d/M/yyyy", "M/d/yyyy"];

    internal static IEnumerable<ContactMatch> Find(string text, IEnumerable<OcrTemplate> templates)
    {
        foreach (var template in templates)
        {
            if (template is null || string.IsNullOrWhiteSpace(template.Name) || template.Labels is null)
                continue;

            var labels = template.Labels.Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(label => label.Length)
                .Select(label => string.Join(@"[^\S\r\n]+", Regex.Split(label, @"\s+").Select(Regex.Escape)))
                .ToArray();
            if (labels.Length == 0)
                continue;

            var pattern = @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", labels) + @")(?![\p{L}\p{N}_])";
            foreach (Match label in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                // Older rules with the literal IBAN label should capture the whole grouped value.
                var format = label.Value.Equals("IBAN", StringComparison.OrdinalIgnoreCase)
                    && template.Format is OcrTemplateFormat.Code or OcrTemplateFormat.Digits or OcrTemplateFormat.Text
                    ? OcrTemplateFormat.Iban : template.Format;
                var start = label.Index + label.Length;
                var end = EndOfLine(text, start);
                while (start < end && LabelSeparators.Contains(text[start]))
                    start++;

                if (template.Placement == OcrTemplatePlacement.NextLine ||
                    (template.Placement == OcrTemplatePlacement.SameOrNextLine && start == end))
                {
                    if (end == text.Length)
                        continue;
                    (start, end) = NextValueLine(text, end);
                    // Tesseract separates the label and value with a blank line when it sees two blocks.
                    if (format == OcrTemplateFormat.Iban && start == end && end < text.Length)
                        (start, end) = NextValueLine(text, end);
                }

                var value = ReadValue(text[start..end].TrimEnd(), format);
                if (value.Length > 0)
                    yield return new ContactMatch(new RecognizedContact(ContactKind.Template, value)
                    {
                        TemplateName = template.Name
                    }, value, start, value.Length);
            }
        }
    }

    private static (int Start, int End) NextValueLine(string text, int lineEnd)
    {
        var start = lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
        var end = EndOfLine(text, start);
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        return (start, end);
    }

    private static int EndOfLine(string text, int start)
    {
        var end = text.IndexOfAny(LineBreaks, start);
        return end == -1 ? text.Length : end;
    }

    private static string ReadValue(string line, OcrTemplateFormat format) => format switch
    {
        OcrTemplateFormat.Code => Code().Match(line).Value,
        OcrTemplateFormat.Digits => Digits().Match(line).Value,
        OcrTemplateFormat.Amount => FinancialFieldExtractor.AmountAtStart().Match(line).Value,
        OcrTemplateFormat.Date => ReadDate(line),
        OcrTemplateFormat.Text => line,
        OcrTemplateFormat.Iban => ReadIbanValue(line),
        _ => ""
    };

    private static string ReadIbanValue(string line)
    {
        var match = IbanValue().Match(line);
        if (!match.Success)
            return "";

        var checkDigits = match.Groups["checks"];
        // Only these two positions are known to be digits for every country.
        return line[..checkDigits.Index] + checkDigits.Value.Replace('O', '0').Replace('o', '0')
            + line[(checkDigits.Index + checkDigits.Length)..match.Length];
    }

    private static string ReadDate(string line)
    {
        var value = Date().Match(line).Value;
        return DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : "";
    }

    [GeneratedRegex(@"^[\p{L}\p{N}]+(?:[-_/.][\p{L}\p{N}]+)*")]
    private static partial Regex Code();

    [GeneratedRegex(@"^[0-9]+(?![\p{L}\p{N}_/-]|[.,][0-9])")]
    private static partial Regex Digits();

    // Labelled OCR extracts the printed value. The built-in IBAN detector validates it separately.
    [GeneratedRegex(@"^[A-Z]{2}[ \t\u00A0\u202F]*(?<checks>[0-9O][ \t\u00A0\u202F]*[0-9O])(?:[ \t\u00A0\u202F]*[A-Z0-9]){11,30}?(?=\s*(?:$|[,;|]|(?:BIC|SWIFT)\s*[:=]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IbanValue();

    [GeneratedRegex(@"^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{1,2}(?:\.[0-9]{1,2}\.|/[0-9]{1,2}/)[0-9]{4})(?![\p{L}\p{N}/-]|\.[0-9])")]
    private static partial Regex Date();
}
