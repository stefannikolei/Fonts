// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;
using UnicodeTrieGenerator.StateAutomation;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// This shaper is an implementation of the Universal Shaping Engine, which
/// uses Unicode data to shape a number of scripts without a dedicated shaping engine.
/// <see href="https://www.microsoft.com/typography/OpenTypeDev/USE/intro.htm"/>.
/// </summary>
internal sealed class UniversalShaper : DefaultShaper
{
    /// <summary>
    /// The generated category name table, captured once: the generated property
    /// allocates a fresh array on every access.
    /// </summary>
    private static readonly string[] CategoryNames = UniversalShapingData.Categories;

    /// <summary>
    /// Symbol indices for the categories compared during reordering, resolved once from
    /// the generated table so per-glyph category tests are integer comparisons. The
    /// symbol index is the value the state machine consumes, so glyphs never
    /// materialize category name strings.
    /// </summary>
    private static readonly int CategoryB = Array.IndexOf(CategoryNames, "B");

    private static readonly int CategoryGB = Array.IndexOf(CategoryNames, "GB");

    private static readonly int CategoryH = Array.IndexOf(CategoryNames, "H");

    private static readonly int CategoryHVM = Array.IndexOf(CategoryNames, "HVM");

    private static readonly int CategoryIS = Array.IndexOf(CategoryNames, "IS");

    private static readonly int CategoryR = Array.IndexOf(CategoryNames, "R");

    private static readonly int CategoryVPre = Array.IndexOf(CategoryNames, "VPre");

    private static readonly int CategoryVMPre = Array.IndexOf(CategoryNames, "VMPre");

    /// <summary>
    /// The state machine for Universal Shaping Engine syllable identification.
    /// </summary>
    private static readonly StateMachine StateMachine =
        new(UniversalShapingData.StateTable, UniversalShapingData.AcceptingStates, UniversalShapingData.Tags);

    /// <summary>
    /// The syllable type for each machine state, translated from the tag rows once so
    /// match handling never maps rule name strings.
    /// </summary>
    private static readonly SyllableType[] StateSyllableTypes =
        SyllableTypeMap.FromMachineTags(UniversalShapingData.Tags);

    /// <summary>
    /// The 'rphf' (reph forms) feature tag.
    /// </summary>
    private static readonly Tag RphfTag = Tag.Parse("rphf");

    /// <summary>
    /// The 'nukt' (nukta forms) feature tag.
    /// </summary>
    private static readonly Tag NuktTag = Tag.Parse("nukt");

    /// <summary>
    /// The 'akhn' (akhands) feature tag.
    /// </summary>
    private static readonly Tag AkhnTag = Tag.Parse("akhn");

    /// <summary>
    /// The 'pref' (pre-base forms) feature tag.
    /// </summary>
    private static readonly Tag PrefTag = Tag.Parse("pref");

    /// <summary>
    /// The 'rkrf' (rakar forms) feature tag.
    /// </summary>
    private static readonly Tag RkrfTag = Tag.Parse("rkrf");

    /// <summary>
    /// The 'abvf' (above-base forms) feature tag.
    /// </summary>
    private static readonly Tag AbvfTag = Tag.Parse("abvf");

    /// <summary>
    /// The 'blwf' (below-base forms) feature tag.
    /// </summary>
    private static readonly Tag BlwfTag = Tag.Parse("blwf");

    /// <summary>
    /// The 'half' (half forms) feature tag.
    /// </summary>
    private static readonly Tag HalfTag = Tag.Parse("half");

    /// <summary>
    /// The 'pstf' (post-base forms) feature tag.
    /// </summary>
    private static readonly Tag PstfTag = Tag.Parse("pstf");

    /// <summary>
    /// The 'vatu' (vattu variants) feature tag.
    /// </summary>
    private static readonly Tag VatuTag = Tag.Parse("vatu");

    /// <summary>
    /// The 'cjct' (conjunct forms) feature tag.
    /// </summary>
    private static readonly Tag CjctTag = Tag.Parse("cjct");

    /// <summary>
    /// The 'abvs' (above-base substitutions) feature tag.
    /// </summary>
    private static readonly Tag AbvsTag = Tag.Parse("abvs");

    /// <summary>
    /// The 'blws' (below-base substitutions) feature tag.
    /// </summary>
    private static readonly Tag BlwsTag = Tag.Parse("blws");

