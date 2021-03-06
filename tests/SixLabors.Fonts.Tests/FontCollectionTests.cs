// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace SixLabors.Fonts.Tests
{
    public class FontCollectionTests
    {
        [Fact]
        public void InstallViaPathReturnsDecription()
        {
            var sut = new FontCollection();

            FontFamily family = sut.Install(TestFonts.CarterOneFile, out FontDescription description);
            Assert.NotNull(description);
            Assert.Equal("Carter One", description.FontFamilyInvariantCulture);
            Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
            Assert.Equal(FontStyle.Regular, description.Style);
        }

        [Fact]
        public void InstallViaPathInstallFontFileInstances()
        {
            var sut = new FontCollection();
            FontFamily family = sut.Install(TestFonts.CarterOneFile, out FontDescription descriptions);

            IEnumerable<IFontInstance> allInstances = sut.FindAll(family.Name, CultureInfo.InvariantCulture);

            Assert.All(allInstances, i =>
            {
                FileFontInstance font = Assert.IsType<FileFontInstance>(i);
            });
        }

        [Fact]
        public void InstallViaStreamReturnsDecription()
        {
            var sut = new FontCollection();
            using (System.IO.Stream s = TestFonts.CarterOneFileData())
            {
                FontFamily family = sut.Install(s, out FontDescription description);
                Assert.NotNull(description);
                Assert.Equal("Carter One", description.FontFamilyInvariantCulture);
                Assert.Equal("Regular", description.FontSubFamilyNameInvariantCulture);
                Assert.Equal(FontStyle.Regular, description.Style);
            }
        }
    }
}
