// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;
using UnicodeTrieGenerator.StateAutomation;
using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Shaper for the Myanmar script. Handles syllable identification, reordering,
/// and application of Myanmar-specific OpenType features.
/// </summary>
internal sealed class MyanmarShaper : DefaultShaper
{
    /// <summary>The state machine for Myanmar syllable identification.</summary>
    private static readonly StateMachine StateMachine =
        new(
            Unicode.Resources.MyanmarShapingData.StateTable,
            Unicode.Resources.MyanmarShapingData.AcceptingStates,
            Unicode.Resources.MyanmarShapingData.Tags);

    /// <summary>Maps Myanmar shaping category codes to compact DFA symbol indices.</summary>
    private static readonly int[] CategoryToSymbolId = BuildCategoryToSymbolId();

    /// <summary>The 'rphf' (reph forms) feature tag.</summary>
    private static readonly Tag RphfTag = Tag.Parse("rphf");

    /// <summary>The 'pref' (pre-base forms) feature tag.</summary>
    private static readonly Tag PrefTag = Tag.Parse("pref");

    /// <summary>The 'blwf' (below-base forms) feature tag.</summary>
    private static readonly Tag BlwfTag = Tag.Parse("blwf");

    /// <summary>The 'pstf' (post-base forms) feature tag.</summary>
    private static readonly Tag PstfTag = Tag.Parse("pstf");

    /// <summary>The 'pres' (pre-base substitutions) feature tag.</summary>
    private static readonly Tag PresTag = Tag.Parse("pres");

    /// <summary>The 'abvs' (above-base substitutions) feature tag.</summary>
    private static readonly Tag AbvsTag = Tag.Parse("abvs");

    /// <summary>The 'blws' (below-base substitutions) feature tag.</summary>
    private static readonly Tag BlwsTag = Tag.Parse("blws");

    /// <summary>The 'psts' (post-base substitutions) feature tag.</summary>
    private static readonly Tag PstsTag = Tag.Parse("psts");

    /// <summary>Dotted circle code point (U+25CC) used as a placeholder base.</summary>
    private const int DottedCircle = 0x25cc;

    /// <summary>The text options.</summary>
    private readonly TextOptions textOptions;

    /// <summary>The font metrics used for glyph lookups.</summary>
    private readonly FontMetrics fontMetrics;

    /// <summary>Whether any broken clusters were detected during syllable setup.</summary>
    private bool hasBrokenClusters;

    /// <summary>
    /// Initializes a new instance of the <see cref="MyanmarShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="textOptions">The text options.</param>
    /// <param name="fontMetrics">The font metrics for glyph lookups.</param>
    public MyanmarShaper(ScriptClass script, TextOptions textOptions, FontMetrics fontMetrics)
       : base(script, MarkZeroingMode.PreGPos, textOptions)
    {
        this.textOptions = textOptions;
        this.fontMetrics = fontMetrics;
    }

    /// <inheritdoc />
    protected override void PlanFeatures(ShapingBuffer buffer, int index, int count)
    {
        this.AddFeature(buffer, index, count, LoclTag, preAction: this.SetupSyllables);
        this.AddFeature(buffer, index, count, CcmpTag);

        this.AddFeature(buffer, index, count, RphfTag, preAction: this.InitialReorder);
        this.AddFeature(buffer, index, count, PrefTag);
        this.AddFeature(buffer, index, count, BlwfTag);
        this.AddFeature(buffer, index, count, PstfTag);

        this.AddFeature(buffer, index, count, PresTag);
        this.AddFeature(buffer, index, count, AbvsTag);
        this.AddFeature(buffer, index, count, BlwsTag);
        this.AddFeature(buffer, index, count, PstsTag);
    }

    /// <summary>
    /// Identifies Myanmar syllables using the state machine and assigns shaping info to each glyph.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void SetupSyllables(ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        this.hasBrokenClusters = false;

        Span<int> values = count <= 64 ? stackalloc int[count] : new int[count];

