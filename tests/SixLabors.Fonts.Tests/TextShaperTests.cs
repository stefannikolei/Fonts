// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;

namespace SixLabors.Fonts.Tests;

public class TextShaperTests
{
    [Fact]
    public void Shape_Latin_ProducesSequentialGlyphStream()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("Hxp", new TextOptions(font));

        Assert.Equal(3, glyphs.Count);
        for (int i = 0; i < glyphs.Count; i++)
        {
            Assert.Equal(i, glyphs[i].CodePointIndex);
            Assert.Equal(1, glyphs[i].CodePointCount);
            Assert.NotEqual(0, glyphs[i].GlyphId);
            Assert.True(glyphs[i].AdvanceWidth > 0);
            Assert.Same(font, glyphs[i].Font);
        }
    }

    [Fact]
    public void Shape_EmptyText_ProducesNoGlyphs()
        => Assert.Empty(TextShaper.Shape(
            string.Empty,
            new TextOptions(new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72))));

    [Fact]
    public void Shape_AdvancesMatchMeasuredAdvance()
    {
        // Design-unit advances scaled to pixel units must agree with the measured
        // logical advance for a single unwrapped line.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        TextOptions options = new(font);
        const string text = "Hxplq";

        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape(text, options);

        float scale = font.Size / font.FontMetrics.UnitsPerEm;
        float shapedAdvance = 0;
        foreach (ShapedGlyph glyph in glyphs)
        {
            shapedAdvance += glyph.AdvanceWidth * scale;
        }

        FontRectangle measured = TextMeasurer.MeasureAdvance(text, options);
        Assert.Equal(measured.Width, shapedAdvance, 3F);
    }

    [Fact]
    public void Shape_Ligature_MergesCodePoints()
    {
        // Dubai applies mandatory Arabic ligatures; Lam + Alef must merge into a single
        // glyph spanning both codepoints.
        Font font = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("لا", new TextOptions(font));

        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].CodePointIndex);
        Assert.Equal(2, glyphs[0].CodePointCount);
    }

    [Fact]
    public void Shape_RightToLeft_KeepsLogicalOrder()
    {
        // The shaper reports glyphs in logical (source) order; visual reordering is a
        // layout concern.
        Font font = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("سلام", new TextOptions(font));

        Assert.True(glyphs.Count > 1);
        for (int i = 1; i < glyphs.Count; i++)
        {
            Assert.True(glyphs[i].CodePointIndex > glyphs[i - 1].CodePointIndex);
        }
    }

    [Fact]
    public void Shape_UnmappedCodePoint_ProducesMissingGlyph()
    {
        // With no fallback fonts configured an unmapped codepoint emits the font's
        // missing glyph, matching the single-face shaping model.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("☃", new TextOptions(font));

        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].GlyphId);
    }

    [Fact]
    public void Shape_Vertical_PopulatesAdvanceHeight()
    {
        Font font = new FontCollection().Add(TestFonts.NotoSansSCBaselineSubsetFile).CreateFont(72);
        TextOptions options = new(font)
        {
            LayoutMode = LayoutMode.VerticalLeftRight
        };

        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("永国", options);

        Assert.Equal(2, glyphs.Count);
        foreach (ShapedGlyph glyph in glyphs)
        {
            Assert.True(glyph.AdvanceHeight > 0);
        }
    }

    [Theory]
    [InlineData("ro-RO")]
    [InlineData("ro-MD")]
    public void Shape_Culture_AppliesRomanianLocalizedForms(string cultureName)
    {
        // Open Sans carries ROM and MOL language systems whose locl feature substitutes
        // the legacy cedilla forms with the correct comma accent forms: s cedilla
        // (U+015F) must shape as the s comma (U+0219) glyph. Moldova resolves to MOL and
        // Romania to ROM; both select the same substitution in this font.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);

        ushort plain = Assert.Single(TextShaper.Shape("ş", new TextOptions(font))).GlyphId;
        ushort expected = Assert.Single(TextShaper.Shape("ș", new TextOptions(font))).GlyphId;
        Assert.NotEqual(plain, expected);

        TextOptions options = new(font)
        {
            Culture = new CultureInfo(cultureName)
        };

        Assert.Equal(expected, Assert.Single(TextShaper.Shape("ş", options)).GlyphId);
    }

    [Theory]
    [InlineData("sr-RS")]
    [InlineData("mk-MK")]
    public void Shape_Culture_AppliesSerbianCyrillicForms(string cultureName)
    {
        // Open Sans carries SRB and MKD language systems on the Cyrillic script whose
        // locl feature substitutes the Cyrillic be (U+0431) with its Serbian form. The
        // substituted glyph has no codepoint of its own, so the assertion pins that the
        // culture changes the resolved glyph.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);

        ushort plain = Assert.Single(TextShaper.Shape("б", new TextOptions(font))).GlyphId;

        TextOptions options = new(font)
        {
            Culture = new CultureInfo(cultureName)
        };

        ushort localized = Assert.Single(TextShaper.Shape("б", options)).GlyphId;
        Assert.NotEqual(plain, localized);
        Assert.NotEqual(0, localized);
    }

    [Fact]
    public void Shape_Culture_WithoutMatchingLanguageSystem_UsesDefault()
    {
        // Turkish has no language system in Open Sans, so shaping falls back to the
        // default language system and the localized substitution must not apply.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);

        ushort plain = Assert.Single(TextShaper.Shape("ş", new TextOptions(font))).GlyphId;

        TextOptions options = new(font)
        {
            Culture = new CultureInfo("tr-TR")
        };

        Assert.Equal(plain, Assert.Single(TextShaper.Shape("ş", options)).GlyphId);
    }

    /// <summary>
    /// Mirrors the HarfBuzz language-tags.tests shaping expectations for the
    /// HarfBuzz-LanguageTags fixture font: 'J' is substituted via locl to a different
    /// glyph per selected language system on the latn script. The font declares no
    /// default language system, so a language the font does not carry, and the invariant
    /// culture's absent language preference, must apply no substitutions rather than
    /// aggregating the named language systems' lookups. A null culture is not pinned
    /// here: it resolves the ambient current culture, mirroring the reference engines.
    /// </summary>
    /// <param name="cultureName">The culture to shape with, or <see langword="null"/> for the invariant culture.</param>
    /// <param name="expectedGlyphId">The expected glyph id from the HarfBuzz expectations.</param>
    [Theory]
    [InlineData(null, 2)]
    [InlineData("fa", 2)]
    [InlineData("ja", 2)]
    [InlineData("zh", 4)]
    [InlineData("zh-CN", 4)]
    [InlineData("zh-SG", 4)]
    [InlineData("zh-TW", 5)]
    [InlineData("zh-Hans", 4)]
    [InlineData("zh-Hant", 5)]
    [InlineData("zh-Hant-HK", 6)]
    [InlineData("zh-HK", 6)]
    [InlineData("zh-MO", 6)]
    [InlineData("zh-Hant-MO", 6)]
    public void Shape_Culture_MatchesHarfBuzzLanguageTagExpectations(string? cultureName, int expectedGlyphId)
    {
        Font font = new FontCollection().Add(TestFonts.LanguageTagsFile).CreateFont(72);
        TextOptions options = new(font)
        {
            Culture = cultureName is null ? CultureInfo.InvariantCulture : new CultureInfo(cultureName)
        };

        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape("J", options);

        Assert.Single(glyphs);
        Assert.Equal(expectedGlyphId, glyphs[0].GlyphId);
    }
}
