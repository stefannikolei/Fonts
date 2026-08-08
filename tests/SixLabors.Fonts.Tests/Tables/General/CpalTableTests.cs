// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.General;

namespace SixLabors.Fonts.Tests.Tables.General;

public class CpalTableTests
{
    private static CpalTable CreateTwoPaletteTable()
        => new(
            2,
            [0, 2],
            [
                new GlyphColor(10, 11, 12, 255),
                new GlyphColor(20, 21, 22, 255),
                new GlyphColor(30, 31, 32, 255),
                new GlyphColor(40, 41, 42, 255)
            ]);

    [Fact]
    public void GetPaletteColors_NullSelection_ReturnsDefaultPalette()
    {
        CpalTable table = CreateTwoPaletteTable();

        GlyphColor[] colors = table.GetPaletteColors(null);

        Assert.Equal(2, colors.Length);
        Assert.Equal(new GlyphColor(10, 11, 12, 255), colors[0]);
        Assert.Equal(new GlyphColor(20, 21, 22, 255), colors[1]);
    }

    [Fact]
    public void GetPaletteColors_SelectsPaletteByIndex()
    {
        CpalTable table = CreateTwoPaletteTable();

        GlyphColor[] colors = table.GetPaletteColors(new FontPalette(1));

        Assert.Equal(2, colors.Length);
        Assert.Equal(new GlyphColor(30, 31, 32, 255), colors[0]);
        Assert.Equal(new GlyphColor(40, 41, 42, 255), colors[1]);
    }

    [Fact]
    public void GetPaletteColors_OutOfRangeIndex_FallsBackToDefaultAndAppliesOverrides()
    {
        CpalTable table = CreateTwoPaletteTable();

        GlyphColor[] colors = table.GetPaletteColors(new FontPalette(9, [new FontPaletteOverride(1, GlyphColor.Red)]));

        Assert.Equal(new GlyphColor(10, 11, 12, 255), colors[0]);
        Assert.Equal(GlyphColor.Red, colors[1]);
    }

    [Fact]
    public void GetPaletteColors_LaterOverrideWins_AndOutOfRangeOverrideIsIgnored()
    {
        CpalTable table = CreateTwoPaletteTable();

        GlyphColor[] colors = table.GetPaletteColors(new FontPalette(0,
        [
            new FontPaletteOverride(0, GlyphColor.Blue),
            new FontPaletteOverride(0, GlyphColor.Lime),
            new FontPaletteOverride(5, GlyphColor.Red)
        ]));

        Assert.Equal(GlyphColor.Lime, colors[0]);
        Assert.Equal(new GlyphColor(20, 21, 22, 255), colors[1]);
    }

    [Fact]
    public void GetPaletteColors_SharesOneArrayPerSelection()
    {
        CpalTable table = CreateTwoPaletteTable();

        // The default selection and the explicit default selection share one array.
        Assert.Same(table.GetPaletteColors(null), table.GetPaletteColors(null));
        Assert.Same(table.GetPaletteColors(null), table.GetPaletteColors(new FontPalette(0)));

        // Value-equal custom selections share one array even across distinct instances.
        FontPalette a = new(1, [new FontPaletteOverride(0, GlyphColor.Red)]);
        FontPalette b = new(1, [new FontPaletteOverride(0, GlyphColor.Red)]);
        Assert.Same(table.GetPaletteColors(a), table.GetPaletteColors(b));
    }

    [Fact]
    public void FontPalette_ValueEquality()
    {
        FontPalette a = new(1, [new FontPaletteOverride(0, GlyphColor.Red), new FontPaletteOverride(1, GlyphColor.Blue)]);
        FontPalette b = new(1, [new FontPaletteOverride(0, GlyphColor.Red), new FontPaletteOverride(1, GlyphColor.Blue)]);
        FontPalette reordered = new(1, [new FontPaletteOverride(1, GlyphColor.Blue), new FontPaletteOverride(0, GlyphColor.Red)]);
        FontPalette differentIndex = new(2, [new FontPaletteOverride(0, GlyphColor.Red), new FontPaletteOverride(1, GlyphColor.Blue)]);

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // Overrides apply in order and a later override wins, so order participates in equality.
        Assert.False(a.Equals(reordered));
        Assert.False(a.Equals(differentIndex));
        Assert.False(a.Equals(null));
    }
}
