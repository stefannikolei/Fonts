// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Tables.AdvancedTypographic.GSub;
using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;
using UnicodeTrieGenerator.StateAutomation;
using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// The IndicShaper supports Indic scripts e.g. Devanagari, Kannada, etc.
/// </summary>
internal sealed class IndicShaper : DefaultShaper
{
    /// <summary>
    /// The state machine for Indic syllable identification.
    /// </summary>
    private static readonly StateMachine StateMachine =
        new(StateTable, AcceptingStates, Tags);

    /// <summary>
    /// The syllable type for each machine state, translated from the tag rows once so
    /// match handling never maps rule name strings.
    /// </summary>
    private static readonly SyllableType[] StateSyllableTypes =
        SyllableTypeMap.FromMachineTags(Tags);

    /// <summary>
    /// Maps Indic shaping category codes to compact DFA symbol indices.
    /// </summary>
    private static readonly int[] CategoryToSymbolId = BuildCategoryToSymbolId();

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
    /// The 'cfar' (conjunct form after Ra) feature tag.
    /// </summary>
    private static readonly Tag CfarTag = Tag.Parse("cfar");

    /// <summary>
    /// The 'init' (initial forms) feature tag.
    /// </summary>
    private static readonly Tag InitTag = Tag.Parse("init");

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
    /// The 'haln' (halant forms) feature tag.
    /// </summary>
    private static readonly Tag HalnTag = Tag.Parse("haln");

    /// <summary>
    /// The 'dist' (distances) feature tag.
    /// </summary>
    private static readonly Tag DistTag = Tag.Parse("dist");

    /// <summary>
    /// The 'abvm' (above-base mark positioning) feature tag.
    /// </summary>
    private static readonly Tag AbvmTag = Tag.Parse("abvm");

    /// <summary>
    /// The 'blwm' (below-base mark positioning) feature tag.
    /// </summary>
    private static readonly Tag BlwmTag = Tag.Parse("blwm");

    /// <summary>
    /// Dotted circle code point (U+25CC) used as a placeholder base.
    /// </summary>
    private const int DottedCircle = 0x25cc;

    /// <summary>
    /// The font metrics used for glyph lookups.
    /// </summary>
    private readonly FontMetrics fontMetrics;

    /// <summary>
    /// The script-specific shaping configuration for this Indic script.
    /// </summary>
    private ShapingConfiguration indicConfiguration;

    /// <summary>
    /// Whether this font uses old-spec Indic script tags.
    /// </summary>
    private readonly bool isOldSpec;

    /// <summary>
    /// Whether feature probes disallow matching context outside the probed glyph
    /// sequence. New-spec scripts other than Malayalam match with zero context.
    /// </summary>
    private readonly bool zeroContext;

    /// <summary>
    /// Whether any broken clusters were detected during syllable setup.
    /// </summary>
    private bool hasBrokenClusters;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndicShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="unicodeScriptTag">The Unicode script tag found in the font.</param>
    /// <param name="textOptions">The text options.</param>
    /// <param name="fontMetrics">The font metrics for glyph lookups.</param>
    public IndicShaper(ScriptClass script, Tag unicodeScriptTag, TextOptions textOptions, FontMetrics fontMetrics)
        : base(script, MarkZeroingMode.None, textOptions)
    {
        this.fontMetrics = fontMetrics;

        if (IndicConfigurations.TryGetValue(script, out ShapingConfiguration value))
        {
            this.indicConfiguration = value;
        }
        else
        {
            this.indicConfiguration = ShapingConfiguration.Default;
        }

        this.isOldSpec = this.indicConfiguration.HasOldSpec && !unicodeScriptTag.ToString().EndsWith("2", StringComparison.OrdinalIgnoreCase);
        this.zeroContext = !this.isOldSpec && script != ScriptClass.Malayalam;
    }

    /// <inheritdoc />
    protected override void PlanFeatures(ShapingBuffer buffer, int index, int count)
    {
        this.AddFeature(buffer, index, count, LoclTag, preAction: this.SetupSyllables);
        this.AddFeature(buffer, index, count, CcmpTag);

        this.AddFeature(buffer, index, count, NuktTag, preAction: this.InitialReorder);
        this.AddFeature(buffer, index, count, AkhnTag);

        this.AddFeature(buffer, index, count, RphfTag, false);
        this.AddFeature(buffer, index, count, RkrfTag);
        this.AddFeature(buffer, index, count, PrefTag, false);
        this.AddFeature(buffer, index, count, BlwfTag, false);
        this.AddFeature(buffer, index, count, AbvfTag, false);
        this.AddFeature(buffer, index, count, HalfTag, false);
        this.AddFeature(buffer, index, count, PstfTag, false);
        this.AddFeature(buffer, index, count, VatuTag);
        this.AddFeature(buffer, index, count, CjctTag);
        this.AddFeature(buffer, index, count, CfarTag, false, postAction: this.FinalReorder);

        this.AddFeature(buffer, index, count, InitTag, false);
        this.AddFeature(buffer, index, count, PresTag);
        this.AddFeature(buffer, index, count, AbvsTag);
        this.AddFeature(buffer, index, count, BlwsTag);
        this.AddFeature(buffer, index, count, PstsTag);
        this.AddFeature(buffer, index, count, HalnTag);
        this.AddFeature(buffer, index, count, DistTag);
        this.AddFeature(buffer, index, count, AbvmTag);
        this.AddFeature(buffer, index, count, BlwmTag);
    }

