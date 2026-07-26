// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using HarfBuzzSharp;
using HBBuffer = HarfBuzzSharp.Buffer;
using HBFace = HarfBuzzSharp.Face;
using HBFont = HarfBuzzSharp.Font;

namespace SixLabors.Fonts.Tests;

/// <summary>
/// Differential shaping checks against HarfBuzz. Both engines shape identical font
/// bytes; glyph ids and advances must match exactly. These tests are the correctness
/// gate for shaping performance work: any change to the shaping pipeline must keep
/// them green, and new benchmark scenarios must add a matching case here.
/// </summary>
public class HarfBuzzDifferentialTests
{
    public static TheoryData<string, string, bool> ShapingCases()
        => new()
        {
            // Latin: ligature opportunities and kern-sensitive pairs.
            { TestFonts.OpenSansFile, "The quick brown fox jumps over the lazy dog; fifty fluffy waffles.", false },
            { TestFonts.OpenSansFile, "AVATAR To Ya. WAV Tc office flag 1/2 fi ffl", false },

            // Arabic: joining forms, mandatory ligatures, mark anchoring.
            { TestFonts.ArabicFontFile, "سلام عليكم ورحمة الله وبركاته لا إله إلا الله", true },
            { TestFonts.ArabicFontFile, "لآلئ", true },

            // Devanagari: conjuncts, matras, and reordering.
            { TestFonts.NotoSansDevanagariRegular, "क्षत्रिय द्वारा प्रकृति की रक्षा कर्तव्य है", false },
            { TestFonts.NotoSansDevanagariRegular, "श्रद्धांजलि", false },

            // Emoji joiner sequences: the zero width joiner and variation selector
            // participate in the fonts' sequence lookups (including contextual
            // rules whose lookahead spans them) and then render invisibly.
            { TestFonts.SegoeuiEmojiFile, "\U0001F469\U0001F3FB\u200D\U0001F91D\u200D\U0001F469\U0001F3FC", false },
            { TestFonts.NotoColorEmojiRegular, "\u2764\uFE0F\u200D\U0001F525", false },
            { TestFonts.NotoColorEmojiRegular, "\u2764\uFE0F\u200D\U0001FA79", false },

            // Joiners inside shaping contexts: the joiner must steer the shaping
            // (ligature suppression/formation, joining forms, half forms) and then
            // render invisibly at zero advance.
            { TestFonts.OpenSansFile, "of\u200Cfice fi\u200Dnal fluff", false },
            { TestFonts.ArabicFontFile, "\u0644\u200C\u0627 \u0644\u200D\u0627", true },
            { TestFonts.NotoSansDevanagariRegular, "\u0915\u094D\u200D\u0937 \u0915\u094D\u200C\u0937", false },
        };

    [Theory]
    [MemberData(nameof(ShapingCases))]
    public void ShapesIdenticallyToHarfBuzz(string fontFile, string text, bool rightToLeft)
    {
        // SixLabors side, font design units, logical order.
        Font font = new FontCollection().Add(fontFile).CreateFont(16);
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape(text, new TextOptions(font));

        // HarfBuzz side. Output for right-to-left runs is in visual order, so the
        // comparison walks it reversed to recover logical order.
        using Blob blob = Blob.FromFile(fontFile);
        using HBFace face = new(blob, 0);
        using HBFont hbFont = new(face);
        hbFont.SetFunctionsOpenType();
        using HBBuffer buffer = new();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        hbFont.Shape(buffer);

        GlyphInfo[] infos = buffer.GetGlyphInfoSpan().ToArray();
        GlyphPosition[] positions = buffer.GetGlyphPositionSpan().ToArray();

        Assert.Equal(infos.Length, glyphs.Count);

        for (int i = 0; i < glyphs.Count; i++)
        {
            int hbIndex = rightToLeft ? infos.Length - 1 - i : i;
            Assert.Equal(infos[hbIndex].Codepoint, glyphs[i].GlyphId);
            Assert.Equal(positions[hbIndex].XAdvance, glyphs[i].AdvanceWidth);
        }
    }
}
