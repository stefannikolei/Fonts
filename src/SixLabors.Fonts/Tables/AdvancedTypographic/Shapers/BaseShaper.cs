// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// Abstract base class for all script shapers. Defines the shaping pipeline
/// consisting of preprocessing, feature planning, postprocessing, and feature assignment stages.
/// </summary>
internal abstract class BaseShaper
{
    /// <summary>
    /// Gets the feature bit assignment for the plan this shaper belongs to. The
    /// shaper creates it and the owning plan adopts it, so both hold the same
    /// non-null instance for their whole lifetime; a shaper exists only inside a
    /// plan, so feature state is never reachable without one.
    /// </summary>
    public ShapePlanFeatures Features { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the first planning pass has collected the
    /// plan's feature registrations. Registration is deterministic for a plan's
    /// identity, so later passes replay the collected masks instead of
    /// re-registering.
    /// </summary>
    public bool FeaturesCollected { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the first planning pass is
    /// currently collecting registrations; whole-segment feature additions fold
    /// their masks only while this is set. Shapers clear it around additions
    /// whose ranges depend on the text, such as direction spans.
    /// </summary>
    public bool CollectingFeatures { get; protected set; }

    /// <summary>
    /// Gets or sets the registered-mask fold of every whole-segment feature the
    /// collecting pass added, replayed over later segments in one walk.
    /// </summary>
    public uint FoldedRegisteredMask { get; protected set; }

    /// <summary>
    /// Gets or sets the enabled-mask fold paired with
    /// <see cref="FoldedRegisteredMask"/>.
    /// </summary>
    public uint FoldedEnabledMask { get; protected set; }

    /// <summary>
    /// Gets the segment start the collecting pass is planning; feature additions
    /// fold only when their range is exactly the planned segment.
    /// </summary>
    public int CollectSegmentIndex { get; private set; }

    /// <summary>
    /// Gets the segment length the collecting pass is planning, tracking count
    /// adjustments between phases.
    /// </summary>
    public int CollectSegmentCount { get; private set; }

    /// <summary>
    /// Gets or sets the script classification for this shaper.
    /// </summary>
    public ScriptClass ScriptClass { get; protected set; }

    /// <summary>
    /// Gets or sets the mark zeroing mode that determines when mark advances are zeroed.
    /// </summary>
    public MarkZeroingMode MarkZeroingMode { get; protected set; }

    /// <summary>
    /// Assigns the features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    public void Plan(ShapingBuffer buffer, int index, int count)
    {
        // Registration is deterministic for a plan's identity, so the first pass
        // collects it once: feature bits, stages, joiner flags, and the fold of
        // every whole-segment mask. Later passes replay the fold in one walk and
        // run only the per-text work. A mixed-vertical layout decides features
        // from the glyphs themselves, so it re-plans every pass.
        if (!this.FeaturesCollected)
        {
            int collectionCount = buffer.Count;
            this.CollectingFeatures = true;
            this.CollectSegmentIndex = index;
            this.CollectSegmentCount = count;

            var preProbe = ShapingProbe.Enter();
            this.PlanPreprocessingFeatures(buffer, index, count);
            ShapingProbe.Exit(ShapingProbe.PlanPre, preProbe);

            RecalculateCount(buffer, ref collectionCount, ref count);
            this.CollectSegmentCount = count;

            var mainProbe = ShapingProbe.Enter();
            this.PlanFeatures(buffer, index, count);
            ShapingProbe.Exit(ShapingProbe.PlanMain, mainProbe);

            RecalculateCount(buffer, ref collectionCount, ref count);
            this.CollectSegmentCount = count;

            var postProbe = ShapingProbe.Enter();
            this.PlanPostprocessingFeatures(buffer, index, count);
            ShapingProbe.Exit(ShapingProbe.PlanPost, postProbe);

            RecalculateCount(buffer, ref collectionCount, ref count);

            this.CollectingFeatures = false;
            this.FeaturesCollected = !buffer.TextOptions.LayoutMode.IsVerticalMixed();
        }
        else
        {
            var foldProbe = ShapingProbe.Enter();
            if (this.FoldedRegisteredMask != 0)
            {
                buffer.AddShapingFeatureMasks(index, count, this.FoldedRegisteredMask, this.FoldedEnabledMask);
            }

            ShapingProbe.Exit(ShapingProbe.PlanPre, foldProbe);
        }

        var masksProbe = ShapingProbe.Enter();
        this.SetupMasks(buffer, index, count);
        ShapingProbe.Exit(ShapingProbe.PlanPost, masksProbe);

        var assignProbe = ShapingProbe.Enter();
        this.AssignFeatures(buffer, index, count);
        ShapingProbe.Exit(ShapingProbe.PlanAssign, assignProbe);
    }

    /// <summary>
    /// Applies the per-text feature masks that cannot fold into the collected
    /// whole-segment masks, such as spans that follow the resolved direction of
    /// the records. Runs on every planning pass, after collection or replay.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected virtual void SetupMasks(ShapingBuffer buffer, int index, int count)
    {
    }

    /// <summary>
    /// Assigns the features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the preprocessing features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanPreprocessingFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the postprocessing features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void PlanPostprocessingFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Assigns the shaper specific substitution features to each glyph within the buffer.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the elements to assign.</param>
    /// <param name="count">The number of elements to assign.</param>
    protected abstract void AssignFeatures(ShapingBuffer buffer, int index, int count);

    /// <summary>
    /// Gets the ordered buffer of shaping stages for this shaper. The concrete
    /// list type lets the per-section stage walk enumerate without interface
    /// dispatch or a boxed enumerator.
    /// </summary>
    /// <returns>The shaping stages.</returns>
    public abstract List<ShapingStage> GetShapingStages();

    /// <summary>
    /// Recalculates the count when the buffer size changes during shaping.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="oldCount">The previous buffer count, updated to the current count.</param>
    /// <param name="count">The element count, adjusted by the size delta.</param>
    private static void RecalculateCount(ShapingBuffer buffer, ref int oldCount, ref int count)
    {
        // If the buffer has changed size we need to recalculate the count.
        int delta = buffer.Count - oldCount;
        count += delta;
        oldCount += delta;
    }
}
