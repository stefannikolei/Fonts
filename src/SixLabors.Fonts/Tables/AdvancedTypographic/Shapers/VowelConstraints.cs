// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Separates the character sequences that spell one vowel but read as another.
/// The shapers whose scripts define such sequences run this over the text before
/// classifying it into syllables.
/// </summary>
internal static class VowelConstraints
{
    /// <summary>
    /// The dotted circle, which stands between the characters of a sequence so
    /// it cannot be mistaken for the vowel it imitates.
    /// </summary>
    private const int DottedCircle = 0x25CC;

    /// <summary>
    /// Places a dotted circle before the final character of every constrained
    /// sequence in the range. A sequence is consumed once recognised, so the
    /// characters it spans cannot begin another.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="fontMetrics">The font metrics used to resolve the dotted circle.</param>
    /// <param name="script">The script the segment is written in.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records.</param>
    /// <returns>The number of dotted circles placed.</returns>
    public static int Insert(ShapingBuffer buffer, FontMetrics fontMetrics, ScriptClass script, int index, int count)
    {
        if (!buffer.HasVowelConstraintCandidates || !VowelConstraintData.IsConstrainedScript(script))
        {
            return 0;
        }

        // The sequence is separated whether or not the font draws the circle;
        // a font without one renders the missing glyph in its place.
        if (!fontMetrics.TryGetGlyphId(new CodePoint(DottedCircle), out ushort circleId))
        {
            circleId = 0;
        }

        int inserted = 0;
        int end = index + count;
        int i = index;
        while (i + 1 < end)
        {
            int first = buffer[i].CodePoint.Value;
            int second = buffer[i + 1].CodePoint.Value;

            int length = 0;
            if (i + 2 < end && VowelConstraintData.IsConstrainedTriple(script, first, second, buffer[i + 2].CodePoint.Value))
            {
                length = 3;
            }
            else if (VowelConstraintData.IsConstrainedPair(script, first, second))
            {
                length = 2;
            }

            if (length == 0)
            {
                i++;
                continue;
            }

            buffer.InsertDottedCircle(i + length - 1, circleId);
            inserted++;
            end++;

            // The sequence and the circle now placed within it are behind the
            // cursor, so the search resumes past all of them.
            i += length + 1;
        }

        return inserted;
    }
}
