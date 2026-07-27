// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tables.AdvancedTypographic.Shapers;

/// <summary>
/// This is a shaper for Arabic, and other cursive scripts.
/// </summary>
/// <remarks>
/// The joining state machine and feature order follow <c>hb-ot-shaper-arabic.cc</c>.
/// </remarks>
internal sealed class ArabicShaper : DefaultShaper
{
    /// <summary>
    /// The 'mset' (mark positioning via substitution) feature tag.
    /// </summary>
    private static readonly Tag MsetTag = Tag.Parse("mset");

    /// <summary>
    /// The 'fina' (terminal forms) feature tag.
    /// </summary>
    private static readonly Tag FinaTag = Tag.Parse("fina");

    /// <summary>
    /// The 'fin2' (terminal forms #2) feature tag.
    /// </summary>
    private static readonly Tag Fin2Tag = Tag.Parse("fin2");

    /// <summary>
    /// The 'fin3' (terminal forms #3) feature tag.
    /// </summary>
    private static readonly Tag Fin3Tag = Tag.Parse("fin3");

    /// <summary>
    /// The 'isol' (isolated forms) feature tag.
    /// </summary>
    private static readonly Tag IsolTag = Tag.Parse("isol");

    /// <summary>
    /// The 'init' (initial forms) feature tag.
    /// </summary>
    private static readonly Tag InitTag = Tag.Parse("init");

    /// <summary>
    /// The 'medi' (medial forms) feature tag.
    /// </summary>
    private static readonly Tag MediTag = Tag.Parse("medi");

    /// <summary>
    /// The 'med2' (medial forms #2) feature tag.
    /// </summary>
    private static readonly Tag Med2Tag = Tag.Parse("med2");

    /// <summary>
    /// No joining action.
    /// </summary>
    private const byte None = 0;

    /// <summary>
    /// Isolated form action.
    /// </summary>
    private const byte Isol = 1;

    /// <summary>
    /// Final form action.
    /// </summary>
    private const byte Fina = 2;

    /// <summary>
    /// Final form #2 action (for ALAPH).
    /// </summary>
    private const byte Fin2 = 3;

    /// <summary>
    /// Final form #3 action (for ALAPH after DALATH RISH).
    /// </summary>
    private const byte Fin3 = 4;

    /// <summary>
    /// Medial form action.
    /// </summary>
    private const byte Medi = 5;

    /// <summary>
    /// Medial form #2 action (for ALAPH).
    /// </summary>
    private const byte Med2 = 6;

    /// <summary>
    /// Initial form action.
    /// </summary>
    private const byte Init = 7;

    /// <summary>
    /// The pause action separating joining-form lookup stages.
    /// </summary>
    private readonly Action<ShapePlan, ShapingBuffer, int, int> pauseAction;

    /// <summary>
    /// The action applying presentation-form fallback after required ligatures.
    /// </summary>
    private readonly Action<ShapePlan, ShapingBuffer, int, int> fallbackAction;

    /// <summary>
    /// The font-resolved fallback substitutions, created on first use.
    /// </summary>
    private ArabicFallbackSubstitutions? fallbackSubstitutions;

