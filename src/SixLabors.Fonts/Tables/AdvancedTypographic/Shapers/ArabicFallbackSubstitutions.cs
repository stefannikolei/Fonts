// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Buffers.Binary;
using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Applies Arabic presentation forms and required ligatures when a font provides no joining-form substitutions.
/// </summary>
internal sealed class ArabicFallbackSubstitutions
{
    /// <summary>
    /// The initial presentation-form column.
    /// </summary>
    private const int InitialFormIndex = 0;

    /// <summary>
    /// The medial presentation-form column.
    /// </summary>
    private const int MedialFormIndex = 1;

    /// <summary>
    /// The final presentation-form column.
    /// </summary>
    private const int FinalFormIndex = 2;

    /// <summary>
    /// The isolated presentation-form column.
    /// </summary>
    private const int IsolatedFormIndex = 3;

    /// <summary>
    /// The byte offset of the second character in a packed ligature entry.
    /// </summary>
    private const int SecondCharacterOffset = sizeof(ushort);

    /// <summary>
    /// The byte offset of the third character in a packed three-character ligature entry.
    /// </summary>
    private const int ThirdCharacterOffset = sizeof(ushort) * 2;

    /// <summary>
    /// The byte offset of the result in a packed two-character ligature entry.
    /// </summary>
    private const int TwoCharacterResultOffset = sizeof(ushort) * 2;

    /// <summary>
    /// The byte offset of the result in a packed three-character ligature entry.
    /// </summary>
    private const int ThreeCharacterResultOffset = sizeof(ushort) * 3;

    /// <summary>
    /// The synthesized three-character required ligatures.
    /// </summary>
    private readonly FallbackLigature[] threeCharacterLigatures;

    /// <summary>
    /// The synthesized two-character required ligatures.
    /// </summary>
    private readonly FallbackLigature[] twoCharacterLigatures;

    /// <summary>
    /// The synthesized mark required ligatures.
    /// </summary>
    private readonly FallbackLigature[] markLigatures;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArabicFallbackSubstitutions"/> class.
    /// </summary>
    /// <param name="threeCharacterLigatures">The three-character required ligatures available in the font.</param>
    /// <param name="twoCharacterLigatures">The two-character required ligatures available in the font.</param>
    /// <param name="markLigatures">The mark required ligatures available in the font.</param>
    private ArabicFallbackSubstitutions(FallbackLigature[] threeCharacterLigatures, FallbackLigature[] twoCharacterLigatures, FallbackLigature[] markLigatures)
    {
        this.threeCharacterLigatures = threeCharacterLigatures;
        this.twoCharacterLigatures = twoCharacterLigatures;
        this.markLigatures = markLigatures;
    }

    /// <summary>
    /// Creates the fallback substitutions supported by the given font.
    /// </summary>
    /// <param name="fontMetrics">The font metrics used to resolve characters to glyph identifiers.</param>
    /// <returns>The resolved fallback substitutions.</returns>
    public static ArabicFallbackSubstitutions Create(FontMetrics fontMetrics)
        => new(
            ResolveLigatures(fontMetrics, ArabicFallbackData.ThreeCharacterLigatures, true),
            ResolveLigatures(fontMetrics, ArabicFallbackData.TwoCharacterLigatures, false),
            ResolveLigatures(fontMetrics, ArabicFallbackData.MarkLigatures, false));

