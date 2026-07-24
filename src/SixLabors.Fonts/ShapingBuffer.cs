// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts;

/// <summary>
/// The shaping pipeline's glyph buffer. Glyph state lives in one flat array of
/// <see cref="GlyphShapingData"/> records with a parallel metrics stream seeded after
/// substitution, both mutated in place through interior references. Storage doubles on
/// demand, is truncated rather than released on reset, and so stays at the workload's
/// high-water mark across pooled shaping passes.
/// </summary>
/// <remarks>
/// Mutation contract: write through the indexer expression
/// (<c>buffer[i].GlyphId = x</c>), which addresses storage directly. Binding an element
/// to a local without <see langword="ref"/> copies it and silently discards writes; a
/// convention test rejects such bindings. Interior references are invalidated by any
/// operation that inserts or removes glyphs.
/// </remarks>
internal sealed class ShapingBuffer
{
    /// <summary>
    /// The flat glyph storage. Only the first <see cref="count"/> records are live;
    /// records beyond the count are stale leftovers awaiting overwrite.
    /// </summary>
    private GlyphShapingData[] data = new GlyphShapingData[64];

    /// <summary>
    /// The metrics stream, parallel to <see cref="data"/>. Entries are seeded by
    /// <see cref="TryAdd"/> and <see cref="TryUpdate"/>; substitution-phase buffers
    /// never populate it.
    /// </summary>
    private GlyphMetricsEntry[] metrics = new GlyphMetricsEntry[64];

    /// <summary>
    /// The positioning stream, parallel to <see cref="data"/>: shaping bounds,
    /// attachment links, and positioned and kerned marks. Seeded alongside the
    /// metrics stream; keeping this state out of the glyph record keeps the record
    /// narrow for the substitution walks that never touch it.
    /// </summary>
    private GlyphShapingPosition[] positions = new GlyphShapingPosition[64];

    /// <summary>
    /// The live record count.
    /// </summary>
    private int count;

    /// <summary>
    /// The approximate membership filter over every glyph id the buffer has ever
    /// contained. See <see cref="GlyphDigest"/> for the growth contract.
    /// </summary>
    private GlyphSetDigest glyphDigest;

    /// <summary>
    /// Validation tags for the direct-mapped glyph metrics cache. A slot's tag packs
    /// every key field of the font's own metrics cache above a marker bit, so a hit is
    /// one load and one compare. Zero marks a slot empty.
    /// </summary>
    private readonly ulong[] metricsCacheTags = new ulong[256];

    /// <summary>
    /// The resolved metrics for each slot of <see cref="metricsCacheTags"/>.
    /// </summary>
    private readonly FontGlyphMetrics?[] metricsCacheValues = new FontGlyphMetrics?[256];

    /// <summary>
    /// The font metrics instance the cache entries belong to. Seeding from a different
    /// font clears the cache before use.
    /// </summary>
    private FontMetrics? metricsCacheOwner;

    /// <summary>
    /// The bit offset of the encoded following codepoint in a glyph id cache entry.
    /// </summary>
    private const int GlyphIdCacheNextShift = 21;

    /// <summary>
    /// The bit offset of the resolved glyph id in a glyph id cache entry.
    /// </summary>
    private const int GlyphIdCacheGlyphShift = 43;

    /// <summary>
    /// The glyph id cache entry bit recording that the lookup found a glyph.
    /// </summary>
    private const ulong GlyphIdCacheFoundFlag = 1UL << 59;

    /// <summary>
    /// The glyph id cache entry bit recording that the following codepoint was
    /// consumed as part of a variation sequence.
    /// </summary>
    private const ulong GlyphIdCacheSkipFlag = 1UL << 60;

    /// <summary>
    /// The glyph id cache entry bit distinguishing a populated slot from an empty
    /// one, since a zero entry could otherwise read as a valid all-zero lookup.
    /// </summary>
    private const ulong GlyphIdCacheMarkerFlag = 1UL << 63;

    /// <summary>
    /// The glyph id cache entry bits forming the lookup key: the marker, the
    /// codepoint, and the encoded following codepoint.
    /// </summary>
    private const ulong GlyphIdCacheTagMask = GlyphIdCacheMarkerFlag | ((1UL << GlyphIdCacheGlyphShift) - 1);

    /// <summary>
    /// Direct-mapped codepoint-to-glyph cache: one word per slot packing the lookup
    /// key alongside the found flag, skip flag, and glyph id, so a repeat lookup is
    /// one load and one masked compare. Zero marks a slot empty.
    /// </summary>
    private readonly ulong[] glyphIdCacheEntries = new ulong[256];

    /// <summary>
    /// The font metrics instance the glyph id cache entries belong to. Populating
    /// from a different font clears the cache before use.
    /// </summary>
    private FontMetrics? glyphIdCacheOwner;

    /// <summary>
    /// The bit offset of the packed class props word in a shaping class cache entry.
    /// </summary>
    private const int ShapingClassCachePropsShift = 16;

    /// <summary>
    /// The shaping class cache entry bit distinguishing a populated slot from an
    /// empty one, since a zero entry could otherwise read as a valid all-zero lookup.
    /// </summary>
    private const ulong ShapingClassCacheMarkerFlag = 1UL << 63;

    /// <summary>
    /// The shaping class cache entry bits forming the lookup key: the marker and the
    /// glyph id.
    /// </summary>
    private const ulong ShapingClassCacheTagMask = ShapingClassCacheMarkerFlag | ((1UL << ShapingClassCachePropsShift) - 1);

    /// <summary>
    /// Direct-mapped glyph-id-to-class cache: one word per slot packing the glyph id
    /// key alongside the packed class props word, so a repeat classification is one
    /// load and one masked compare instead of a class definition table walk. Only
    /// table-derived classes enter the cache; the codepoint fallback classification
    /// depends on record state and stays out. Zero marks a slot empty.
    /// </summary>
    private readonly ulong[] shapingClassCacheEntries = new ulong[256];

    /// <summary>
    /// The font metrics instance the shaping class cache entries belong to.
    /// Populating from a different font clears the cache before use.
    /// </summary>
    private FontMetrics? shapingClassCacheOwner;

    /// <summary>
    /// The bidi runs recorded for inline placeholders, keyed by codepoint offset.
    /// Placeholder state lives here rather than on every glyph record because only
    /// placeholders carry a bidi run of their own, and only the copy-out reads it.
    /// </summary>
    private readonly List<(int CodePointIndex, BidiRun Run)> placeholderBidiRuns = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ShapingBuffer"/> class.
    /// </summary>
    /// <param name="textOptions">The text options.</param>
    /// <param name="featureMap">The feature bit assignment shared by the shaping pass.</param>
    /// <param name="role">The shaping phase this buffer serves.</param>
    public ShapingBuffer(TextOptions textOptions, ShapingFeatureMap featureMap, ShapingBufferRole role)
    {
        this.TextOptions = textOptions;
        this.FeatureMap = featureMap;
        this.Role = role;
        this.LanguageTags = ResolveLanguageTags(textOptions);
    }

