// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SixLabors.Fonts.Tables.AdvancedTypographic.GSub;
using SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// The Glyph Substitution (GSUB) table provides data for substitution of glyphs for appropriate rendering of scripts,
/// such as cursively-connecting forms in Arabic script, or for advanced typographic effects, such as ligatures.
/// <see href="https://docs.microsoft.com/en-us/typography/opentype/spec/gsub"/>
/// </summary>
internal class GSubTable : Table
{
    /// <summary>
    /// Caches resolved feature lookups per stage feature, script, and language
    /// candidates. See <see cref="TryGetFeatureLookups"/> for the variable-font bypass
    /// and the read-only contract on cached lists.
    /// </summary>
    private readonly ConcurrentDictionary<FeatureLookupsKey, List<(Tag Feature, ushort Index, LookupTable LookupTable)>> featureLookupsCache = new();

    /// <summary>
    /// The OpenType table tag for the GSUB table.
    /// </summary>
    public const string TableName = "GSUB";

    /// <summary>
    /// The invalid but widely shipped language system record tag 'dflt'.
    /// </summary>
    private static readonly Tag DefaultLangSysTag = Tag.Parse("dflt");

    /// <summary>
    /// Initializes a new instance of the <see cref="GSubTable"/> class.
    /// </summary>
    /// <param name="scriptList">The script list table, or <see langword="null"/> if not present.</param>
    /// <param name="featureList">The feature list table.</param>
    /// <param name="lookupList">The lookup list table.</param>
    /// <param name="featureVariations">The feature variations table for variable fonts, or <see langword="null"/>.</param>
    public GSubTable(ScriptList? scriptList, FeatureListTable featureList, LookupListTable lookupList, FeatureVariationsTable? featureVariations = null)
    {
        this.ScriptList = scriptList;
        this.FeatureList = featureList;
        this.LookupList = lookupList;
        this.FeatureVariations = featureVariations;
    }

    /// <summary>
    /// Gets the script list table, or <see langword="null"/> if not present.
    /// </summary>
    public ScriptList? ScriptList { get; }

    /// <summary>
    /// Gets the feature list table.
    /// </summary>
    public FeatureListTable FeatureList { get; }

    /// <summary>
    /// Gets the lookup list table containing all substitution lookups.
    /// </summary>
    public LookupListTable LookupList { get; }

    /// <summary>
    /// Gets the feature variations table for variable fonts, or <see langword="null"/> if not present.
    /// </summary>
    public FeatureVariationsTable? FeatureVariations { get; }

    /// <summary>
    /// Loads the <see cref="GSubTable"/> from the font reader.
    /// </summary>
    /// <param name="fontReader">The font reader.</param>
    /// <returns>The <see cref="GSubTable"/>, or <see langword="null"/> if not present.</returns>
    public static GSubTable? Load(FontReader fontReader)
    {
        if (!fontReader.TryGetReaderAtTablePosition(TableName, out BigEndianBinaryReader? binaryReader))
        {
            return null;
        }

        using (binaryReader)
        {
            return Load(binaryReader);
        }
    }

