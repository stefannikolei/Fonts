// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using System.Text;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.TrueType;
using SixLabors.Fonts.Tables.TrueType.Glyphs;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests;

public class HintingTests
{
    // The layout tests below render one pangram through every layout mode. Reading a rotated
    // column against the horizontal baseline, or one hinting mode against another, is only
    // meaningful when the text, the size and the resolution are identical across all of them,
    // so all four share these values and none of them carries its own.
    private const string LayoutPangram = "The quick brown fox jumps over the lazy dog.";
    private const float LayoutFontSize = 10F;
    private const float LayoutDpi = 72F;

    public static TheoryData<string, string, HintingMode> HintingTestData { get; } = new()
    {
        // Arial and Tahoma are legacy TrueType fonts whose bytecode was written
        // for pre-ClearType rasterizers. Under a v40-style interpreter (vertical
        // hinting only, no horizontal grid-fitting, no backward-compatibility
        // constraints), both fonts generally render cleanly, but small differences
        // in horizontal features, joins and bar heights can occur at low ppem.
        // This behaviour matches FreeType v40 expectations for older fonts that
        // relied on full-axis grid fitting in legacy engines.
        { TestFonts.Arial, nameof(TestFonts.Arial), HintingMode.Standard },
        { TestFonts.Tahoma, nameof(TestFonts.Tahoma), HintingMode.Standard },

        // Modern ClearType-hinted OpenType fonts (for example Open Sans) are
        // authored for the same vertical-dominant model used by v40 and therefore
        // render consistently and predictably under these semantics.
        { TestFonts.OpenSansFile, nameof(TestFonts.OpenSansFile), HintingMode.Standard },

        // Full hinting executes the complete instruction set on both axes and grid
        // fits any axis the instructions leave unfitted, reproducing the legacy
        // full grid fitting model those fonts were originally authored for.
        { TestFonts.Arial, nameof(TestFonts.Arial) + "_Full", HintingMode.Full },
        { TestFonts.Tahoma, nameof(TestFonts.Tahoma) + "_Full", HintingMode.Full },
        { TestFonts.OpenSansFile, nameof(TestFonts.OpenSansFile) + "_Full", HintingMode.Full },
    };

    [Theory]
    [MemberData(nameof(HintingTestData))]
    public void Test_Hinting_Robustness(string path, string name, HintingMode hintingMode)
    {
        const string copy = "The quick brown fox jumps over the lazy dog.";
        FontCollection collection = new();
        FontFamily family = collection.Add(path);
        Font font = family.CreateFont(5);

        int fontSize = 5;
        int start = 0;
        int end = copy.GetGraphemeCount();
        int length = (end - start) + 1; // include the line ending.
        List<TextRun> textRuns = [];
        StringBuilder stringBuilder = new();
        while (fontSize < 64)
        {
            stringBuilder.AppendLine(copy);
            TextRun run = new()
            {
                Start = start,
                End = end,
                Font = new Font(font, fontSize),
            };

            textRuns.Add(run);
            fontSize += 1;
            start += length;
            end += length;
        }

        string text = stringBuilder.ToString();

        TextOptions options = new(font)
        {
            TextRuns = textRuns,
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            text,
            options,
            properties: name);
    }

    // Reproduces ImageSharp.Drawing issue #134: 8pt Tahoma at 100 dpi on a small panel.
    // Full hinting grid fits both axes so the rendered strokes land on whole pixels,
    // matching the clarity of classic GDI grayscale output.
    [Theory]
    [InlineData(HintingMode.Standard)]
    [InlineData(HintingMode.Full)]
    public void Issue134_SmallTahomaPanel(HintingMode hintingMode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(8);

        TextOptions options = new(font)
        {
            Dpi = 100,
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            "Lorem ipsum dolor sit amet",
            options,
            properties: hintingMode);
    }

    // The horizontal baseline for every layout comparison. Rotated glyphs in a mixed vertical
    // line must accumulate the same advances this test does, so its output is the reference a
    // rotated column is read against.
    [Theory]
    [InlineData(HintingMode.None)]
    [InlineData(HintingMode.Standard)]
    [InlineData(HintingMode.Full)]
    public void Hinting_HorizontalLayout(HintingMode hintingMode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(LayoutFontSize);

        TextOptions options = new(font)
        {
            Dpi = LayoutDpi,
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            LayoutPangram,
            options,
            properties: hintingMode);
    }