    /// <summary>
    /// Applies the joining forms and required ligatures to a shaping segment.
    /// </summary>
    /// <param name="plan">The shaping plan supplying feature masks and font metrics.</param>
    /// <param name="buffer">The shaping buffer.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the segment.</param>
    /// <param name="initialTag">The initial-form feature tag.</param>
    /// <param name="medialTag">The medial-form feature tag.</param>
    /// <param name="finalTag">The final-form feature tag.</param>
    /// <param name="isolatedTag">The isolated-form feature tag.</param>
    /// <param name="requiredLigaturesTag">The required-ligatures feature tag.</param>
    public void Apply(ShapePlan plan, ShapingBuffer buffer, int index, int count, Tag initialTag, Tag medialTag, Tag finalTag, Tag isolatedTag, Tag requiredLigaturesTag)
    {
        FontMetrics fontMetrics = plan.FontMetrics;
        ApplyPresentationForms(fontMetrics, plan.Features.GetMask(initialTag), buffer, index, count, initialTag, InitialFormIndex);
        ApplyPresentationForms(fontMetrics, plan.Features.GetMask(medialTag), buffer, index, count, medialTag, MedialFormIndex);
        ApplyPresentationForms(fontMetrics, plan.Features.GetMask(finalTag), buffer, index, count, finalTag, FinalFormIndex);
        ApplyPresentationForms(fontMetrics, plan.Features.GetMask(isolatedTag), buffer, index, count, isolatedTag, IsolatedFormIndex);

        uint requiredLigaturesMask = plan.Features.GetMask(requiredLigaturesTag);
        buffer.SetLookupMatchState(requiredLigaturesMask, true, false, false, false);

        count = ApplyLigatures(fontMetrics, buffer, index, count, requiredLigaturesTag, requiredLigaturesMask, this.threeCharacterLigatures, LookupFlags.IgnoreMarks);
        count = ApplyLigatures(fontMetrics, buffer, index, count, requiredLigaturesTag, requiredLigaturesMask, this.twoCharacterLigatures, LookupFlags.IgnoreMarks);
        _ = ApplyLigatures(fontMetrics, buffer, index, count, requiredLigaturesTag, requiredLigaturesMask, this.markLigatures, default);
    }