    /// <summary>
    /// Loads the <see cref="GSubTable"/> from a big endian binary reader.
    /// </summary>
    /// <param name="reader">The big endian binary reader.</param>
    /// <returns>The <see cref="GSubTable"/>.</returns>
    public static GSubTable Load(BigEndianBinaryReader reader)
    {
        // GSUB Header, Version 1.0
        // +----------+-------------------+-----------------------------------------------------------+
        // | Type     | Name              | Description                                               |
        // +==========+===================+===========================================================+
        // | uint16   | majorVersion      | Major version of the GSUB table, = 1                      |
        // +----------+-------------------+-----------------------------------------------------------+
        // | uint16   | minorVersion      | Minor version of the GSUB table, = 0                      |
        // +----------+-------------------+-----------------------------------------------------------+
        // | Offset16 | scriptListOffset  | Offset to ScriptList table, from beginning of GSUB table  |
        // +----------+-------------------+-----------------------------------------------------------+
        // | Offset16 | featureListOffset | Offset to FeatureList table, from beginning of GSUB table |
        // +----------+-------------------+-----------------------------------------------------------+
        // | Offset16 | lookupListOffset  | Offset to LookupList table, from beginning of GSUB table  |
        // +----------+-------------------+-----------------------------------------------------------+

        // GSUB Header, Version 1.1
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | Type     | Name                    | Description                                                                   |
        // +==========+=========================+===============================================================================+
        // | uint16   | majorVersion            | Major version of the GSUB table, = 1                                          |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | uint16   | minorVersion            | Minor version of the GSUB table, = 1                                          |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | Offset16 | scriptListOffset        | Offset to ScriptList table, from beginning of GSUB table                      |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | Offset16 | featureListOffset       | Offset to FeatureList table, from beginning of GSUB table                     |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | Offset16 | lookupListOffset        | Offset to LookupList table, from beginning of GSUB table                      |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        // | Offset32 | featureVariationsOffset | Offset to FeatureVariations table, from beginning of GSUB table (may be NULL) |
        // +----------+-------------------------+-------------------------------------------------------------------------------+
        ushort majorVersion = reader.ReadUInt16();
        ushort minorVersion = reader.ReadUInt16();

        ushort scriptListOffset = reader.ReadOffset16();
        ushort featureListOffset = reader.ReadOffset16();
        ushort lookupListOffset = reader.ReadOffset16();
        uint featureVariationsOffset = (minorVersion == 1) ? reader.ReadOffset32() : 0;

        // TODO: Optimization. Allow only reading the scriptList.
        ScriptList? scriptList = ScriptList.Load(reader, scriptListOffset);

        FeatureListTable featureList = FeatureListTable.Load(reader, featureListOffset);

        LookupListTable lookupList = LookupListTable.Load(reader, lookupListOffset);

        FeatureVariationsTable? featureVariations = featureVariationsOffset != 0
            ? FeatureVariationsTable.Load(reader, featureVariationsOffset, featureList)
            : null;

        return new GSubTable(scriptList, featureList, lookupList, featureVariations);
    }

