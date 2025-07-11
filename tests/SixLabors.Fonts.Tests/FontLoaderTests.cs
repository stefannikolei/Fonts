// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests;

public class FontLoaderTests
{
    [Fact]
    public void Issue21_LoopDetectedLoadingGlyphs()
    {
        Font font = new FontCollection().Add(TestFonts.CarterOneFileData()).CreateFont(12);

        Assert.True(font.FontMetrics.TryGetGlyphMetrics(
            new CodePoint('\0'),
            TextAttributes.None,
            TextDecorations.None,
            LayoutMode.HorizontalTopBottom,
            ColorFontSupport.None,
            out IReadOnlyList<GlyphMetrics> _));
    }

    [Fact]
    public void LoadFontMetadata()
    {
        FontDescription description = FontDescription.LoadDescription(TestFonts.SimpleFontFileData());

        Assert.Equal("SixLaborsSampleAB regular", description.FontNameInvariantCulture);
        Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
    }

    [Fact]
    public void LoadFontMetadataWoff()
    {
        FontDescription description = FontDescription.LoadDescription(TestFonts.SimpleFontFileWoffData());

        Assert.Equal("SixLaborsSampleAB regular", description.FontNameInvariantCulture);
        Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
    }

    [Fact]
    public void LoadFont_WithTtfFormat()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFile).CreateFont(12);

        Assert.True(font.TryGetGlyphs(new CodePoint('A'), ColorFontSupport.None, out IReadOnlyList<Glyph> glyphs));

        Glyph glyph = glyphs[0];
        GlyphRenderer r = new();
        glyph.RenderTo(r, Vector2.Zero, Vector2.Zero, GlyphLayoutMode.Horizontal, new TextOptions(font));

        Assert.Equal(37, r.ControlPoints.Count);
        Assert.Single(r.GlyphKeys);
        Assert.Single(r.GlyphRects);
    }

    [Fact]
    public void LoadFont_WithWoff1Format()
    {
        Font font = new FontCollection().Add(TestFonts.OpenSansFileWoff1).CreateFont(12);

        Assert.True(font.TryGetGlyphs(new CodePoint('A'), ColorFontSupport.None, out IReadOnlyList<Glyph> glyphs));
        Glyph glyph = glyphs[0];
        GlyphRenderer r = new();
        glyph.RenderTo(r, Vector2.Zero, Vector2.Zero, GlyphLayoutMode.Horizontal, new TextOptions(font));

        Assert.Equal(37, r.ControlPoints.Count);
        Assert.Single(r.GlyphKeys);
        Assert.Single(r.GlyphRects);
    }

    [Fact]
    public void LoadFontMetadata_WithWoff1Format()
    {
        FontDescription description = FontDescription.LoadDescription(TestFonts.OpensSansWoff1Data());

        Assert.Equal("Open Sans Regular", description.FontNameInvariantCulture);
        Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
    }

    [Fact]
    public void LoadFontMetadata_WithWoff2Format()
    {
        FontDescription description = FontDescription.LoadDescription(TestFonts.OpensSansWoff2Data());

        Assert.Equal("Open Sans Regular", description.FontNameInvariantCulture);
        Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
    }

    [Fact]
    public void LoadFont_WithWoff2Format()
    {
        Font font = new FontCollection().Add(TestFonts.OpensSansWoff2Data()).CreateFont(12);

        Assert.True(font.TryGetGlyphs(new CodePoint('A'), ColorFontSupport.None, out IReadOnlyList<Glyph> glyphs));
        Glyph glyph = glyphs[0];
        GlyphRenderer r = new();
        glyph.RenderTo(r, Vector2.Zero, Vector2.Zero, GlyphLayoutMode.Horizontal, new TextOptions(font));

        Assert.Equal(37, r.ControlPoints.Count);
        Assert.Single(r.GlyphKeys);
        Assert.Single(r.GlyphRects);
    }

    [Fact]
    public void LoadFont()
    {
        Font font = new FontCollection().Add(TestFonts.SimpleFontFileData()).CreateFont(12);

        Assert.Equal("SixLaborsSampleAB regular", font.FontMetrics.Description.FontNameInvariantCulture);
        Assert.Equal("Regular", font.FontMetrics.Description.FontSubFamilyNameInvariantCulture);

        Assert.True(font.TryGetGlyphs(new CodePoint('a'), ColorFontSupport.None, out IReadOnlyList<Glyph> glyphs));
        Glyph glyph = glyphs[0];
        GlyphRenderer r = new();
        glyph.RenderTo(r, Vector2.Zero, Vector2.Zero, GlyphLayoutMode.Horizontal, new TextOptions(font));

        // the test font only has characters .notdef, 'a' & 'b' defined
        Assert.Equal(6, r.ControlPoints.Distinct().Count());
    }

    [Fact]
    public void LoadFontWoff()
    {
        Font font = new FontCollection().Add(TestFonts.SimpleFontFileWoffData()).CreateFont(12);

        Assert.Equal("SixLaborsSampleAB regular", font.FontMetrics.Description.FontNameInvariantCulture);
        Assert.Equal("Regular", font.FontMetrics.Description.FontSubFamilyNameInvariantCulture);

        Assert.True(font.TryGetGlyphs(new CodePoint('a'), ColorFontSupport.None, out IReadOnlyList<Glyph> glyphs));
        Glyph glyph = glyphs[0];
        GlyphRenderer r = new();
        glyph.RenderTo(r, Vector2.Zero, Vector2.Zero, GlyphLayoutMode.Horizontal, new TextOptions(font));

        // the test font only has characters .notdef, 'a' & 'b' defined
        Assert.Equal(6, r.ControlPoints.Distinct().Count());
    }

    [Fact]
    public void LoadFontWithIncorrectClassDefinitionTableOffset()
    {
        // The following font contains a ClassDefinitionTable with an invalid offset.
        // See https://forum.stimulsoft.com/viewtopic.php?t=60972
        Font font = new FontCollection().Add(TestFonts.THSarabunFile).CreateFont(12);

        FontRectangle advance = TextMeasurer.MeasureAdvance("เราใช้คุกกี้เพื่อพัฒนาประสิทธิภาพ และประสบการณ์ที่ดีในการใช้เว็บไซต์ของคุณ คุณสามารถศึกษารายละเอียดได้ที่", new TextOptions(font));

        Assert.NotEqual(default, advance);
    }
}
