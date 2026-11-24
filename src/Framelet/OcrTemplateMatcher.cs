using System.Globalization;
using System.Text.RegularExpressions;

namespace Framelet;

internal enum OcrTemplatePlacement { SameLine, NextLine, SameOrNextLine }
internal enum OcrTemplateFormat { Code, Digits, Amount, Date, Text }

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
                var start = label.Index + label.Length;
                var end = EndOfLine(text, start);
                while (start < end && LabelSeparators.Contains(text[start]))
                    start++;

                if (template.Placement == OcrTemplatePlacement.NextLine ||
                    (template.Placement == OcrTemplatePlacement.SameOrNextLine && start == end))
                {
                    if (end == text.Length)
                        continue;
                    start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
                    end = EndOfLine(text, start);
                    while (start < end && char.IsWhiteSpace(text[start]))
                        start++;
                }

                var value = ReadValue(text[start..end].TrimEnd(), template.Format);
                if (value.Length > 0)
                    yield return new ContactMatch(new RecognizedContact(ContactKind.Template, value)
                    {
                        TemplateName = template.Name
                    }, value, start, value.Length);
            }
        }
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
        _ => ""
    };

    private static string ReadDate(string line)
    {
        var value = Date().Match(line).Value;
        return DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : "";
    }

    [GeneratedRegex(@"^[\p{L}\p{N}]+(?:[-_/.][\p{L}\p{N}]+)*")]
    private static partial Regex Code();

    [GeneratedRegex(@"^[0-9]+(?![\p{L}\p{N}_/-]|[.,][0-9])")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{1,2}(?:\.[0-9]{1,2}\.|/[0-9]{1,2}/)[0-9]{4})(?![\p{L}\p{N}/-]|\.[0-9])")]
    private static partial Regex Date();
}
