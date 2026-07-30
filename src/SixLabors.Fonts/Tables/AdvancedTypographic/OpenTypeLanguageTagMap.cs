// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Resolves a <see cref="CultureInfo"/> to the OpenType language system tags used to
/// select a language system within a script's feature table.
/// <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/languagetags"/>
/// </summary>
/// <remarks>
/// Candidates are ordered most specific first; callers select the first tag the font's
/// script table declares and fall back to the default language system otherwise. The
/// resolution rules mirror HarfBuzz's hb_ot_tags_from_language and are pinned against
/// its test corpus. The registry data half of this class is generated; see the
/// UnicodeTrieGenerator project.
/// </remarks>
internal sealed partial class OpenTypeLanguageTagMap
{
    /// <summary>
    /// The lazily-initialized ISO 639 code to language system tag map.
    /// </summary>
    private static readonly Lazy<Dictionary<string, Tag[]>> LazyMap = new(CreateIsoLanguageMap, isThreadSafe: true);

    /// <summary>
    /// Maps BCP 47 variant and script subtags to the language system tags the registry
    /// defines by cross reference rather than by ISO 639 code.
    /// </summary>
    private static readonly Dictionary<string, Tag> SubtagTagMap = new(StringComparer.Ordinal)
    {
        { "fonipa", Tag.Parse("IPPH") }, // Phonetic transcription, IPA conventions
        { "fonnapa", Tag.Parse("APPH") }, // Phonetic transcription, Americanist conventions
        { "polyton", Tag.Parse("PGR ") }, // Polytonic Greek
        { "provenc", Tag.Parse("PRO ") }, // Provencal
        { "geok", Tag.Parse("KGE ") }, // Khutsuri Georgian
        { "syre", Tag.Parse("SYRE") }, // Syriac, Estrangela script variant
        { "syrj", Tag.Parse("SYRJ") }, // Syriac, Western script variant
        { "syrn", Tag.Parse("SYRN") }, // Syriac, Eastern script variant
        { "latg", Tag.Parse("IRT ") }, // Irish Traditional, Latin Gaelic script
        { "arevmda", Tag.Parse("HYE ") }, // Western Armenian
    };

    private static readonly Tag ZhsTag = Tag.Parse("ZHS ");

    private static readonly Tag ZhtTag = Tag.Parse("ZHT ");

    private static readonly Tag ZhhTag = Tag.Parse("ZHH ");

    private static readonly Tag ZhtmTag = Tag.Parse("ZHTM");

    private static readonly Tag MolTag = Tag.Parse("MOL ");

    /// <summary>
    /// Prevents a default instance of the <see cref="OpenTypeLanguageTagMap"/> class
    /// from being created.
    /// </summary>
    private OpenTypeLanguageTagMap()
    {
    }

    /// <summary>
    /// Resolves the candidate OpenType language system tags for the supplied culture,
    /// most specific first.
    /// </summary>
    /// <param name="culture">The culture to resolve.</param>
    /// <param name="tags">
    /// When this method returns, contains the candidate tags if any resolved; otherwise
    /// an empty array. This parameter is passed uninitialized.
    /// </param>
    /// <returns><see langword="true"/> if any tags resolved; otherwise <see langword="false"/>.</returns>
    public static bool TryGetTags(CultureInfo? culture, out Tag[] tags)
    {
        if (culture is null || string.IsNullOrEmpty(culture.Name))
        {
            // The invariant culture expresses no language preference: the default
            // language system applies.
            tags = [];
            return false;
        }

        List<Tag> candidates = [];
        string[] subtags = culture.Name.ToLowerInvariant().Split('-');
        string threeLetter = culture.ThreeLetterISOLanguageName.ToLowerInvariant();

        // BCP 47 variant and script subtags override the language mapping entirely: the
        // registry defines these tags by subtag, not by ISO code.
        for (int i = 1; i < subtags.Length; i++)
        {
            if (SubtagTagMap.TryGetValue(subtags[i], out Tag subtagTag))
            {
                AddDistinct(candidates, subtagTag);
            }
        }

        if (subtags[0] is "zh" or "yue" or "cmn" || threeLetter is "zho" or "cmn" or "yue")
        {
            AddChineseCandidates(candidates, subtags);
        }
        else if ((subtags[0] == "ro" || threeLetter == "ron") && HasSubtag(subtags, "md"))
        {
            // Moldavian keeps its own registered tag for Romanian in Moldova; the
            // Romanian tag follows from the general lookup below.
            AddDistinct(candidates, MolTag);
        }

        // The registry lists ISO 639-3 codes; older rows also carry two letter forms.
        // The runtime reports an empty or echoed code for languages it has no data for,
        // so the two letter form doubles as the raw subtag for unknown languages.
        string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        Dictionary<string, Tag[]> map = LazyMap.Value;
        if (map.TryGetValue(threeLetter, out Tag[]? mapped) || map.TryGetValue(twoLetter, out mapped))
        {
            foreach (Tag tag in mapped)
            {
                AddDistinct(candidates, tag);
            }
        }
        else if (TryGetSynthesisSource(threeLetter, twoLetter, out string source))
        {
            // The specification directs fonts supporting languages without registered
            // tags to use the uppercase ISO 639-3 code as the language system tag, so an
            // unmapped code synthesizes that candidate, matching HarfBuzz.
            Span<char> synthesized = ['\0', '\0', '\0', ' '];
            for (int i = 0; i < 3; i++)
            {
                synthesized[i] = char.ToUpperInvariant(source[i]);
            }

            AddDistinct(candidates, Tag.Parse(new string(synthesized)));
        }

        tags = [.. candidates];
        return tags.Length > 0;
    }