    /// <summary>
    /// Gets the shaping phase this buffer serves. Shapers gate phase-specific work,
    /// such as syllable analysis and reordering, on the substitution role.
    /// </summary>
    public ShapingBufferRole Role { get; private set; }

    /// <summary>
    /// Gets the number of live glyph records. Substitution can leave this greater or
    /// smaller than the input codepoint count.
    /// </summary>
    public int Count => this.count;

    /// <summary>
    /// Gets the text options used by this buffer.
    /// </summary>
    public TextOptions TextOptions { get; private set; }

    /// <summary>
    /// Gets the candidate OpenType language system tags resolved from
    /// <see cref="TextOptions.Culture"/>, most specific first, or an empty array when
    /// the culture expresses no language preference. Resolved once per shaping pass.
    /// </summary>
    public Tag[] LanguageTags { get; private set; }

    /// <summary>
    /// Gets the feature bit assignment shared by every buffer of the shaping pass.
    /// See <see cref="ShapingFeatureMap"/> for the mask model and why the instance must
    /// be shared across the substitution and positioning phases.
    /// </summary>
    public ShapingFeatureMap FeatureMap { get; }

    /// <summary>
    /// Gets the approximate membership filter over every glyph id the buffer has ever
    /// contained. The digest only grows: substituted-away ids remain, keeping a
    /// definitive negative from <see cref="GlyphSetDigest.MightIntersect"/> sound while
    /// lookups mutate the buffer mid-application.
    /// </summary>
    public GlyphSetDigest GlyphDigest => this.glyphDigest;

    /// <summary>
    /// Gets or sets the running id of any ligature glyphs contained within this buffer.
    /// </summary>
    public int LigatureId { get; set; } = 1;

    /// <summary>
    /// Gets the text runs covering the pass's input. Records store run indices into
    /// this list; the substitution and positioning buffers of a pass must share one
    /// list so the indices agree when records are seeded across buffers.
    /// </summary>
    public IReadOnlyList<TextRun> TextRuns { get; private set; } = Array.Empty<TextRun>();

    /// <summary>
    /// Gets the shaping segments recorded during substitution: each script segment's
    /// final range, script, and the shaper that planned it. The in-place positioning
    /// pass reuses these so one plan drives both tables; the list stays empty when
    /// records were seeded across buffers and positioning must segment for itself.
    /// </summary>
    public List<(int Index, int Count, ScriptClass Script, Tables.AdvancedTypographic.Shapers.BaseShaper Shaper)> SegmentShapers { get; } = new();

    /// <summary>
    /// Gets the reusable scratch the substitution table uses to merge a stage group's
    /// lookups into lookup-index order. Cleared by each group merge; kept on the pooled
    /// buffer so application allocates nothing.
    /// </summary>
    public List<(Tag Feature, ushort Index, Tables.AdvancedTypographic.GSub.LookupTable LookupTable, ulong Mask)> GSubLookupScratch { get; } = new(16);

    /// <summary>
    /// Gets the reusable scratch the positioning table uses to merge a stage group's
    /// lookups into lookup-index order. Cleared by each group merge; kept on the pooled
    /// buffer so application allocates nothing.
    /// </summary>
    public List<(Tag Feature, ushort Index, Tables.AdvancedTypographic.GPos.LookupTable LookupTable, ulong Mask)> GPosLookupScratch { get; } = new(16);