        for (int i = index; i < index + count; i++)
        {
            // Convert HarfBuzz-style Myanmar shaping categories into the compact
            // DFA symbol indices used by the generated state machine.
            //
            // HarfBuzz category codes (C=1, V=2, MR=36, VBlw=21, etc.) are sparse
            // and can be larger than the alphabet size of the DFA. Our state
            // machine expects its input alphabet to be dense 0..N-1, matching the
            // sequential IDs assigned in GenerateMyanmarShapingData.
            //
            // CategoryToSymbolId[(int)my] performs this mapping, ensuring that
            // every codepoint is presented to the DFA using the correct compact
            // symbol index.
            CodePoint codePoint = buffer[i].CodePoint;
            MyanmarCategories my = (MyanmarCategories)IndicShapingCategory(codePoint);
            values[i - index] = CategoryToSymbolId[(int)my];
        }

        int syllable = 0;
        int last = 0;
        foreach (StateMatch match in StateMachine.Match(values))
        {
            if (match.StartIndex > last)
            {
                ++syllable;
                for (int i = last; i < match.StartIndex; i++)
                {
                    ref GlyphShapingData data = ref buffer[i + index];
                    data.Syllable.IndicCategory = Categories.X;
                    data.Syllable.IndicPosition = Positions.End;
                    data.Syllable.Type = SyllableType.NonIndicCluster;
                    data.Syllable.Number = syllable;
                }
            }

            ++syllable;

            // Create shaper info.
            for (int i = match.StartIndex; i <= match.EndIndex; i++)
            {
                ref GlyphShapingData data = ref buffer[i + index];
                CodePoint codePoint = data.CodePoint;

                SyllableType syllableType = SyllableTypeMap.FromTag(match.Tags[0]);

                if (syllableType == SyllableType.BrokenCluster)
                {
                    this.hasBrokenClusters = true;
                }

                data.Syllable.IndicCategory = (Categories)IndicShapingCategory(codePoint);
                data.Syllable.IndicPosition = (Positions)IndicShapingPosition(codePoint);
                data.Syllable.Type = syllableType;
                data.Syllable.Number = syllable;
            }

            last = match.EndIndex + 1;
        }