    /// <summary>
    /// The 'pres' (pre-base substitutions) feature tag.
    /// </summary>
    private static readonly Tag PresTag = Tag.Parse("pres");

    /// <summary>
    /// The 'psts' (post-base substitutions) feature tag.
    /// </summary>
    private static readonly Tag PstsTag = Tag.Parse("psts");

    /// <summary>
    /// Dotted circle code point (U+25CC) used as a placeholder base.
    /// </summary>
    private const int DottedCircle = 0x25cc;

    /// <summary>
    /// The font metrics used for glyph lookups.
    /// </summary>
    private readonly FontMetrics fontMetrics;

    /// <summary>
    /// Whether any broken clusters were detected during syllable setup.
    /// </summary>
    private bool hasBrokenClusters;

    /// <summary>
    /// The syllable setup pause, converted to a delegate once so per-pass feature
    /// planning never allocates for the conversion.
    /// </summary>
    private readonly Action<ShapePlan, ShapingBuffer, int, int> setupSyllablesAction;

    /// <summary>
    /// The reorder pause, converted to a delegate once so per-pass feature
    /// planning never allocates for the conversion.
    /// </summary>
    private readonly Action<ShapePlan, ShapingBuffer, int, int> reorderAction;

    /// <summary>
    /// Initializes a new instance of the <see cref="UniversalShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="textOptions">The text options.</param>
    /// <param name="fontMetrics">The font metrics for glyph lookups.</param>
    public UniversalShaper(ScriptClass script, TextOptions textOptions, FontMetrics fontMetrics)
       : base(script, MarkZeroingMode.PreGPos, textOptions)
    {
        this.FallbackMarkPositioning = false;
        this.fontMetrics = fontMetrics;
        this.setupSyllablesAction = this.SetupSyllables;
        this.reorderAction = this.Reorder;

        // Every character comes apart first, even one the font already draws whole.
        // This shaper divides a run into syllables and moves their pieces about, so a
        // character standing for several pieces must be split before the division can
        // see them.
        this.NormalizationMode = NormalizationMode.ComposedDiacriticsNoShortCircuit;
    }

    /// <inheritdoc/>
    protected override void PreprocessText(ShapingBuffer buffer, int index, int count)
        => VowelConstraints.Insert(buffer, this.fontMetrics, this.ScriptClass, index, count);

    /// <inheritdoc/>
    protected override void PlanFeatures(ShapingBuffer buffer, int index, int count)
    {
        // A cursive script needs a mask for each form its characters can take, so
        // the pass that settles them has somewhere to record its choice.
        if (ArabicJoining.Joins(this.ScriptClass))
        {
            this.AddFeature(buffer, index, count, ArabicJoining.IsolTag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.FinaTag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.Fin2Tag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.Fin3Tag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.MediTag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.Med2Tag, ShapingFeatureFlags.ManualZwj, false, null, null);
            this.AddFeature(buffer, index, count, ArabicJoining.InitTag, ShapingFeatureFlags.ManualZwj, false, null, null);
        }

        // Default glyph pre-processing group
        this.EnableFeature(buffer, index, count, LoclTag, ShapingFeatureFlags.PerSyllable, this.setupSyllablesAction, null);
        this.EnableFeature(buffer, index, count, CcmpTag, ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, NuktTag, ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, AkhnTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);

        // Reordering group. The repha feature varies per glyph: syllable setup
        // enables it on each syllable's leading glyphs only, so a repha forms
        // there and nowhere else.
        this.AddFeature(buffer, index, count, RphfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable, false, ClearSubstitutionFlags, RecordRhpf);
        this.EnableFeature(buffer, index, count, PrefTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable, ClearSubstitutionFlags, RecordPref);

        // Orthographic unit shaping group
        this.EnableFeature(buffer, index, count, RkrfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, AbvfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, BlwfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, HalfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, PstfTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, VatuTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable);
        this.EnableFeature(buffer, index, count, CjctTag, ShapingFeatureFlags.ManualZwj | ShapingFeatureFlags.PerSyllable, null, this.reorderAction);

        // Standard topographic presentation and positional feature application
        this.EnableFeature(buffer, index, count, AbvsTag, ShapingFeatureFlags.ManualZwj);
        this.EnableFeature(buffer, index, count, BlwsTag, ShapingFeatureFlags.ManualZwj);
        this.EnableFeature(buffer, index, count, PresTag, ShapingFeatureFlags.ManualZwj);
        this.EnableFeature(buffer, index, count, PstsTag, ShapingFeatureFlags.ManualZwj);
    }

