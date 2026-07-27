// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Globalization;
using SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;
using SixLabors.Fonts.Unicode;
using SixLabors.Fonts.Unicode.Resources;

namespace SixLabors.Fonts.Tables.AdvancedTypographic;

/// <summary>
/// Splits a shaping buffer into runs of a single script and language and plans a
/// shaper for each run.
/// <para>
/// Planning is what prepares a run's text and registers its features, so it
/// belongs to every font rather than to the fonts that carry a substitution
/// table. A font without one still needs the characters a script cannot be read
/// without, and positioning reuses the runs planning records.
/// </para>
/// </summary>
internal static class ScriptItemizer
{
    /// <summary>
    /// Reads the run of one script that begins at <paramref name="index"/>,
    /// leaving <paramref name="index"/> on the run's last record.
    /// <para>
    /// Feature lookups are assigned to runs rather than to the text as a whole so
    /// that the shapers of two languages cannot interfere with one another.
    /// Characters belonging to no script of their own join the run around them.
    /// </para>
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based index of the run's first record, left on its last.</param>
    /// <param name="maxCount">The largest record count a run may reach.</param>
    /// <param name="count">When this method returns, contains the number of records in the run.</param>
    /// <returns>The script and culture shared by the run.</returns>
    public static ShapingRun ReadRun(ShapingBuffer buffer, ref int index, int maxCount, out int count)
    {
        ShapingRun run = new(buffer, index);
        count = 1;

        while (index < buffer.Count - 1)
        {
            if (!run.TryInclude(buffer, index + 1))
            {
                break;
            }

            index++;
            count++;

            if (index >= maxCount)
            {
                break;
            }
        }

        return run;
    }

    /// <summary>
    /// Resolves the script that applies to one shaping record.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based record index.</param>
    /// <returns>The explicit run script, the whole-text script, or the script inferred from the character, in that order.</returns>
    public static ScriptClass ResolveScript(ShapingBuffer buffer, int index)
    {
        ref GlyphShapingData data = ref buffer[index];
        ScriptClass? script = buffer.TextRuns[data.TextRunIndex].Script ?? buffer.TextOptions.Script;

        // Explicit metadata belongs to the whole declared run, including its
        // punctuation and any characters whose Unicode Script value differs.
        return script ?? CodePoint.GetScriptClass(data.CodePoint);
    }

    /// <summary>
    /// Resolves the culture that applies to one shaping record.
    /// </summary>
    /// <param name="buffer">The glyph shaping buffer.</param>
    /// <param name="index">The zero-based record index.</param>
    /// <returns>The explicit run culture, the whole-text culture, or the current culture, in that order.</returns>
    public static CultureInfo ResolveCulture(ShapingBuffer buffer, int index)
    {
        ref GlyphShapingData data = ref buffer[index];
        return buffer.TextRuns[data.TextRunIndex].Culture ?? buffer.TextOptions.Culture ?? CultureInfo.CurrentCulture;
    }

    /// <summary>
    /// Plans a shaper over every run of the buffer and records the runs for the
    /// positioning pass. Used for a font that carries no substitution table,
    /// where there are no lookups to apply but the text still has to be prepared.
    /// </summary>
    /// <param name="fontMetrics">The font metrics.</param>
    /// <param name="buffer">The glyph shaping buffer.</param>
    public static void PlanRuns(FontMetrics fontMetrics, ShapingBuffer buffer)
    {
        // Set max constraints to prevent OutOfMemoryException or infinite loops from attacks.
        int maxCount = AdvancedTypographicUtils.GetMaxAllowableShapingCollectionCount(buffer.Count);

        for (int i = 0; i < buffer.Count; i++)
        {
            int index = i;
            ShapingRun run = ReadRun(buffer, ref i, maxCount, out int count);

            // With no substitution table the font offers no script of its own, so
            // the run is planned against the default design.
            ShapePlan shapePlan = buffer.GetOrCreatePlan(run.Script, default, fontMetrics, run.Culture);

            // Preparing the text can insert records, so the run grows with it.
            int collectionCount = buffer.Count;

            shapePlan.Shaper.Plan(fontMetrics, buffer, index, count);

            int delta = buffer.Count - collectionCount;
            i += delta;
            count += delta;

            // A substitution table with no lookups still walks every stage and
            // invokes its actions. Preserve that path for fonts with no table,
            // accounting for actions that insert or remove records.
            List<ShapePlanStageGroup<GSub.LookupTable>> groups = shapePlan.GetOrBuildGSubStageGroups();
            List<ShapingStage> stages = shapePlan.Stages;
            for (int g = 0; g < groups.Count; g++)
            {
                ShapePlanStageGroup<GSub.LookupTable> group = groups[g];

                collectionCount = buffer.Count;
                stages[group.Start].PreProcessFeature(shapePlan, buffer, index, count);

                delta = buffer.Count - collectionCount;
                i += delta;
                count += delta;

                collectionCount = buffer.Count;
                stages[group.End - 1].PostProcessFeature(shapePlan, buffer, index, count);

                delta = buffer.Count - collectionCount;
                i += delta;
                count += delta;
            }

            buffer.SegmentPlans.Add((index, count, run.Script, shapePlan));
        }
    }

    /// <summary>
    /// Tracks the script and language shared by one shaping run.
    /// </summary>
    public struct ShapingRun
    {
        private int textRunIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="ShapingRun"/> struct from the first shaping record in a run.
        /// </summary>
        /// <param name="buffer">The glyph shaping buffer.</param>
        /// <param name="index">The zero-based index of the first record.</param>
        public ShapingRun(ShapingBuffer buffer, int index)
        {
            this.Script = ResolveScript(buffer, index);
            this.Culture = ResolveCulture(buffer, index);
            this.textRunIndex = buffer[index].TextRunIndex;
        }

        /// <summary>
        /// Gets the script resolved for the run.
        /// </summary>
        public ScriptClass Script { get; private set; }

        /// <summary>
        /// Gets the culture resolved for the run.
        /// </summary>
        public CultureInfo Culture { get; }

        /// <summary>
        /// Attempts to extend the run through one adjacent shaping record.
        /// </summary>
        /// <param name="buffer">The glyph shaping buffer.</param>
        /// <param name="index">The zero-based index of the adjacent record.</param>
        /// <returns><see langword="true"/> when the record belongs to this run.</returns>
        public bool TryInclude(ShapingBuffer buffer, int index)
        {
            CodePoint codePoint = buffer[index].CodePoint;
            ScriptClass next = ResolveScript(buffer, index);
            int nextTextRunIndex = buffer[index].TextRunIndex;
            if (nextTextRunIndex != this.textRunIndex)
            {
                // Language-system features differ within the same script, so a
                // culture change is a shaping boundary even when the script matches.
                CultureInfo nextCulture = ResolveCulture(buffer, index);
                if (!ReferenceEquals(nextCulture, this.Culture) && !string.Equals(nextCulture.Name, this.Culture.Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (next != this.Script &&
                this.Script is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited &&
                next is not ScriptClass.Common and not ScriptClass.Unknown and not ScriptClass.Inherited &&
                !ScriptExtensionData.Contains(codePoint, this.Script))
            {
                return false;
            }

            if (this.Script is ScriptClass.Common or ScriptClass.Unknown or ScriptClass.Inherited)
            {
                this.Script = next;
            }

            this.textRunIndex = nextTextRunIndex;
            return true;
        }
    }
}