    /// <summary>
    /// Adds the Chinese language system candidates. The Chinese tags encode script and
    /// region rather than language, so resolution mirrors HarfBuzz: an explicit
    /// simplified script wins over any region, an explicit traditional script defers to
    /// the Macao and Hong Kong regional conventions, then regions decide, and bare
    /// Cantonese defaults to the Hong Kong conventions while any other bare Chinese
    /// defaults to simplified. Traditional regional conventions fall back to the general
    /// traditional tag.
    /// </summary>
    /// <param name="candidates">The candidate list to add to.</param>
    /// <param name="subtags">The lowercase culture name subtags.</param>
    private static void AddChineseCandidates(List<Tag> candidates, string[] subtags)
    {
        bool simplified = HasSubtag(subtags, "hans");
        bool traditional = HasSubtag(subtags, "hant");
        bool taiwan = HasSubtag(subtags, "tw");
        bool hongKong = HasSubtag(subtags, "hk");
        bool macao = HasSubtag(subtags, "mo");

        if (simplified)
        {
            AddDistinct(candidates, ZhsTag);
        }
        else if (macao)
        {
            // Macao's dedicated tag is rare in fonts; the Hong Kong conventions are the
            // regional fallback before the general traditional tag, matching HarfBuzz's
            // language-tags shaping expectations.
            AddDistinct(candidates, ZhtmTag);
            AddDistinct(candidates, ZhhTag);
            AddDistinct(candidates, ZhtTag);
        }
        else if (hongKong || subtags[0] == "yue")
        {
            // Cantonese without an explicit simplified script or region uses the Hong
            // Kong conventions, including with an explicit traditional script.
            AddDistinct(candidates, ZhhTag);
            AddDistinct(candidates, ZhtTag);
        }
        else if (traditional || taiwan)
        {
            AddDistinct(candidates, ZhtTag);
        }
        else
        {
            AddDistinct(candidates, ZhsTag);
        }
    }

    /// <summary>
    /// Selects the ISO 639-3 code an unmapped language synthesizes its tag from,
    /// preferring the resolved three letter code and falling back to the raw subtag the
    /// runtime echoes for languages it has no data for.
    /// </summary>
    /// <param name="threeLetter">The lowercase three letter ISO language name.</param>
    /// <param name="twoLetter">The lowercase two letter ISO language name.</param>
    /// <param name="source">The selected code.</param>
    /// <returns><see langword="true"/> if a three letter code is available; otherwise <see langword="false"/>.</returns>
    private static bool TryGetSynthesisSource(string threeLetter, string twoLetter, out string source)
    {
        if (threeLetter.Length == 3 && threeLetter != "und" && IsAsciiLetters(threeLetter))
        {
            source = threeLetter;
            return true;
        }

        if (twoLetter.Length == 3 && twoLetter != "und" && IsAsciiLetters(twoLetter))
        {
            source = twoLetter;
            return true;
        }

        source = string.Empty;
        return false;
    }

    private static bool HasSubtag(string[] subtags, string value)
    {
        for (int i = 1; i < subtags.Length; i++)
        {
            if (subtags[i] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiLetters(string value)
    {
        foreach (char c in value)
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }

        return true;
    }

    private static void AddDistinct(List<Tag> candidates, Tag tag)
    {
        if (!candidates.Contains(tag))
        {
            candidates.Add(tag);
        }
    }
}