    /// <inheritdoc/>
    protected override void AssignFeatures(ShapingBuffer buffer, int index, int count)
    {
        this.DecomposeSplitVowels(buffer, index, count);

        // Several of the scripts this engine shapes are cursive, and their
        // characters take their form from the ones around them.
        if (ArabicJoining.Joins(this.ScriptClass))
        {
            ArabicJoining.Apply(buffer, index, count, this.ScriptClass, this.Features);
        }
    }

    /// <summary>
    /// Decomposes split vowels into their constituent parts if supported by the font.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void DecomposeSplitVowels(ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        FontMetrics fontMetrics = this.fontMetrics;
        Span<ushort> decompositionIds = stackalloc ushort[16];
        int end = index + count;
        for (int i = end - 1; i >= index; i--)
        {
            ref GlyphShapingData data = ref buffer[i];
            if (UniversalShapingData.Decompositions.TryGetValue(data.CodePoint.Value, out int[]? decompositions) && decompositions != null)
            {
                Span<ushort> ids = decompositionIds[..decompositions.Length];
                bool shouldDecompose = true;
                for (int j = 0; j < decompositions.Length; j++)
                {
                    if (!fontMetrics.TryGetGlyphId(new CodePoint(decompositions[j]), out ushort id))
                    {
                        shouldDecompose = false;
                        break;
                    }

                    ids[j] = id;
                }

                if (shouldDecompose)
                {
                    buffer.Replace(i, ids, KnownFeatureTags.GlyphCompositionDecomposition);
                    for (int j = 0; j < decompositions.Length; j++)
                    {
                        buffer[i + j].CodePoint = new(decompositions[j]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Identifies syllables using the Universal Shaping Engine state machine and assigns shaping info to each glyph.
    /// </summary>
    /// <param name="plan">The plan whose segment is being shaped.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void SetupSyllables(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        this.hasBrokenClusters = false;

        Span<int> values = count <= 64 ? stackalloc int[count] : new int[count];
        for (int i = index; i < index + count; i++)
        {
            CodePoint codePoint = buffer[i].CodePoint;
            values[i - index] = UnicodeData.GetUniversalShapingSymbolCount((uint)codePoint.Value);
        }

        int syllable = 0;
        uint rphfMask = this.Features.GetMask(RphfTag);
        StateMachine.MatchEnumerator match = StateMachine.EnumerateMatches(values);
        while (match.MoveNext())
        {
            ++syllable;

            // Create shaper info. The symbol index is stored directly: it is the value
            // the state machine consumes and the key into the generated name table.
            SyllableType syllableType = StateSyllableTypes[match.TagState];
            if (syllableType == SyllableType.BrokenCluster)
            {
                this.hasBrokenClusters = true;
            }

            for (int i = match.StartIndex; i <= match.EndIndex; i++)
            {
                ref GlyphShapingData data = ref buffer[i + index];
                data.Syllable.UseCategory = values[i];
                data.Syllable.Type = syllableType;
                data.Syllable.Number = syllable;
            }

            // Enable the repha feature on the syllable's leading glyphs only: a
            // repha can form there and nowhere else, so the feature stays off for
            // the rest of the syllable.
            int limit = buffer[match.StartIndex + index].Syllable.UseCategory == CategoryR
                ? 1
                : Math.Min(3, match.EndIndex - match.StartIndex);

            for (int i = match.StartIndex; i < match.StartIndex + limit; i++)
            {
                buffer.EnableShapingFeature(i + index, rphfMask);
            }
        }
    }

    /// <summary>
    /// Clears substitution flags on all glyphs in the range, preparing for the next substitution pass.
    /// </summary>
    /// <param name="plan">The plan whose segment is being shaped.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private static void ClearSubstitutionFlags(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        int end = index + count;
        for (int i = index; i < end; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            data.IsSubstituted = false;
        }
    }

    /// <summary>
    /// Records glyphs substituted by the 'rphf' feature by marking their category as repha ("R").
    /// </summary>
    /// <param name="plan">The plan whose segment is being shaped.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private static void RecordRhpf(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        int end = index + count;
        uint rphfMask = plan.Features.GetMask(RphfTag);
        for (int i = index; i < end; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            if (data.IsSubstituted && (data.FeatureMask & rphfMask) != 0)
            {
                // Mark a substituted repha.
                if (data.Syllable.Type != SyllableType.None)
                {
                    data.Syllable.UseCategory = CategoryR;
                }
            }
        }
    }

    /// <summary>
    /// Records glyphs substituted by the 'pref' feature by marking their category as pre-base vowel ("VPre").
    /// </summary>
    /// <param name="plan">The plan whose segment is being shaped.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private static void RecordPref(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        int end = index + count;
        for (int i = index; i < end; i++)
        {
            ref GlyphShapingData data = ref buffer[i];
            if (data.IsSubstituted)
            {
                // Mark a substituted pref as VPre, as they behave the same way.
                if (data.Syllable.Type != SyllableType.None)
                {
                    data.Syllable.UseCategory = CategoryVPre;
                }
            }
        }
    }

    /// <summary>
    /// Reorders glyphs within syllables, handling repha movement, pre-base vowel movement,
    /// and dotted circle insertion for broken clusters.
    /// </summary>
    /// <param name="plan">The plan whose segment is being shaped.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void Reorder(ShapePlan plan, ShapingBuffer buffer, int index, int count)
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
                            ref GlyphShapingData candidate = ref buffer[i];
                            if (candidate.Syllable.Type == SyllableType.None || candidate.Syllable.UseCategory != CategoryR)
                            {
                                break;
                            }
                        }

