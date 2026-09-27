using System.Text.RegularExpressions;

namespace Mediathekarr.Services;

// Ordnet eine Sendereihe einer Kategorie zu. Die Filmliste kennt keine
// Kategorien, nur Sender, Sendungsname und Beschreibung - also wird aus
// Schluesselwoertern geraten. Das trifft die grossen Reihen zuverlaessig
// (Tatort, Terra X, Tagesschau), bei kleinen landet manches unter "Weitere".
//
// Drei Dinge, die der erste Versuch falsch machte und die man sonst wieder
// falsch macht:
//  - Teilwoerter: "ski" steckt in "skizzieren", "doktor" im "Bergdoktor".
//    Gesucht wird deshalb nach ganzen Woertern (mit ueblichen Endungen);
//    nur ein Stern am Ende ("doku*") erlaubt ausdruecklich einen Wortanfang.
//  - Beschreibungen sind lang und voller Zufallstreffer. Sie zaehlen nur,
//    wenn der Name nichts hergibt, und dann erst ab zwei Treffern derselben
//    Kategorie.
//  - Reihenfolge: beim Namen gewinnt die erste passende Regel. Eindeutige
//    Namen stehen deshalb vor allgemeinen Woertern, sonst waere die
//    "heute-show" eine Nachrichtensendung und "Schweiz aktuell" keine
//    Regionalsendung.
public static class Categorizer
{
    public const string Other = "Weitere";

