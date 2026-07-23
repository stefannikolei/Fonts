// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Tables.AdvancedTypographic;

namespace SixLabors.Fonts.Tests.Tables.AdvancedTypographic;

public class OpenTypeLanguageTagMapTests
{
    /// <summary>
    /// Rows ported from the HarfBuzz hb_ot_tags_from_language test corpus
    /// (test/api/test-ot-tag.c), asserting the primary candidate tag. Rows whose inputs
    /// cannot be expressed as a <see cref="CultureInfo"/> are omitted: private use
    /// extensions (x-hbot), grandfathered tags (i-lux, zh-min-nan), the und language,
    /// and HarfBuzz's locale syntax (tr@foo=bar). Rows where the runtime demands a
    /// region between the language and a variant subtag carry one.
    /// </summary>
    [Theory]
    [InlineData("alt", "ALT ")]
    [InlineData("ar", "ARA ")]
    [InlineData("ar-001", "ARA ")]
    [InlineData("az", "AZE ")]
    [InlineData("az-IR", "AZE ")]
    [InlineData("en", "ENG ")]
    [InlineData("en-US", "ENG ")]
    [InlineData("cjm", "CJM ")]
    [InlineData("eve", "EVN ")]
    [InlineData("cfm", "HAL ")]
    [InlineData("hy", "HYE0")]
    [InlineData("hyw", "HYE ")]
    [InlineData("bgr", "QIN ")]
    [InlineData("cnh", "QIN ")]
    [InlineData("ctd", "QIN ")]
    [InlineData("zom", "QIN ")]
    [InlineData("fa", "FAR ")]
    [InlineData("fa-IR", "FAR ")]
    [InlineData("man", "MNK ")]
    [InlineData("aii", "SWA ")]
    [InlineData("syr", "SYR ")]
    [InlineData("amw", "SYR ")]
    [InlineData("cld", "SYR ")]
    [InlineData("syc", "SYR ")]
    [InlineData("tru", "TUA ")]
    [InlineData("ghc", "IRT ")]
    [InlineData("ga-Latg", "IRT ")]
    [InlineData("ka-Geok", "KGE ")]
    [InlineData("ro-MD", "MOL ")]
    [InlineData("el-CY-polyton", "PGR ")]
    [InlineData("el-GR-polyton", "PGR ")]
    [InlineData("en-US-fonipa", "IPPH")]
    [InlineData("zh-CN-fonipa", "IPPH")]
    [InlineData("en-US-fonnapa", "APPH")]
    [InlineData("chr-US-fonnapa", "APPH")]
    [InlineData("aii-Syre", "SYRE")]
    [InlineData("de-Syre", "SYRE")]
    [InlineData("syr-Syre", "SYRE")]
    [InlineData("aii-Syrj", "SYRJ")]
    [InlineData("de-Syrj", "SYRJ")]
    [InlineData("syr-Syrj", "SYRJ")]
    [InlineData("aii-Syrn", "SYRN")]
    [InlineData("de-Syrn", "SYRN")]
    [InlineData("syr-Syrn", "SYRN")]
    [InlineData("aao", "ARA ")]
    [InlineData("gom", "KOK ")]
    [InlineData("drh", "MNG ")]
    [InlineData("als", "SQI ")]
    [InlineData("nb", "NOR ")]
    [InlineData("nn", "NYN ")]
    [InlineData("hak", "ZHS ")]
    [InlineData("wuu", "ZHS ")]
    [InlineData("lzh", "ZHT ")]
    [InlineData("qu", "QUZ ")]
    [InlineData("quy", "QUZ ")]
    [InlineData("dwk", "KUI ")]
    [InlineData("ggo", "GON ")]
    [InlineData("kpp", "KRN ")]
    [InlineData("nln", "NAH ")]
    [InlineData("xwo", "TOD ")]
    public void ResolvesHarfBuzzCorpusPrimaryTag(string cultureName, string expected)
    {
        Assert.True(OpenTypeLanguageTagMap.TryGetTags(new CultureInfo(cultureName), out Tag[] tags));
        Assert.Equal(Tag.Parse(expected), tags[0]);
    }

