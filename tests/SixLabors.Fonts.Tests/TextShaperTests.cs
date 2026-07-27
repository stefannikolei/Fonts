// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests;

public class TextShaperTests
{
    /// <summary>
    /// Shapes one run of left to right text, the arrangement most of these tests
    /// need, and returns its glyphs.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="text">The text of the run.</param>
    /// <returns>The shaped glyphs.</returns>
    private static ShapedGlyph[] Shape(Font font, string text)
    {
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        TextShaper.Shape(font, buffer);

        return buffer.Glyphs.ToArray();
    }

    /// <summary>
    /// Shapes one run of left to right text written in the given language.
    /// </summary>
    /// <param name="font">The font to shape against.</param>
    /// <param name="text">The text of the run.</param>
    /// <param name="language">The language the run is written in.</param>
    /// <returns>The shaped glyphs.</returns>
    private static ShapedGlyph[] Shape(Font font, string text, CultureInfo language)
    {
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Language = language;
        TextShaper.Shape(font, buffer);

        return buffer.Glyphs.ToArray();
    }

    [Fact]
    public void Shape_Latin_ProducesSequentialGlyphStream()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        ShapedGlyph[] glyphs = Shape(font, "Hxp");

        Assert.Equal(3, glyphs.Length);
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(i, glyphs[i].CodePointIndex);
            Assert.NotEqual(0, glyphs[i].GlyphId);
            Assert.True(glyphs[i].AdvanceWidth > 0);
        }
    }

    [Fact]
    public void Shape_EmptyText_ProducesNoGlyphs()
        => Assert.Empty(Shape(
            new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72),
            string.Empty));

    [Fact]
    public void Shape_AdvancesMatchMeasuredAdvance()
    {
        // Design-unit advances scaled to pixel units must agree with the measured
        // logical advance for a single unwrapped line.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        TextOptions options = new(font);
        const string text = "Hxplq";

        ShapedGlyph[] glyphs = Shape(font, text);

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
        ShapedGlyph[] glyphs = Shape(font, "لا");

        Assert.Single(glyphs);
        // Two characters merged into one glyph, so the run's only glyph carries the
        // cluster of the first of them.
        Assert.Equal(0, glyphs[0].CodePointIndex);
    }

    [Fact]
    public void Shape_RightToLeft_ReportsRunInReadingOrder()
    {
        // A run that reads right to left is handed back in that order, so the first
        // glyph is the rightmost one and the codepoints it came from descend.
        // Ordering runs against one another belongs to the caller, which alone
        // knows where its lines break.
        Font font = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add("سلام");
        buffer.Direction = TextDirection.RightToLeft;
        TextShaper.ShapeRun(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;

        Assert.True(glyphs.Length > 1);
        for (int i = 1; i < glyphs.Length; i++)
        {
            Assert.True(glyphs[i].CodePointIndex < glyphs[i - 1].CodePointIndex);
        }
    }

    [Fact]
    public void Shape_MixedDirection_ReturnsVisualOrderForOneLine()
    {
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Direction = TextDirection.Auto;

        TextShaper.Shape(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;
        int[] expectedCodePointIndices = [0, 1, 2, 3, 6, 5, 4, 7, 8, 9, 10];
        Assert.Equal(expectedCodePointIndices.Length, glyphs.Length);
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(expectedCodePointIndices[i], glyphs[i].CodePointIndex);
        }
    }

    [Theory]
    [InlineData(TextDirection.Auto)]
    [InlineData(TextDirection.RightToLeft)]
    public void Shape_MixedDirection_MatchesUnwrappedLayoutOrder(TextDirection direction)
    {
        // Every codepoint is in the BMP and produces one glyph, so the shaper's
        // codepoint indices can be compared directly with layout's UTF-16 indices.
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Direction = direction;

        TextShaper.Shape(font, buffer);

        TextOptions options = new(font)
        {
            TextDirection = direction
        };

        ReadOnlySpan<GlyphMetrics> layoutGlyphs = TextMeasurer.Measure(text, options).GetGlyphMetrics().Span;
        ReadOnlySpan<ShapedGlyph> shapedGlyphs = buffer.Glyphs;
        Assert.Equal(layoutGlyphs.Length, shapedGlyphs.Length);
        for (int i = 0; i < shapedGlyphs.Length; i++)
        {
            Assert.Equal(layoutGlyphs[i].StringIndex, shapedGlyphs[i].CodePointIndex);
        }
    }

    /// <summary>
    /// Verifies that automatic paragraph direction and visual reordering restart
    /// after a hard break.
    /// </summary>
    [Fact]
    public void Shape_HardBreaks_ResolveAndReorderEachParagraph()
    {
        const string text = "אבג\nabc";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Direction = TextDirection.Auto;

        TextShaper.Shape(font, buffer);

        // The first paragraph resolves RTL and the second resolves LTR. Their
        // records remain in line order rather than participating in one reversal.
        int[] expectedCodePointIndices = [3, 2, 1, 0, 4, 5, 6];
        Assert.Equal(expectedCodePointIndices.Length, buffer.Count);
        for (int i = 0; i < buffer.Count; i++)
        {
            Assert.Equal(expectedCodePointIndices[i], buffer[i].CodePointIndex);
        }
    }

    /// <summary>
    /// Verifies that shaping newline-delimited text together produces the same
    /// records as shaping those logical lines independently.
    /// </summary>
    /// <param name="hardBreak">The newline function separating the logical lines.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Shape_HardBreaks_MatchSeparateCalls(string hardBreak)
    {
        string firstLine = "abc אבג" + hardBreak;
        const string secondLine = "אבג abc";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer combined = new();
        combined.Add(firstLine + secondLine);
        combined.Direction = TextDirection.Auto;
        TextShaper.Shape(font, combined);

        TextShapingBuffer first = new();
        first.Add(firstLine);
        first.Direction = TextDirection.Auto;
        TextShaper.Shape(font, first);

        TextShapingBuffer second = new();
        second.Add(secondLine);
        second.Direction = TextDirection.Auto;
        TextShaper.Shape(font, second);

        Assert.Equal(first.Count + second.Count, combined.Count);
        int firstLineCodePointCount = CodePoint.GetCodePointCount(firstLine);

        for (int i = 0; i < first.Count; i++)
        {
            ShapedGlyph expected = first[i];
            ShapedGlyph actual = combined[i];
            Assert.Equal(expected.GlyphId, actual.GlyphId);
            Assert.Equal(expected.CodePointIndex, actual.CodePointIndex);
            Assert.Equal(expected.AdvanceWidth, actual.AdvanceWidth);
            Assert.Equal(expected.AdvanceHeight, actual.AdvanceHeight);
            Assert.Equal(expected.Offset, actual.Offset);
        }

        for (int i = 0; i < second.Count; i++)
        {
            ShapedGlyph expected = second[i];
            ShapedGlyph actual = combined[first.Count + i];
            Assert.Equal(expected.GlyphId, actual.GlyphId);
            Assert.Equal(expected.CodePointIndex + firstLineCodePointCount, actual.CodePointIndex);
            Assert.Equal(expected.AdvanceWidth, actual.AdvanceWidth);
            Assert.Equal(expected.AdvanceHeight, actual.AdvanceHeight);
            Assert.Equal(expected.Offset, actual.Offset);
        }
    }

    /// <summary>
    /// Verifies that a directional-run request remains one protocol unit even when
    /// its contents include a separator.
    /// </summary>
    [Fact]
    public void ShapeRun_HardBreak_RemainsOneDirectionalRun()
    {
        const string text = "אבג\nabc";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Direction = TextDirection.RightToLeft;

        TextShaper.ShapeRun(font, buffer);

        Assert.Equal(text.Length, buffer.Count);
        for (int i = 1; i < buffer.Count; i++)
        {
            Assert.True(buffer[i].CodePointIndex < buffer[i - 1].CodePointIndex);
        }
    }

    [Fact]
    public void ShapeRun_MixedDirection_UsesStatedDirectionForWholeRun()
    {
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.Direction = TextDirection.LeftToRight;

        TextShaper.ShapeRun(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;
        Assert.Equal(text.Length, glyphs.Length);
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(i, glyphs[i].CodePointIndex);
        }
    }

    [Fact]
    public void Shape_UnmappedCodePoint_ProducesMissingGlyph()
    {
        // With no fallback fonts configured an unmapped codepoint emits the font's
        // missing glyph, matching the single-face shaping model.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        ShapedGlyph[] glyphs = Shape(font, "☃");

        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].GlyphId);
    }

    [Fact]
    public void ZawgyiScriptOverrideAppliesThroughTextOptionsAndTextRun()
    {
        const string text = "\u1000\u103A\u1004\u1037\u1039\u1041";
        string fontPath = Path.Combine(TestEnvironment.SolutionDirectoryFullPath, "tests", "harfbuzz", "test", "shape", "data", "in-house", "fonts", "ab14b4eb9d7a67e293f51d30d719add06c9d6e06.ttf");
        Font font = new FontCollection().Add(fontPath).CreateFont(16);

        // The shaping buffer establishes the expected Zawgyi glyph stream using
        // the direct API.
        TextShapingBuffer buffer = new()
        {
            Script = ScriptClass.MyanmarZawgyi
        };
        buffer.Add(text);
        TextShaper.ShapeRun(font, buffer);

        TextOptions options = new(font)
        {
            Script = ScriptClass.MyanmarZawgyi
        };
        using TextShaper.ShapedTextScope wholeTextScope = TextShaper.ShapeText(text, options, null);
        ShapedText wholeText = wholeTextScope.Shaped;

        Assert.Equal(buffer.Count, wholeText.GlyphCount);
        for (int i = 0; i < buffer.Count; i++)
        {
            Assert.Equal(buffer[i].GlyphId, wholeText.Infos[i].GlyphId);
        }

        // A run selection is more specific than the whole-text selection, so it
        // can identify Zawgyi inside otherwise ordinary Myanmar text.
        options.Script = ScriptClass.Myanmar;
        options.TextRuns =
        [
            new TextRun
            {
                Start = 0,
                End = text.GetGraphemeCount(),
                Script = ScriptClass.MyanmarZawgyi
            }
        ];

        using TextShaper.ShapedTextScope runScope = TextShaper.ShapeText(text, options, null);
        ShapedText runText = runScope.Shaped;

        Assert.Equal(buffer.Count, runText.GlyphCount);
        for (int i = 0; i < buffer.Count; i++)
        {
            Assert.Equal(buffer[i].GlyphId, runText.Infos[i].GlyphId);
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

        ushort plain = Assert.Single(Shape(font, "ş")).GlyphId;
        ushort expected = Assert.Single(Shape(font, "ș")).GlyphId;
        Assert.NotEqual(plain, expected);

        Assert.Equal(expected, Assert.Single(Shape(font, "ş", new CultureInfo(cultureName))).GlyphId);
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

        ushort plain = Assert.Single(Shape(font, "б")).GlyphId;

        ushort localized = Assert.Single(Shape(font, "б", new CultureInfo(cultureName))).GlyphId;
        Assert.NotEqual(plain, localized);
        Assert.NotEqual(0, localized);
    }

    [Fact]
    public void Shape_Culture_WithoutMatchingLanguageSystem_UsesDefault()
    {
        // Turkish has no language system in Open Sans, so shaping falls back to the
        // default language system and the localized substitution must not apply.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);

        ushort plain = Assert.Single(Shape(font, "ş")).GlyphId;

        Assert.Equal(plain, Assert.Single(Shape(font, "ş", new CultureInfo("tr-TR"))).GlyphId);
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
        CultureInfo language = cultureName is null ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);

        ShapedGlyph[] glyphs = Shape(font, "J", language);

        Assert.Single(glyphs);
        Assert.Equal(expectedGlyphId, glyphs[0].GlyphId);
    }

    [Fact]
    public void Shape_TextRunCultureOverridesTextOptionsCulture()
    {
        Font font = new FontCollection().Add(TestFonts.LanguageTagsFile).CreateFont(72);
        TextOptions options = new(font)
        {
            Culture = CultureInfo.InvariantCulture,
            TextRuns =
            [
                new TextRun { Start = 0, End = 1, Culture = new CultureInfo("zh-CN") },
                new TextRun { Start = 1, End = 2, Culture = new CultureInfo("zh-TW") }
            ]
        };

        // Both characters use the same script, so distinct glyphs prove the
        // language override also forms a shaping boundary between adjacent runs.
        using TextShaper.ShapedTextScope scope = TextShaper.ShapeText("JJ", options, null);
        ShapedText shaped = scope.Shaped;

        Assert.Equal(2, shaped.GlyphCount);
        Assert.Equal(4, shaped.Infos[0].GlyphId);
        Assert.Equal(5, shaped.Infos[1].GlyphId);
    }

    [Fact]
    public void Shape_ReusedBuffer_MatchesFreshShaping()
    {
        // One shared buffer across interleaved scripts and repeated rounds must
        // produce records identical to the allocating overload every time, with
        // each call fully replacing the previous contents.
        Font latin = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        Font arabic = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        Font devanagari = new FontCollection().Add(TestFonts.NotoSansDevanagariRegular).CreateFont(72);

        (Font Font, string Text)[] cases =
        [
            (latin, "The quick brown fox; fifty fluffy waffles."),
            (arabic, "سلام عليكم ورحمة الله"),
            (devanagari, "क्षत्रिय द्वारा प्रकृति की रक्षा"),
            (latin, "a"),
        ];

        TextShapingBuffer buffer = new();
        for (int round = 0; round < 3; round++)
        {
            foreach ((Font font, string text) in cases)
            {
                ShapedGlyph[] expected = Shape(font, text);

                buffer.Add(text);
                TextShaper.Shape(font, buffer);

                Assert.Equal(expected.Length, buffer.Count);
                for (int i = 0; i < expected.Length; i++)
                {
                    ShapedGlyph expectedGlyph = expected[i];
                    ShapedGlyph actual = buffer[i];
                    Assert.Equal(expectedGlyph.GlyphId, actual.GlyphId);
                    Assert.Equal(expectedGlyph.CodePointIndex, actual.CodePointIndex);
                    Assert.Equal(expectedGlyph.AdvanceWidth, actual.AdvanceWidth);
                    Assert.Equal(expectedGlyph.AdvanceHeight, actual.AdvanceHeight);
                    Assert.Equal(expectedGlyph.Offset, actual.Offset);
                }
            }
        }
    }

    [Fact]
    public void Shape_EmptyTextIntoBuffer_ClearsPreviousContents()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        TextShapingBuffer buffer = new();

        buffer.Add("Hxp");
        TextShaper.Shape(font, buffer);
        Assert.Equal(3, buffer.Count);

        buffer.Add(string.Empty);
        TextShaper.Shape(font, buffer);
        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.Glyphs.IsEmpty);
    }

    [Theory]
    [InlineData("latin")]
    [InlineData("arabic")]
    [InlineData("devanagari")]
    public void Shape_ReusedBuffer_SteadyStateDoesNotAllocate(string scenario)
    {
        // After warm-up calls have grown every pooled structure to its high-water
        // mark, repeated shaping through the same buffer must allocate nothing.
        (string fontFile, string text) = scenario switch
        {
            "arabic" => (TestFonts.ArabicFontFile, "سلام عليكم ورحمة الله وبركاته لا إله إلا الله"),
            "devanagari" => (TestFonts.NotoSansDevanagariRegular, "क्षत्रिय द्वारा प्रकृति की रक्षा कर्तव्य है"),
            _ => (TestFonts.OpenSansFile, "The quick brown fox; fifty fluffy waffles."),
        };

        Font font = new FontCollection().Add(fontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);

        // Parallel tests share the pipeline's scratch pool, so any single call may
        // rent state another test left cold for this font and pay its one-time
        // population. Steady state is the minimum over several attempts: one
        // zero-allocation call proves the warmed path allocates nothing.
        long minimum = long.MaxValue;
        for (int attempt = 0; attempt < 10 && minimum != 0; attempt++)
        {
            for (int i = 0; i < 8; i++)
            {
                TextShaper.Shape(font, buffer);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            TextShaper.Shape(font, buffer);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, minimum);
    }
}