                        {
                            ref GlyphShapingData current = ref buffer[i];
                            glyphs[0] = current.GlyphId;
                            glyphs[1] = circleId;
                        }

                        buffer.Replace(i, glyphs, KnownFeatureTags.GlyphCompositionDecomposition);

                        // Update shaping info for newly inserted data. The insertion
                        // copied the source record, so type and syllable number are
                        // already correct; only the category changes.
                        buffer[i + 1].Syllable.UseCategory = CategoryB;

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
            ref GlyphShapingData data = ref buffer[start];

            // Only a few syllable types need reordering.
            if (data.Syllable.Type is not SyllableType.ViramaTerminatedCluster and not SyllableType.StandardCluster and not SyllableType.BrokenCluster)
            {
                // TODO: Check this. Harfbuzz seems to test more categories and returns.
                goto Increment;
            }

            // Move things forward
            if (data.Syllable.UseCategory == CategoryR && end - start > 1)
            {
                // Got a repha. Reorder it to after first base, before first halant.
                for (int i = start + 1; i < end; i++)
                {
                    ref GlyphShapingData current = ref buffer[i];
                    if (IsBase(ref current) || IsHalant(ref current))
                    {
                        // If we hit a halant, move before it; otherwise it's a base: move to it's
                        // place, and shift things in between backward.
                        if (IsHalant(ref current))
                        {
                            i--;
                        }

                        buffer.MoveGlyph(start, i);
                        break;
                    }
                }
            }

            // Move things back
            for (int i = start, j = start; i < end; i++)
            {
                ref GlyphShapingData current = ref buffer[i];

                if (IsBase(ref current) || IsHalant(ref current))
                {
                    // If we hit a halant, move after it; otherwise move to the beginning, and
                    // shift things in between forward.
                    if (IsHalant(ref current))
                    {
                        j = i + 1;
                    }
                    else
                    {
                        j = i;
                    }
                }
                else if (current.Syllable.Type != SyllableType.None
                    && (current.Syllable.UseCategory == CategoryVPre || current.Syllable.UseCategory == CategoryVMPre)
                    && current.LigatureComponent <= 0 // Only move the first component of a MultipleSubst
                    && j < i)
                {
                    buffer.MoveGlyph(i, j);
                }
            }

            Increment:
            start = end;
            end = NextSyllable(buffer, start, max);
        }
    }

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
    /// Determines whether the glyph is a halant, halant-like, or invisible stacker character.
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a halant or equivalent.</returns>
    private static bool IsHalant(ref GlyphShapingData data)
        => data.Syllable.Type != SyllableType.None
        && (data.Syllable.UseCategory == CategoryH || data.Syllable.UseCategory == CategoryHVM || data.Syllable.UseCategory == CategoryIS)
        && !data.IsLigated;

    /// <summary>
    /// Determines whether the glyph is a base consonant or generic base.
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a base.</returns>
    private static bool IsBase(ref GlyphShapingData data)
        => data.Syllable.Type != SyllableType.None
        && (data.Syllable.UseCategory == CategoryB || data.Syllable.UseCategory == CategoryGB);
}