    /// <summary>
    /// Gets an interior reference to the glyph shaping data at the specified index.
    /// The reference writes through to the buffer's storage and is invalidated by any
    /// operation that inserts or removes glyphs.
    /// </summary>
    /// <param name="index">The zero-based index of the record to get.</param>
    /// <returns>The <see cref="GlyphShapingData"/>.</returns>
    public ref GlyphShapingData this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref this.data[index];
    }

    /// <summary>
    /// Gets an interior reference to the metrics entry at the specified index. Valid
    /// only after the buffer has been seeded by <see cref="TryAdd"/>.
    /// </summary>
    /// <param name="index">The zero-based index of the entry to get.</param>
    /// <returns>The <see cref="GlyphMetricsEntry"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref GlyphMetricsEntry MetricsAt(int index) => ref this.metrics[index];

    /// <summary>
    /// Gets an interior reference to the positioning entry at the specified index.
    /// Valid only after the buffer has been seeded.
    /// </summary>
    /// <param name="index">The zero-based index of the entry to get.</param>
    /// <returns>The <see cref="GlyphShapingPosition"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref GlyphShapingPosition PositionAt(int index) => ref this.positions[index];

    /// <summary>
    /// Assigns the text runs for the pass. Must run on both of a pass's buffers
    /// before any glyph is added, so record run indices resolve identically across
    /// them.
    /// </summary>
    /// <param name="textRuns">The resolved text runs covering the input.</param>
    public void SetTextRuns(IReadOnlyList<TextRun> textRuns) => this.TextRuns = textRuns;

    /// <summary>
    /// Changes the shaping phase this buffer serves. The single-run fast path shapes
    /// and seeds one buffer in place, flipping it from substitution to positioning
    /// instead of copying every record into a second buffer.
    /// </summary>
    /// <param name="role">The shaping phase the buffer serves next.</param>
    public void SetRole(ShapingBufferRole role) => this.Role = role;

    /// <summary>
    /// Seeds this buffer's metrics stream in place after substitution: fetches each
    /// glyph's metrics from <paramref name="font"/>, clears the record's feature
    /// registration for the positioning pass, and starts the shaping bounds from the
    /// single-axis advance. Behaviorally the in-place equivalent of
    /// <see cref="TryAdd"/> without the cross-buffer record copy; valid only when the
    /// buffer holds no placeholders.
    /// </summary>
    /// <param name="font">The font used to resolve metrics.</param>
    /// <returns>
    /// <see langword="true"/> when every mapped codepoint resolved a real glyph;
    /// <see langword="false"/> when fallback glyphs remain for a later font pass.
    /// </returns>
    public bool SeedMetricsInPlace(Font font)
    {
        bool hasFallBacks = false;
        FontMetrics fontMetrics = font.FontMetrics;
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        ColorFontSupport colorFontSupport = this.TextOptions.ColorFontSupport;

        ulong verticalMask = this.GetVerticalFeatureMask();

        for (int i = 0; i < this.count; i++)
        {
            ref GlyphShapingData slot = ref this.data[i];
            CodePoint codePoint = slot.CodePoint;

            TextRun textRun = this.TextRuns[slot.TextRunIndex];
            TextAttributes textAttributes = textRun.TextAttributes;
            TextDecorations textDecorations = textRun.TextDecorations;

            bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode)
                || (slot.AppliedFeatureMask & verticalMask) != 0;

            FontGlyphMetrics glyphMetrics = this.GetGlyphMetricsCached(fontMetrics, codePoint, slot.GlyphId, textAttributes, textDecorations, layoutMode, colorFontSupport);

            if (glyphMetrics.GlyphType == GlyphType.Fallback && !CodePoint.IsControl(codePoint))
            {
                hasFallBacks = true;
            }

            // Feature masks persist deliberately: the in-place positioning pass
            // reuses the substitution pass's plan, whose registrations already cover
            // the positioning features.
            this.positions[i] = new(isVertical
                ? new(0, 0, 0, glyphMetrics.AdvanceHeight)
                : new(0, 0, glyphMetrics.AdvanceWidth, 0));

            this.metrics[i] = new(font, font.Size, glyphMetrics);
        }

        return !hasFallBacks;
    }

    /// <summary>
    /// Resets the buffer for reuse by a new shaping pass: adopts the new options,
    /// re-resolves the language candidates, empties the digest, and truncates the glyph
    /// count. Records are stored by value, so no per-record cleanup is required and
    /// storage is retained at its high-water mark.
    /// </summary>
    /// <param name="textOptions">The text options for the new pass.</param>
    public void Reset(TextOptions textOptions)
    {
        this.count = 0;
        this.LigatureId = 1;
        this.glyphDigest = default;
        this.placeholderBidiRuns.Clear();
        this.SegmentShapers.Clear();
        this.TextOptions = textOptions;
        this.LanguageTags = ResolveLanguageTags(textOptions);
    }

    /// <summary>
    /// Removes all glyph records while keeping the pass-wide state, so a fresh font run
    /// can populate the buffer without re-resolving options or language tags.
    /// </summary>
    public void Clear()
    {
        this.count = 0;
        this.LigatureId = 1;
        this.placeholderBidiRuns.Clear();
        this.SegmentShapers.Clear();
    }

    /// <summary>
    /// Sets the glyph id at the specified index, recording the id in
    /// <see cref="GlyphDigest"/>. Callers outside the buffer must use this rather than
    /// writing <see cref="GlyphShapingData.GlyphId"/> directly, which would leave the
    /// digest unaware of the new id.
    /// </summary>
    /// <param name="index">The zero-based index of the record.</param>
    /// <param name="glyphId">The glyph id to set.</param>
    public void SetGlyphId(int index, ushort glyphId)
    {
        this.glyphDigest.Add(glyphId);
        this.data[index].GlyphId = glyphId;
    }

    /// <summary>
    /// Adds the shaping feature to the record at the given index.
    /// </summary>
    /// <remarks>
    /// Registration only ever accumulates: adding a disabled entry for an already
    /// enabled feature must not clear the enabled bit.
    /// </remarks>
    /// <param name="index">The zero-based index of the record.</param>
    /// <param name="feature">The feature to apply.</param>
    public void AddShapingFeature(int index, TagEntry feature)
    {
        ulong mask = this.FeatureMap.GetOrAddMask(feature.Tag);
        ref GlyphShapingData item = ref this.data[index];
        item.RegisteredFeatureMask |= mask;
        if (feature.Enabled)
        {
            item.FeatureMask |= mask;
        }
    }

    /// <summary>
    /// Adds the shaping feature to every record in the given range, resolving the
    /// feature's mask bit once for the whole range. Shaper plans register each stage
    /// feature across the full run, so the per-glyph work must be a single bitwise OR.
    /// </summary>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the range.</param>
    /// <param name="feature">The feature to apply.</param>
    public void AddShapingFeatureRange(int index, int count, TagEntry feature)
    {
        ulong mask = this.FeatureMap.GetOrAddMask(feature.Tag);
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            ref GlyphShapingData item = ref this.data[i];
            item.RegisteredFeatureMask |= mask;
            if (feature.Enabled)
            {
                item.FeatureMask |= mask;
            }
        }
    }

    /// <summary>
    /// Enables a previously added shaping feature.
    /// </summary>
    /// <remarks>
    /// Intersecting with the registered mask preserves the contract that enabling a
    /// feature a shaper never added for this record is a no-op.
    /// </remarks>
    /// <param name="index">The zero-based index of the record.</param>
    /// <param name="feature">The feature to enable.</param>
    public void EnableShapingFeature(int index, Tag feature)
    {
        ref GlyphShapingData item = ref this.data[index];
        item.FeatureMask |= item.RegisteredFeatureMask & this.FeatureMap.GetMask(feature);
    }

    /// <summary>
    /// Disables a previously added shaping feature.
    /// </summary>
    /// <remarks>
    /// An unregistered tag yields a zero mask whose complement clears nothing.
    /// </remarks>
    /// <param name="index">The zero-based index of the record.</param>
    /// <param name="feature">The feature to disable.</param>
    public void DisableShapingFeature(int index, Tag feature)
    {
        ref GlyphShapingData item = ref this.data[index];
        item.FeatureMask &= ~this.FeatureMap.GetMask(feature);
    }

    /// <summary>
    /// Adds a copy of the glyph shaping data at the specified codepoint offset.
    /// </summary>
    /// <param name="data">The data to copy.</param>
    /// <param name="offset">The zero-based index within the input codepoint buffer.</param>
    public void AddGlyph(GlyphShapingData data, int offset)
    {
        this.glyphDigest.Add(data.GlyphId);
        ref GlyphShapingData slot = ref this.Append();
        slot = new(data, false);
        slot.CodePointIndex = offset;
    }

    /// <summary>
    /// Adds the glyph id and the codepoint it represents.
    /// </summary>
    /// <param name="glyphId">The id of the glyph to add.</param>
    /// <param name="codePoint">The codepoint the glyph represents.</param>
    /// <param name="direction">The resolved text direction for the codepoint.</param>
    /// <param name="textRunIndex">The index of the text run this glyph belongs to.</param>
    /// <param name="offset">The zero-based index within the input codepoint buffer.</param>
    public void AddGlyph(ushort glyphId, CodePoint codePoint, TextDirection direction, ushort textRunIndex, int offset)
    {
        this.glyphDigest.Add(glyphId);
        ref GlyphShapingData slot = ref this.Append();
        slot = new(textRunIndex)
        {
            CodePointIndex = offset,
            CodePoint = codePoint,
            Direction = direction,
            GlyphId = glyphId,
        };
    }

    /// <summary>
    /// Adds an atomic inline placeholder.
    /// </summary>
    /// <param name="codePoint">The object replacement codepoint used for Unicode processing.</param>
    /// <param name="bidiRun">The resolved bidi run for the placeholder.</param>
    /// <param name="textRunIndex">The index of the text run this placeholder belongs to.</param>
    /// <param name="offset">The zero-based index within the input codepoint buffer.</param>
    public void AddPlaceholder(CodePoint codePoint, BidiRun bidiRun, ushort textRunIndex, int offset)
    {
        ref GlyphShapingData slot = ref this.Append();
        slot = new(textRunIndex)
        {
            CodePointIndex = offset,
            CodePoint = codePoint,
            Direction = (TextDirection)bidiRun.Direction,
            GlyphId = 0,
            IsPlaceholder = true,
        };

        this.placeholderBidiRuns.Add((offset, bidiRun));
    }

    /// <summary>
    /// Gets the bidi run recorded for the placeholder at the given codepoint offset.
    /// Placeholders shape in isolated single-glyph runs, so their offsets are stable
    /// for the lifetime of the pass.
    /// </summary>
    /// <param name="codePointIndex">The placeholder's zero-based codepoint offset.</param>
    /// <returns>The recorded <see cref="BidiRun"/>, or the default when none was recorded.</returns>
    public BidiRun GetPlaceholderBidiRun(int codePointIndex)
    {
        List<(int CodePointIndex, BidiRun Run)> runs = this.placeholderBidiRuns;
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].CodePointIndex == codePointIndex)
            {
                return runs[i].Run;
            }
        }

        return default;
    }

    /// <summary>
    /// Copies the placeholder bidi run recorded at the given codepoint offset in
    /// <paramref name="source"/> into this buffer, so seeding from a workspace
    /// preserves placeholder bidi state without carrying it on every glyph record.
    /// </summary>
    /// <param name="source">The buffer to copy the recorded run from.</param>
    /// <param name="codePointIndex">The placeholder's zero-based codepoint offset.</param>
    public void CopyPlaceholderBidiRun(ShapingBuffer source, int codePointIndex)
        => this.placeholderBidiRuns.Add((codePointIndex, source.GetPlaceholderBidiRun(codePointIndex)));

    /// <summary>
    /// Moves the specified glyph to the specified position. Codepoint offsets stay
    /// bound to their slots: only the shaping state travels.
    /// </summary>
    /// <param name="fromIndex">The index to move from.</param>
    /// <param name="toIndex">The index to move to.</param>
    public void MoveGlyph(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex)
        {
            return;
        }

        GlyphShapingData[] items = this.data;
        GlyphShapingData moved = items[fromIndex];
        int targetOffset = items[toIndex].CodePointIndex;

        if (fromIndex > toIndex)
        {
            // Move item to the right
            for (int i = fromIndex; i > toIndex; i--)
            {
                int keep = items[i].CodePointIndex;
                items[i] = items[i - 1];
                items[i].CodePointIndex = keep;
            }
        }
        else
        {
            // Move item to the left
            for (int i = fromIndex; i < toIndex; i++)
            {
                int keep = items[i].CodePointIndex;
                items[i] = items[i + 1];
                items[i].CodePointIndex = keep;
            }
        }

        items[toIndex] = moved;
        items[toIndex].CodePointIndex = targetOffset;
    }

    /// <summary>
    /// Reverses the order of glyph records in the specified range.
    /// </summary>
    /// <remarks>
    /// The range is interpreted as half-open, from <paramref name="startIndex"/>
    /// (inclusive) to <paramref name="endIndex"/> (exclusive). Both indices are clamped
    /// to the valid range [0, <see cref="Count"/>]. If the resulting range contains
    /// fewer than two records, the method performs no action.
    /// </remarks>
    /// <param name="startIndex">The zero-based index at which to start reversing (inclusive).</param>
    /// <param name="endIndex">The zero-based index at which to stop reversing (exclusive).</param>
    public void ReverseRange(int startIndex, int endIndex)
    {
        int s = Math.Min(startIndex, this.count);
        int e = Math.Min(endIndex, this.count);

        if (e < s + 2)
        {
            return;
        }

        Array.Reverse(this.data, s, e - s);
    }

    /// <summary>
    /// Performs a stable sort of the glyph records by the comparison delegate.
    /// Codepoint offsets stay bound to their slots: only the shaping state is
    /// reordered.
    /// </summary>
    /// <param name="startIndex">The start index.</param>
    /// <param name="endIndex">The end index.</param>
    /// <param name="comparer">The comparison delegate.</param>
    public void Sort(int startIndex, int endIndex, Comparison<GlyphShapingData> comparer)
    {
        // Stable insertion sort using adjacent swaps. The sorted ranges are typically
        // small (syllable clusters of 2-10 glyphs), so insertion sort is optimal and
        // avoids allocations.
        GlyphShapingData[] items = this.data;
        for (int i = startIndex + 1; i < endIndex; i++)
        {
            int j = i;
            while (j > startIndex && comparer(items[j - 1], items[j]) > 0)
            {
                // Swap the records, then swap the offsets back so they keep their slots.
                (items[j], items[j - 1]) = (items[j - 1], items[j]);
                (items[j].CodePointIndex, items[j - 1].CodePointIndex) = (items[j - 1].CodePointIndex, items[j].CodePointIndex);
                j--;
            }
        }
    }

    /// <summary>
    /// Gets the glyph records matching the given codepoint offset as copies.
    /// </summary>
    /// <param name="offset">The zero-based index within the input codepoint buffer.</param>
    /// <param name="data">
    /// When this method returns, contains copies of the records associated with the
    /// specified offset, if any were found.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the buffer contains records for the specified offset;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryGetGlyphShapingDataAtOffset(int offset, [NotNullWhen(true)] out IReadOnlyList<GlyphShapingData>? data)
    {
        List<GlyphShapingData> match = [];
        for (int i = 0; i < this.count; i++)
        {
            if (this.data[i].CodePointIndex == offset)
            {
                match.Add(this.data[i]);
            }
            else if (match.Count > 0)
            {
                // Offsets, though non-sequential, are sorted, so we can stop searching.
                break;
            }
        }

        data = match;
        return match.Count > 0;
    }

    /// <summary>
    /// Performs a 1:1 replacement of a glyph id at the given position.
    /// </summary>
    /// <param name="index">The zero-based index of the record to replace.</param>
    /// <param name="glyphId">The replacement glyph id.</param>
    /// <param name="feature">The feature to apply to the record at the specified index.</param>
    public void Replace(int index, ushort glyphId, Tag feature)
    {
        this.glyphDigest.Add(glyphId);
        ref GlyphShapingData current = ref this.data[index];
        current.GlyphId = glyphId;
        current.LigatureId = 0;
        current.LigatureComponent = -1;
        current.IsSubstituted = true;
        current.AppliedFeatureMask |= this.FeatureMap.GetOrAddMask(feature);
    }

    /// <summary>
    /// Performs a 1:1 replacement of a glyph id at the given position while removing a
    /// series of records at the given positions within the sequence.
    /// </summary>
    /// <param name="index">The zero-based index of the record to replace.</param>
    /// <param name="removalIndices">The indices at which to remove records.</param>
    /// <param name="glyphId">The replacement glyph id.</param>
    /// <param name="ligatureId">The ligature id.</param>
    /// <param name="feature">The feature to apply to the record at the specified index.</param>
    public void Replace(int index, ReadOnlySpan<int> removalIndices, ushort glyphId, int ligatureId, Tag feature)
    {
        // Remove the glyphs at each index.
        int codePointCount = 0;
        CodePoint codePoint = default;
        for (int i = removalIndices.Length - 1; i >= 0; i--)
        {
            int match = removalIndices[i];
            codePointCount += this.data[match].CodePointCount;
            CodePoint currentCodePoint = this.data[match].CodePoint;
            if (!UnicodeUtility.IsDefaultIgnorableCodePoint((uint)codePoint.Value) || UnicodeUtility.ShouldRenderWhiteSpaceOnly(codePoint))
            {
                if (!CodePoint.IsZeroWidthJoiner(currentCodePoint) && !CodePoint.IsZeroWidthNonJoiner(currentCodePoint))
                {
                    codePoint = currentCodePoint;
                }
            }

            this.RemoveAt(match);
        }

        // Assign our new id at the index. The reference is taken after every removal
        // so it addresses the record's final slot.
        this.glyphDigest.Add(glyphId);
        ref GlyphShapingData current = ref this.data[index];
        if (codePoint != default)
        {
            current.CodePoint = codePoint;
        }

        current.CodePointCount += codePointCount;
        current.GlyphId = glyphId;
        current.LigatureId = ligatureId;
        current.IsLigated = true;
        current.LigatureComponent = -1;
        current.IsSubstituted = true;
        current.AppliedFeatureMask |= this.FeatureMap.GetOrAddMask(feature);
    }

    /// <summary>
    /// Performs a 1:1 replacement of a glyph id at the given position while removing a
    /// series of following records.
    /// </summary>
    /// <param name="index">The zero-based index of the record to replace.</param>
    /// <param name="count">The number of following records to remove.</param>
    /// <param name="glyphId">The replacement glyph id.</param>
    /// <param name="feature">The feature to apply to the record at the specified index.</param>
    public void Replace(int index, int count, ushort glyphId, Tag feature)
    {
        // Remove the glyphs at each index.
        int codePointCount = 0;
        CodePoint codePoint = default;
        for (int i = count; i > 0; i--)
        {
            int match = index + i;
            codePointCount += this.data[match].CodePointCount;
            CodePoint currentCodePoint = this.data[match].CodePoint;
            if (!UnicodeUtility.IsDefaultIgnorableCodePoint((uint)codePoint.Value) || UnicodeUtility.ShouldRenderWhiteSpaceOnly(codePoint))
            {
                if (!CodePoint.IsZeroWidthJoiner(currentCodePoint) && !CodePoint.IsZeroWidthNonJoiner(currentCodePoint))
                {
                    codePoint = currentCodePoint;
                }
            }

            this.RemoveAt(match);
        }

        // Assign our new id at the index. The reference is taken after every removal
        // so it addresses the record's final slot.
        this.glyphDigest.Add(glyphId);
        ref GlyphShapingData current = ref this.data[index];
        if (codePoint != default)
        {
            current.CodePoint = codePoint;
        }

        current.CodePointCount += codePointCount;
        current.GlyphId = glyphId;
        current.LigatureId = 0;
        current.LigatureComponent = -1;
        current.IsSubstituted = true;
        current.AppliedFeatureMask |= this.FeatureMap.GetOrAddMask(feature);
    }

    /// <summary>
    /// Replaces a single glyph id with a buffer of glyph ids.
    /// </summary>
    /// <param name="index">The zero-based index of the record to replace.</param>
    /// <param name="glyphIds">The buffer of replacement glyph ids.</param>
    /// <param name="feature">The feature to apply to the record at the specified index.</param>
    public void Replace(int index, ReadOnlySpan<ushort> glyphIds, Tag feature)
    {
        if (glyphIds.Length > 0)
        {
            this.glyphDigest.Add(glyphIds[0]);
            this.data[index].GlyphId = glyphIds[0];
            this.data[index].LigatureComponent = 0;
            this.data[index].IsSubstituted = true;
            this.data[index].IsDecomposed = true;

            // Add additional glyphs from the rest of the sequence. Insertion can grow
            // and shift the storage, so the mutated record is captured by value as the
            // template for the additions rather than held by reference.
            if (glyphIds.Length > 1)
            {
                GlyphShapingData template = this.data[index];
                ulong mask = this.FeatureMap.GetOrAddMask(feature);
                for (int i = 1; i < glyphIds.Length; i++)
                {
                    GlyphShapingData inserted = new(template, false)
                    {
                        GlyphId = glyphIds[i],
                        LigatureComponent = i,
                    };

                    inserted.AppliedFeatureMask |= mask;
                    this.glyphDigest.Add(glyphIds[i]);
                    this.InsertAt(++index, inserted);
                }
            }
        }
        else
        {
            // Spec disallows removal of glyphs in this manner but it's common enough practice to allow it.
            // https://github.com/MicrosoftDocs/typography-issues/issues/673
            this.RemoveAt(index);
        }
    }

    /// <summary>
    /// Inserts the shaping data at the given index, adopting the slot's codepoint offset.
    /// </summary>
    /// <param name="index">The zero-based index at which to insert.</param>
    /// <param name="data">The shaping data to insert.</param>
    public void Insert(int index, GlyphShapingData data)
    {
        data.CodePointIndex = this.data[index].CodePointIndex;
        this.InsertAt(index, data);
    }

    /// <summary>
    /// Seeds this buffer from a substituted workspace buffer: fetches each glyph's
    /// metrics from <paramref name="font"/>, seeds the record's shaping bounds with the
    /// single-axis advance so positioning starts from clean dirty-tracking, and appends
    /// record and metrics entry. Placeholders receive synthetic metrics and skip glyph
    /// lookup entirely.
    /// </summary>
    /// <param name="font">The font used to resolve metrics.</param>
    /// <param name="workspace">The substituted workspace buffer.</param>
    /// <returns>
    /// <see langword="true"/> when every mapped codepoint resolved a real glyph;
    /// <see langword="false"/> when fallback glyphs remain for a later font pass.
    /// </returns>
    public bool TryAdd(Font font, ShapingBuffer workspace)
    {
        bool hasFallBacks = false;
        FontMetrics fontMetrics = font.FontMetrics;
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        ColorFontSupport colorFontSupport = this.TextOptions.ColorFontSupport;

        ulong verticalMask = this.GetVerticalFeatureMask();

        for (int i = 0; i < workspace.count; i++)
        {
            ref GlyphShapingData source = ref workspace.data[i];
            CodePoint codePoint = source.CodePoint;
            ushort id = source.GlyphId;

            if (source.IsPlaceholder)
            {
                // Placeholders are synthetic glyphs: they need layout metrics but must not
                // go through font glyph lookup, fallback resolution, or GPOS positioning.
                this.CopyPlaceholderBidiRun(workspace, source.CodePointIndex);
                FontGlyphMetrics placeholderMetrics = PlaceholderGlyphMetrics.Create(font, this.TextRuns[source.TextRunIndex], this.TextOptions.Dpi);

                this.glyphDigest.Add(placeholderMetrics.GlyphId);
                ref GlyphShapingData placeholderSlot = ref this.Append();
                placeholderSlot = source;
                placeholderSlot.ClearFeatures();
                this.positions[this.count - 1] = new(layoutMode.IsVertical()
                    ? new(0, 0, 0, placeholderMetrics.AdvanceHeight)
                    : new(0, 0, placeholderMetrics.AdvanceWidth, 0))
                {
                    IsPositioned = true,
                };

                this.metrics[this.count - 1] = new(font, font.Size, placeholderMetrics);
                continue;
            }

            TextRun sourceRun = this.TextRuns[source.TextRunIndex];
            TextAttributes textAttributes = sourceRun.TextAttributes;
            TextDecorations textDecorations = sourceRun.TextDecorations;

            bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode)
                || (source.AppliedFeatureMask & verticalMask) != 0;

            FontGlyphMetrics glyphMetrics = this.GetGlyphMetricsCached(fontMetrics, codePoint, id, textAttributes, textDecorations, layoutMode, colorFontSupport);

            if (glyphMetrics.GlyphType == GlyphType.Fallback && !CodePoint.IsControl(codePoint))
            {
                hasFallBacks = true;
            }

            // We only want a single dimensional advance for positioning; assigning a
            // fresh bounds value starts dirty tracking clean for GPOS.
            this.glyphDigest.Add(glyphMetrics.GlyphId);
            ref GlyphShapingData slot = ref this.Append();
            slot = source;
            slot.ClearFeatures();
            this.positions[this.count - 1] = new(isVertical
                ? new(0, 0, 0, glyphMetrics.AdvanceHeight)
                : new(0, 0, glyphMetrics.AdvanceWidth, 0));

            this.metrics[this.count - 1] = new(font, font.Size, glyphMetrics);
        }

        return !hasFallBacks;
    }

    /// <summary>
    /// Replaces fallback glyphs in this buffer with glyphs shaped by a fallback font:
    /// records whose metrics resolved to real glyphs in <paramref name="workspace"/>
    /// supersede the fallback records at the same codepoint offset.
    /// </summary>
    /// <param name="font">The fallback font used to resolve metrics.</param>
    /// <param name="workspace">The substituted workspace buffer for the fallback font.</param>
    /// <returns>
    /// <see langword="true"/> when no fallback glyphs remain;
    /// <see langword="false"/> when further font passes are required.
    /// </returns>
    public bool TryUpdate(Font font, ShapingBuffer workspace)
    {
        FontMetrics fontMetrics = font.FontMetrics;
        LayoutMode layoutMode = this.TextOptions.LayoutMode;
        ColorFontSupport colorFontSupport = this.TextOptions.ColorFontSupport;
        bool hasFallBacks = false;

        ulong verticalMask = this.GetVerticalFeatureMask();

        for (int i = 0; i < this.count; i++)
        {
            if (this.metrics[i].Metrics.GlyphType != GlyphType.Fallback)
            {
                // We've already got the correct glyph.
                continue;
            }

            int offset = this.data[i].CodePointIndex;
            float pointSize = this.metrics[i].PointSize;
            if (workspace.TryGetGlyphShapingDataAtOffset(offset, out IReadOnlyList<GlyphShapingData>? replacements))
            {
                int replacementCount = 0;
                for (int j = 0; j < replacements.Count; j++)
                {
                    GlyphShapingData shape = replacements[j];
                    ushort id = shape.GlyphId;
                    CodePoint codePoint = shape.CodePoint;

                    TextRun shapeRun = this.TextRuns[shape.TextRunIndex];
                    TextAttributes textAttributes = shapeRun.TextAttributes;
                    TextDecorations textDecorations = shapeRun.TextDecorations;

                    bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode)
                        || (shape.AppliedFeatureMask & verticalMask) != 0;

                    FontGlyphMetrics glyphMetrics = this.GetGlyphMetricsCached(fontMetrics, codePoint, id, textAttributes, textDecorations, layoutMode, colorFontSupport);

                    // If the glyphs are fallbacks we don't want them as
                    // we've already captured them on the first run.
                    if (glyphMetrics.GlyphType == GlyphType.Fallback && !CodePoint.IsControl(codePoint))
                    {
                        hasFallBacks = true;
                        continue;
                    }

                    if (replacementCount == 0)
                    {
                        // There should only be a single fallback glyph at this position
                        // from the previous buffer.
                        this.RemoveAt(i);
                    }

                    // Track the number of inserted glyphs at the offset so we can
                    // correctly increment our position.
                    shape.CodePointIndex = offset;
                    shape.ClearFeatures();

                    this.glyphDigest.Add(glyphMetrics.GlyphId);
                    this.InsertAt(i + replacementCount, shape, new(font, pointSize, glyphMetrics));
                    this.positions[i + replacementCount] = new(isVertical
                        ? new(0, 0, 0, glyphMetrics.AdvanceHeight)
                        : new(0, 0, glyphMetrics.AdvanceWidth, 0));
                    replacementCount++;
                }

                if (replacementCount > 0)
                {
                    i += replacementCount - 1;
                }
            }
        }

        return !hasFallBacks;
    }

    /// <summary>
    /// Resolves a glyph id through a direct-mapped cache in front of the font's own
    /// resolver, which hashes a dictionary per lookup. A hit is one load and one
    /// masked compare. No synchronization is needed: a pooled buffer is exclusively
    /// owned for the duration of a shaping pass.
    /// </summary>
    /// <param name="fontMetrics">The font metrics to resolve against.</param>
    /// <param name="codePoint">The codepoint to look up.</param>
    /// <param name="nextCodePoint">The optional following codepoint for variation sequence matching.</param>
    /// <param name="glyphId">When this method returns, contains the glyph id if found.</param>
    /// <param name="skipNextCodePoint">When this method returns, indicates whether the following codepoint was consumed.</param>
    /// <returns><see langword="true"/> if a glyph was found.</returns>
    public bool TryGetGlyphIdCached(FontMetrics fontMetrics, CodePoint codePoint, CodePoint? nextCodePoint, out ushort glyphId, out bool skipNextCodePoint)
    {
        if (!ReferenceEquals(this.glyphIdCacheOwner, fontMetrics))
        {
            Array.Clear(this.glyphIdCacheEntries);
            this.glyphIdCacheOwner = fontMetrics;
        }

        ulong tag = GlyphIdCacheMarkerFlag
            | (uint)codePoint.Value
            | ((ulong)(uint)((nextCodePoint?.Value + 1) ?? 0) << GlyphIdCacheNextShift);

        int slot = codePoint.Value & 0xFF;
        ulong entry = this.glyphIdCacheEntries[slot];
        if ((entry & GlyphIdCacheTagMask) == tag)
        {
            glyphId = (ushort)(entry >> GlyphIdCacheGlyphShift);
            skipNextCodePoint = (entry & GlyphIdCacheSkipFlag) != 0;
            return (entry & GlyphIdCacheFoundFlag) != 0;
        }

        bool found = fontMetrics.TryGetGlyphId(codePoint, nextCodePoint, out glyphId, out skipNextCodePoint);
        this.glyphIdCacheEntries[slot] = tag
            | ((ulong)glyphId << GlyphIdCacheGlyphShift)
            | (found ? GlyphIdCacheFoundFlag : 0)
            | (skipNextCodePoint ? GlyphIdCacheSkipFlag : 0);
        return found;
    }

    /// <summary>
    /// Looks up a table-derived shaping class through a direct-mapped cache in front
    /// of the font's class definition tables, whose walks bisect range records per
    /// query. A hit is one load and one masked compare. No synchronization is needed:
    /// a pooled buffer is exclusively owned for the duration of a shaping pass.
    /// </summary>
    /// <param name="fontMetrics">The font metrics the class belongs to.</param>
    /// <param name="glyphId">The glyph id to look up.</param>
    /// <param name="shapingClass">When this method returns, contains the cached class if found.</param>
    /// <returns><see langword="true"/> if a cached class was found.</returns>
    public bool TryGetShapingClassCached(FontMetrics fontMetrics, ushort glyphId, out GlyphShapingClass shapingClass)
    {
        if (!ReferenceEquals(this.shapingClassCacheOwner, fontMetrics))
        {
            Array.Clear(this.shapingClassCacheEntries);
            this.shapingClassCacheOwner = fontMetrics;
        }

        ulong entry = this.shapingClassCacheEntries[glyphId & 0xFF];
        if ((entry & ShapingClassCacheTagMask) == (ShapingClassCacheMarkerFlag | glyphId))
        {
            shapingClass = new((ushort)(entry >> ShapingClassCachePropsShift));
            return true;
        }

        shapingClass = default;
        return false;
    }

    /// <summary>
    /// Stores a table-derived shaping class in the direct-mapped class cache. Must
    /// only be called for classes computed purely from the font's class definition
    /// tables, after <see cref="TryGetShapingClassCached"/> has established the cache
    /// owner for the same font.
    /// </summary>
    /// <param name="glyphId">The glyph id the class was computed for.</param>
    /// <param name="shapingClass">The computed class.</param>
    public void SetShapingClassCached(ushort glyphId, GlyphShapingClass shapingClass)
        => this.shapingClassCacheEntries[glyphId & 0xFF] = ShapingClassCacheMarkerFlag
            | glyphId
            | ((ulong)shapingClass.Props << ShapingClassCachePropsShift);

    /// <summary>
    /// Resolves glyph metrics through a direct-mapped cache in front of the font's own
    /// resolver. The tag packs the same key fields the font's cache hashes, so a hit
    /// replaces a dictionary probe with one load and one compare. No synchronization is
    /// needed: a pooled buffer is exclusively owned for the duration of a shaping pass.
    /// </summary>
    /// <param name="fontMetrics">The font metrics to resolve against.</param>
    /// <param name="codePoint">The code point represented by the glyph.</param>
    /// <param name="glyphId">The glyph id.</param>
    /// <param name="textAttributes">The text attributes applied to the glyph.</param>
    /// <param name="textDecorations">The text decorations applied to the glyph.</param>
    /// <param name="layoutMode">The layout mode.</param>
    /// <param name="colorFontSupport">The color font support level.</param>
    /// <returns>The resolved <see cref="FontGlyphMetrics"/>.</returns>
    private FontGlyphMetrics GetGlyphMetricsCached(
        FontMetrics fontMetrics,
        CodePoint codePoint,
        ushort glyphId,
        TextAttributes textAttributes,
        TextDecorations textDecorations,
        LayoutMode layoutMode,
        ColorFontSupport colorFontSupport)
    {
        if (!ReferenceEquals(this.metricsCacheOwner, fontMetrics))
        {
            Array.Clear(this.metricsCacheTags);
            this.metricsCacheOwner = fontMetrics;
        }

        bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(codePoint, layoutMode);
        ulong tag = (1UL << 63)
            | (uint)codePoint.Value
            | ((ulong)glyphId << 21)
            | ((ulong)(uint)textAttributes << 37)
            | ((ulong)(uint)colorFontSupport << 45)
            | ((isVertical ? 1UL : 0UL) << 49);

        int slot = glyphId & 0xFF;
        if (this.metricsCacheTags[slot] == tag)
        {
            return this.metricsCacheValues[slot]!;
        }

        FontGlyphMetrics glyphMetrics = fontMetrics.GetGlyphMetrics(codePoint, glyphId, textAttributes, textDecorations, layoutMode, colorFontSupport);
        this.metricsCacheTags[slot] = tag;
        this.metricsCacheValues[slot] = glyphMetrics;
        return glyphMetrics;
    }

    /// <summary>
    /// Marks the glyph at the specified index as positioned. Positions accumulate in
    /// the position entry's shaping bounds and are read from there by consumers, so
    /// the shared metrics instance is never mutated.
    /// </summary>
    /// <param name="index">The zero-based index of the record.</param>
    public void UpdatePosition(int index) => this.positions[index].IsPositioned = true;

    /// <summary>
    /// Adds dx and dy to the positioned advance of the glyph at the given index and id.
    /// Advances accumulate in the position entry's shaping bounds so the shared
    /// metrics instance is never mutated.
    /// </summary>
    /// <param name="fontMetrics">The font face with metrics.</param>
    /// <param name="index">The zero-based index of the record.</param>
    /// <param name="glyphId">The id of the glyph to offset.</param>
    /// <param name="dx">The delta x-advance.</param>
    /// <param name="dy">The delta y-advance.</param>
    public void Advance(FontMetrics fontMetrics, int index, ushort glyphId, short dx, short dy)
    {
        FontGlyphMetrics m = this.metrics[index].Metrics;
        if (m.GlyphId != glyphId || fontMetrics != m.FontMetrics)
        {
            return;
        }

        bool isVertical = AdvancedTypographicUtils.IsVerticalGlyph(m.CodePoint, this.TextOptions.LayoutMode)
            || (this.data[index].AppliedFeatureMask & this.GetVerticalFeatureMask()) != 0;

        // Advance heights grow downward but font-space grows upward, hence the negation.
        this.positions[index].Bounds.Width += dx;
        if (isVertical)
        {
            this.positions[index].Bounds.Height -= dy;
        }
    }

    /// <summary>
    /// Returns a value indicating whether the record at the given index should be
    /// processed by the given font's positioning pass.
    /// </summary>
    /// <param name="fontMetrics">The font face with metrics.</param>
    /// <param name="index">The zero-based index of the record.</param>
    /// <returns><see langword="true"/> if the record should be processed.</returns>
    public bool ShouldProcess(FontMetrics fontMetrics, int index)
        => !this.positions[index].IsPositioned && this.metrics[index].Metrics.FontMetrics == fontMetrics;

    /// <summary>
    /// Gets the combined mask of the three vertical alternate features. Computed from
    /// the shared feature map so it stays valid for applied bits written during
    /// substitution and read after the seed into the positioning phase.
    /// </summary>
    /// <returns>The combined mask, or zero when no vertical feature was registered.</returns>
    public ulong GetVerticalFeatureMask()
        => this.FeatureMap.GetMask(KnownFeatureTags.VerticalAlternates)
        | this.FeatureMap.GetMask(KnownFeatureTags.VerticalAlternatesForRotation)
        | this.FeatureMap.GetMask(KnownFeatureTags.VerticalKerning);

    /// <summary>
    /// Resolves the candidate OpenType language system tags for the options' culture.
    /// A null culture takes the ambient current culture; the invariant culture
    /// expresses no language preference.
    /// </summary>
    /// <param name="textOptions">The text options.</param>
    /// <returns>The candidate tags, most specific first.</returns>
    private static Tag[] ResolveLanguageTags(TextOptions textOptions)
    {
        CultureInfo culture = textOptions.Culture ?? CultureInfo.CurrentCulture;
        return OpenTypeLanguageTagMap.TryGetTags(culture, out Tag[] tags) ? tags : [];
    }

    /// <summary>
    /// Appends one record and returns an interior reference to it. The slot may hold a
    /// stale record from an earlier pass; callers overwrite it entirely. The metrics
    /// slot is grown in lockstep but left untouched.
    /// </summary>
    /// <returns>The appended record.</returns>
    private ref GlyphShapingData Append()
    {
        if (this.count == this.data.Length)
        {
            Array.Resize(ref this.data, this.data.Length * 2);
            Array.Resize(ref this.metrics, this.metrics.Length * 2);
            Array.Resize(ref this.positions, this.positions.Length * 2);
        }

        return ref this.data[this.count++];
    }

    /// <summary>
    /// Inserts one record at the given index, shifting later records right.
    /// </summary>
    /// <param name="index">The zero-based index at which to insert.</param>
    /// <param name="item">The record to insert.</param>
    private void InsertAt(int index, GlyphShapingData item)
    {
        this.InsertAt(index, item, default);
    }

    /// <summary>
    /// Inserts one record and its metrics entry at the given index, shifting later
    /// entries in both streams right.
    /// </summary>
    /// <param name="index">The zero-based index at which to insert.</param>
    /// <param name="item">The record to insert.</param>
    /// <param name="metricsEntry">The metrics entry to insert.</param>
    private void InsertAt(int index, GlyphShapingData item, GlyphMetricsEntry metricsEntry)
    {
        if (this.count == this.data.Length)
        {
            Array.Resize(ref this.data, this.data.Length * 2);
            Array.Resize(ref this.metrics, this.metrics.Length * 2);
            Array.Resize(ref this.positions, this.positions.Length * 2);
        }

        Array.Copy(this.data, index, this.data, index + 1, this.count - index);
        Array.Copy(this.metrics, index, this.metrics, index + 1, this.count - index);
        Array.Copy(this.positions, index, this.positions, index + 1, this.count - index);
        this.data[index] = item;
        this.metrics[index] = metricsEntry;
        this.positions[index] = default;
        this.count++;
    }

    /// <summary>
    /// Removes the record and metrics entry at the given index, shifting later entries
    /// left. Stale entries beyond the count are overwritten by later appends.
    /// </summary>
    /// <param name="index">The zero-based index to remove at.</param>
    private void RemoveAt(int index)
    {
        Array.Copy(this.data, index + 1, this.data, index, this.count - index - 1);
        Array.Copy(this.metrics, index + 1, this.metrics, index, this.count - index - 1);
        Array.Copy(this.positions, index + 1, this.positions, index, this.count - index - 1);
        this.count--;
    }