    // Upright vertical layout keeps its shaped fractional advance heights in every hinting
    // mode, so a column stacks on identical positions under standard and full hinting. Full
    // hinting adds the cross axis centring that lands each glyph's ink on whole pixel columns.
    [Theory]
    [InlineData(HintingMode.None)]
    [InlineData(HintingMode.Standard)]
    [InlineData(HintingMode.Full)]
    public void Hinting_VerticalLayout(HintingMode hintingMode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(LayoutFontSize);

        TextOptions options = new(font)
        {
            Dpi = LayoutDpi,
            LayoutMode = LayoutMode.VerticalLeftRight,
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            LayoutPangram,
            options,
            properties: hintingMode);
    }

    // Mixed vertical layout rotates Latin glyphs, mapping their hinted vertical axis onto
    // device X. Standard hinting must snap that axis and full hinting both, so the rotated
    // pen accumulates whole pixel hinted widths along the vertical baseline.
    [Theory]
    [InlineData(HintingMode.None)]
    [InlineData(HintingMode.Standard)]
    [InlineData(HintingMode.Full)]
    public void Hinting_VerticalMixedLayout(HintingMode hintingMode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(LayoutFontSize);

        TextOptions options = new(font)
        {
            Dpi = LayoutDpi,
            LayoutMode = LayoutMode.VerticalMixedLeftRight,
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            LayoutPangram,
            options,
            properties: hintingMode);
    }

    // A fractional origin exercises the render-time origin snapping: standard hinting snaps
    // the hinted axis only (the baseline), full hinting snaps both axes, and neither may let
    // the fraction resample the grid fitted outline. The spacing rhythm across the pangram
    // also locks the whole pixel advance substitution, including the dropped pair kerns.
    [Theory]
    [InlineData(HintingMode.None)]
    [InlineData(HintingMode.Standard)]
    [InlineData(HintingMode.Full)]
    public void Hinting_FractionalOrigin(HintingMode hintingMode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(LayoutFontSize);

        TextOptions options = new(font)
        {
            Dpi = LayoutDpi,
            Origin = new Vector2(10.4F, 10.6F),
            HintingMode = hintingMode,
        };

        TextLayoutTestUtilities.TestLayout(
            LayoutPangram,
            options,
            properties: hintingMode);
    }

    // The TrueType bytecode interpreter is pooled and reused across renders for the same
    // font. When a pooled interpreter is reused for a different pixel size it re-runs the
    // font's prep (CVT) program, which must execute from the same pristine state as a freshly
    // created interpreter. If transient interpreter state (twilight zone, storage, rounding
    // state, zone pointers, ...) is not reset first, the prep result — and therefore the
    // hinted glyph outline — depends on which sizes were rendered previously on that
    // interpreter. Because interpreters are shared through a pool, that made hinting output
    // non-deterministic when a single font family was rendered concurrently from multiple
    // threads
    [Fact]
    public void Hinting_OutputIsIndependentOfPreviouslyRenderedSizes()
    {
        const string text = "The quick brown fox 12345";
        const float dpi = 150F;
        const float targetSize = 7F;
        const float otherSize = 12F;

        static List<Vector2> RenderControlPoints(string text, float size, float dpi, float? warmUpSize)
        {
            FontCollection collection = new();
            FontFamily family = collection.Add(TestFonts.Arial);

            if (warmUpSize is { } w)
            {
                RenderTo(family, text, w, dpi, new GlyphRenderer());
            }

            GlyphRenderer renderer = new();
            RenderTo(family, text, size, dpi, renderer);
            return renderer.ControlPoints;
        }

        static void RenderTo(FontFamily family, string text, float size, float dpi, GlyphRenderer renderer)
        {
            Font font = family.CreateFont(size);
            TextOptions options = new(font)
            {
                Dpi = dpi,
                HintingMode = HintingMode.Standard,
            };

            TextRenderer.RenderTo(renderer, text, options);
        }

        // Render the target size on a font whose interpreter has processed nothing else.
        List<Vector2> reference = RenderControlPoints(text, targetSize, dpi, warmUpSize: null);

        // Render the same target size, but on a font whose pooled interpreter has already
        // processed a different size. With a correct per-size reset this is byte-for-byte equal.
        List<Vector2> afterOtherSize = RenderControlPoints(text, targetSize, dpi, warmUpSize: otherSize);

        Assert.Equal(reference, afterOtherSize);
    }

