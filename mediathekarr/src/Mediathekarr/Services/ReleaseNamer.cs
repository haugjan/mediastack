using System.Globalization;
using System.Text;
using Mediathekarr.Data;

namespace Mediathekarr.Services;

// Baut aus einem Mediathek-Beitrag einen Namen, wie ihn Sonarr und Radarr
// lesen koennen. Der Name ist die ganze Schnittstelle: die Apps sehen vom
// Beitrag nichts ausser ihm.
public static class ReleaseNamer
{
    public const string Group = "MEDIATHEK";

    public static string Tv(string series, int season, int episode, string episodeTitle, MediathekItem item) =>
        Join($"{Clean(series)}.S{season:00}E{episode:00}", Clean(episodeTitle), Suffix(item));

    public static string TvByDate(string series, MediathekItem item) =>
        Join($"{Clean(series)}.{item.Published:yyyy-MM-dd}", Clean(item.Title), Suffix(item));

    public static string Movie(string title, int year, MediathekItem item) =>
        Join($"{Clean(title)}.{(year > 0 ? year : item.Published.Year)}", "", Suffix(item));

    private static string Join(string head, string middle, string tail) =>
        string.IsNullOrWhiteSpace(middle) ? $"{head}.{tail}" : $"{head}.{middle}.{tail}";

    private static string Suffix(MediathekItem item) => $"GERMAN.{Quality(item)}.WEB.h264-{Group}";

    // Die Sender schreiben nirgends hin, welche Aufloesung eine Datei hat.
    // Die Bitrate verraet es zuverlaessig genug, und sie ist ehrlicher als
    // ein pauschales "1080p" auf jedem Beitrag - die Qualitaetsprofile
    // sortieren sonst falsch.
    public static string Quality(MediathekItem item)
    {
        if (item.Duration <= 0 || item.Size <= 0) return "720p";
        var mbit = item.Size * 8.0 / item.Duration / 1_000_000.0;
        return mbit switch
        {
            >= 4.5 => "1080p",
            >= 2.0 => "720p",
            _ => "480p",
        };
    }

    // Punkte statt Leerzeichen, keine Sonderzeichen, Umlaute ausgeschrieben.
    // Genau so, wie Release-Namen ueblicherweise aussehen - alles andere
    // stolpert durch die Parser der Apps.
    public static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue")
            .Replace("ß", "ss");
        var sb = new StringBuilder(s.Length);
        var dot = true;
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); dot = false; }
            else if (!dot) { sb.Append('.'); dot = true; }
        }
        return sb.ToString().Trim('.');
    }
}