    /// <summary>
    /// Applies one presentation-form column to records carrying the corresponding feature mask.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="featureMask">The joining-form feature mask.</param>
    /// <param name="buffer">The shaping buffer.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the segment.</param>
    /// <param name="feature">The joining-form feature tag.</param>
    /// <param name="formIndex">The presentation-form column.</param>
    private static void ApplyPresentationForms(FontMetrics fontMetrics, uint featureMask, ShapingBuffer buffer, int index, int count, Tag feature, int formIndex)
    {
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            if ((data.FeatureMask & featureMask) == 0
                || !fontMetrics.TryGetGlyphId(data.CodePoint, out ushort baseGlyphId)
                || data.GlyphId != baseGlyphId)
            {
                continue;
            }

            ushort presentationForm = ArabicFallbackData.GetPresentationForm(data.CodePoint.Value, formIndex);
            if (presentationForm != 0
                && fontMetrics.TryGetGlyphId(new CodePoint(presentationForm), out ushort presentationGlyphId)
                && presentationGlyphId != baseGlyphId)
            {
                buffer.Replace(i, presentationGlyphId, feature);
            }
        }
    }

    /// <summary>
    /// Resolves one packed required-ligature table against the font's character map.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="data">The packed character entries.</param>
    /// <param name="hasThirdCharacter">Whether each entry contains a third input character.</param>
    /// <returns>The entries whose input and result characters all have glyphs.</returns>
    private static FallbackLigature[] ResolveLigatures(FontMetrics fontMetrics, ReadOnlySpan<byte> data, bool hasThirdCharacter)
    {
        int entrySize = hasThirdCharacter ? ArabicFallbackData.ThreeCharacterLigatureEntrySize : ArabicFallbackData.TwoCharacterLigatureEntrySize;
        int resultOffset = hasThirdCharacter ? ThreeCharacterResultOffset : TwoCharacterResultOffset;
        List<FallbackLigature> ligatures = new(data.Length / entrySize);
        for (int offset = 0; offset < data.Length; offset += entrySize)
        {
            ushort firstCharacter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, sizeof(ushort)));
            ushort secondCharacter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + SecondCharacterOffset, sizeof(ushort)));
            ushort thirdCharacter = hasThirdCharacter ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + ThirdCharacterOffset, sizeof(ushort))) : (ushort)0;
            ushort resultCharacter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + resultOffset, sizeof(ushort)));
            ushort thirdGlyph = 0;

            if (!fontMetrics.TryGetGlyphId(new CodePoint(firstCharacter), out ushort firstGlyph)
                || !fontMetrics.TryGetGlyphId(new CodePoint(secondCharacter), out ushort secondGlyph)
                || (hasThirdCharacter && !fontMetrics.TryGetGlyphId(new CodePoint(thirdCharacter), out thirdGlyph))
                || !fontMetrics.TryGetGlyphId(new CodePoint(resultCharacter), out ushort resultGlyph))
            {
                continue;
            }

            ushort[] components;
            if (hasThirdCharacter)
            {
                components = [secondGlyph, thirdGlyph];
            }
            else
            {
                components = [secondGlyph];
            }

            ligatures.Add(new FallbackLigature(firstGlyph, components, resultGlyph));
        }

        return [.. ligatures];
    }

    /// <summary>
    /// Applies one synthesized required-ligature lookup.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The shaping buffer.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the segment.</param>
    /// <param name="feature">The required-ligatures feature tag.</param>
    /// <param name="featureMask">The required-ligatures feature mask.</param>
    /// <param name="ligatures">The synthesized ligatures.</param>
    /// <param name="lookupFlags">The lookup flags controlling skipped glyphs.</param>
    /// <returns>The segment count after substitutions.</returns>
    private static int ApplyLigatures(FontMetrics fontMetrics, ShapingBuffer buffer, int index, int count, Tag feature, uint featureMask, FallbackLigature[] ligatures, LookupFlags lookupFlags)
    {
        int end = index + count;
        Span<int> matchBuffer = stackalloc int[AdvancedTypographicUtils.MaxContextLength];
        for (int position = index; position < end; position++)
        {
            ref GlyphShapingData data = ref buffer[position];
            if ((data.FeatureMask & featureMask) == 0)
            {
                continue;
            }

            for (int i = 0; i < ligatures.Length; i++)
            {
                FallbackLigature ligature = ligatures[i];
                if (data.GlyphId != ligature.FirstGlyph)
                {
                    continue;
                }

                SkippingGlyphIterator iterator = new(fontMetrics, buffer, position, lookupFlags, 0);
                if (!AdvancedTypographicUtils.MatchInputSequence(iterator, featureMask, 1, ligature.Components, matchBuffer))
                {
                    continue;
                }

                Span<int> matches = matchBuffer[..ligature.Components.Length];
                AdvancedTypographicUtils.ApplyLigatureSubstitution(fontMetrics, buffer, position, matches, ligature.ResultGlyph, feature, end - position);
                end -= matches.Length;
                count -= matches.Length;
                break;
            }
        }

        return count;
    }

    /// <summary>
    /// Stores one font-resolved ligature substitution.
    /// </summary>
    private readonly struct FallbackLigature
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FallbackLigature"/> struct.
        /// </summary>
        /// <param name="firstGlyph">The first input glyph.</param>
        /// <param name="components">The remaining input glyphs.</param>
        /// <param name="resultGlyph">The result glyph.</param>
        public FallbackLigature(ushort firstGlyph, ushort[] components, ushort resultGlyph)
        {
            this.FirstGlyph = firstGlyph;
            this.Components = components;
            this.ResultGlyph = resultGlyph;
        }

        /// <summary>
        /// Gets the first input glyph.
        /// </summary>
        public ushort FirstGlyph { get; }

        /// <summary>
        /// Gets the remaining input glyphs.
        /// </summary>
        public ushort[] Components { get; }

        /// <summary>
        /// Gets the result glyph.
        /// </summary>
        public ushort ResultGlyph { get; }
    }
}