    private static readonly (string Category, string[] Words)[] Rules =
    [
        ("Comedy & Satire", ["heute-show", "extra 3", "comedy*", "satire*", "kabarett*", "die anstalt",
                             "late night", "late-night", "magazin royale", "giacobbo", "deville",
                             "spassbremse", "nuhr", "mitternachtsspitzen", "comedymänner"]),
        ("Kinder", ["kika", "zdf-tivi", "logo!", "sendung mit der maus", "löwenzahn", "pur+", "checker",
                    "wissen macht ah", "sandmännchen", "tigerenten", "schloss einstein", "kinder*", "kids",
                    "zambo", "siebenstein", "teletubbies", "sesamstraße", "biene maja", "wickie",
                    "sendung mit dem elefanten", "trickfilm*", "zeichentrick*", "kindernachrichten"]),
        ("Regional", ["landesschau", "lokalzeit", "hessenschau", "hallo hessen", "hallo niedersachsen",
                      "nordmagazin", "sachsenspiegel", "abendschau", "wir im saarland", "wir in bayern",
                      "unser land", "schweiz aktuell", "regional*", "heimat*", "brandenburg aktuell",
                      "mdr um 4", "hier und heute", "schleswig-holstein magazin"]),
        ("Nachrichten", ["tagesschau", "tagesthemen", "heute journal", "heute 19:00 uhr", "heute xpress",
                         "zdfheute", "10 vor 10", "zib", "nachrichten", "news", "brennpunkt",
                         "morgenmagazin", "mittagsmagazin", "aktuell", "aktueller bericht", "heute",
                         "rundschau", "bericht vor 8"]),
        ("Krimi", ["tatort", "polizeiruf", "soko", "krimi*", "kommissar*", "der alte", "ein fall für zwei",
                   "notruf hafenkante", "rosenheim-cops", "wapo", "morden im norden", "bozen-krimi",
                   "zürich-krimi", "helen dorn", "nord bei nordwest", "wilsberg", "der bestatter",
                   "wallander", "donna leon", "thriller", "wahre verbrechen", "true crime", "mord",
                   "kriminal*", "ermittler*"]),
        ("Filme", ["filme", "film", "spielfilm*", "fernsehfilm*", "kinofilm*", "film im ersten", "filmmittwoch",
                   "herzkino", "kleines fernsehspiel", "kurzfilm*", "movie", "kino"]),
        ("Serien & Reihen", ["serie", "serien", "der bergdoktor", "die bergretter", "das traumschiff", "rote rosen",
                    "sturm der liebe", "in aller freundschaft", "lindenstraße", "die fallers",
                    "um himmels willen", "dahoam is dahoam", "hubert und staller", "watzmann ermittelt",
                    "bettys diagnose", "die rentnercops", "fascht e familie", "top secret", "sitcom*"]),
        ("Politik & Talk", ["arena", "maischberger", "markus lanz", "illner", "hart aber fair", "caren miosga",
                            "presseclub", "phoenix runde", "talk", "im gespräch", "politik", "politisch*",
                            "berlin direkt", "bericht aus berlin", "monitor", "panorama", "frontal",
                            "kontraste", "fakt", "club", "sternstunde", "gredig direkt", "weltspiegel",
                            "europa*", "bundestag*", "wahl*", "abstimmung*", "auslandsjournal",
                            "standpunkte", "politix", "srfglobal", "nzz format", "forum am freitag"]),
        ("Ratgeber & Wirtschaft", ["wirtschaft*", "börse*", "eco", "cash tv", "bilanz", "trend","kassensturz", "ratgeber", "plusminus", "wiso", "marktcheck", "markt", "verbraucher*",
                      "service", "haushaltscheck", "servicezeit", "volle kanne", "kaffee oder tee",
                      "ard-buffet", "live nach neun", "hallo deutschland", "geld*", "recht"]),
        ("Sport", ["sport", "sportschau", "sportstudio", "sportheute", "sportpanorama", "sportlounge",
                   "fussball", "fußball", "bundesliga", "champions league", "olympia*", "olympisch*",
                   "paralympics", "ski", "skispringen", "formel 1", "tennis", "handball", "eishockey",
                   "biathlon", "tour de france", "wintersport", "leichtathletik", "schwingen", "jass",
                   "super league", "uefa", "champions", "ski alpin", "radsport", "motorsport",
                   "wm", "em", "fifa", "nations league", "finals", "sailgp", "weltmeisterschaft*"]),
        ("Musik", ["konzert*", "musik*", "potzmusig", "festival*", "rockpalast", "jazz*", "klassik",
                   "oper", "hitparade", "schlager*", "musikantenstadl", "philharmonie", "orchester",
                   "sinfonie*", "chor", "musig", "song*", "pop"]),
        ("Kochen", ["kochen", "koch", "kochshow", "küchenschlacht", "chuchi", "küche", "rezept*", "backen",
                    "grillen", "lecker", "kulinar*", "mini beiz", "landfrauenküche", "essen und trinken"]),
        ("Natur & Reisen", ["natur", "tiere", "tier", "wildnis", "expedition*", "reise*", "von oben",
                            "landschaft*", "meer", "berge", "alpen", "insel*", "safari", "nationalpark*",
                            "garten", "zoo", "eisenbahn-romantik", "fahr mal hin", "schätze der welt",
                            "länder", "doku natur", "doku reise", "wildlife", "planet erde"]),
        ("Wissen", ["terra x*", "nano", "quarks", "wissen", "einstein", "puls", "gesundheit*", "medizin*",
                    "planet e", "planet wissen", "alpha-centauri", "leschs kosmos", "xenius", "odysso",
                    "abenteuer forschung", "w wie wissen", "scobel", "physik", "geschichte", "history",
                    "zdfinfo", "technik", "forschung", "mrwissen2go", "planet schule", "schulfernsehen",
                    "uni", "frag den lesch", "bildung"]),
        ("Dokumentation", ["doku*", "einzeldoku*", "kurzreportage*", "dokumentation*", "reportage*", "37 grad", "dok", "die story",
                           "exclusiv im ersten", "reporter", "rec.", "porträt*", "portrait*",
                           "zeitgeschichte", "y-kollektiv", "strg_f", "stark!", "zdfzoom", "play suisse"]),
        ("Kultur", ["kultur*", "kulturplatz", "kulturzeit", "literatur*", "theater", "kunst", "museum",
                    "aspekte", "titel thesen temperamente", "ttt", "druckfrisch", "literarische quartett",
                    "bühne", "tanz", "architektur", "philosophie", "gottesdienst*", "kirche*",
                    "fenster zum sonntag", "wort zum sonntag", "religion*", "glaube*", "buchmesse"]),
        ("Unterhaltung", ["show", "quiz", "gala", "wer weiß denn sowas", "verstehen sie spaß",
                          "wetten, dass", "fernsehgarten", "gefragt – gejagt", "gefragt - gejagt",
                          "silbereisen", "unterhaltung", "gameshow", "1 gegen 100", "deal or no deal",
                          "happy day", "bares für rares", "traumpaar", "rate*"]),
    ];

