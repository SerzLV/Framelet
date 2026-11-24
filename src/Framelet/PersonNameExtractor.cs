using System.Text.RegularExpressions;

namespace Framelet;

internal static partial class PersonNameExtractor
{
    private const string HorizontalSpace = @"[^\S\r\n]";
    private const string NameWord = @"\p{L}[\p{L}\p{M}]*(?:[’'\-]\p{L}[\p{L}\p{M}]*)*";
    private const string CapitalizedName = @"\p{Lu}[\p{L}\p{M}]+(?:[’'\-]\p{L}[\p{L}\p{M}]*)*";
    private const string Initials = @"(?:\p{Lu}\." + HorizontalSpace + @"*){1,2}";

    internal static IEnumerable<ContactMatch> Find(string text)
    {
        var labelled = LabelledName().Matches(text).Select(match => match.Groups["name"]).ToArray();
        var signatures = SignatureName().Matches(text).Select(match => (Group)match)
            .Where(name => !labelled.Any(field => name.Index < field.Index + field.Length && field.Index < name.Index + name.Length));
        foreach (var name in labelled.Concat(signatures).OrderBy(name => name.Index))
        {
            var value = Regex.Replace(name.Value, @"\s+", " ");
            yield return new ContactMatch(new RecognizedContact(ContactKind.PersonName, value), value.ToUpperInvariant(), name.Index, name.Length);
        }
    }

    [GeneratedRegex(@"(?m)(?:^|[|•])" + HorizontalSpace + @"*(?:full name|first name|last name|surname|name|ф\.?и\.?о\.?|фио|фамилия имя отчество|имя|фамилия|vārds(?:,?" + HorizontalSpace + @"+(?:un" + HorizontalSpace + @"+)?uzvārds)?|uzvārds)"
        + HorizontalSpace + @"*[:：]" + HorizontalSpace + @"*(?:\r?\n" + HorizontalSpace + @"*)?(?<name>" + NameWord + @"(?:" + HorizontalSpace
        + @"+" + NameWord + @"){0,4})(?=" + HorizontalSpace + @"*(?:\r?$|[|•]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex LabelledName();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:" + Initials + CapitalizedName + "|" + CapitalizedName + HorizontalSpace + @"+"
        + @"(?:\p{Lu}\." + HorizontalSpace + @"*){0,1}\p{Lu}\.)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex SignatureName();
}