    /// <summary>
    /// The Chinese rows from the HarfBuzz corpus: the tags encode script and region, an
    /// explicit simplified script overrides any region, traditional defers to the Macao
    /// and Hong Kong conventions, and bare Cantonese defaults to Hong Kong.
    /// </summary>
    [Theory]
    [InlineData("zh", "ZHS ")]
    [InlineData("zh-CN", "ZHS ")]
    [InlineData("zh-SG", "ZHS ")]
    [InlineData("zh-MO", "ZHTM")]
    [InlineData("zh-Hant-MO", "ZHTM")]
    [InlineData("zh-Hans-MO", "ZHS ")]
    [InlineData("zh-HK", "ZHH ")]
    [InlineData("zh-Hant-HK", "ZHH ")]
    [InlineData("zh-Hans-HK", "ZHS ")]
    [InlineData("zh-TW", "ZHT ")]
    [InlineData("zh-Hans", "ZHS ")]
    [InlineData("zh-Hant", "ZHT ")]
    [InlineData("zh-Hans-TW", "ZHS ")]
    [InlineData("yue", "ZHH ")]
    [InlineData("yue-Hant", "ZHH ")]
    [InlineData("yue-Hans", "ZHS ")]
    public void ResolvesChineseByScriptAndRegion(string cultureName, string expected)
    {
        Assert.True(OpenTypeLanguageTagMap.TryGetTags(new CultureInfo(cultureName), out Tag[] tags));
        Assert.Equal(Tag.Parse(expected), tags[0]);
    }

    [Theory]
    [InlineData("zh-HK", "ZHH ", "ZHT ")]
    [InlineData("zh-MO", "ZHTM", "ZHH ")]
    public void ChineseRegionalConventionsFallBackToTraditional(string cultureName, string first, string second)
    {
        // A font without the regional conventions must still get the nearest regional
        // then traditional forms before simplified ones; Macao falls back through the
        // Hong Kong conventions per the HarfBuzz language-tags expectations.
        Assert.True(OpenTypeLanguageTagMap.TryGetTags(new CultureInfo(cultureName), out Tag[] tags));
        Assert.Equal(Tag.Parse(first), tags[0]);
        Assert.Equal(Tag.Parse(second), tags[1]);
        Assert.Contains(Tag.Parse("ZHT "), tags);
    }

    [Fact]
    public void VariantOutranksLanguageMapping()
    {
        // The variant defines the transcription system; the plain language mapping
        // remains as a candidate for fonts without the phonetic language system.
        Assert.True(OpenTypeLanguageTagMap.TryGetTags(new CultureInfo("el-GR-polyton"), out Tag[] tags));
        Assert.Equal(Tag.Parse("PGR "), tags[0]);
        Assert.Contains(Tag.Parse("ELL "), tags);
    }

    [Fact]
    public void UnknownIsoCodeSynthesizesUppercaseTag()
    {
        // The specification directs fonts supporting languages without registered tags
        // to use the uppercase ISO 639-3 code, matching the HarfBuzz xyz -> XYZ row.
        Assert.True(OpenTypeLanguageTagMap.TryGetTags(new CultureInfo("xyz"), out Tag[] tags));
        Assert.Equal(Tag.Parse("XYZ "), tags[0]);
    }

    [Fact]
    public void InvariantCultureResolvesNoTags()
    {
        // The invariant culture expresses no language preference: the default language
        // system applies.
        Assert.False(OpenTypeLanguageTagMap.TryGetTags(CultureInfo.InvariantCulture, out Tag[] tags));
        Assert.Empty(tags);
    }

    [Fact]
    public void NullCultureResolvesNoTags()
    {
        Assert.False(OpenTypeLanguageTagMap.TryGetTags(null, out Tag[] tags));
        Assert.Empty(tags);
    }
}
