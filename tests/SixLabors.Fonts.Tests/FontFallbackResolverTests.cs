// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests;

public class FontFallbackResolverTests
{
    /// <summary>
    /// Records every query and answers with one fixed family, or with no match when the
    /// family is null. Deterministic: no system fonts are consulted.
    /// </summary>
    private sealed class RecordingResolver : IFontFallbackResolver
    {
        private readonly FontFamily? family;

        public RecordingResolver(FontFamily? family) => this.family = family;

        public List<int> QueriedCodePoints { get; } = [];

        public CultureInfo? LastCulture { get; private set; }

        public FontFamily LastRequestedFamily { get; private set; }

        public bool TryResolve(CodePoint codePoint, FontFamily requestedFamily, FontStyle style, CultureInfo? culture, out FontFamily family)
        {
            this.QueriedCodePoints.Add(codePoint.Value);
            this.LastCulture = culture;
            this.LastRequestedFamily = requestedFamily;

            if (this.family is FontFamily resolved)
            {
                family = resolved;
                return true;
            }

            family = default;
            return false;
        }
    }

    [Fact]
    public void ResolverSuppliesFamilyForUnresolvedCodePoint()
    {
        Font font = TestFonts.GetFont(TestFonts.OpenSansFile, 12);
        FontFamily emoji = TestFonts.GetFont(TestFonts.TwemojiMozillaFile, 12).Family;
        RecordingResolver resolver = new(emoji);
        CultureInfo culture = CultureInfo.GetCultureInfo("en-GB");

        // Open Sans cannot shape the emoji; the resolver supplies the family that can,
        // and the emoji then renders through its COLR layers.
        ColorGlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "A😀", new TextOptions(font)
        {
            ColorFontSupport = ColorFontSupport.ColrV0,
            FontFallbackResolver = resolver,
            Culture = culture
        });

        Assert.Equal(3, renderer.Colors.Count);
        Assert.Equal([0x1F600], resolver.QueriedCodePoints);
        Assert.Equal(culture, resolver.LastCulture);
        Assert.Equal(font.Family, resolver.LastRequestedFamily);
    }

    [Fact]
    public void ResolverNotConsultedWhenPrimaryFontCovers()
    {
        Font font = TestFonts.GetFont(TestFonts.OpenSansFile, 12);
        RecordingResolver resolver = new(font.Family);

        ColorGlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "AB", new TextOptions(font)
        {
            FontFallbackResolver = resolver
        });

        Assert.Empty(resolver.QueriedCodePoints);
    }

    [Fact]
    public void ExplicitFallbackFamiliesWinOverResolver()
    {
        Font font = TestFonts.GetFont(TestFonts.OpenSansFile, 12);
        FontFamily emoji = TestFonts.GetFont(TestFonts.TwemojiMozillaFile, 12).Family;
        RecordingResolver resolver = new(emoji);

        // The explicit fallback list already covers the emoji, so the resolver is the
        // last resort and must never be queried.
        ColorGlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "😀", new TextOptions(font)
        {
            ColorFontSupport = ColorFontSupport.ColrV0,
            FallbackFontFamilies = [emoji],
            FontFallbackResolver = resolver
        });

        Assert.Equal(3, renderer.Colors.Count);
        Assert.Empty(resolver.QueriedCodePoints);
    }

    [Fact]
    public void ResolverReturningNonCoveringFamilyTerminates()
    {
        Font font = TestFonts.GetFont(TestFonts.OpenSansFile, 12);
        RecordingResolver resolver = new(font.Family);

        // The resolver answers with the same family that already failed to shape the
        // emoji. The pass must run once, resolve nothing, and stop: one query per
        // distinct code point, no colors, no hang.
        ColorGlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "😀😀", new TextOptions(font)
        {
            ColorFontSupport = ColorFontSupport.ColrV0,
            FontFallbackResolver = resolver
        });

        Assert.Empty(renderer.Colors);
        Assert.Equal([0x1F600], resolver.QueriedCodePoints);
    }

    [Fact]
    public void ResolverFailureLeavesMissingGlyph()
    {
        Font font = TestFonts.GetFont(TestFonts.OpenSansFile, 12);
        RecordingResolver resolver = new(null);

        ColorGlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "😀", new TextOptions(font)
        {
            ColorFontSupport = ColorFontSupport.ColrV0,
            FontFallbackResolver = resolver
        });

        Assert.Empty(renderer.Colors);
        Assert.Equal([0x1F600], resolver.QueriedCodePoints);
    }
}