    /// <inheritdoc />
    protected override void AssignFeatures(ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        FontMetrics fontMetrics = this.fontMetrics;

        // Decompose split matras
        Span<ushort> decompositionIds = stackalloc ushort[16];
        int end = index + count;
        for (int i = end - 1; i >= index; i--)
        {
            ref GlyphShapingData data = ref buffer[i];
            if ((Decompositions.TryGetValue(data.CodePoint.Value, out int[]? decompositions) ||
                UniversalShapingData.Decompositions.TryGetValue(data.CodePoint.Value, out decompositions)) &&
                decompositions != null)
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
    /// Identifies Indic syllables using the state machine and assigns shaping info to each glyph.
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
            // Convert HarfBuzz-style Indic shaping categories into the compact
            // DFA symbol indices used by the generated state machine.
            //
            // HarfBuzz category codes (C=1, V=2, MR=36, VBlw=21, etc.) are sparse
            // and can be larger than the alphabet size of the DFA. Our state
            // machine expects its input alphabet to be dense 0..N-1, matching the
            // sequential IDs assigned in GenerateIndicShapingDataTrie.
            //
            // CategoryToSymbolId[IndicShapingCategory(codePoint)] performs this mapping, ensuring that
            // every codepoint is presented to the DFA using the correct compact
            // symbol index.
            CodePoint codePoint = buffer[i].CodePoint;
            values[i - index] = CategoryToSymbolId[IndicShapingCategory(codePoint)];
        }

        int syllable = 0;
        int last = 0;
        StateMachine.MatchEnumerator match = StateMachine.EnumerateMatches(values);
        while (match.MoveNext())
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

            SyllableType syllableType = StateSyllableTypes[match.TagState];
            if (syllableType == SyllableType.BrokenCluster)
            {
                this.hasBrokenClusters = true;
            }

            // Create shaper info.
            for (int i = match.StartIndex; i <= match.EndIndex; i++)
            {
                ref GlyphShapingData data = ref buffer[i + index];
                CodePoint codePoint = data.CodePoint;

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
    /// Performs the initial reordering pass for Indic syllables, including base consonant
    /// identification, reph handling, matra reordering, and feature assignment.
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

        // Reusable glyph id span for feature probes. Hoisted out of the syllable loop
        // because a stack allocation inside the loop body would grow the stack once
        // per syllable for the lifetime of the call.
        Span<ushort> probeGlyphs = stackalloc ushort[3];

        ShapingConfiguration indicConfiguration = this.indicConfiguration;
        FontMetrics fontMetrics = this.fontMetrics;
        CodePoint viramaPoint = new(indicConfiguration.Virama);

        if (fontMetrics.TryGetGlyphId(viramaPoint, out ushort viramaId))
        {
            for (int i = 0; i < count; i++)
            {
                ref GlyphShapingData data = ref buffer[i + index];

                if (data.Syllable.IndicPosition == Positions.Base_C)
                {
                    data.Syllable.IndicPosition = this.ConsonantPosition(buffer, viramaId, data.GlyphId);
                }
            }
        }

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
                        glyphs[0] = circleId;
                        glyphs[1] = current.GlyphId;

                        buffer.Replace(i, glyphs, KnownFeatureTags.GlyphCompositionDecomposition);

                        // The dotted circle is now at position i (inherits original shaping info).
                        // Update it to be a dotted circle base.
                        ref GlyphShapingData dotted = ref buffer[i];
                        dotted.Syllable.IndicCategory = Categories.Dotted_Circle;
                        dotted.Syllable.IndicPosition = Positions.End;

                        // The original mark glyph is now at position i + 1 (copy of original info).
                        // Its shaping info is already correct from the copy.
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

        _ = fontMetrics.TryGetGSubTable(out GSubTable? gSubTable);
        while (start < max)
        {
            if (buffer[start].Syllable.Type is SyllableType.SymbolCluster or SyllableType.NonIndicCluster)
            {
                goto Increment;
            }

            // 1. Find base consonant:
            //
            // The shaping engine finds the base consonant of the syllable, using the
            // following algorithm: starting from the end of the syllable, move backwards
            // until a consonant is found that does not have a below-base or post-base
            // form (post-base forms have to follow below-base forms), or that is not a
            // pre-base reordering Ra, or arrive at the first consonant. The consonant
            // stopped at will be the base.
            int basePosition = end;
            int limit = start;
            bool hasReph = false;

            // If the syllable starts with Ra + Halant (in a script that has Reph)
            // and has more than one consonant, Ra is excluded from candidates for
            // base consonants.
            if (start + 3 <= end &&
                indicConfiguration.RephPosition != Positions.Ra_To_Become_Reph &&
                gSubTable?.TryGetFeatureLookups(fontMetrics, in RphfTag, this.ScriptClass, buffer.LanguageTags, out _) == true &&
                ((indicConfiguration.RephMode == RephMode.Implicit && !IsJoiner(ref buffer[start + 2])) ||
                 (indicConfiguration.RephMode == RephMode.Explicit && buffer[start + 2].Syllable.IndicCategory == Categories.ZWJ)))
            {
                // See if it matches the 'rphf' feature.
                probeGlyphs[0] = buffer[start].GlyphId;
                probeGlyphs[1] = buffer[start + 1].GlyphId;
                probeGlyphs[2] = buffer[start + 2].GlyphId;

                if ((indicConfiguration.RephMode == RephMode.Explicit && this.WouldSubstitute(buffer, in RphfTag, probeGlyphs)) ||
                    this.WouldSubstitute(buffer, in RphfTag, probeGlyphs[..2]))
                {
                    limit += 2;
                    while (limit < end && IsJoiner(ref buffer[limit]))
                    {
                        limit++;
                    }

                    basePosition = start;
                    hasReph = true;
                }
            }
            else if (indicConfiguration.RephMode == RephMode.Log_Repha &&
                     buffer[start].Syllable.IndicCategory == Categories.Repha)
            {
                limit++;
                while (limit < end && IsJoiner(ref buffer[limit]))
                {
                    limit++;
                }

                basePosition = start;
                hasReph = true;
            }

            switch (indicConfiguration.BasePosition)
            {
                case BasePosition.Last:
                {
                    // Starting from the end of the syllable, move backwards
                    int i = end;
                    bool seenBelow = false;

                    do
                    {
                        ref GlyphShapingData prev = ref buffer[--i];

                        // Until a consonant is found
                        if (IsConsonant(ref prev))
                        {
                            // that does not have a below-base or post-base form
                            // (post-base forms have to follow below-base forms),
                            if (prev.Syllable.IndicPosition != Positions.Below_C && (prev.Syllable.IndicPosition != Positions.Post_C || seenBelow))
                            {
                                basePosition = i;
                                break;
                            }

                            // or that is not a pre-base reordering Ra,
                            //
                            // IMPLEMENTATION NOTES:
                            //
                            // Our pre-base reordering Ra's are marked POS_POST_C, so will be skipped
                            // by the logic above already.
                            //

                            // or arrive at the first consonant. The consonant stopped at will
                            // be the base.
                            if (prev.Syllable.Type != SyllableType.None && prev.Syllable.IndicPosition == Positions.Below_C)
                            {
                                seenBelow = true;
                            }

                            basePosition = i;
                        }
                        else if (start < i && prev.Syllable.IndicCategory == Categories.ZWJ && prev.Syllable.Type != SyllableType.None &&
                                 buffer[i - 1].Syllable.IndicCategory == Categories.H)
                        {
                            // A ZWJ after a Halant stops the base search, and requests an explicit
                            // half form.
                            // A ZWJ before a Halant, requests a subjoined form instead, and hence
                            // search continues.  This is particularly important for Bengali
                            // sequence Ra,H,Ya that should form Ya-Phalaa by subjoining Ya.
                            break;
                        }
                    }
                    while (i > limit);

                    break;
                }

                case BasePosition.First:
                {
                    // The first consonant is always the base.
                    basePosition = start;

                    for (int i = basePosition + 1; i < end; i++)
                    {
                        ref GlyphShapingData c = ref buffer[i];
                        if (IsConsonant(ref c))
                        {
                            c.Syllable.IndicPosition = Positions.Below_C;
                        }
                    }

                    break;
                }
            }

            // If the syllable starts with Ra + Halant (in a script that has Reph)
            // and has more than one consonant, Ra is excluded from candidates for
            // base consonants.
            //
            //  Only do this for unforced Reph. (ie. not for Ra,H,ZWJ)
            if (hasReph && basePosition == start && limit - basePosition <= 2)
            {
                hasReph = false;
            }

            // 2. Decompose and reorder Matras:
            //
            // Each matra and any syllable modifier sign in the cluster are moved to the
            // appropriate position relative to the consonant(s) in the cluster. The
            // shaping engine decomposes two- or three-part matras into their constituent
            // parts before any repositioning. Matra characters are classified by which
            // consonant in a conjunct they have affinity for and are reordered to the
            // following positions:
            //
            //   o Before first half form in the syllable
            //   o After subjoined consonants
            //   o After post-form consonant
            //   o After main consonant (for above marks)
            //
            // IMPLEMENTATION NOTES:
            //
            // The normalize() routine has already decomposed matras for us, so we don't
            // need to worry about that.

            // 3.  Reorder marks to canonical order:
            //
            // Adjacent nukta and halant or nukta and vedic sign are always repositioned
            // if necessary, so that the nukta is first.
            //
            // IMPLEMENTATION NOTES:
            //
            // We don't need to do this: the normalize() routine already did this for us.

            // Reorder characters
            for (int i = start; i < basePosition; i++)
            {
                ref GlyphShapingData item = ref buffer[i];
                if (item.Syllable.Type != SyllableType.None)
                {
                    item.Syllable.IndicPosition = (Positions)Math.Min((int)Positions.Pre_C, (int)item.Syllable.IndicPosition);
                }
            }

            if (basePosition < end)
            {
                ref GlyphShapingData item = ref buffer[basePosition];
                if (item.Syllable.Type != SyllableType.None)
                {
                    item.Syllable.IndicPosition = Positions.Base_C;
                }
            }

            // Mark final consonants.  A final consonant is one appearing after a matra,
            // like in Khmer.
            for (int i = basePosition + 1; i < end; i++)
            {
                if (buffer[i].Syllable.IndicCategory == Categories.M)
                {
                    for (int j = i + 1; j < end; j++)
                    {
                        ref GlyphShapingData c = ref buffer[j];
                        if (IsConsonant(ref c))
                        {
                            c.Syllable.IndicPosition = Positions.Final_C;
                            break;
                        }
                    }

                    break;
                }
            }

            // Handle beginning Ra
            if (hasReph)
            {
                ref GlyphShapingData c = ref buffer[start];
                if (c.Syllable.Type != SyllableType.None)
                {
                    c.Syllable.IndicPosition = Positions.Ra_To_Become_Reph;
                }
            }

            // For old-style Indic script tags, move the first post-base Halant after
            // last consonant.
            //
            // Reports suggest that in some scripts Uniscribe does this only if there
            // is *not* a Halant after last consonant already (eg. Kannada), while it
            // does it unconditionally in other scripts (eg. Malayalam).  We don't
            // currently know about other scripts, so we single out Malayalam for now.
            //
            // Kannada test case:
            // U+0C9A,U+0CCD,U+0C9A,U+0CCD
            // With some versions of Lohit Kannada.
            // https://bugs.freedesktop.org/show_bug.cgi?id=59118
            //
            // Malayalam test case:
            // U+0D38,U+0D4D,U+0D31,U+0D4D,U+0D31,U+0D4D
            // With lohit-ttf-20121122/Lohit-Malayalam.ttf
            if (this.isOldSpec)
            {
                bool disallowDoubleHalants = this.ScriptClass != ScriptClass.Malayalam;
                for (int i = basePosition + 1; i < end; i++)
                {
                    if (buffer[i].Syllable.IndicCategory == Categories.H)
                    {
                        int j;
                        for (j = end - 1; j > i; j--)
                        {
                            ref GlyphShapingData c = ref buffer[j];
                            if (IsConsonant(ref c) || (disallowDoubleHalants && c.Syllable.IndicCategory == Categories.H))
                            {
                                break;
                            }
                        }

                        if (j > i && buffer[j].Syllable.IndicCategory != Categories.H)
                        {
                            // Move Halant to after last consonant.
                            buffer.MoveGlyph(i, j);
                        }

                        break;
                    }
                }
            }

            // Attach misc marks to previous char to move with them.
            Positions lastPosition = Positions.Start;
            for (int i = start; i < end; i++)
            {
                ref GlyphShapingData item = ref buffer[i];
                if (item.Syllable.Type != SyllableType.None)
                {
                    Categories category = item.Syllable.IndicCategory;
                    if ((FlagUnsafe(category) & (JoinerFlags | Flag(Categories.N) | Flag(Categories.RS) | Flag(Categories.CM) | (HalantOrCoengFlags & FlagUnsafe(category)))) != 0)
                    {
                        item.Syllable.IndicPosition = lastPosition;
                        if (category == Categories.H && item.Syllable.IndicPosition == Positions.Pre_M)
                        {
                            // Uniscribe doesn't move the Halant with Left Matra.
                            // TEST: U+092B,U+093F,U+094DE
                            // We follow.  This is important for the Sinhala
                            // U+0DDA split matra since it decomposes to U+0DD9,U+0DCA
                            // where U+0DD9 is a left matra and U+0DCA is the virama.
                            // We don't want to move the virama with the left matra.
                            // TEST: U+0D9A,U+0DDA
                            for (int j = i; j > start; j--)
                            {
                                // An unassigned record reads as position zero, matching
                                // the previous null semantics: keep scanning.
                                Positions pos = buffer[j - 1].Syllable.IndicPosition;
                                if (pos is not 0 and not Positions.Pre_M)
                                {
                                    item.Syllable.IndicPosition = pos;
                                    break;
                                }
                            }
                        }
                    }
                    else if (item.Syllable.IndicPosition != Positions.SMVD)
                    {
                        // If an MPst follows an SM, update the SM's position to match
                        // so they move together during reordering.
                        if (category == Categories.MPst
                            && i > start
                            && buffer[i - 1].Syllable.IndicCategory == Categories.SM)
                        {
                            buffer[i - 1].Syllable.IndicPosition = item.Syllable.IndicPosition;
                        }

                        lastPosition = item.Syllable.IndicPosition;
                    }
                }
            }

            // For post-base consonants let them own anything before them
            // since the last consonant or matra.
            int last = basePosition;
            for (int i = basePosition + 1; i < end; i++)
            {
                ref GlyphShapingData current = ref buffer[i];
                if (current.Syllable.Type != SyllableType.None)
                {
                    if (IsConsonant(ref current))
                    {
                        for (int j = last + 1; j < i; j++)
                        {
                            ref GlyphShapingData between = ref buffer[j];
                            if (between.Syllable.Type != SyllableType.None && between.Syllable.IndicPosition < Positions.SMVD)
                            {
                                between.Syllable.IndicPosition = current.Syllable.IndicPosition;
                            }
                        }

                        last = i;
                    }
                    else if ((FlagUnsafe(current.Syllable.IndicCategory) & (Flag(Categories.M) | Flag(Categories.MPst))) != 0)
                    {
                        last = i;
                    }
                }
            }

            buffer.Sort(start, end, (a, b) =>
            {
                int pa = (int)a.Syllable.IndicPosition;
                int pb = (int)b.Syllable.IndicPosition;
                return pa - pb;
            });

            // Find base again
            for (int i = start; i < end; i++)
            {
                if (buffer[i].Syllable.IndicPosition == Positions.Base_C)
                {
                    basePosition = i;
                    break;
                }
            }

            // Setup features now.

            // Reph.
            for (int i = start; i < end; i++)
            {
                if (buffer[i].Syllable.IndicPosition != Positions.Ra_To_Become_Reph)
                {
                    break;
                }

                buffer.EnableShapingFeature(i, RphfTag);
            }

            // Pre-base
            bool blwf = !this.isOldSpec && indicConfiguration.BlwfMode == BlwfMode.Pre_And_Post;
            for (int i = start; i < basePosition; i++)
            {
                buffer.EnableShapingFeature(i, HalfTag);
                if (blwf)
                {
                    buffer.EnableShapingFeature(i, BlwfTag);
                }
            }

            // Post-base
            for (int i = basePosition + 1; i < end; i++)
            {
                buffer.EnableShapingFeature(i, AbvfTag);
                buffer.EnableShapingFeature(i, PstfTag);
                buffer.EnableShapingFeature(i, BlwfTag);
            }

            if (this.isOldSpec && this.ScriptClass == ScriptClass.Devanagari)
            {
                // Old-spec eye-lash Ra needs special handling.
                // From the spec:
                //
                // "The feature 'below-base form' is applied to consonants
                // having below-base forms and following the base consonant.
                // The exception is vattu, which may appear below half forms
                // as well as below the base glyph. The feature 'below-base
                // form' will be applied to all such occurrences of Ra as well."
                //
                // Test case: U+0924,U+094D,U+0930,U+094d,U+0915
                // with Sanskrit 2003 font.
                //
                // However, note that Ra,Halant,ZWJ is the correct way to
                // request eyelash form of Ra, so we wouldn't inhibit it
                // in that sequence.
                //
                // Test case: U+0924,U+094D,U+0930,U+094d,U+200D,U+0915
                for (int i = start; i + 1 < basePosition; i++)
                {
                    if (buffer[i].Syllable.IndicCategory == Categories.Ra &&
                        buffer[i + 1].Syllable.IndicCategory == Categories.H &&
                        (i + 1 == basePosition || buffer[i + 2].Syllable.IndicCategory == Categories.ZWJ))
                    {
                        buffer.EnableShapingFeature(i, BlwfTag);
                        buffer.EnableShapingFeature(i + 1, BlwfTag);
                    }
                }
            }

            const int prefLen = 2;
            if (basePosition + prefLen < end &&
                gSubTable?.TryGetFeatureLookups(fontMetrics, in PrefTag, this.ScriptClass, buffer.LanguageTags, out _) == true)
            {
                // Find a Halant,Ra sequence and mark it for pre-base reordering processing.
                for (int i = basePosition + 1; i + prefLen - 1 < end; i++)
                {
                    probeGlyphs[0] = buffer[i].GlyphId;
                    probeGlyphs[1] = buffer[i + 1].GlyphId;
                    if (this.WouldSubstitute(buffer, in PrefTag, probeGlyphs[..2]))
                    {
                        for (int j = 0; j < prefLen; j++)
                        {
                            buffer.EnableShapingFeature(i++, PrefTag);
                        }

                        // Mark the subsequent stuff with 'cfar'.  Used in Khmer.
                        // Read the feature spec.
                        // This allows distinguishing the following cases with MS Khmer fonts:
                        // U+1784,U+17D2,U+179A,U+17D2,U+1782
                        // U+1784,U+17D2,U+1782,U+17D2,U+179A
                        if (gSubTable.TryGetFeatureLookups(fontMetrics, in CfarTag, this.ScriptClass, buffer.LanguageTags, out _))
                        {
                            while (i < end)
                            {
                                buffer.EnableShapingFeature(i, CfarTag);
                                i++;
                            }
                        }

                        break;
                    }
                }
            }

            // Apply ZWJ/ZWNJ effects
            for (int i = start + 1; i < end; i++)
            {
                ref GlyphShapingData current = ref buffer[i];
                if (IsJoiner(ref current))
                {
                    bool nonJoiner = current.Syllable.IndicCategory == Categories.ZWNJ;
                    int j = i;

                    do
                    {
                        j--;

                        // ZWJ/ZWNJ should disable CJCT.  They do that by simply
                        // being there, since we don't skip them for the CJCT
                        // feature (ie. F_MANUAL_ZWJ)

                        // A ZWNJ disables HALF.
                        if (nonJoiner)
                        {
                            buffer.DisableShapingFeature(j, HalfTag);
                        }
                    }
                    while (j > start && !IsConsonant(ref buffer[j]));
                }
            }

            Increment:
            start = end;
            end = NextSyllable(buffer, start, max);
        }
    }

    /// <summary>
    /// Determines the positional class of a consonant by testing whether the
    /// virama-consonant and consonant-virama pairs would be substituted by the
    /// below-base, vattu, post-base, or pre-base forming features.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer providing the language tags.</param>
    /// <param name="virama">The virama glyph id.</param>
    /// <param name="consonant">The consonant glyph id.</param>
    /// <returns>The consonant's positional class.</returns>
    private Positions ConsonantPosition(ShapingBuffer buffer, ushort virama, ushort consonant)
    {
        Span<ushort> glyphs = stackalloc ushort[3];
        glyphs[0] = virama;
        glyphs[1] = consonant;
        glyphs[2] = virama;

        if (this.WouldSubstitute(buffer, in BlwfTag, glyphs[..2]) ||
            this.WouldSubstitute(buffer, in BlwfTag, glyphs.Slice(1, 2)) ||
            this.WouldSubstitute(buffer, in VatuTag, glyphs[..2]) ||
            this.WouldSubstitute(buffer, in VatuTag, glyphs.Slice(1, 2)))
        {
            return Positions.Below_C;
        }

        if (this.WouldSubstitute(buffer, in PstfTag, glyphs[..2]) ||
            this.WouldSubstitute(buffer, in PstfTag, glyphs.Slice(1, 2)))
        {
            return Positions.Post_C;
        }

        if (this.WouldSubstitute(buffer, in PrefTag, glyphs[..2]) ||
            this.WouldSubstitute(buffer, in PrefTag, glyphs.Slice(1, 2)))
        {
            return Positions.Post_C;
        }

        return Positions.Base_C;
    }

    /// <summary>
    /// Tests whether applying a specific feature to the given glyph sequence would
    /// produce a substitution, querying the feature's lookups directly without
    /// running any substitution.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer providing the language tags.</param>
    /// <param name="featureTag">The feature tag to test.</param>
    /// <param name="glyphs">The glyph id sequence to test.</param>
    /// <returns><see langword="true"/> if a substitution would occur.</returns>
    private bool WouldSubstitute(ShapingBuffer buffer, in Tag featureTag, ReadOnlySpan<ushort> glyphs)
    {
        if (!this.fontMetrics.TryGetGSubTable(out GSubTable? gSubTable) ||
            !gSubTable.TryGetFeatureLookups(this.fontMetrics, in featureTag, this.ScriptClass, buffer.LanguageTags, out List<(Tag Feature, ushort Index, LookupTable LookupTable)>? lookups))
        {
            return false;
        }

        foreach ((Tag _, ushort _, LookupTable lookupTable) in lookups)
        {
            if (lookupTable.WouldApply(glyphs, this.zeroContext))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the glyph data represents an Indic consonant.
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a consonant.</returns>
    private static bool IsConsonant(ref GlyphShapingData data)
        => (FlagUnsafe(data.Syllable.IndicCategory) & ConsonantFlags) != 0;

    /// <summary>
    /// Determines whether the glyph data represents a joiner (ZWJ or ZWNJ).
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a joiner.</returns>
    private static bool IsJoiner(ref GlyphShapingData data)
        => (FlagUnsafe(data.Syllable.IndicCategory) & JoinerFlags) != 0;

    /// <summary>
    /// Determines whether the glyph data represents a halant or coeng character.
    /// </summary>
    /// <param name="data">The glyph shaping data.</param>
    /// <returns><see langword="true"/> if the glyph is a halant or coeng.</returns>
    private static bool IsHalantOrCoeng(ref GlyphShapingData data)
        => (FlagUnsafe(data.Syllable.IndicCategory) & HalantOrCoengFlags) != 0;

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

        int? syllable = buffer[index].Syllable.Number;
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
    /// Performs the final reordering pass for Indic syllables, repositioning reph,
    /// pre-base consonants, and pre-base matras after basic shaping.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based start index.</param>
    /// <param name="count">The number of elements to process.</param>
    private void FinalReorder(ShapingBuffer buffer, int index, int count)
    {
        if (buffer.Role != ShapingBufferRole.Substitution)
        {
            return;
        }

        int max = index + count;
        int start = index;
        int end = NextSyllable(buffer, index, max);
        FontMetrics fontMetrics = this.fontMetrics;
        _ = fontMetrics.TryGetGSubTable(out GSubTable? gSubTable);
        while (start < max)
        {
            // 4. Final reordering:
            //
            // After the localized forms and basic shaping forms GSUB features have been
            // applied (see below), the shaping engine performs some final glyph
            // reordering before applying all the remaining font features to the entire
            // cluster.
            bool tryPref = gSubTable?.TryGetFeatureLookups(fontMetrics, in PrefTag, this.ScriptClass, buffer.LanguageTags, out _) == true;

            // Find base consonant again.
            int basePosition = start;
            for (; basePosition < end; basePosition++)
            {
                if (buffer[basePosition].Syllable.IndicPosition >= Positions.Base_C)
                {
                    if (tryPref && basePosition + 1 < end)
                    {
                        for (int i = basePosition + 1; i < end; i++)
                        {
                            ref GlyphShapingData current = ref buffer[i];
                            if ((current.FeatureMask & buffer.FeatureMap.GetMask(PrefTag)) != 0)
                            {
                                if (!current.IsSubstituted && current.IsLigated && !current.IsDecomposed)
                                {
                                    // Ok, this was a 'pref' candidate but didn't form any.
                                    // Base is around here...
                                    basePosition = i;
                                    while (basePosition < end && IsHalantOrCoeng(ref buffer[basePosition]))
                                    {
                                        basePosition++;
                                    }

                                    ref GlyphShapingData newBase = ref buffer[basePosition];
                                    if (newBase.Syllable.Type != SyllableType.None)
                                    {
                                        newBase.Syllable.IndicPosition = Positions.Base_C;
                                        tryPref = false;
                                    }
                                }

                                break;
                            }
                        }
                    }

                    // For Malayalam, skip over unformed below- (but NOT post-) forms.
                    if (this.ScriptClass == ScriptClass.Malayalam)
                    {
                        for (int i = basePosition + 1; i < end; i++)
                        {
                            while (i < end && IsJoiner(ref buffer[i]))
                            {
                                i++;
                            }

                            if (i == end || !IsHalantOrCoeng(ref buffer[i]))
                            {
                                break;
                            }

                            i++; // Skip halant.
                            while (i < end && IsJoiner(ref buffer[i]))
                            {
                                i++;
                            }

                            if (i < end)
                            {
                                ref GlyphShapingData current = ref buffer[i];
                                if (IsConsonant(ref current) && current.Syllable.IndicPosition == Positions.Below_C)
                                {
                                    basePosition = i;
                                    ref GlyphShapingData newBase = ref buffer[basePosition];
                                    if (newBase.Syllable.Type != SyllableType.None)
                                    {
                                        newBase.Syllable.IndicPosition = Positions.Base_C;
                                    }
                                }
                            }
                        }
                    }

                    if (start < basePosition && buffer[basePosition].Syllable.IndicPosition > Positions.Base_C)
                    {
                        basePosition--;
                    }

                    break;
                }
            }

            if (basePosition == end && start < basePosition && buffer[basePosition - 1].Syllable.IndicCategory == Categories.ZWJ)
            {
                basePosition--;
            }

            if (basePosition < end)
            {
                while (start < basePosition && (FlagUnsafe(buffer[basePosition].Syllable.IndicCategory) & (Flag(Categories.N) | HalantOrCoengFlags)) != 0)
                {
                    basePosition--;
                }
            }

            // o Reorder matras:
            //
            // If a pre-base matra character had been reordered before applying basic
            // features, the glyph can be moved closer to the main consonant based on
            // whether half-forms had been formed. Actual position for the matra is
            // defined as "after last standalone halant glyph, after initial matra
            // position and before the main consonant". If ZWJ or ZWNJ follow this
            // halant, position is moved after it.
            //
            // Otherwise there can't be any pre-base matra characters.
            if (start + 1 < end && start < basePosition)
            {
                // If we lost track of base, alas, position before last thingy.
                int newPos = basePosition == end ? basePosition - 2 : basePosition - 1;

                // Malayalam / Tamil do not have "half" forms or explicit virama forms.
                // The glyphs formed by 'half' are Chillus or ligated explicit viramas.
                // We want to position matra after them.
                if (this.ScriptClass is not ScriptClass.Malayalam and not ScriptClass.Tamil)
                {
                    while (newPos > start && (FlagUnsafe(buffer[newPos].Syllable.IndicCategory) & (Flag(Categories.M) | HalantOrCoengFlags)) == 0)
                    {
                        newPos--;
                    }

                    // If we found no Halant we are done.
                    // Otherwise only proceed if the Halant does
                    // not belong to the Matra itself!
                    ref GlyphShapingData current = ref buffer[newPos];
                    if (IsHalantOrCoeng(ref current) && current.Syllable.IndicPosition != Positions.Pre_M)
                    {
                        // If ZWJ or ZWNJ follow this halant, position is moved after it.
                        if (newPos + 1 < end && IsJoiner(ref buffer[newPos + 1]))
                        {
                            newPos++;
                        }
                    }
                    else
                    {
                        newPos = start; // No move.
                    }
                }

                if (start < newPos && buffer[newPos].Syllable.IndicPosition != Positions.Pre_M)
                {
                    // Now go see if there's actually any matras...
                    for (int i = newPos; i > start; i--)
                    {
                        if (buffer[i - 1].Syllable.IndicPosition == Positions.Pre_M)
                        {
                            int oldPos = i - 1;
                            if (oldPos < basePosition && basePosition <= newPos)
                            {
                                // Shouldn't actually happen.
                                basePosition--;
                            }

                            buffer.MoveGlyph(oldPos, newPos);
                            newPos--;
                        }
                    }
                }
            }

            // o Reorder reph:
            //
            // Reph’s original position is always at the beginning of the syllable,
            // (i.e. it is not reordered at the character reordering stage). However,
            // it will be reordered according to the basic-forms shaping results.
            // Possible positions for reph, depending on the script, are; after main,
            // before post-base consonant forms, and after post-base consonant forms.

            // Two cases:
            //
            // - If repha is encoded as a sequence of characters (Ra,H or Ra,H,ZWJ), then
            //   we should only move it if the sequence ligated to the repha form.
            //
            // - If repha is encoded separately and in the logical position, we should only
            //   move it if it did NOT ligate.  If it ligated, it's probably the font trying
            //   to make it work without the reordering.
            ref GlyphShapingData original = ref buffer[start];
            if (start + 1 < end &&
                original.Syllable.IndicPosition == Positions.Ra_To_Become_Reph &&
                (original.Syllable.IndicCategory == Categories.Repha != (original.IsLigated && !original.IsDecomposed)))
            {
                int newRephPos = start;
                Positions rephPos = this.indicConfiguration.RephPosition;
                bool found = false;

                // 1. If reph should be positioned after post-base consonant forms,
                //    proceed to step 5.
                if (rephPos != Positions.After_Post)
                {
                    // 2. If the reph repositioning class is not after post-base: target
                    //    position is after the first explicit halant glyph between the
                    //    first post-reph consonant and last main consonant. If ZWJ or ZWNJ
                    //    are following this halant, position is moved after it. If such
                    //    position is found, this is the target position. Otherwise,
                    //    proceed to the next step.
                    //
                    //    Note: in old-implementation fonts, where classifications were
                    //    fixed in shaping engine, there was no case where reph position
                    //    will be found on this step.
                    newRephPos = start + 1;
                    while (newRephPos < basePosition && !IsHalantOrCoeng(ref buffer[newRephPos]))
                    {
                        newRephPos++;
                    }

                    if (newRephPos < basePosition && IsHalantOrCoeng(ref buffer[newRephPos]))
                    {
                        // ->If ZWJ or ZWNJ are following this halant, position is moved after it.
                        if (newRephPos + 1 < basePosition && IsJoiner(ref buffer[newRephPos + 1]))
                        {
                            newRephPos++;
                        }

                        found = true;
                    }

                    // 3. If reph should be repositioned after the main consonant: find the
                    //    first consonant not ligated with main, or find the first
                    //    consonant that is not a potential pre-base reordering Ra.
                    if (!found && rephPos == Positions.After_Main)
                    {
                        newRephPos = basePosition;
                        while (newRephPos + 1 < end && buffer[newRephPos + 1].Syllable.IndicPosition <= Positions.After_Main)
                        {
                            newRephPos++;
                        }

                        found = newRephPos < end;
                    }

                    // 4. If reph should be positioned before post-base consonant, find
                    //    first post-base classified consonant not ligated with main. If no
                    //    consonant is found, the target position should be before the
                    //    first matra, syllable modifier sign or vedic sign.
                    //
                    // This is our take on what step 4 is trying to say (and failing, BADLY).
                    if (!found && rephPos == Positions.After_Sub)
                    {
                        newRephPos = basePosition;
                        while (newRephPos + 1 < end
                            && buffer[newRephPos + 1].Syllable.IndicPosition is not Positions.Post_C and not Positions.After_Post and not Positions.SMVD)
                        {
                            newRephPos++;
                        }

                        found = newRephPos < end;
                    }
                }

                // 5. If no consonant is found in steps 3 or 4, move reph to a position
                //    immediately before the first post-base matra, syllable modifier
                //    sign or vedic sign that has a reordering class after the intended
                //    reph position. For example, if the reordering position for reph
                //    is post-main, it will skip above-base matras that also have a
                //    post-main position.
                if (!found)
                {
                    // Copied from step 2.
                    newRephPos = start + 1;
                    while (newRephPos < basePosition && !IsHalantOrCoeng(ref buffer[newRephPos]))
                    {
                        newRephPos++;
                    }

                    if (newRephPos < basePosition && IsHalantOrCoeng(ref buffer[newRephPos]))
                    {
                        // ->If ZWJ or ZWNJ are following this halant, position is moved after it.
                        if (newRephPos + 1 < basePosition && IsJoiner(ref buffer[newRephPos + 1]))
                        {
                            newRephPos++;
                        }

                        found = true;
                    }
                }

                // 6. Otherwise, reorder reph to the end of the syllable.
                if (!found)
                {
                    newRephPos = end - 1;
                    while (newRephPos > start && buffer[newRephPos].Syllable.IndicPosition == Positions.SMVD)
                    {
                        newRephPos--;
                    }

                    // If the Reph is to be ending up after a Matra,Halant sequence,
                    // position it before that Halant so it can interact with the Matra.
                    // However, if it's a plain Consonant,Halant we shouldn't do that.
                    // Uniscribe doesn't do this.
                    // TEST: U+0930,U+094D,U+0915,U+094B,U+094D
                    if (IsHalantOrCoeng(ref buffer[newRephPos]))
                    {
                        for (int i = basePosition + 1; i < newRephPos; i++)
                        {
                            if ((FlagUnsafe(buffer[i].Syllable.IndicCategory) & Flag(Categories.M)) != 0)
                            {
                                newRephPos--;
                            }
                        }
                    }
                }

                if (newRephPos != start)
                {
                    buffer.MoveGlyph(start, newRephPos);
                }

                if (start < basePosition && basePosition <= newRephPos)
                {
                    basePosition--;
                }
            }

            // o Reorder pre-base reordering consonants:
            //
            // If a pre-base reordering consonant is found, reorder it according to
            // the following rules:
            if (tryPref && basePosition + 1 < end)
            {
                for (int i = basePosition + 1; i < end; i++)
                {
                    ref GlyphShapingData current = ref buffer[i];
                    if ((current.FeatureMask & buffer.FeatureMap.GetMask(PrefTag)) != 0)
                    {
                        // 1. Only reorder a glyph produced by substitution during application
                        //    of the <pref> feature. (Note that a font may shape a Ra consonant with
                        //    the feature generally but block it in certain contexts.)

                        // Note: We just check that something got substituted.  We don't check that
                        // the <pref> feature actually did it...
                        //
                        // Reorder pref only if it ligated.
                        if (current.IsLigated && !current.IsDecomposed)
                        {
                            // 2. Try to find a target position the same way as for pre-base matra.
                            //    If it is found, reorder pre-base consonant glyph.
                            //
                            // 3. If position is not found, reorder immediately before main
                            //    consonant.
                            int newPos = basePosition;

                            // Malayalam / Tamil do not have "half" forms or explicit virama forms.
                            // The glyphs formed by 'half' are Chillus or ligated explicit viramas.
                            // We want to position matra after them.
                            if (this.ScriptClass is not ScriptClass.Malayalam and not ScriptClass.Tamil)
                            {
                                while (newPos > start && (FlagUnsafe(buffer[newPos - 1].Syllable.IndicCategory) & (Flag(Categories.M) | HalantOrCoengFlags)) == 0)
                                {
                                    newPos--;
                                }

                                // TODO: Remove once we have Kmher shaper.
                                // In Khmer coeng model, a H,Ra can go *after* matras.  If it goes after a
                                // split matra, it should be reordered to *before* the left part of such matra.
                                if (newPos > start && buffer[newPos - 1].Syllable.IndicCategory == Categories.M)
                                {
                                    int oldPos = i;
                                    for (int j = basePosition + 1; j < oldPos; j++)
                                    {
                                        if (buffer[j].Syllable.IndicCategory == Categories.M)
                                        {
                                            newPos--;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (newPos > start && IsHalantOrCoeng(ref buffer[newPos - 1]))
                            {
                                // -> If ZWJ or ZWNJ follow this halant, position is moved after it.
                                if (newPos < end && IsJoiner(ref buffer[newPos]))
                                {
                                    newPos++;
                                }
                            }

                            buffer.MoveGlyph(i, newPos);

                            if (newPos <= basePosition && basePosition < i)
                            {
                                basePosition++;
                            }
                        }

                        break;
                    }
                }
            }

            // Apply 'init' to the Left Matra if it's a word start.
            if (buffer[start].Syllable.IndicPosition == Positions.Pre_M &&
                (start == 0 || CodePoint.GetGeneralCategory(buffer[start - 1].CodePoint) is not UnicodeCategory.NonSpacingMark and not UnicodeCategory.Format))
            {
                buffer.EnableShapingFeature(start, InitTag);
            }

            start = end;
            end = NextSyllable(buffer, start, max);
        }
    }

    /// <summary>
    /// Builds a lookup table mapping Indic shaping category codes to compact DFA symbol indices.
    /// </summary>
    /// <returns>An array mapping category codes to symbol IDs.</returns>
    private static int[] BuildCategoryToSymbolId()
    {
        // Get all enum values in declared order (important!)
        Categories[] values = Enum.GetValues<Categories>();

        // Determine maximum underlying numeric category so we can index safetly
        int maxCategoryValue = 0;
        foreach (Categories v in values)
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
            Categories cat = values[symbolId];
            int categoryCode = (int)cat;    // Harfbuzz-style category code
            map[categoryCode] = symbolId;   // DFA symbol id
        }

        return map;
    }
}