#pragma warning disable SA1401 // Fields exposed so callers can take interior references into buffer storage.
    /// <summary>
    /// One glyph's metrics-phase state: the resolving font, its point size, and the
    /// resolved metrics instance. Stored in a stream parallel to the glyph records.
    /// </summary>
    public struct GlyphMetricsEntry
    {
        /// <summary>
        /// The font that resolved the glyph.
        /// </summary>
        public Font Font;

        /// <summary>
        /// The font size in PT units of the font containing this glyph.
        /// </summary>
        public float PointSize;

        /// <summary>
        /// The font glyph metrics.
        /// </summary>
        public FontGlyphMetrics Metrics;

        /// <summary>
        /// Initializes a new instance of the <see cref="GlyphMetricsEntry"/> struct.
        /// </summary>
        /// <param name="font">The font that resolved the glyph.</param>
        /// <param name="pointSize">The font size in PT units.</param>
        /// <param name="metrics">The font glyph metrics.</param>
        public GlyphMetricsEntry(Font font, float pointSize, FontGlyphMetrics metrics)
        {
            this.Font = font;
            this.PointSize = pointSize;
            this.Metrics = metrics;
        }

        /// <summary>
        /// Gets the positioned horizontal advance in font design units for the paired
        /// entry: the shaping bounds value once positioning has written one, otherwise
        /// the metrics advance.
        /// </summary>
        /// <param name="position">The paired positioning entry.</param>
        /// <returns>The advance.</returns>
        public readonly ushort GetAdvanceWidth(in GlyphShapingPosition position)
            => position.Bounds.IsDirtyWH ? (ushort)position.Bounds.Width : this.Metrics.AdvanceWidth;

        /// <summary>
        /// Gets the positioned vertical advance in font design units for the paired
        /// entry: the shaping bounds value once positioning has written one, otherwise
        /// the metrics advance.
        /// </summary>
        /// <param name="position">The paired positioning entry.</param>
        /// <returns>The advance.</returns>
        public readonly ushort GetAdvanceHeight(in GlyphShapingPosition position)
            => position.Bounds.IsDirtyWH ? (ushort)position.Bounds.Height : this.Metrics.AdvanceHeight;
    }
#pragma warning restore SA1401
}