    /// <summary>Die Kategorien in der Reihenfolge, in der die Oberflaeche sie zeigt.</summary>
    public static readonly string[] All =
        [.. Rules.Select(r => r.Category).Distinct().OrderBy(c => c), Other];

    // Je Kategorie die Woerter als fertige Suchmuster. Ganzes Wort mit den
    // ueblichen deutschen Endungen, oder mit Stern ein Wortanfang.
    private static readonly (string Category, Regex[] Patterns)[] Compiled =
        Rules.Select(r => (r.Category, r.Words.Select(ToRegex).ToArray())).ToArray();

    private static Regex ToRegex(string word)
    {
        var prefix = word.EndsWith('*');
        var core = Regex.Escape(prefix ? word[..^1] : word);
        var pattern = prefix
            ? $@"(?<![\p{{L}}\p{{N}}]){core}"
            : $@"(?<![\p{{L}}\p{{N}}]){core}(?:s|e|en|n|er|es)?(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    // "Folge 12", "Staffel 2", "(S01/E05)": so heissen Folgen von Serien.
    // Viele grosse Serien verraten sich weder im Namen noch in der
    // Beschreibung ("Die WG", "Lena Lorenz"), wohl aber an ihren Folgen.
    // "Teil 3" zaehlt bewusst nicht, so nummerieren Dokureihen.
    private static readonly Regex EpisodeTitle = new(
        @"\b(?:folge|staffel|episode)\s*\d+|\(s\d+\s*/\s*e\d+\)|\bs\d{1,2}\s*e\d{1,3}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <param name="descriptions">Beschreibungen einiger Folgen, aneinandergehaengt.</param>
    /// <param name="titles">Titel einiger Folgen, eine je Zeile.</param>
    public static string Classify(string channel, string topic, string descriptions, string titles = "")
    {
        // Kinderkanaele sind immer Kinder, egal wie die Reihe heisst.
        var c = channel.ToLowerInvariant();
        if (c.Contains("kika") || c.Contains("tivi")) return "Kinder";

        foreach (var (category, patterns) in Compiled)
            if (patterns.Any(p => p.IsMatch(topic))) return category;

        // Die Beschreibung erst, wenn der Name nichts hergibt, und nur mit
        // mindestens zwei verschiedenen Treffern derselben Kategorie.
        var best = Other;
        var bestHits = 1;
        foreach (var (category, patterns) in Compiled)
        {
            var hits = patterns.Count(p => p.IsMatch(descriptions));
            if (hits <= bestHits) continue;
            bestHits = hits;
            best = category;
        }
        if (best != Other) return best;

        // Zuletzt die Folgentitel: mindestens zwei Folgen, die Haelfte mit
        // Folgennummer. Erst nach der Beschreibung, denn auch Dokureihen
        // nummerieren, und die sagen in der Beschreibung, was sie sind.
        var list = titles.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (list.Length >= 2 && list.Count(t => EpisodeTitle.IsMatch(t)) * 2 >= list.Length)
            return "Serien & Reihen";
        return Other;
    }
}
