// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Rendering;
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
            Assert.Equal(i, glyphs[i].StringIndex);
            Assert.NotEqual(0, glyphs[i].GlyphId);
            Assert.True(glyphs[i].AdvanceWidth > 0);
        }
    }

    /// <summary>
    /// Verifies that public shaping and its measurement consumer preserve the
    /// distinct UTF-16 and grapheme source indices of a combining sequence.
    /// </summary>
    [Fact]
    public void Shape_CombiningSequence_PreservesStringAndGraphemeIndices()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add("a\u0308b");

        TextShaper.Shape(font, buffer);

        int index = -1;
        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer[i].StringIndex == 2)
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0);
        Assert.Equal(1, buffer[index].GraphemeIndex);

        GlyphOptions options = new()
        {
            Font = font,
            GraphemeIndex = 7
        };

        ReadOnlySpan<GlyphMetrics> metrics = TextMeasurer.GetGlyphMetrics(buffer, options).Span;
        Assert.Equal(buffer[index].GlyphId, metrics[index].GlyphId);
        Assert.Equal(8, metrics[index].GraphemeIndex);
        Assert.Equal(2, metrics[index].StringIndex);

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, buffer, options);

        Assert.Equal(8, renderer.GlyphKeys[index].GraphemeIndex);
    }

    /// <summary>
    /// Verifies that a new public shaping buffer infers paragraph direction.
    /// </summary>
    [Fact]
    public void TextShapingBuffer_DefaultsToAutomaticDirection()
        => Assert.Equal(TextDirection.Auto, new TextShapingBuffer().TextDirection);

    [Fact]
    public void Shape_EmptyText_ProducesNoGlyphs()
        => Assert.Empty(Shape(
            new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72),
            string.Empty));

    [Fact]
    public void Shape_AdvancesMatchMeasuredAdvance()
    {
        // Public shaped advances must agree with the measured logical advance for a
        // single unwrapped line.
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        TextOptions options = new(font);
        const string text = "Hxplq";

        ShapedGlyph[] glyphs = Shape(font, text);

        float shapedAdvance = 0;
        foreach (ShapedGlyph glyph in glyphs)
        {
            shapedAdvance += glyph.AdvanceWidth;
        }

        FontRectangle measured = TextMeasurer.MeasureAdvance(text, options);
        Assert.Equal(measured.Width, shapedAdvance, 3F);
    }

    [Fact]
    public void Shape_Ligature_MergesCodePoints()
    {
        // Dubai applies mandatory Arabic ligatures; Lam + Alef must merge into a single
        // glyph spanning both code points.
        Font font = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        ShapedGlyph[] glyphs = Shape(font, "لا");

        Assert.Single(glyphs);

        // Two characters merged into one glyph, so the run's only glyph carries the
        // cluster of the first of them.
        Assert.Equal(0, glyphs[0].StringIndex);
    }

    [Fact]
    public void Shape_RightToLeft_ReportsRunInReadingOrder()
    {
        // A run that reads right to left is handed back in that order, so the first
        // glyph is the rightmost one and the source indices descend.
        // Ordering runs against one another belongs to the caller, which alone
        // knows where its lines break.
        Font font = new FontCollection().Add(TestFonts.ArabicFontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add("سلام");
        buffer.TextDirection = TextDirection.RightToLeft;
        TextShaper.ShapeRun(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;

        Assert.True(glyphs.Length > 1);
        for (int i = 1; i < glyphs.Length; i++)
        {
            Assert.True(glyphs[i].StringIndex < glyphs[i - 1].StringIndex);
        }
    }

    [Fact]
    public void Shape_MixedDirection_ReturnsVisualOrderForOneLine()
    {
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.TextDirection = TextDirection.Auto;

        TextShaper.Shape(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;
        int[] expectedStringIndices = [0, 1, 2, 3, 6, 5, 4, 7, 8, 9, 10];
        Assert.Equal(expectedStringIndices.Length, glyphs.Length);
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(expectedStringIndices[i], glyphs[i].StringIndex);
        }
    }

    [Theory]
    [InlineData(TextDirection.Auto)]
    [InlineData(TextDirection.RightToLeft)]
    public void Shape_MixedDirection_MatchesUnwrappedLayoutOrder(TextDirection direction)
    {
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.TextDirection = direction;

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
            Assert.Equal(layoutGlyphs[i].StringIndex, shapedGlyphs[i].StringIndex);
        }
    }

    /// <summary>
    /// Verifies that public source indices count UTF-16 code units while
    /// grapheme indices count text elements.
    /// </summary>
    [Fact]
    public void Shape_StringIndexUsesUtf16CodeUnits()
    {
        const string text = "😀A";
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(72);
        ShapedGlyph[] glyphs = Shape(font, text);

        Assert.Equal(2, glyphs[^1].StringIndex);
        Assert.Equal(1, glyphs[^1].GraphemeIndex);
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
        buffer.TextDirection = TextDirection.Auto;

        TextShaper.Shape(font, buffer);

        // The first paragraph resolves RTL and the second resolves LTR. Their
        // records remain in line order rather than participating in one reversal.
        int[] expectedStringIndices = [3, 2, 1, 0, 4, 5, 6];
        Assert.Equal(expectedStringIndices.Length, buffer.Count);
        for (int i = 0; i < buffer.Count; i++)
        {
            Assert.Equal(expectedStringIndices[i], buffer[i].StringIndex);
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
        combined.TextDirection = TextDirection.Auto;
        TextShaper.Shape(font, combined);

        TextShapingBuffer first = new();
        first.Add(firstLine);
        first.TextDirection = TextDirection.Auto;
        TextShaper.Shape(font, first);

        TextShapingBuffer second = new();
        second.Add(secondLine);
        second.TextDirection = TextDirection.Auto;
        TextShaper.Shape(font, second);

        Assert.Equal(first.Count + second.Count, combined.Count);
        for (int i = 0; i < first.Count; i++)
        {
            ShapedGlyph expected = first[i];
            ShapedGlyph actual = combined[i];
            Assert.Equal(expected.GlyphId, actual.GlyphId);
            Assert.Equal(expected.StringIndex, actual.StringIndex);
            Assert.Equal(expected.GraphemeIndex, actual.GraphemeIndex);
            Assert.Equal(expected.AdvanceWidth, actual.AdvanceWidth);
            Assert.Equal(expected.AdvanceHeight, actual.AdvanceHeight);
            Assert.Equal(expected.Offset, actual.Offset);
        }

        int firstLineGraphemeCount = firstLine.GetGraphemeCount();
        for (int i = 0; i < second.Count; i++)
        {
            ShapedGlyph expected = second[i];
            ShapedGlyph actual = combined[first.Count + i];
            Assert.Equal(expected.GlyphId, actual.GlyphId);
            Assert.Equal(expected.StringIndex + firstLine.Length, actual.StringIndex);
            Assert.Equal(expected.GraphemeIndex + firstLineGraphemeCount, actual.GraphemeIndex);
            Assert.Equal(expected.AdvanceWidth, actual.AdvanceWidth);
            Assert.Equal(expected.AdvanceHeight, actual.AdvanceHeight);
            Assert.Equal(expected.Offset, actual.Offset);
        }
    }

    /// <summary>
    /// Verifies that the complex-script lines from the browser fixture remain
    /// independent shaping units across hard breaks in every shaping orientation.
    /// </summary>
    /// <param name="layoutMode">The horizontal, upright vertical, or mixed vertical shaping path.</param>
    [Theory]
    [InlineData(LayoutMode.HorizontalTopBottom)]
    [InlineData(LayoutMode.VerticalLeftRight)]
    [InlineData(LayoutMode.VerticalMixedLeftRight)]
    public void Shape_BrowserArabicHardBreaks_MatchSeparateCalls(LayoutMode layoutMode)
    {
        string[] logicalLines =
        [
            "\u062E\u064E\u0637\u0650\u0651\u064A\u064E\u0651\u0629\n",
            "\u062E\u064E\u0637\u0650\u0651\u064A\u064E\u0651\u0629\u060C\u0627\u0644\u0646\u064E\u0651\u0635\u0650\u0651\u064A\u064E\u0651\u0629\n",
            "\u062E\u064E\u0637\u0650\u0651\u064A\u064E\u0651\u0629 \u0627\u0644\u0646\u064E\u0651\u0635\u0650\u0651\u064A\u064E\u0651\u0629",
        ];

        Font font = new FontCollection().Add(TestFonts.NotoSansArabicRegular).CreateFont(72);
        TextShapingBuffer combined = new()
        {
            LayoutMode = layoutMode,
            TextDirection = TextDirection.RightToLeft,
        };

        combined.Add(string.Concat(logicalLines));
        TextShaper.Shape(font, combined);

        // The differential fixture checks each line's values against HarfBuzz.
        // This comparison separately pins the public hard-break contract: shaping
        // the lines together cannot let one line affect another.
        int combinedStart = 0;
        int stringOffset = 0;
        int graphemeOffset = 0;
        for (int line = 0; line < logicalLines.Length; line++)
        {
            string lineText = logicalLines[line];
            TextShapingBuffer separate = new()
            {
                LayoutMode = layoutMode,
                TextDirection = TextDirection.RightToLeft,
            };

            separate.Add(lineText);
            TextShaper.Shape(font, separate);

            int combinedEnd = line < combined.LineEnds.Length ? combined.LineEnds[line] : combined.Count;
            Assert.Equal(separate.Count, combinedEnd - combinedStart);

            // The visual glyph stream and all positioned values must be identical;
            // only the source indices advance through the preceding logical lines.
            for (int i = 0; i < separate.Count; i++)
            {
                ShapedGlyph expected = separate[i];
                ShapedGlyph actual = combined[combinedStart + i];
                Assert.Equal(expected.GlyphId, actual.GlyphId);
                Assert.Equal(expected.StringIndex + stringOffset, actual.StringIndex);
                Assert.Equal(expected.GraphemeIndex + graphemeOffset, actual.GraphemeIndex);
                Assert.Equal(expected.AdvanceWidth, actual.AdvanceWidth);
                Assert.Equal(expected.AdvanceHeight, actual.AdvanceHeight);
                Assert.Equal(expected.Offset, actual.Offset);
            }

            combinedStart = combinedEnd;
            stringOffset += lineText.Length;
            graphemeOffset += lineText.GetGraphemeCount();
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
        buffer.TextDirection = TextDirection.RightToLeft;

        TextShaper.ShapeRun(font, buffer);

        Assert.Equal(text.Length, buffer.Count);
        for (int i = 1; i < buffer.Count; i++)
        {
            Assert.True(buffer[i].StringIndex < buffer[i - 1].StringIndex);
        }
    }

    [Fact]
    public void ShapeRun_MixedDirection_UsesStatedDirectionForWholeRun()
    {
        const string text = "abc אבג def";
        Font font = new FontCollection().Add(TestFonts.Anchor2FontFile).CreateFont(72);
        TextShapingBuffer buffer = new();
        buffer.Add(text);
        buffer.TextDirection = TextDirection.LeftToRight;

        TextShaper.ShapeRun(font, buffer);

        ReadOnlySpan<ShapedGlyph> glyphs = buffer.Glyphs;
        Assert.Equal(text.Length, glyphs.Length);
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(i, glyphs[i].StringIndex);
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
                    Assert.Equal(expectedGlyph.StringIndex, actual.StringIndex);
                    Assert.Equal(expectedGlyph.GraphemeIndex, actual.GraphemeIndex);
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