    /// <summary>
    /// Applies glyph substitution to the buffer using GSUB lookup rules.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph substitution buffer.</param>
    public void ApplySubstitution(FontMetrics fontMetrics, ShapingBuffer buffer)
    {
        // Set max constraints to prevent OutOfMemoryException or infinite loops from attacks.
        int maxCount = AdvancedTypographicUtils.GetMaxAllowableShapingCollectionCount(buffer.Count);
        int maxOperationsCount = AdvancedTypographicUtils.GetMaxAllowableShapingOperationsCount(buffer.Count);
        int currentOperations = 0;

        for (int i = 0; i < buffer.Count; i++)
        {
            // Choose a shaper based on the script.
            // This determines which features to apply to which glyphs.
            ScriptClass current = this.GetScriptClass(CodePoint.GetScriptClass(buffer[i].CodePoint));

            int index = i;
            int count = 1;
            while (i < buffer.Count - 1)
            {
                // We want to assign the same feature lookups to individual sections of the text rather
                // than the text as a whole to ensure that different language shapers do not interfere
                // with each other when the text contains multiple languages.
                ScriptClass next = this.GetScriptClass(CodePoint.GetScriptClass(buffer[i + 1].CodePoint));
                if (next != current &&
                    current is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited &&
                    next is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited)
                {
                    break;
                }

                if (current is ScriptClass.Common or ScriptClass.Unknown or ScriptClass.Inherited)
                {
                    current = next;
                }

                i++;
                count++;

                if (i >= maxCount)
                {
                    break;
                }
            }

            Tag unicodeScriptTag = this.GetUnicodeScriptTag(current);
            var createProbe = ShapingProbe.Enter();
            BaseShaper shaper = buffer.GetOrCreateShaper(current, unicodeScriptTag, fontMetrics);
            ShapingProbe.Exit(ShapingProbe.SubShaperCreate, createProbe);

            // Plan substitution features for each glyph.
            // Shapers can adjust the count during initialization and feature processing so we must capture
            // the current count to allow resetting indexes and processing counts.
            int collectionCount = buffer.Count;
            var planProbe = ShapingProbe.Enter();
            shaper.Plan(buffer, index, count);
            ShapingProbe.Exit(ShapingProbe.SubShaperPlan, planProbe);
            int delta = buffer.Count - collectionCount;
            i += delta;
            count += delta;

            // Stages are applied in pause-delimited groups: a stage action is a
            // synchronization point, and between two actions every registered
            // feature's lookups apply together in lookup-list order, the order the
            // specification defines for lookups within a single application pass. A
            // lookup registered by several of the group's features applies once with
            // their glyph masks combined.
            List<ShapingStage> stages = shaper.GetShapingStages();
            SkippingGlyphIterator iterator = new(fontMetrics, buffer, index, default, 0);
            List<(Tag Feature, ushort Index, LookupTable LookupTable, ulong Mask)> merged = buffer.GSubLookupScratch;

            int stageIndex = 0;
            while (stageIndex < stages.Count)
            {
                collectionCount = buffer.Count;
                var preProbe = ShapingProbe.Enter();
                stages[stageIndex].PreProcessFeature(buffer, index, count);
                ShapingProbe.Exit(ShapingProbe.SubStagePrePost, preProbe);

                // Account for substitutions changing the length of the buffer.
                delta = buffer.Count - collectionCount;
                count += delta;
                i += delta;

                // Extend the group while its interior holds no actions: a post action
                // closes the group after its stage and a pre action opens a new one.
                int groupEnd = stageIndex;
                while (true)
                {
                    groupEnd++;
                    if (stages[groupEnd - 1].HasPostAction || groupEnd >= stages.Count || stages[groupEnd].HasPreAction)
                    {
                        break;
                    }
                }

                // Merge the group's lookups into lookup-index order. Insertion keeps
                // the scratch sorted; a lookup already present from another feature
                // gains that feature's mask instead of a second entry.
                merged.Clear();
                for (int s = stageIndex; s < groupEnd; s++)
                {
                    Tag featureTag = stages[s].FeatureTag;
                    var lookupProbe = ShapingProbe.Enter();
                    bool found = this.TryGetFeatureLookups(fontMetrics, in featureTag, current, buffer, out List<(Tag Feature, ushort Index, LookupTable LookupTable)>? lookups);
                    ShapingProbe.Exit(ShapingProbe.LookupResolve, lookupProbe);
                    if (!found || lookups is null)
                    {
                        continue;
                    }

                    ulong mask = buffer.FeatureMap.GetMask(featureTag);
                    foreach ((Tag Feature, ushort Index, LookupTable LookupTable) featureLookup in lookups)
                    {
                        int insertAt = merged.Count;
                        bool alreadyMerged = false;
                        while (insertAt > 0)
                        {
                            (Tag Feature, ushort Index, LookupTable LookupTable, ulong Mask) prior = merged[insertAt - 1];
                            if (prior.Index == featureLookup.Index)
                            {
                                merged[insertAt - 1] = (prior.Feature, prior.Index, prior.LookupTable, prior.Mask | mask);
                                alreadyMerged = true;
                                break;
                            }

                            if (prior.Index < featureLookup.Index)
                            {
                                break;
                            }

                            insertAt--;
                        }

                        if (!alreadyMerged)
                        {
                            merged.Insert(insertAt, (featureLookup.Feature, featureLookup.Index, featureLookup.LookupTable, mask));
                        }
                    }
                }

                var applyProbe = ShapingProbe.Enter();
                this.ApplyMergedLookups(
                    fontMetrics,
                    buffer,
                    ref iterator,
                    merged,
                    index,
                    ref count,
                    ref i,
                    ref collectionCount,
                    maxCount,
                    maxOperationsCount,
                    ref currentOperations);
                ShapingProbe.Exit(ShapingProbe.SubStageApply, applyProbe);

                collectionCount = buffer.Count;
                var postProbe = ShapingProbe.Enter();
                stages[groupEnd - 1].PostProcessFeature(buffer, index, count);
                ShapingProbe.Exit(ShapingProbe.SubStagePrePost, postProbe);

                // Account for substitutions changing the length of the buffer.
                delta = buffer.Count - collectionCount;
                count += delta;
                i += delta;

                stageIndex = groupEnd;
            }

            // Record the segment with its post-substitution range so the in-place
            // positioning pass can reuse the shaper and its plan; one plan then
            // drives both tables.
            buffer.SegmentShapers.Add((index, count, current, shaper));
        }
    }

