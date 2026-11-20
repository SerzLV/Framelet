using System.Text.RegularExpressions;
using PhoneNumbers;

namespace Framelet;

internal enum ContactKind
{
    Email,
    Phone,
    Link,
    Address,
    Location
}

internal sealed record RecognizedContact(ContactKind Kind, string Value)
{
    public string Label => UiText.Get(Kind.ToString());
    public string Description => $"{Label}: {Value}";
}

internal sealed record ContactMatch(RecognizedContact Contact, string Identity, int Start, int Length);

internal static partial class ContactExtractor
{
    private static readonly PhoneNumberUtil PhoneNumbers = PhoneNumberUtil.GetInstance();

    internal static IReadOnlyList<ContactMatch> Find(string text, string region)
    {
        var contacts = new List<ContactMatch>();
        foreach (Match email in EmailPattern().Matches(text))
            Add(ContactKind.Email, email.Value, email.Value.ToLowerInvariant(), email.Index, email.Length);

        var normalized = PhonePunctuation().Replace(text, match =>
            match.Value[0] is '\u00A0' or '\u2007' or '\u202F' ? " " : match.Value[0] == '\uFF0B' ? "+" : "-");
        var validated = new HashSet<int>();
        foreach (var match in PhoneNumbers.FindNumbers(normalized, region, PhoneNumberUtil.Leniency.VALID, 120))
        {
            var identity = PhoneNumbers.Format(match.Number, PhoneNumberFormat.E164);
            validated.Add(match.Start);
            Add(ContactKind.Phone, match.RawString.Trim(), identity + "/" + match.Number.Extension, match.Start, match.Length);
        }

        var rowOffset = 0;
        foreach (var row in normalized.Split('\n'))
        {
            var label = PhoneLabel().Match(row);
            foreach (Match match in PhoneCandidate().Matches(row))
            {
                var labelled = label.Success && label.Index + label.Length <= match.Index;
                var raw = PhoneEdgeNoise().Replace(match.Value.Trim(), "");
                if (raw.Count(char.IsDigit) < 7)
                    continue;

                var cleaned = RepairPhoneDigits(raw);
                if (cleaned is null)
                    continue;

                var international = cleaned.StartsWith('+') || cleaned.StartsWith("00", StringComparison.Ordinal);
                var nationalRegion = RussianPhoneShape().IsMatch(cleaned) ? "RU"
                    : NorthAmericanPhoneShape().IsMatch(cleaned) ? "US"
                    : BritishPhoneShape().IsMatch(cleaned) ? "GB"
                    : null;
                if (!labelled && !international && nationalRegion is null)
                    continue;

                var digits = PhoneNumberUtil.NormalizeDigitsOnly(cleaned);
                if (digits.Length is < 7 or > 15)
                    continue;

                var parsed = ParsePhone(cleaned, international, region, nationalRegion);
                if (parsed is null && !labelled)
                    continue;

                var identity = parsed is null ? digits : PhoneNumbers.Format(parsed, PhoneNumberFormat.E164);
                var start = rowOffset + match.Index;
                if (!validated.Contains(start))
                    Add(ContactKind.Phone, cleaned, identity + "/", start, match.Length);
            }
            rowOffset += row.Length + 1;
        }

        contacts.AddRange(FindAddresses(text));

        foreach (Match url in UrlPattern().Matches(text))
        {
            var value = url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
            Add(ContactKind.Link, value, value.ToLowerInvariant(), url.Index, value.Length);
        }

        return contacts;

        void Add(ContactKind kind, string value, string identity, int start, int length)
        {
            contacts.Add(new ContactMatch(new RecognizedContact(kind, value), identity, start, length));
        }
    }

