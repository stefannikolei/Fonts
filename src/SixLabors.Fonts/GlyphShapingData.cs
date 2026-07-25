// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Diagnostics;
using SixLabors.Fonts.Tables.AdvancedTypographic;
using SixLabors.Fonts.Unicode;
using static SixLabors.Fonts.Unicode.Resources.IndicShapingData;

namespace SixLabors.Fonts;

/// <summary>
/// Contains supplementary data that allows the shaping of glyphs. Stored by value in
/// the shaping buffer's flat storage; call sites mutate through the buffer indexer so
/// writes land in place.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
internal struct GlyphShapingData
{
#pragma warning disable SA1401 // Fields exposed so shaping mutates embedded values in place.
    /// <summary>
    /// The syllable classification assigned by the complex-script shapers, stored by
    /// value so classification never allocates. A <see cref="SyllableInfo.Type"/> of
    /// <see cref="SyllableType.None"/> means no classification has been assigned.
    /// </summary>
    public SyllableInfo Syllable;
#pragma warning restore SA1401

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsLigated"/>.
    /// </summary>
    private const ushort LigatedFlag = 1 << 0;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsSubstituted"/>.
    /// </summary>
    private const ushort SubstitutedFlag = 1 << 1;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsDecomposed"/>.
    /// </summary>
    private const ushort DecomposedFlag = 1 << 2;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsPlaceholder"/>.
    /// </summary>
    private const ushort PlaceholderFlag = 1 << 3;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsDefaultIgnorable"/>.
    /// </summary>
    private const ushort DefaultIgnorableFlag = 1 << 4;

    /// <summary>
    /// The <see cref="flags"/> bit recording <see cref="IsHidden"/>.
    /// </summary>
    private const ushort HiddenFlag = 1 << 5;

    /// <summary>
    /// The <see cref="flags"/> bit recording that <see cref="shapingClassCacheId"/>
    /// holds the glyph id <see cref="CachedShapingClass"/> was computed for. A default
    /// record therefore reports an invalid cache.
    /// </summary>
    private const ushort ShapingClassCacheValidFlag = 1 << 6;

    /// <summary>
    /// The modulus folding ligature ids into the stored byte range 1..255, keeping a
    /// live id distinct from the zero not-a-ligature value. Id equality is only ever
    /// compared between a mark and its neighbouring ligature, where folded ids stay
    /// unique.
    /// </summary>
    private const int LigatureIdModulus = byte.MaxValue;

    private ushort glyphId;

    /// <summary>
    /// The ligature id folded into byte range, or zero when the glyph is not a
    /// ligature.
    /// </summary>
    private byte ligatureId;

    /// <summary>
    /// The ligature component index stored as component + 1, so the default record
    /// encodes the -1 no-component sentinel as zero.
    /// </summary>
    private byte ligatureComponentPlusOne;

    /// <summary>
    /// Packed boolean shaping state addressed through the named flag constants above.
    /// Single bits keep the record narrow; the properties are the only readers and
    /// writers.
    /// </summary>
    private ushort flags;

    /// <summary>
    /// The glyph id the cached shaping class was computed for; meaningful only while
    /// the cache-valid flag bit is set.
    /// </summary>
    private ushort shapingClassCacheId;

