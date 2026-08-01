// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Tables.TrueType;
using SixLabors.Fonts.Tables.TrueType.Glyphs;
using SixLabors.Fonts.Tables.TrueType.Hinting;

namespace SixLabors.Fonts.Tests;

public class GlyphGridFitterTests
{
    // A single stem spanning x 1.3 to 2.6 is 1.3px wide: the width rounds to one pixel and
    // the center 1.95 rounds to the nearest half integer 1.5, so the flanks land on the
    // pixel boundaries 1 and 2.
    [Fact]
    public void SnapsRectangleStemToWholePixels()
    {
        GlyphVector vector = CreateVector([[On(1.3F, 0F), On(1.3F, 5F), On(2.6F, 5F), On(2.6F, 0F)]]);
        GridFitOptions options = new(8F, GridFitAxisMode.Full, GridFitAxisMode.None, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(1F, vector.ControlPoints[0].Point.X);
        Assert.Equal(1F, vector.ControlPoints[1].Point.X);
        Assert.Equal(2F, vector.ControlPoints[2].Point.X);
        Assert.Equal(2F, vector.ControlPoints[3].Point.X);

        Assert.Equal(0F, vector.ControlPoints[0].Point.Y);
        Assert.Equal(5F, vector.ControlPoints[1].Point.Y);
        Assert.Equal(5F, vector.ControlPoints[2].Point.Y);
        Assert.Equal(0F, vector.ControlPoints[3].Point.Y);
    }

    // Two sub pixel stems both widen to one pixel on integer boundaries and the counter
    // between them stays open at a full pixel.
    [Fact]
    public void FitsTwoStemsAndPreservesCounter()
    {
        GlyphVector vector = CreateVector(
        [
            [On(0.8F, 0F), On(0.8F, 5F), On(1.7F, 5F), On(1.7F, 0F)],
            [On(3.1F, 0F), On(3.1F, 5F), On(4.0F, 5F), On(4.0F, 0F)]
        ]);
        GridFitOptions options = new(8F, GridFitAxisMode.Full, GridFitAxisMode.None, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(1F, vector.ControlPoints[0].Point.X);
        Assert.Equal(2F, vector.ControlPoints[2].Point.X);
        Assert.Equal(3F, vector.ControlPoints[4].Point.X);
        Assert.Equal(4F, vector.ControlPoints[6].Point.X);
    }

    // A stem whose natural rounding would close the counter to its neighbor shifts away by
    // a whole pixel instead, keeping the counter open. Sub pixel stems receive a movement
    // allowance for the widening itself, so the shifted position remains reachable.
    [Fact]
    public void ShiftsStemToPreserveCounter()
    {
        GlyphVector vector = CreateVector(
        [
            [On(0.8F, 0F), On(0.8F, 5F), On(1.7F, 5F), On(1.7F, 0F)],
            [On(2.3F, 0F), On(2.3F, 5F), On(3.2F, 5F), On(3.2F, 0F)]
        ]);
        GridFitOptions options = new(8F, GridFitAxisMode.Full, GridFitAxisMode.None, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(1F, vector.ControlPoints[0].Point.X);
        Assert.Equal(2F, vector.ControlPoints[2].Point.X);
        Assert.Equal(3F, vector.ControlPoints[4].Point.X);
        Assert.Equal(4F, vector.ControlPoints[6].Point.X);
    }

    // The vertical pass snaps a slight overshoot below the baseline back to zero and a top
    // edge near the x-height to the rounded x-height pixel.
    [Fact]
    public void VerticalPassSnapsBaselineAndXHeight()
    {
        GlyphVector vector = CreateVector([[On(1F, -0.2F), On(1F, 4.7F), On(2F, 4.7F), On(2F, -0.2F)]]);
        GridFitOptions options = new(8F, GridFitAxisMode.None, GridFitAxisMode.Full, 4.6F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(0F, vector.ControlPoints[0].Point.Y);
        Assert.Equal(5F, vector.ControlPoints[1].Point.Y);
        Assert.Equal(5F, vector.ControlPoints[2].Point.Y);
        Assert.Equal(0F, vector.ControlPoints[3].Point.Y);

        Assert.Equal(1F, vector.ControlPoints[0].Point.X);
        Assert.Equal(2F, vector.ControlPoints[2].Point.X);
    }

    // A point between two fitted flanks scales linearly between them exactly as the IUP
    // instruction would interpolate it, and curve flags survive untouched.
    [Fact]
    public void InterpolatesUntouchedPointsBetweenFittedEdges()
    {
        GlyphVector vector = CreateVector([[On(1.3F, 0F), On(1.3F, 5F), Off(1.95F, 6F), On(2.6F, 5F), On(2.6F, 0F)]]);
        GridFitOptions options = new(8F, GridFitAxisMode.Full, GridFitAxisMode.None, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(1F, vector.ControlPoints[0].Point.X);
        Assert.Equal(1F, vector.ControlPoints[1].Point.X);
        Assert.Equal(1.5F, vector.ControlPoints[2].Point.X, 3);
        Assert.Equal(2F, vector.ControlPoints[3].Point.X);
        Assert.Equal(2F, vector.ControlPoints[4].Point.X);

        Assert.True(vector.ControlPoints[0].OnCurve);
        Assert.False(vector.ControlPoints[2].OnCurve);
        Assert.Equal(6F, vector.ControlPoints[2].Point.Y);
    }

    // Rescue mode widens a stroke thinner than a pixel to exactly one pixel on integer
    // boundaries so coverage sampling cannot drop it, while leaving thicker geometry alone.
    [Fact]
    public void RescueWidensSubPixelStrokeToOnePixel()
    {
        GlyphVector vector = CreateVector(
        [
            [On(0F, 2.3F), On(0F, 2.6F), On(5F, 2.6F), On(5F, 2.3F)]
        ]);
        GridFitOptions options = new(8F, GridFitAxisMode.None, GridFitAxisMode.Rescue, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(2F, vector.ControlPoints[0].Point.Y);
        Assert.Equal(3F, vector.ControlPoints[1].Point.Y);
        Assert.Equal(3F, vector.ControlPoints[2].Point.Y);
        Assert.Equal(2F, vector.ControlPoints[3].Point.Y);
    }

    // Rescue mode must not disturb strokes that are already a pixel or wider.
    [Fact]
    public void RescueLeavesWiderStrokesAlone()
    {
        GlyphVector vector = CreateVector(
        [
            [On(0F, 2.3F), On(0F, 3.4F), On(5F, 3.4F), On(5F, 2.3F)]
        ]);
        GridFitOptions options = new(8F, GridFitAxisMode.None, GridFitAxisMode.Rescue, 0F, 0F);

        Assert.False(GlyphGridFitter.FitInPlace(ref vector, in options));

        Assert.Equal(2.3F, vector.ControlPoints[0].Point.Y);
        Assert.Equal(3.4F, vector.ControlPoints[1].Point.Y);
    }

    // A second fitting pass finds every edge already on the grid and reports no movement,
    // leaving the outline bit for bit identical.
    [Fact]
    public void IsIdempotent()
    {
        GlyphVector vector = CreateVector([[On(1.3F, 0F), On(1.3F, 5F), On(2.6F, 5F), On(2.6F, 0F)]]);
        GridFitOptions options = new(8F, GridFitAxisMode.Full, GridFitAxisMode.None, 0F, 0F);

        Assert.True(GlyphGridFitter.FitInPlace(ref vector, in options));

        List<Vector2> snapshot = [];
        for (int i = 0; i < vector.ControlPoints.Count; i++)
        {
            snapshot.Add(vector.ControlPoints[i].Point);
        }

        Assert.False(GlyphGridFitter.FitInPlace(ref vector, in options));

        for (int i = 0; i < vector.ControlPoints.Count; i++)
        {
            Assert.Equal(snapshot[i], vector.ControlPoints[i].Point);
        }
    }

    public static TheoryData<string> StructureFonts { get; } = new()
    {
        TestFonts.Arial,
        TestFonts.Tahoma,
        TestFonts.OpenSansFile,
    };

    // Rendering under full hinting is deterministic and produces finite geometry for
    // hinted legacy and modern fonts across the small pixel sizes the fitter targets.
    // Emitted point counts are not compared against unhinted output: instructions may
    // legitimately flip curve flags, which changes the emitted command structure.
    [Theory]
    [MemberData(nameof(StructureFonts))]
    public void FullHintingRenderIsDeterministicAndPreservesStructure(string fontFileName)
    {
        const string text = "abcdefghijklmnopqrstuvwxyz";
        for (float size = 8F; size <= 12F; size++)
        {
            List<Vector2> first = RenderFreshCollection(fontFileName, text, size, HintingMode.Full);
            List<Vector2> second = RenderFreshCollection(fontFileName, text, size, HintingMode.Full);

            Assert.Equal(first, second);
            Assert.True(first.Count > 0);
            Assert.All(first, static p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y)));
        }
    }

    // CFF outlines never pass through the TrueType hinting or fitting path, so full mode
    // must render them identically to unhinted output.
    [Fact]
    public void DoesNotAlterCffRendering()
    {
        const string text = "Hamburgefonstiv";
        List<Vector2> none = RenderFreshCollection(TestFonts.PlantinStdRegularFile, text, 10F, HintingMode.None);
        List<Vector2> full = RenderFreshCollection(TestFonts.PlantinStdRegularFile, text, 10F, HintingMode.Full);

        Assert.Equal(none, full);
    }

    private static List<Vector2> RenderFreshCollection(string path, string text, float size, HintingMode mode)
    {
        FontCollection collection = new();
        FontFamily family = collection.Add(path);
        Font font = family.CreateFont(size);
        TextOptions options = new(font)
        {
            HintingMode = mode,
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        return renderer.ControlPoints;
    }

    private static ControlPoint On(float x, float y) => new(new Vector2(x, y), true);

    private static ControlPoint Off(float x, float y) => new(new Vector2(x, y), false);

    private static GlyphVector CreateVector(ControlPoint[][] contours)
    {
        List<ControlPoint> points = [];
        List<ushort> endPoints = [];
        foreach (ControlPoint[] contour in contours)
        {
            points.AddRange(contour);
            endPoints.Add((ushort)(points.Count - 1));
        }

        return new GlyphVector(points, endPoints, default, ReadOnlyMemory<byte>.Empty, false);
    }
}