    /// <summary>
    /// Applies a stage group's merged lookups to the glyph substitution buffer in
    /// lookup-index order. Each entry's mask combines every group feature that
    /// registered the lookup, so the per-glyph gate stays a single bitwise AND.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph substitution buffer.</param>
    /// <param name="iterator">The skipping glyph iterator.</param>
    /// <param name="merged">The group's lookups, sorted by lookup index.</param>
    /// <param name="index">The starting index in the buffer.</param>
    /// <param name="count">The number of glyphs to process (updated by substitutions).</param>
    /// <param name="i">The outer loop index (updated by substitutions).</param>
    /// <param name="collectionCount">The tracked buffer count (updated by substitutions).</param>
    /// <param name="maxCount">The maximum allowable buffer count.</param>
    /// <param name="maxOperationsCount">The maximum allowable operations count.</param>
    /// <param name="currentOperations">The current operations counter.</param>
    private void ApplyMergedLookups(
        FontMetrics fontMetrics,
        ShapingBuffer buffer,
        ref SkippingGlyphIterator iterator,
        List<(Tag Feature, ushort Index, LookupTable LookupTable, ulong Mask)> merged,
        int index,
        ref int count,
        ref int i,
        ref int collectionCount,
        int maxCount,
        int maxOperationsCount,
        ref int currentOperations)
    {
        for (int m = 0; m < merged.Count; m++)
        {
            (Tag feature, ushort _, LookupTable featureLookupTable, ulong featureMask) = merged[m];

            // Skip the whole lookup when its coverage cannot intersect any glyph id
            // the buffer has ever contained; most fonts carry many lookups for
            // glyphs a given text never produces.
            if (ShapingProbe.Enabled)
            {
                ShapingProbe.LookupsConsidered++;
            }

            if (!featureLookupTable.Digest.MightIntersect(buffer.GlyphDigest))
            {
                if (ShapingProbe.Enabled)
                {
                    ShapingProbe.LookupsSkippedByDigest++;
                }

                continue;
            }

            iterator.Reset(index, featureLookupTable.LookupFlags, featureLookupTable.MarkFilteringSet);
            long featureStart = ShapingProbe.Timestamp();
            long featureApplies = 0;

            while (iterator.Index < index + count)
            {
                if (buffer.Count >= maxCount || currentOperations++ >= maxOperationsCount)
                {
                    return;
                }

                if (ShapingProbe.Enabled)
                {
                    ShapingProbe.GlyphGateChecks++;
                }

                // The digest cheaply rejects glyphs no subtable of this lookup can
                // affect; a maybe falls through to the exact coverage test inside.
                ref GlyphShapingData glyphData = ref buffer[iterator.Index];
                if ((glyphData.FeatureMask & featureMask) == 0 || !featureLookupTable.Digest.MightContain(glyphData.GlyphId))
                {
                    iterator.Next();
                    continue;
                }

                if (ShapingProbe.Enabled)
                {
                    ShapingProbe.SubstitutionAttempts++;
                }

                collectionCount = buffer.Count;
                featureLookupTable.TrySubstitution(fontMetrics, this, buffer, feature, iterator.Index, count - (iterator.Index - index));
                featureApplies++;
                iterator.Next();

                // Account for substitutions changing the length of the buffer.
                int delta = buffer.Count - collectionCount;
                count += delta;
                i += delta;
            }

            ShapingProbe.ExitFeature("GSUB", feature, featureStart, featureApplies);
        }
    }