    /// <summary>
    /// The text direction, stored as a byte to keep the record narrow.
    /// </summary>
    private byte direction;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingData"/> struct. The
    /// feature masks are seeded with the shared global bit: global features apply to
    /// every glyph by definition, so their registration never walks glyph ranges and
    /// every record is born carrying the bit their lookups gate on.
    /// </summary>
    /// <param name="textRunIndex">The index of the text run this glyph belongs to.</param>
    public GlyphShapingData(ushort textRunIndex)
    {
        this.TextRunIndex = textRunIndex;
        this.RegisteredFeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
        this.FeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GlyphShapingData"/> struct.
    /// </summary>
    /// <param name="data">The data to copy properties from.</param>
    /// <param name="clearFeatures">Whether to clear features.</param>
    public GlyphShapingData(GlyphShapingData data, bool clearFeatures = false)
    {
        this.GlyphId = data.GlyphId;
        this.CodePointIndex = data.CodePointIndex;
        this.CodePoint = data.CodePoint;
        this.CodePointCount = data.CodePointCount;
        this.Direction = data.Direction;
        this.TextRunIndex = data.TextRunIndex;
        this.LigatureId = data.LigatureId;
        this.IsLigated = data.IsLigated;
        this.LigatureComponent = data.LigatureComponent;
        this.IsSubstituted = data.IsSubstituted;
        this.IsDecomposed = data.IsDecomposed;
        this.IsPlaceholder = data.IsPlaceholder;
        this.IsDefaultIgnorable = data.IsDefaultIgnorable;
        this.IsHidden = data.IsHidden;

        this.Syllable = data.Syllable;

        if (!clearFeatures)
        {
            this.RegisteredFeatureMask = data.RegisteredFeatureMask;
            this.FeatureMask = data.FeatureMask;
        }
        else
        {
            this.RegisteredFeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
            this.FeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
        }

        this.AppliedFeatureMask = data.AppliedFeatureMask;

        this.CachedShapingClass = data.CachedShapingClass;
        this.ShapingClassCacheKey = data.ShapingClassCacheKey;
    }

    /// <summary>
    /// Gets or sets the glyph id. Setting this value invalidates the cached shaping class.
    /// </summary>
    public ushort GlyphId
    {
        get => this.glyphId;
        set
        {
            if (this.glyphId != value)
            {
                this.glyphId = value;
                this.ShapingClassCacheKey = -1;
            }
        }
    }

    /// <summary>
    /// Gets or sets the cached glyph shaping class, avoiding repeated GDEF lookups.
    /// </summary>
    public GlyphShapingClass CachedShapingClass { get; set; }

    /// <summary>
    /// Gets or sets the cache key for <see cref="CachedShapingClass"/>.
    /// A value of <c>-1</c> indicates the cache is invalid. Valid entries store the glyph id.
    /// </summary>
    public int ShapingClassCacheKey
    {
        readonly get => (this.flags & ShapingClassCacheValidFlag) != 0 ? this.shapingClassCacheId : -1;
        set
        {
            if (value < 0)
            {
                this.flags = (ushort)(this.flags & ~ShapingClassCacheValidFlag);
            }
            else
            {
                this.shapingClassCacheId = (ushort)value;
                this.flags = (ushort)(this.flags | ShapingClassCacheValidFlag);
            }
        }
    }

    /// <summary>
    /// Gets or sets the zero-based index within the input codepoint collection of the
    /// leading codepoint this glyph represents.
    /// </summary>
    public int CodePointIndex { get; set; }

    /// <summary>
    /// Gets or sets the leading codepoint.
    /// </summary>
    public CodePoint CodePoint { get; set; }

    /// <summary>
    /// Gets or sets the codepoint count represented by this glyph.
    /// </summary>
    public int CodePointCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the text direction.
    /// </summary>
    public TextDirection Direction
    {
        readonly get => (TextDirection)this.direction;
        set => this.direction = (byte)value;
    }

    /// <summary>
    /// Gets or sets the index of the text run this glyph belongs to, resolved against
    /// the buffer's run list. An index keeps the record free of object references, so
    /// the garbage collector never scans the pooled glyph arrays.
    /// </summary>
    public ushort TextRunIndex { get; set; }

    /// <summary>
    /// Gets or sets the id of any ligature this glyph is a member of. Zero means the
    /// glyph is not a ligature member; assigned ids fold into the stored byte range
    /// while remaining distinct from zero.
    /// </summary>
    public int LigatureId
    {
        readonly get => this.ligatureId;
        set => this.ligatureId = value == 0 ? (byte)0 : (byte)(((value - 1) % LigatureIdModulus) + 1);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the glyph is ligated.
    /// </summary>
    public bool IsLigated
    {
        readonly get => (this.flags & LigatedFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | LigatedFlag) : (ushort)(this.flags & ~LigatedFlag);
    }

    /// <summary>
    /// Gets or sets the ligature component index of the glyph, or -1 when the glyph
    /// is not a ligature component. Stored offset by one so the default record holds
    /// the -1 sentinel; indices clamp to the storable range, far beyond any real
    /// font's component count.
    /// </summary>
    public int LigatureComponent
    {
        readonly get => this.ligatureComponentPlusOne - 1;
        set => this.ligatureComponentPlusOne = (byte)(Math.Min(value, byte.MaxValue - 1) + 1);
    }

    /// <summary>
    /// Gets or sets the mask of features a shaper has registered for this glyph, enabled
    /// or not. Bits are assigned by the owning plan's
    /// <see cref="Tables.AdvancedTypographic.ShapePlanFeatures"/>. Enabling a feature
    /// only ever reveals a registered bit; a feature that was never registered for the
    /// glyph cannot be enabled.
    /// </summary>
    public uint RegisteredFeatureMask { get; set; }

    /// <summary>
    /// Gets or sets the mask of features currently enabled for this glyph: the subset of
    /// <see cref="RegisteredFeatureMask"/> a lookup application gate tests with a single
    /// bitwise AND.
    /// </summary>
    public uint FeatureMask { get; set; }

    /// <summary>
    /// Gets or sets the mask of features whose lookups actually changed this glyph.
    /// Only the vertical trio is recorded, in the reserved bits every plan shares,
    /// because vertical alternate detection is the sole consumer; the record
    /// therefore survives the copy into the positioning buffer regardless of which
    /// plan wrote it.
    /// </summary>
    public uint AppliedFeatureMask { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph is the result of a substitution.
    /// </summary>
    public bool IsSubstituted
    {
        readonly get => (this.flags & SubstitutedFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | SubstitutedFlag) : (ushort)(this.flags & ~SubstitutedFlag);
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph is the result of a decomposition substitution
    /// </summary>
    public bool IsDecomposed
    {
        readonly get => (this.flags & DecomposedFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | DecomposedFlag) : (ushort)(this.flags & ~DecomposedFlag);
    }

    /// <summary>
    /// Gets or sets a value indicating whether this glyph represents an inline placeholder.
    /// A placeholder's bidi run lives on the buffer, keyed by codepoint offset.
    /// </summary>
    public bool IsPlaceholder
    {
        readonly get => (this.flags & PlaceholderFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | PlaceholderFlag) : (ushort)(this.flags & ~PlaceholderFlag);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the glyph's codepoint is a default
    /// ignorable that renders invisibly. Classified once as the record enters the
    /// buffer; the carve-outs that render as regular spacing glyphs, such as the
    /// Hangul fillers, never receive the bit.
    /// </summary>
    public bool IsDefaultIgnorable
    {
        readonly get => (this.flags & DefaultIgnorableFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | DefaultIgnorableFlag) : (ushort)(this.flags & ~DefaultIgnorableFlag);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the hide stage replaced this glyph
    /// with the invisible glyph at zero advance. Consumers read this recorded
    /// decision instead of re-deriving it from the codepoint.
    /// </summary>
    public bool IsHidden
    {
        readonly get => (this.flags & HiddenFlag) != 0;
        set => this.flags = value ? (ushort)(this.flags | HiddenFlag) : (ushort)(this.flags & ~HiddenFlag);
    }

    private string DebuggerDisplay
        => FormattableString
        .Invariant($" {this.GlyphId} : {this.CodePoint.ToDebuggerDisplay()} : {CodePoint.GetScriptClass(this.CodePoint)} : {this.Direction} : run {this.TextRunIndex} : {this.LigatureId} : {this.LigatureComponent} : {this.IsDecomposed}");

    /// <summary>
    /// Resets the registered and enabled feature masks to the seeded global bit
    /// while preserving the applied mask, matching the semantics of copying with
    /// cleared features. Positioning reuses the substituted glyph data and re-plans
    /// its own varying features, but the applied record of what substitution did
    /// must survive for consumers such as vertical alternate detection.
    /// </summary>
    public void ClearFeatures()
    {
        this.RegisteredFeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
        this.FeatureMask = Tables.AdvancedTypographic.ShapePlanFeatures.GlobalFeatureMask;
    }

    public string ToDebuggerDisplay() => this.DebuggerDisplay;
}
