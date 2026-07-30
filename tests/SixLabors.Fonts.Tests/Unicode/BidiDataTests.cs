// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests.Unicode;

public class BidiDataTests
{
    /// <summary>
    /// The ASCII bidi tables are compile-time constant data generated from the
    /// general classification path. This guard asserts the constants match that
    /// path for every ASCII value, so a Unicode data update can never silently
    /// diverge the two.
    /// </summary>
    [Fact]
    public void AsciiBidiTablesMatchGeneralClassification()
    {
        for (int c = 0; c < 128; c++)
        {
            CodePoint codePoint = new(c);
            BidiClass bidi = CodePoint.GetBidiClass(codePoint);

            Assert.Equal((byte)bidi.CharacterType, BidiData.AsciiCharacterTypes[c]);
            Assert.Equal((byte)bidi.PairedBracketType, BidiData.AsciiPairedBracketTypes[c]);

            int expectedPairedValue = 0;
            if (bidi.PairedBracketType == BidiPairedBracketType.Open)
            {
                Assert.True(bidi.TryGetPairedBracket(out CodePoint paired));
                expectedPairedValue = CodePoint.GetCanonicalType(paired).Value;
            }
            else if (bidi.PairedBracketType == BidiPairedBracketType.Close)
            {
                expectedPairedValue = CodePoint.GetCanonicalType(codePoint).Value;
            }

            Assert.Equal(expectedPairedValue, BidiData.AsciiPairedBracketValues[c]);
        }
    }
}