    // Full hinting shares the pooled interpreter with standard hinting, and the prep (CVT)
    // program branches on the interpreter identity reported by GETINFO. The prep memoization
    // therefore keys on the hinting mode as well as the scale: rendering in full mode after
    // a standard render at the same size must equal a full render on a pristine collection.
    [Fact]
    public void FullHinting_IsIndependentOfPooledInterpreterHistory()
    {
        const string text = "The quick brown fox 12345";
        const float dpi = 150F;
        const float size = 7F;

        FontCollection shared = new();
        FontFamily sharedFamily = shared.Add(TestFonts.Arial);
        List<Vector2> standard = RenderModeControlPoints(sharedFamily, text, size, dpi, HintingMode.Standard);
        List<Vector2> fullAfterStandard = RenderModeControlPoints(sharedFamily, text, size, dpi, HintingMode.Full);

        FontCollection fresh = new();
        FontFamily freshFamily = fresh.Add(TestFonts.Arial);
        List<Vector2> fullFresh = RenderModeControlPoints(freshFamily, text, size, dpi, HintingMode.Full);

        Assert.Equal(fullFresh, fullAfterStandard);
        Assert.NotEqual(standard, fullFresh);
    }

    // Scaled outlines are cached per pixel size and hinting mode. Alternating modes at a
    // single size must return each mode its own outline rather than whichever was built first.
    [Fact]
    public void FullHinting_CachesOutlinesPerHintingMode()
    {
        const string text = "The quick brown fox 12345";
        const float dpi = 150F;
        const float size = 7F;

        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Arial);
        List<Vector2> standardFirst = RenderModeControlPoints(family, text, size, dpi, HintingMode.Standard);
        List<Vector2> full = RenderModeControlPoints(family, text, size, dpi, HintingMode.Full);
        List<Vector2> standardSecond = RenderModeControlPoints(family, text, size, dpi, HintingMode.Standard);

