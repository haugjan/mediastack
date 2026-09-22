using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aircheckarr.Services;

/// <summary>
/// Vergleicht, was ein Sender als laufenden Titel meldet, mit der Wunschliste.
///
/// Das ist der Kern der ganzen Anwendung, denn Sender schreiben denselben
/// Titel auf ein Dutzend Arten:
///
///   "Queen - Bohemian Rhapsody (Remastered 2011)"
///   "QUEEN / BOHEMIAN RHAPSODY"
///   "Bohemian Rhapsody - Queen"
///   "Queen feat. nobody - Bohemian Rhapsody [Live]"
///
/// Ein exakter Vergleich findet davon genau eine Schreibweise. Deshalb wird
/// erst normalisiert (Kleinschreibung, Akzente weg, Zusaetze in Klammern
/// weg, Satzzeichen weg) und dann mit Levenshtein auf Aehnlichkeit geprueft.
/// </summary>
public static partial class TitleMatcher
{
    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+(feat|ft|featuring|with|vs|versus)\.?\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s*-\s*(remaster(ed)?|live|radio edit|single version|mono|stereo)\b.*$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Suffixes();

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>
    /// Bringt eine Zeichenkette in die Form, in der verglichen wird.
    /// Was hier verschwindet, darf keinen Unterschied machen duerfen.
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.Trim().ToLowerInvariant();
        s = Brackets().Replace(s, " ");
        s = Featuring().Replace(s, "");
        s = Suffixes().Replace(s, "");

        // Akzente abtrennen und verwerfen: Bjoerk und Bj\u00f6rk sind dasselbe.
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        s = sb.ToString().Normalize(NormalizationForm.FormC);

        // Das kaufmaennische Und ist die haeufigste Abweichung ueberhaupt.
        s = s.Replace("&", " and ").Replace("+", " and ");
        s = NonWord().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();

        // Fuehrende Artikel kosten Aehnlichkeit, ohne etwas zu unterscheiden.
        foreach (var article in (string[])["the ", "die ", "der ", "das "])
            if (s.StartsWith(article, StringComparison.Ordinal))
            { s = s[article.Length..]; break; }

        return s;
    }

    /// <summary>
    /// Zerlegt die ICY-Zeile eines Senders in Interpret und Titel.
    /// Ueblich ist "Interpret - Titel", vorkommen tut auch "Titel - Interpret";
    /// unterscheiden laesst sich das nicht, deshalb wird spaeter in beiden
    /// Richtungen verglichen.
    /// </summary>
    public static (string Artist, string Title)? SplitStreamTitle(string streamTitle)
    {
        if (string.IsNullOrWhiteSpace(streamTitle)) return null;

        var text = streamTitle.Trim();
        // Sender haengen gern Werbung an: "Titel - Interpret | RADIO XY"
        var pipe = text.IndexOf('|');
        if (pipe > 10) text = text[..pipe].Trim();

        // Halbgeviert- und Geviertstrich als Escape, damit die Datei ASCII
        // bleibt: sie kommen in echten Titelmeldungen haeufig vor.
        foreach (var sep in (string[])[" - ", " \u2013 ", " \u2014 ", " / "])
        {
            var i = text.IndexOf(sep, StringComparison.Ordinal);
            if (i <= 0) continue;
            var left = text[..i].Trim();
            var right = text[(i + sep.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0) continue;
            return (left, right);
        }
        return null;
    }

    /// <summary>Aehnlichkeit zweier normalisierter Zeichenketten, 0 bis 1.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        // Enthaltensein wird hoch bewertet: "bohemian rhapsody" in
        // "bohemian rhapsody remastered" ist derselbe Titel.
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return 0.95;

        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        // Nur zwei Zeilen statt der ganzen Matrix: der Vergleich laeuft bei
        // jedem Metadatenwechsel gegen die gesamte Wunschliste.
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                                      previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// Passt das Gehoerte zu einem Wunsch? Vergleicht beide Leserichtungen,
    /// weil manche Sender "Titel - Interpret" senden.
    /// </summary>
    public static double Score(string heardArtist, string heardTitle,
                               string wishArtist, string wishTitle)
    {
        var a = Normalize(heardArtist);
        var t = Normalize(heardTitle);

        var straight = (Similarity(a, wishArtist) + Similarity(t, wishTitle)) / 2;
        var swapped = (Similarity(t, wishArtist) + Similarity(a, wishTitle)) / 2;
        return Math.Max(straight, swapped);
    }
}