    /// <summary>
    /// Tries to get the feature lookups for the given stage feature, script, and the
    /// buffer's language candidates.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="stageFeature">The feature tag for the current shaping stage.</param>
    /// <param name="script">The script class.</param>
    /// <param name="buffer">
    /// The glyph shaping buffer carrying the language candidates and the per-pass
    /// resolution cache.
    /// </param>
    /// <param name="value">When this method returns, contains the list of feature lookups if found.</param>
    /// <returns><see langword="true"/> if lookups were found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetFeatureLookups(
        FontMetrics fontMetrics,
        in Tag stageFeature,
        ScriptClass script,
        ShapingBuffer buffer,
        [NotNullWhen(true)] out List<(Tag Feature, ushort Index, LookupTable LookupTable)>? value)
    {
        if (this.ScriptList is null)
        {
            value = null;
            return false;
        }

        Tag[] languageTags = buffer.LanguageTags;

        // Feature variations resolve against the font's live variation coordinates, so
        // caching would mix results across differently configured variable fonts.
        if (this.FeatureVariations is not null)
        {
            value = this.ResolveFeatureLookups(fontMetrics, stageFeature, script, languageTags);
            return value.Count > 0;
        }

        // The buffer fronts the table cache with a direct-mapped cache whose hit is
        // one load and one compare, skipping the dictionary probe that hashes the
        // language candidates per query.
        if (buffer.TryGetFeatureLookupsCached(this, stageFeature, script, out object? cached))
        {
            value = (List<(Tag Feature, ushort Index, LookupTable LookupTable)>)cached!;
            return value.Count > 0;
        }

        // Resolution depends only on this table's data for a given feature, script,
        // and language candidates, so results, including empty ones, are cached for
        // the table's lifetime. The cached list is shared: consumers must not mutate
        // it. A concurrent first-resolution race only duplicates deterministic work.
        FeatureLookupsKey key = new(stageFeature, script, languageTags);
        if (!this.featureLookupsCache.TryGetValue(key, out value))
        {
            value = this.ResolveFeatureLookups(fontMetrics, stageFeature, script, languageTags);
            this.featureLookupsCache.TryAdd(key, value);
        }

        buffer.SetFeatureLookupsCached(stageFeature, script, value);
        return value.Count > 0;
    }

    /// <summary>
    /// Resolves the feature lookups for the given stage feature, script, and language
    /// through the selection ladder documented inline.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="stageFeature">The feature tag for the current shaping stage.</param>
    /// <param name="script">The script class.</param>
    /// <param name="languageTags">The candidate OpenType language system tags, most specific first.</param>
    /// <returns>The resolved lookups; empty when the feature yields none.</returns>
    private List<(Tag Feature, ushort Index, LookupTable LookupTable)> ResolveFeatureLookups(
        FontMetrics fontMetrics,
        Tag stageFeature,
        ScriptClass script,
        Tag[] languageTags)
    {
        // Resolve feature substitutions from FeatureVariations (variable fonts).
        FeatureTableSubstitutionRecord[]? substitutions = this.FeatureVariations
            ?.FindMatchingSubstitutions(fontMetrics.GetNormalizedCoordinates());

        // Step 1: script selection. Map the Unicode script class onto the font's
        // script table, falling back to the font's first script when the font does not
        // declare the script.
        // The caching entry point rejects a null script list before dispatching here.
        ScriptListTable scriptListTable = this.ScriptList!.Default();
        Tag[] tags = UnicodeScriptTagMap.Instance[script];
        for (int i = 0; i < tags.Length; i++)
        {
            if (this.ScriptList.TryGetValue(tags[i].Value, out ScriptListTable? table))
            {
                scriptListTable = table;
                break;
            }
        }

        // Step 2: language selection. Walk the culture's candidate tags in priority
        // order, most specific first, scanning the script's named language systems for
        // a tag match; the first candidate the font declares wins, so a zh-HK run
        // selects ZHH before ZHT. A match commits even when the selected language
        // system lacks this stage feature: per the specification a LangSys table is the
        // complete feature set for its language, so falling through to the default
        // below would merge two languages' features, exactly the output language
        // systems exist to prevent. An empty candidate array skips this step entirely.
        LangSysTable[] langSysTables = scriptListTable.LangSysTables;
        for (int i = 0; i < languageTags.Length; i++)
        {
            uint language = languageTags[i].Value;
            for (int j = 0; j < langSysTables.Length; j++)
            {
                if (langSysTables[j].LangSysTag == language)
                {
                    return this.GetFeatureLookups(stageFeature, substitutions, langSysTables[j]);
                }
            }
        }

        // Step 3: no culture, or no candidate the font declares. A language system
        // record explicitly tagged dflt is preferred over the true default: the tag is
        // invalid per the specification, but fonts built from old documentation typos
        // carry one, and the reference engines honor it.
        LangSysTable[] langSysRecords = scriptListTable.LangSysTables;
        for (int i = 0; i < langSysRecords.Length; i++)
        {
            if (langSysRecords[i].LangSysTag == DefaultLangSysTag.Value)
            {
                return this.GetFeatureLookups(stageFeature, substitutions, langSysRecords[i]);
            }
        }

        LangSysTable? defaultLangSysTable = scriptListTable.DefaultLangSysTable;
        if (defaultLangSysTable != null)
        {
            return this.GetFeatureLookups(stageFeature, substitutions, defaultLangSysTable);
        }

        // Step 4: no default language system either. Nothing applies: the font scoped
        // every feature to specific languages, and the reference engines agree that no
        // language system means no lookups. Features such as SimSun's vertical
        // alternates, which live only under its Chinese language systems, are reached by
        // setting TextOptions.Culture to a Chinese culture.
        return [];
    }

    /// <summary>
    /// Gets the OpenType script tag for the given script class, checking against the font's ScriptList.
    /// </summary>
    /// <param name="script">The script class.</param>
    /// <returns>The matching script tag, or default if not found.</returns>
    private Tag GetUnicodeScriptTag(ScriptClass script)
    {
        if (this.ScriptList is null)
        {
            return default;
        }

        Tag[] tags = UnicodeScriptTagMap.Instance[script];
        for (int i = 0; i < tags.Length; i++)
        {
            if (this.ScriptList.TryGetValue(tags[i].Value, out ScriptListTable? _))
            {
                return tags[i];
            }
        }

        return default;
    }

    /// <summary>
    /// Gets the feature lookups for the given stage feature from the specified language system tables.
    /// </summary>
    /// <param name="stageFeature">The feature tag for the current shaping stage.</param>
    /// <param name="substitutions">Optional feature table substitutions from FeatureVariations.</param>
    /// <param name="langSysTables">The language system tables to search.</param>
    /// <returns>A sorted list of feature lookups.</returns>
    private List<(Tag Feature, ushort Index, LookupTable LookupTable)> GetFeatureLookups(
        in Tag stageFeature,
        FeatureTableSubstitutionRecord[]? substitutions,
        params LangSysTable[] langSysTables)
    {
        List<(Tag Feature, ushort Index, LookupTable LookupTable)> lookups = [];
        for (int i = 0; i < langSysTables.Length; i++)
        {
            ushort[] featureIndices = langSysTables[i].FeatureIndices;
            for (int j = 0; j < featureIndices.Length; j++)
            {
                ushort featureIndex = featureIndices[j];
                FeatureTable featureTable = ResolveFeatureTable(this.FeatureList, featureIndex, substitutions);
                Tag feature = featureTable.FeatureTag;

                if (stageFeature != feature)
                {
                    continue;
                }

                ushort[] lookupListIndices = featureTable.LookupListIndices;
                for (int k = 0; k < lookupListIndices.Length; k++)
                {
                    ushort lookupIndex = lookupListIndices[k];
                    LookupTable lookupTable = this.LookupList.LookupTables[lookupIndex];
                    lookups.Add(new(feature, lookupIndex, lookupTable));
                }
            }
        }

        lookups.Sort((x, y) => x.Index - y.Index);
        return lookups;
    }

    /// <summary>
    /// Resolves the feature table for the given index, checking for substitutions from FeatureVariations first.
    /// </summary>
    /// <param name="featureList">The feature list table.</param>
    /// <param name="featureIndex">The feature index.</param>
    /// <param name="substitutions">Optional feature table substitutions from FeatureVariations.</param>
    /// <returns>The resolved feature table.</returns>
    private static FeatureTable ResolveFeatureTable(
        FeatureListTable featureList,
        ushort featureIndex,
        FeatureTableSubstitutionRecord[]? substitutions)
    {
        if (substitutions is not null)
        {
            for (int i = 0; i < substitutions.Length; i++)
            {
                if (substitutions[i].FeatureIndex == featureIndex)
                {
                    return substitutions[i].AlternateFeatureTable;
                }
            }
        }

        return featureList.FeatureTables[featureIndex];
    }

    /// <summary>
    /// Maps a script class to an effective script class, checking whether the font supports it.
    /// Falls back to <see cref="ScriptClass.Default"/> if the script is not present in the font.
    /// </summary>
    /// <param name="current">The script class to check.</param>
    /// <returns>The effective script class.</returns>
    private ScriptClass GetScriptClass(ScriptClass current)
    {
        if (current is ScriptClass.Common or ScriptClass.Unknown or ScriptClass.Inherited)
        {
            return current;
        }

        if (this.ScriptList is null)
        {
            return ScriptClass.Default;
        }

        Tag[] tags = UnicodeScriptTagMap.Instance[current];

        for (int i = 0; i < tags.Length; i++)
        {
            if (this.ScriptList.TryGetValue(tags[i].Value, out ScriptListTable? _))
            {
                return current;
            }
        }

        // Script for `current` not present in the font: use default shaper.
        return ScriptClass.Default;
    }
}