        Assert.Equal(standardFirst, standardSecond);
        Assert.NotEqual(standardFirst, full);
    }

    // Full mode lifts the v40 restriction that suppresses X axis movement, so a heavily
    // instructed glyph must produce different X coordinates than standard mode at the same
    // scale. The hinted phantom points are read back as a whole pixel advance.
    [Fact]
    public void FullHinting_ExecutesHorizontalInstructions_AndReadsBackPhantomAdvance()
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Arial);
        Font font = family.CreateFont(12);

        Assert.True(font.FontMetrics.TryGetGlyphMetrics(new CodePoint('H'), TextAttributes.None, TextDecorations.None, LayoutMode.HorizontalTopBottom, ColorFontSupport.None, out FontGlyphMetrics metrics));

        TrueTypeGlyphMetrics ttMetrics = Assert.IsType<TrueTypeGlyphMetrics>(metrics);
        StreamFontMetrics streamMetrics = Assert.IsType<StreamFontMetrics>(ttMetrics.FontMetrics);

        const float scaledPPEM = 12F * 72F;
        const float pixelSize = scaledPPEM / 72F;
        Vector2 scale = new Vector2(scaledPPEM) / ttMetrics.ScaleFactor;

        GlyphVector standard = ScaleAndHint(streamMetrics, ttMetrics, scale, pixelSize, HintingMode.Standard);
        GlyphVector full = ScaleAndHint(streamMetrics, ttMetrics, scale, pixelSize, HintingMode.Full);

        Assert.True(standard.IsHinted);
        Assert.True(full.IsHinted);

        Assert.Equal(MathF.Floor(full.HintedAdvance.X), full.HintedAdvance.X);

        float designAdvance = ttMetrics.AdvanceWidth * scale.X;
        Assert.True(MathF.Abs(full.HintedAdvance.X - designAdvance) <= 1F);

        bool anyXDiffers = false;
        for (int i = 0; i < standard.ControlPoints.Count; i++)
        {
            if (standard.ControlPoints[i].Point.X != full.ControlPoints[i].Point.X)
            {
                anyXDiffers = true;
                break;
            }
        }

        Assert.True(anyXDiffers);
    }

    // Full hinting aligns the outline to the pixel grid in glyph space and snaps the emit
    // translation to whole pixels for upright renders, so a rectangular glyph lands with
    // every coordinate on the grid even when placed at a fractional origin. Standard mode
    // and synthetic oblique renders must not snap.
    [Fact]
    public void FullHinting_SnapsUprightGlyphOriginToWholePixels()
    {
        const string text = "H";
        Vector2 origin = new(10.3F, 10.7F);

        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Arial);
        Font font = family.CreateFont(12);

        List<Vector2> full = RenderAtOrigin(font, text, origin, HintingMode.Full);
        Assert.True(full.Count > 0);
        Assert.All(full, static p => Assert.True(IsOnPixelGrid(p), $"Expected grid aligned point but found {p}."));

        List<Vector2> standard = RenderAtOrigin(font, text, origin, HintingMode.Standard);
        Assert.Contains(standard, static p => !IsOnPixelGrid(p));

        Font oblique = family.CreateFont(12, FontStyle.Italic);
        List<Vector2> synthetic = RenderAtOrigin(oblique, text, origin, HintingMode.Full);
        Assert.Contains(synthetic, static p => !IsOnPixelGrid(p));
    }

    // Full hinting accumulates whole pixel advances read back from the hinted phantom
    // points, so measured text width is integral and differs from the fractional design
    // advance sum that standard hinting preserves.
    [Fact]
    public void FullHinting_UsesWholePixelAdvances()
    {
        const string text = "Lorem ipsum dolor sit amet";
        FontCollection collection = new();
        FontFamily family = collection.Add(TestFonts.Tahoma);
        Font font = family.CreateFont(11);

        TextOptions fullOptions = new(font)
        {
            HintingMode = HintingMode.Full,
        };

        // Advances travel through whole pixel values quantized to font units, so the sum
        // carries a small sub pixel residue proportional to the glyph count.
        FontRectangle full = TextMeasurer.MeasureAdvance(text, fullOptions);
        Assert.True(MathF.Abs(full.Width - MathF.Round(full.Width)) < 0.1F, $"Expected near whole pixel width but found {full.Width}.");

        TextOptions standardOptions = new(font)
        {
            HintingMode = HintingMode.Standard,
        };

        FontRectangle standard = TextMeasurer.MeasureAdvance(text, standardOptions);
        Assert.NotEqual(standard.Width, full.Width);
    }

    private static bool IsOnPixelGrid(Vector2 point) => MathF.Abs(point.X - MathF.Round(point.X)) < 1e-3F && MathF.Abs(point.Y - MathF.Round(point.Y)) < 1e-3F;

    private static List<Vector2> RenderModeControlPoints(FontFamily family, string text, float size, float dpi, HintingMode mode)
    {
        Font font = family.CreateFont(size);
        TextOptions options = new(font)
        {
            Dpi = dpi,
            HintingMode = mode,
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        return renderer.ControlPoints;
    }

    private static List<Vector2> RenderAtOrigin(Font font, string text, Vector2 origin, HintingMode mode)
    {
        TextOptions options = new(font)
        {
            Origin = origin,
            HintingMode = mode,
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        return renderer.ControlPoints;
    }

    private static GlyphVector ScaleAndHint(StreamFontMetrics fontMetrics, TrueTypeGlyphMetrics metrics, Vector2 scale, float pixelSize, HintingMode mode)
    {
        GlyphVector outline = metrics.GetOutline();
        GlyphVector clone = GlyphVector.DeepClone(outline);
        GlyphVector.TransformInPlace(ref clone, Matrix3x2.CreateScale(scale));
        fontMetrics.ApplyTrueTypeHinting(mode, metrics, ref clone, in outline, scale, pixelSize);
        return clone;
    }
}
