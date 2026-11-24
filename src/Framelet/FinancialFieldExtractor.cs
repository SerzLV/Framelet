using System.Text.RegularExpressions;

namespace Framelet;

internal static partial class FinancialFieldExtractor
{
    private const string Space = @"[ \t\u00A0\u202F]";
    private const string Currency = @"(?:[€$£₽]|EUR|USD|GBP|RUB)";
    private const string Number = @"[−-]?(?:[0-9]{1,3}(?:[ ,\.\u00A0\u202F][0-9]{3})+|[0-9]+)(?:[,.][0-9]{2})?";
    private const string Money = @"(?:" + Currency + Space + "*" + Number + "|" + Number + Space + "*" + Currency + ")";
    private const string End = @"(?![\p{L}\p{N}]|[.,][0-9])";

    internal static IReadOnlyList<ContactMatch> Find(string text)
    {
        var fields = new List<ContactMatch>();
        foreach (Match prefix in IbanStart().Matches(text))
        {
            if (ReadIban(text, prefix.Index, out var end) is { } iban)
                fields.Add(new ContactMatch(new RecognizedContact(ContactKind.Iban, iban), iban, prefix.Index, end - prefix.Index));
        }

        foreach (Match total in TotalPattern().Matches(text))
            AddAmount(ContactKind.TotalAmount, total.Groups["amount"]);
        foreach (Match amount in AmountPattern().Matches(text))
            AddAmount(ContactKind.Amount, amount);

        return fields;

        void AddAmount(ContactKind kind, Group amount)
        {
            if (fields.Any(field => amount.Index < field.Start + field.Length && field.Start < amount.Index + amount.Length))
                return;
            fields.Add(new ContactMatch(new RecognizedContact(kind, amount.Value), amount.Value.ToUpperInvariant(), amount.Index, amount.Length));
        }
    }

    private static string? ReadIban(string text, int start, out int end)
    {
        // Lengths and bank identifier formats from the SWIFT ISO 13616 IBAN Registry.
        var country = text.Substring(start, 2).ToUpperInvariant();
        var (length, bankLetters, numericAccount) = country switch
        {
            "EE" or "LT" => (20, 0, true),
            "LV" => (21, 4, false),
            "DE" => (22, 0, true),
            "GB" => (22, 4, true),
            _ => (0, 0, false)
        };
        end = start;
        if (length == 0)
            return null;

        Span<char> iban = stackalloc char[length];
        for (var index = 0; index < length; index++)
        {
            while (end < text.Length && text[end] is ' ' or '\t' or '\u00A0' or '\u202F')
                end++;
            if (end == text.Length || !char.IsAsciiLetterOrDigit(text[end]))
                return null;
            var value = char.ToUpperInvariant(text[end++]);
            if ((index is 2 or 3 || (index >= 4 + bankLetters && numericAccount)) && !char.IsAsciiDigit(value))
                return null;
            if (index >= 4 && index < 4 + bankLetters && !char.IsAsciiLetterUpper(value))
                return null;
            iban[index] = value;
        }
        if (end < text.Length && char.IsLetterOrDigit(text[end]))
            return null;

        var remainder = 0;
        for (var index = 0; index < iban.Length; index++)
        {
            var value = iban[(index + 4) % iban.Length];
            remainder = (char.IsAsciiDigit(value) ? remainder * 10 + value - '0' : remainder * 100 + value - 'A' + 10) % 97;
        }
        return remainder == 1 ? new string(iban) : null;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}@])(?:EE|LT|LV|DE|GB)" + Space + "*[0-9]{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex IbanStart();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.,])" + Money + End, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:Итого|К оплате|Total|Amount due|Kopā|Apmaksai)(?![\p{L}\p{N}])"
        + Space + @"*[:–—]?" + Space + @"*(?:\r?\n" + Space + @"*)?(?<amount>" + Money + "|" + Number + ")" + End,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex TotalPattern();
}