        if (last < count)
        {
            ++syllable;
            for (int i = last; i < count; i++)
            {
                ref GlyphShapingData data = ref buffer[i + index];
                data.Syllable.IndicCategory = Categories.X;
                data.Syllable.IndicPosition = Positions.End;
                data.Syllable.Type = SyllableType.NonIndicCluster;
                data.Syllable.Number = syllable;
            }
        }
    }

    /// <summary>
    /// Performs the initial reordering pass for Myanmar consonant syllables, including
    /// dotted circle insertion for broken clusters.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void InitialReorder(ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        FontMetrics fontMetrics = this.fontMetrics;
        int max = index + count;
        int start = index;
        int end = NextSyllable(buffer, index, max);

        if (this.hasBrokenClusters)
        {
            if (fontMetrics.TryGetGlyphId(new(DottedCircle), out ushort circleId))
            {
                Span<ushort> glyphs = stackalloc ushort[2];
                while (start < max)
                {
                    if (buffer[start].Syllable.Type == SyllableType.BrokenCluster)
                    {
                        // Insert after possible Repha.
                        int i = start;
                        for (i = start; i < end; i++)
                        {
                            if (buffer[i].Syllable.IndicCategory != Categories.Repha)
                            {
                                break;
                            }
                        }

                        ref GlyphShapingData current = ref buffer[i];
                        glyphs[0] = current.GlyphId;
                        glyphs[1] = circleId;

                        buffer.Replace(i, glyphs, KnownFeatureTags.GlyphCompositionDecomposition);

                        // Update shaping info for newly inserted data.
                        ref GlyphShapingData dotted = ref buffer[i + 1];
                        dotted.Syllable.IndicCategory = Categories.Dotted_Circle;

                        end++;
                        max++;
                    }

                    start = end;
                    end = NextSyllable(buffer, start, max);
                }

                start = index;
                end = NextSyllable(buffer, index, max);
            }
        }

        while (start < max)
        {
            switch (buffer[start].Syllable.Type)
            {
                // We already inserted dotted-circles, so just call the consonant_syllable.
                case SyllableType.BrokenCluster:
                case SyllableType.ConsonantSyllable:
                    ReorderConsonantSyllable(buffer, start, end);
                    break;
                default:
                    break;
            }

            start = end;
            end = NextSyllable(buffer, start, max);
        }
    }

    /// <summary>
    /// Reorders glyphs within a single Myanmar consonant syllable according to the Myanmar shaping spec.
    /// </summary>
    /// <param name="buffer">The glyph substitution buffer.</param>
    /// <param name="start">The start index of the syllable.</param>
    /// <param name="end">The exclusive end index of the syllable.</param>
    private static void ReorderConsonantSyllable(ShapingBuffer buffer, int start, int end)
    {
        int basePosition = end;
        bool hasReph = false;
        {
            int limit = start;
            if (start + 3 <= end &&
                buffer[start].Syllable.MyanmarCategory == MyanmarCategories.Ra &&
                buffer[start + 1].Syllable.MyanmarCategory == MyanmarCategories.As &&
                buffer[start + 2].Syllable.MyanmarCategory == MyanmarCategories.H)
            {
                limit += 3;
                basePosition = start;
                hasReph = true;
            }

            {
                if (!hasReph)
                {
                    basePosition = limit;
                }

                for (int i = limit; i < end; i++)
                {
                    if (IsConsonant(ref buffer[i]))
                    {
                        basePosition = i;
                        break;
                    }
                }
            }
        }

        // Reorder
        {
            int i = start;
            for (; i < start + (hasReph ? 3 : 0); i++)
            {
                buffer[i].Syllable.IndicPosition = Positions.After_Main;
            }

            for (; i < basePosition; i++)
            {
                buffer[i].Syllable.IndicPosition = Positions.Pre_C;
            }

            if (i < end)
            {
                buffer[i].Syllable.IndicPosition = Positions.Base_C;
                i++;
            }

            Positions pos = Positions.After_Main;

            // The following loop may be ugly, but it implements all of Myanmar reordering!
            for (; i < end; i++)
            {
                ref GlyphShapingData data = ref buffer[i];

                // Pre-base reordering
                if (data.Syllable.MyanmarCategory == MyanmarCategories.MR)
                {
                    data.Syllable.IndicPosition = Positions.Pre_C;
                    continue;
                }

                // Left matra
                if (data.Syllable.MyanmarCategory == MyanmarCategories.VPre)
                {
                    data.Syllable.IndicPosition = Positions.Pre_M;
                    continue;
                }

                if (data.Syllable.MyanmarCategory == MyanmarCategories.VS)
                {
                    data.Syllable.IndicPosition = buffer[i - 1].Syllable.IndicPosition;
                    continue;
                }

                if (pos == Positions.After_Main && data.Syllable.MyanmarCategory == MyanmarCategories.VBlw)
                {
                    pos = Positions.Below_C;
                    data.Syllable.IndicPosition = pos;
                    continue;
                }

                if (pos == Positions.Below_C && data.Syllable.MyanmarCategory == MyanmarCategories.A)
                {
                    data.Syllable.IndicPosition = Positions.Before_Sub;
                    continue;
                }

                if (pos == Positions.Below_C && data.Syllable.MyanmarCategory == MyanmarCategories.VBlw)
                {
                    data.Syllable.IndicPosition = pos;
                    continue;
                }

                if (pos == Positions.Below_C && data.Syllable.MyanmarCategory != MyanmarCategories.A)
                {
                    pos = Positions.After_Sub;
                    data.Syllable.IndicPosition = pos;
                    continue;
                }

                data.Syllable.IndicPosition = pos;
            }
        }

        buffer.Sort(start, end, (a, b) =>
        {
            int pa = (int)a.Syllable.IndicPosition;
            int pb = (int)b.Syllable.IndicPosition;
            return pa - pb;
        });

        // Flip left-matra sequence.
        int firstLeftMatra = end;
        int lastLeftMatra = end;

        for (int i = start; i < end; i++)
        {
            if (buffer[i].Syllable.IndicPosition == Positions.Pre_M)
            {
                if (firstLeftMatra == end)
                {
                    firstLeftMatra = i;
                }

                lastLeftMatra = i;
            }
        }

        // https://github.com/harfbuzz/harfbuzz/issues/3863
        if (firstLeftMatra < lastLeftMatra)
        {
            // No need to merge clusters, done already?
            buffer.ReverseRange(firstLeftMatra, lastLeftMatra + 1);

            // Reverse back VS, etc.
            int i = firstLeftMatra;
            for (int j = i; j <= lastLeftMatra; j++)
            {
                if (buffer[j].Syllable.MyanmarCategory == MyanmarCategories.VPre)
                {
                    buffer.ReverseRange(i, j + 1);
                    i = j + 1;
                }
            }
        }
    }

    /// <summary>
    /// Determines whether the glyph data represents a Myanmar consonant.
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a consonant.</returns>
    private static bool IsConsonant(ref GlyphShapingData data)
        => data.Syllable.Type != SyllableType.None && (FlagUnsafe(data.Syllable.MyanmarCategory) & MyanmarConsonantFlags) != 0;

    /// <summary>
    /// Finds the start index of the next syllable in the buffer.
    /// </summary>
    /// <param name="buffer">The glyph substitution buffer.</param>
    /// <param name="index">The current index.</param>
    /// <param name="count">The maximum index bound.</param>
    /// <returns>The start index of the next syllable.</returns>
    private static int NextSyllable(ShapingBuffer buffer, int index, int count)
    {
        if (index >= count)
        {
            return index;
        }

        int syllable = buffer[index].Syllable.Number;
        while (++index < count)
        {
            if (buffer[index].Syllable.Number != syllable)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// Gets the Indic shaping category for a code point (upper 8 bits of the shaping properties).
    /// </summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The shaping category value.</returns>
    private static int IndicShapingCategory(CodePoint codePoint)
        => UnicodeData.GetIndicShapingProperties((uint)codePoint.Value) >> 8;

    /// <summary>
    /// Gets the Indic shaping position for a code point. The trie stores the position
    /// zero-based; adding one maps it onto the ordinal enum whose zero is the
    /// unassigned sentinel.
    /// </summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The shaping position ordinal.</returns>
    private static int IndicShapingPosition(CodePoint codePoint)
        => (UnicodeData.GetIndicShapingProperties((uint)codePoint.Value) & 0xFF) + 1;

    /// <summary>
    /// Builds a lookup table mapping Myanmar shaping category codes to compact DFA symbol indices.
    /// </summary>
    /// <returns>An array mapping category codes to symbol IDs.</returns>
    private static int[] BuildCategoryToSymbolId()
    {
        // Get all enum values in declared order (important!)
        MyanmarCategories[] values = Enum.GetValues<MyanmarCategories>();

        // Determine maximum underlying numeric category so we can index safetly
        int maxCategoryValue = 0;
        foreach (MyanmarCategories v in values)
        {
            int val = (int)v;
            if (val > maxCategoryValue)
            {
                maxCategoryValue = val;
            }
        }

        // Allocate mapping table indexed by Harfbuzz category code
        int[] map = new int[maxCategoryValue + 1];

        // Assign compact DFA symbol indices 0..N-1 in enum order
        for (int symbolId = 0; symbolId < values.Length; symbolId++)
        {
            MyanmarCategories cat = values[symbolId];
            int categoryCode = (int)cat;    // Harfbuzz-style category code
            map[categoryCode] = symbolId;   // DFA symbol id
        }

        return map;
    }
}