    /// <summary>
    /// Arabic joining state machine table. Each entry is [prevAction, curAction, nextState].
    /// Rows are states (0-6), columns are joining type categories.
    /// </summary>
    private static readonly byte[,][] StateTable =
    {
        // #           NonJoining,                    LeftJoining,                 RightJoining,                 DualJoining,                    ALAPH,                     DALATH RISH
        // State 0: prev was U,  not willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { None, Isol, 1 }, new byte[] { None, Isol, 2 }, new byte[] { None, Isol, 1 }, new byte[] { None, Isol, 6 } },

        // State 1: prev was R or ISOL/ALAPH,  not willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { None, Isol, 1 }, new byte[] { None, Isol, 2 }, new byte[] { None, Fin2, 5 }, new byte[] { None, Isol, 6 } },

        // State 2: prev was D/L in ISOL form,  willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { Init, Fina, 1 }, new byte[] { Init, Fina, 3 }, new byte[] { Init, Fina, 4 }, new byte[] { Init, Fina, 6 } },

        // State 3: prev was D in FINA form,  willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { Medi, Fina, 1 }, new byte[] { Medi, Fina, 3 }, new byte[] { Medi, Fina, 4 }, new byte[] { Medi, Fina, 6 } },

        // State 4: prev was FINA ALAPH,  not willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { Med2, Isol, 1 }, new byte[] { Med2, Isol, 2 }, new byte[] { Med2, Fin2, 5 }, new byte[] { Med2, Isol, 6 } },

        // State 5: prev was FIN2/FIN3 ALAPH,  not willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { Isol, Isol, 1 }, new byte[] { Isol, Isol, 2 }, new byte[] { Isol, Fin2, 5 }, new byte[] { Isol, Isol, 6 } },

        // State 6: prev was DALATH/RISH,  not willing to join.
        { new byte[] { None, None, 0 }, new byte[] { None, Isol, 2 }, new byte[] { None, Isol, 1 }, new byte[] { None, Isol, 2 }, new byte[] { None, Fin3, 5 }, new byte[] { None, Isol, 6 } },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ArabicShaper"/> class.
    /// </summary>
    /// <param name="script">The script classification.</param>
    /// <param name="textOptions">The text options.</param>
    public ArabicShaper(ScriptClass script, TextOptions textOptions)
        : base(script, MarkZeroingMode.PostGpos, textOptions)
    {
        this.pauseAction = Pause;
        this.fallbackAction = this.ApplyFallback;
    }

    /// <inheritdoc/>
    protected override void PlanFeatures(ShapingBuffer buffer, int index, int count)
    {
        this.EnableFeature(buffer, index, count, CcmpTag, ShapingFeatureFlags.ManualZwj);
        this.EnableFeature(buffer, index, count, LoclTag, ShapingFeatureFlags.ManualZwj);

        this.AddFeature(buffer, index, count, IsolTag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, FinaTag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, Fin2Tag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, Fin3Tag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, MediTag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, Med2Tag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);
        this.AddFeature(buffer, index, count, InitTag, ShapingFeatureFlags.ManualZwj, false, null, this.pauseAction);

        // The ligature trio and the required composition and ligature features
        // match the joiners themselves for this script's shaping model.
        this.EnableFeature(buffer, index, count, RligTag, ShapingFeatureFlags.ManualZwj, null, this.fallbackAction);
        this.Features.AddFlags(CaltTag, ShapingFeatureFlags.ManualZwj);
        this.Features.AddFlags(LigaTag, ShapingFeatureFlags.ManualZwj);
        this.Features.AddFlags(CligTag, ShapingFeatureFlags.ManualZwj);

        // HarfBuzz plans these as Arabic-script features, independently of the
        // generic horizontal feature list. Horizontal runs already get them from
        // DefaultShaper; forced vertical Arabic needs them here as well.
        if (buffer.TextOptions.LayoutMode.IsVertical())
        {
            this.EnableFeature(buffer, index, count, CaltTag);
            this.EnableFeature(buffer, index, count, LigaTag);
            this.EnableFeature(buffer, index, count, CligTag);
        }
    }

    /// <inheritdoc/>
    protected override void PlanPostprocessingFeatures(ShapingBuffer buffer, int index, int count)
    {
        base.PlanPostprocessingFeatures(buffer, index, count);

        this.EnableFeature(buffer, index, count, MsetTag, ShapingFeatureFlags.ManualZwj);
    }

    /// <inheritdoc/>
    protected override void AssignFeatures(ShapingBuffer buffer, int index, int count)
    {
        base.AssignFeatures(buffer, index, count);

        ArabicJoining.Apply(buffer, index, count, this.ScriptClass, this.Features);
    }

    /// <summary>
    /// Separates joining-form features into distinct substitution stages.
    /// </summary>
    /// <param name="plan">The shaping plan.</param>
    /// <param name="buffer">The shaping buffer.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the segment.</param>
    private static void Pause(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
    }

    /// <summary>
    /// Applies presentation-form fallback when all four Arabic joining-form features are absent.
    /// </summary>
    /// <param name="plan">The shaping plan.</param>
    /// <param name="buffer">The shaping buffer.</param>
    /// <param name="index">The zero-based index of the first record.</param>
    /// <param name="count">The number of records in the segment.</param>
    private void ApplyFallback(ShapePlan plan, ShapingBuffer buffer, int index, int count)
    {
        if (this.ScriptClass != ScriptClass.Arabic
            || plan.TryGetGSubFeatureLookups(in IsolTag, out _)
            || plan.TryGetGSubFeatureLookups(in FinaTag, out _)
            || plan.TryGetGSubFeatureLookups(in MediTag, out _)
            || plan.TryGetGSubFeatureLookups(in InitTag, out _))
        {
            return;
        }

        this.fallbackSubstitutions ??= ArabicFallbackSubstitutions.Create(plan.FontMetrics);
        this.fallbackSubstitutions.Apply(plan, buffer, index, count, InitTag, MediTag, FinaTag, IsolTag, RligTag);
    }
}
