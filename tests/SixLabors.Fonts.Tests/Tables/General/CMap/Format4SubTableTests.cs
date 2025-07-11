// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Tables.General.CMap;
using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.WellKnownIds;

namespace SixLabors.Fonts.Tests.Tables.General.CMap;

public class Format4SubTableTests
{
    [Fact]
    public void LoadFormat4()
    {
        BigEndianBinaryWriter writer = new();

        // int subtableCount = 1;
        writer.WriteCMapSubTable(
            new Format4SubTable(
                0,
                PlatformIDs.Windows,
                2,
                new[] { new Format4SubTable.Segment(0, 1, 2, 3, 4) },
                new ushort[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        BigEndianBinaryReader reader = writer.GetReader();
        ushort format = reader.ReadUInt16(); // read format before we pass along as that's what the cmap table does
        Assert.Equal(4, format);

        Format4SubTable table = Format4SubTable.Load(
            new[] { new EncodingRecord(PlatformIDs.Windows, 2, 0) },
            reader).Single();

        Assert.Equal(0, table.Language);
        Assert.Equal(PlatformIDs.Windows, table.Platform);
        Assert.Equal(2, table.Encoding);
        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6, 7, 8 }, table.GlyphIds);

        Assert.Single(table.Segments);
        Format4SubTable.Segment seg = table.Segments[0];
        Assert.Equal(0, seg.Index);
        Assert.Equal(1, seg.End);
        Assert.Equal(2, seg.Start);
        Assert.Equal(3, seg.Delta);
        Assert.Equal(4, seg.Offset);
    }

    [Theory]
    [InlineData(10, 1, true)]
    [InlineData(20, 11, true)]
    [InlineData(30, 12, true)]
    [InlineData(90, 72, true)]
    [InlineData(500, 0, false)] // not in range
    public void GetCharacter(int src, int expected, bool expectedFound)
    {
        // segCountX2:    8
        // searchRange:   8
        // entrySelector: 4
        // rangeShift:    0
        // endCode:       20  90  480  0xffff
        // reservedPad:   0
        // startCode:     10  30  153  0xffff
        // dDelta:        -9  -18 -27  1
        // idRangeOffset: 0   0   0    0
        ushort[] glyphs = Enumerable.Range(0, expected).Select(x => (ushort)x).ToArray();

        Format4SubTable.Segment[] segments = new[]
        {
            new Format4SubTable.Segment(0, 20, 10, -9, 0),
            new Format4SubTable.Segment(1, 90, 30, -18, 0),
            new Format4SubTable.Segment(2, 480, 153, -27, 0),
        };

        Format4SubTable table = new(
            0,
            PlatformIDs.Windows,
            0,
            segments,
            glyphs);

        // ushort id = table.GetGlyphId(src);
        bool found = table.TryGetGlyphId(new CodePoint(src), out ushort id);

        Assert.Equal(expected, id);
        Assert.Equal(expectedFound, found);
    }
}