    private static string? RepairPhoneDigits(string raw)
    {
        var fixes = 0;
        var repaired = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var digit = raw[i] switch
            {
                'O' or 'o' or 'О' or 'о' => '0',
                'I' or 'l' or 'І' or 'і' => '1',
                _ => raw[i]
            };
            if (digit != raw[i] && ++fixes > 2)
                return null;
            repaired[i] = digit;
        }
        return new string(repaired);
    }

    private static PhoneNumber? ParsePhone(string value, bool international, string region, string? nationalRegion)
    {
        if (international && value.StartsWith("00", StringComparison.Ordinal))
            value = "+" + value[2..].TrimStart();

        PhoneNumber? possible = null;
        foreach (var languageRegion in new[] { nationalRegion ?? region, region }.Distinct())
        {
            try
            {
                var result = PhoneNumbers.Parse(value, languageRegion);
                if (PhoneNumbers.IsValidNumber(result))
                    return result;
                if (PhoneNumbers.IsPossibleNumber(result))
                    possible ??= result;
            }
            catch (NumberParseException)
            {
                // Try the next regional interpretation, if any.
            }
        }
        return possible;
    }

    private static IEnumerable<ContactMatch> FindAddresses(string text)
    {
        var rows = new List<(string Text, int Start)>();
        var start = 0;
        foreach (var (end, separatorLength) in ContactRowSeparator().Matches(text)
            .Select(match => (match.Index, match.Length)).Append((text.Length, 0)))
        {
            var raw = text.AsSpan(start, end - start);
            var trimmed = raw.TrimStart().TrimStart("•·©®*›").TrimStart();
            rows.Add((trimmed.TrimEnd().ToString(), end - trimmed.Length));
            start = end + separatorLength;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var (line, offset) = rows[index];
            var field = AddressLabel().Match(line);
            var value = line;
            var kind = ContactKind.Address;
            if (field.Success)
            {
                var valueGroup = field.Groups["value"];
                value = valueGroup.Value;
                offset += valueGroup.Index;
                kind = field.Groups["locality"].Success ? ContactKind.Location : ContactKind.Address;

                if (value.Length == 0 && kind == ContactKind.Address)
                {
                    var next = NextAddressLine(rows, index);
                    if (next < rows.Count && LooksLikeStreet(rows[next].Text))
                    {
                        index = next;
                        (value, offset) = rows[next];
                    }
                }
            }
            else if (!LooksLikeStreet(line))
                continue;

            if (!IsAddressValue(value))
                continue;

            var end = offset + value.Length;
            var postal = NextAddressLine(rows, index);
            if (kind == ContactKind.Address && !PostalLine().IsMatch(value) && postal < rows.Count && PostalLine().IsMatch(rows[postal].Text))
            {
                index = postal;
                var (city, cityOffset) = rows[postal];
                value = value.TrimEnd(',') + ", " + city;
                end = cityOffset + city.Length;
            }
            yield return new ContactMatch(new RecognizedContact(kind, value), value.ToLowerInvariant(), offset, end - offset);
        }
    }

    private static int NextAddressLine(List<(string Text, int Start)> rows, int index)
        => index + 1 < rows.Count && rows[index + 1].Text.Length == 0 ? index + 2 : index + 1;

    private static bool IsAddressValue(string value)
        => value.Length is >= 2 and <= 180 && value.Any(char.IsLetter) && !value.Contains('@')
            && !value.Contains("://", StringComparison.Ordinal) && !EmptyAddress().IsMatch(value);

    private static bool LooksLikeStreet(string line)
        => NumberedStreet().IsMatch(line) || StreetBeforeNumber().IsMatch(line);

    [GeneratedRegex(@"[\u00A0\u2007\u202F\u2010-\u2015\u2212\uFF0B]", RegexOptions.None, 250)]
    private static partial Regex PhonePunctuation();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:телефон|тел\.?|мобильный|моб\.|phone|mobile|cell|tel\.?|fax|whatsapp|tālrunis|talrunis)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhoneLabel();

    [GeneratedRegex(@"(?<![\p{L}\p{N}@])(?:\+|\()?[0-9OОoоIlІі][0-9OОoоIlІі(). \t-]{5,37}[0-9OОoоIlІі](?![\p{L}\p{N}])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhoneCandidate();

    [GeneratedRegex(@"^[OОoоIlІі][ \t]+|[ \t]+[OОoоIlІі]$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhoneEdgeNoise();

    [GeneratedRegex(@"^(?:8[ .-]*)?\(?\d{3}\)?[ .-]*\d{3}[ .-]+\d{2}[ .-]+\d{2}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex RussianPhoneShape();

    [GeneratedRegex(@"^(?:1[ .-]*)?\(?\d{3}\)?[ .-]+\d{3}[ .-]+\d{4}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex NorthAmericanPhoneShape();

    [GeneratedRegex(@"^0\d{4}[ .-]+\d{6}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex BritishPhoneShape();

    [GeneratedRegex(@"\r?\n|[•|]", RegexOptions.None, 250)]
    private static partial Regex ContactRowSeparator();

    [GeneratedRegex(@"^(?:(?<locality>город(?: проживания)?|местоположение|city|location|pilsēta)[ \t]*(?:[:–—-])[ \t]*(?<value>.*)|(?:адрес(?: проживания| регистрации| для корреспонденции)?|почтовый адрес|(?:mailing |postal |home |street )?address|adrese)(?:[ \t]*(?:[:–—-])[ \t]*(?<value>.*)|[ \t]*$))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex AddressLabel();

    [GeneratedRegex(@"^(?:\W*(?:ваш(?: город| адрес)|город проживания|укажите (?:город|адрес)|your (?:city|address)|city,?\s*state(?:,?\s*zip)?|street address|sample address)\W*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmptyAddress();

    [GeneratedRegex(@"(?:^|[ ,])\d{1,6}[\p{L}]?[ \t]+[\p{L}][\p{L}\p{M}\p{N} .’'-]{1,55}?\b(?:street|st|avenue|ave|road|rd|boulevard|blvd|drive|dr|lane|ln|way|court|ct|place|pl|terrace|ter|parkway|pkwy|close|crescent|square|sq)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex NumberedStreet();

    [GeneratedRegex(@"(?<!\p{L})(?:ул\.|улица\b|проспект\b|пр-т\b|пер\.|переулок\b|наб\.|набережная\b|шоссе\b|бульвар\b|бул\.|площадь\b|пл\.|\biela\b|\bbulv[āa]ris\b|\bgatve\b|\bprospekts\b|\blaukums\b)[^0-9;]{0,65}\b\d{1,5}[\p{L}]?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StreetBeforeNumber();

    [GeneratedRegex(@"^(?:[\p{L}][\p{L}\p{M} .’-]{1,48},?[ \t]+(?:[A-Z]{2}[ \t]+\d{5}(?:-\d{4})?|LV[ -]\d{4}|[A-Z]{1,2}\d[A-Z\d]?[ \t]+\d[A-Z]{2})|\d{6},?[ \t]+(?:г\.[ \t]*)?[\p{L}][\p{L}\p{M} .’-]{1,48})(?:,?[ \t]+(?:USA|UK|Latvia|Латвия|Россия|Russia))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex PostalLine();

    [GeneratedRegex(@"(?<![\p{L}\p{N}._%+\-])[\p{L}\p{N}](?:[\p{L}\p{N}._%+\-]*[\p{L}\p{N}])?@(?:[\p{L}\p{N}](?:[\p{L}\p{N}\-]*[\p{L}\p{N}])?\.)+(?:[\p{L}]{2,}|xn--[a-z0-9\-]+)(?![\p{L}\p{N}\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}@])(?:https?://|www\.)[\p{L}\p{N}][^\s<>""«»]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex UrlPattern();
}
