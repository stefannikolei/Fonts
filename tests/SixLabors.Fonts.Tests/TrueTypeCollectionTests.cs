// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;

namespace SixLabors.Fonts.Tests;

public class TrueTypeCollectionTests
{
    [Fact]
    public void AddViaPathReturnsDescription()
    {
        FontCollection suit = new();
        IEnumerable<FontFamily> collectionFromPath = suit.AddCollection(TestFonts.SimpleTrueTypeCollection, out IEnumerable<FontDescription> descriptions);

        Assert.Equal(2, descriptions.Count());
        FontFamily openSans = Assert.Single(collectionFromPath, x => x.Name == "Open Sans");
        FontFamily abFont = Assert.Single(collectionFromPath, x => x.Name == "SixLaborsSampleAB");

        Assert.Equal(2, descriptions.Count());
        FontDescription openSansDescription = Assert.Single(descriptions, x => x.FontNameInvariantCulture == "Open Sans");
        FontDescription abFontDescription = Assert.Single(descriptions, x => x.FontNameInvariantCulture == "SixLaborsSampleAB regular");
    }

    [Fact]
    public void AddViaPathAddFontFileInstances()
    {
        FontCollection sut = new();
        IEnumerable<FontFamily> collectionFromPath = sut.AddCollection(TestFonts.SimpleTrueTypeCollection, out IEnumerable<FontDescription> descriptions);

        IEnumerable<FontMetrics> allInstances = sut.Families.SelectMany(x => ((IReadOnlyFontMetricsCollection)sut).GetAllMetrics(x.Name, CultureInfo.InvariantCulture));

        Assert.All(allInstances, i =>
        {
            FileFontMetrics font = Assert.IsType<FileFontMetrics>(i);
        });
    }

    [Fact]
    public void AddViaStreamReturnsDescription()
    {
        FontCollection suit = new();
        IEnumerable<FontFamily> collectionFromPath = suit.AddCollection(TestFonts.SSimpleTrueTypeCollectionData(), out IEnumerable<FontDescription> descriptions);

        Assert.Equal(2, collectionFromPath.Count());
        FontFamily openSans = Assert.Single(collectionFromPath, x => x.Name == "Open Sans");
        FontFamily abFont = Assert.Single(collectionFromPath, x => x.Name == "SixLaborsSampleAB");

        Assert.Equal(2, descriptions.Count());
        FontDescription openSansDescription = Assert.Single(descriptions, x => x.FontNameInvariantCulture == "Open Sans");
        FontDescription abFontDescription = Assert.Single(descriptions, x => x.FontNameInvariantCulture == "SixLaborsSampleAB regular");
    }
}
