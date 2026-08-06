// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts.Tables.TrueType.Glyphs;

namespace SixLabors.Fonts.Tables.TrueType.Hinting;

/// <summary>
/// Code adapted from
/// <see href="https://github.com/MikePopoloski/SharpFont/blob/b28555e8fae94c57f1b5ccd809cdd1260f0eb55f/SharpFont/Internal/Interpreter.cs"/>.
///
/// Reference material:
/// <see href="https://developer.apple.com/fonts/TrueType-Reference-Manual/RM05/Chap5.html"/> –
/// the original TrueType instruction set and execution model.
/// <see href="https://learn.microsoft.com/en-us/typography/cleartype/truetypecleartype"/> –
/// details on how Microsoft's ClearType rasterizer interprets TrueType hints.
/// <see href="https://freetype.org/freetype2/docs/hinting/subpixel-hinting.html"/> –
/// documentation of FreeType's subpixel hinting engines, including the v40 "minimal" interpreter.
///
/// <para>
/// In <see cref="HintingMode.Standard"/> this implementation matches the behavior of FreeType's
/// v40 subpixel hinting interpreter, with horizontal hinting disabled and full vertical TrueType
/// instruction processing preserved. Backward compatibility mode is active by default, exactly as
/// in FreeType's minimal (v40) engine: X-axis moves are ignored, no point moves after both IUP
/// calls, and SHPIX/DELTAP execute only in their gated forms. Fonts opt out per FreeType's rules
/// by executing INSTCTRL selector 3 (the backward-compatibility waiver) in the prep program, or
/// temporarily within a single glyph program.
/// </para>
///
/// <para>
/// In <see cref="HintingMode.Full"/> the backward compatibility restrictions are lifted entirely,
/// matching FreeType's behavior when subpixel hinting is disabled for a mono render target:
/// instructions move points freely on both axes and GETINFO reports the v35 grayscale identity,
/// so fonts execute their classic bidirectional grid fitting branches.
/// </para>
///
/// <para>
/// Modern ClearType-hinted fonts are designed for this style of processing and will render
/// consistently under this interpreter. Legacy CRT-era fonts such as Arial or Times New Roman
/// also render cleanly under v40 semantics, though without legacy bi-level horizontal snapping,
/// which v40 intentionally omits.
/// </para>
/// </summary>
internal partial class TrueTypeInterpreter
{
    // Current and saved graphics state. cvtState is captured after the prep (CVT) program
    // runs so that each glyph program begins with a consistent baseline.
    private GraphicsState state;
    private GraphicsState cvtState;

    private readonly ExecutionStack stack;
    private readonly InstructionStream[] functions;
    private readonly InstructionStream[] instructionDefs;

    // Control Value Table: baseControlValueTable holds the scaled values after prep execution;
    // controlValueTable is a working copy restored at the start of each glyph program.
    private float[] baseControlValueTable;
    private float[] controlValueTable;

    // Storage area shared between prep and glyph programs. prepStorage holds the reference
    // to the storage array as it was after prep execution. Glyph programs use copy-on-write
    // (see WS instruction) so that prep state is preserved across glyphs.
    private int[] storage;
    private int[]? prepStorage;
    private bool inGlyphProgram;

    private IReadOnlyList<ushort> contours;
    private float scale;
    private int ppem;
    private GlyphVector.TrueTypeScaler trueTypeScaler;
    private int callStackSize;

    // Active hinting mode. Full mode lifts the v40 backward compatibility movement
    // restrictions and reports a v35 grayscale identity through GETINFO. The mode is
    // part of the prep memoization key because prep programs branch on GETINFO.
    private HintingMode hintingMode = HintingMode.Standard;

    // Dot product of freedom and projection vectors, used to decompose
    // scalar distances into movement along the freedom vector.
    private float fdotp;

    // Super-rounding parameters set by SROUND/S45ROUND.
    private float roundThreshold;
    private float roundPhase;
    private float roundPeriod;

    // IUP tracking — once both axes have been interpolated, further IUP calls are skipped
    // and v40 backward compatibility blocks Y movement (post-IUP restriction).
    private bool iupXCalled;
    private bool iupYCalled;
    private bool isComposite;

    // Normalized variation axis coordinates for variable fonts, used by GETVARIATION/GETINFO.
    private float[]? normalizedAxisCoordinates;

    // Instruction, repeated-call, and backward-jump counters prevent malformed bytecode
    // from running indefinitely. The per-program limits below grow with the number of live
    // points and CVT entries so ordinary data-dependent loops retain sufficient headroom.
    private long insCounter;

    private long loopcallCounter;
    private long negJumpCounter;
    private long loopcallCounterMax;
    private long negJumpCounterMax;

    // Zone pointers: zp0/zp1/zp2 are the three zone pointer registers (ZP0-ZP2).
    // They can reference either the glyph zone (points) or the twilight zone.
    private Zone zp0;
    private Zone zp1;
    private Zone zp2;
    private Zone points;
    private Zone twilight;

    // Interpreter-owned glyph zone buffers, grown once and reused for every glyph so
    // hinting performs no per-glyph allocation. The buffers may exceed the live point
    // count, which the zone tracks separately.
    private ControlPoint[] glyphZoneCurrent = [];
    private ControlPoint[] glyphZoneOriginal = [];
    private ControlPoint[] glyphZoneUnscaled = [];
    private TouchState[] glyphZoneTouch = [];

    private static readonly float Sqrt2Over2 = (float)(Math.Sqrt(2) / 2);
    private const int MaxCallStack = 128;
    private const long MaxRunnableOpcodes = 1_000_000;
    private const float Epsilon = 0.000001F;

#if DEBUG
    private readonly List<OpCode> debugList = [];
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueTypeInterpreter"/> class
    /// with resource limits sourced from the font's <c>maxp</c> table.
    /// </summary>
    /// <param name="maxStack">Maximum stack depth.</param>
    /// <param name="maxStorage">Number of storage area locations.</param>
    /// <param name="maxFunctions">Number of function definition slots (FDEF).</param>
    /// <param name="maxInstructionDefs">Number of instruction definition slots (IDEF). When non-zero, a full 256-entry lookup table is allocated.</param>
    /// <param name="maxTwilightPoints">Number of points in the twilight zone.</param>
    public TrueTypeInterpreter(int maxStack, int maxStorage, int maxFunctions, int maxInstructionDefs, int maxTwilightPoints)
    {
        this.stack = new ExecutionStack(maxStack);
        this.storage = new int[maxStorage];
        this.functions = new InstructionStream[maxFunctions];
        this.instructionDefs = new InstructionStream[maxInstructionDefs > 0 ? 256 : 0];
        this.state = default;
        this.cvtState = default;
        this.twilight = new Zone(maxTwilightPoints, isTwilight: true);
        this.controlValueTable = [];
        this.baseControlValueTable = [];
        this.contours = [];
    }

    /// <summary>
    /// Gets the exception that aborted the most recent glyph program, or <see langword="null"/>
    /// when it completed. Execution faults leave the glyph unhinted by design; the fault is
    /// retained so diagnostics can distinguish a failed program from an absent one.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the control value program inhibited grid fitting for
    /// the most recent glyph. A font asking not to be grid fitted at a size is stating that
    /// its outlines render better untouched there, so callers must not substitute their own
    /// fitting for the instructions that were skipped.
    /// </summary>
    public bool LastRunInhibited { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the most recent successful glyph program marked any
    /// outline point as touched on the X axis, excluding the four phantom points. Under
    /// full hinting a touch implies an applied movement, so this reports whether the font's
    /// own instructions grid fitted the horizontal axis.
    /// </summary>
    public bool LastRunTouchedX { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the most recent successful glyph program marked any
    /// outline point as touched on the Y axis, excluding the four phantom points.
    /// </summary>
    public bool LastRunTouchedY { get; private set; }

    /// <summary>
    /// Gets the buffer holding the most recent glyph zone points, hinted in place. Only the
    /// first <see cref="GlyphZonePointCount"/> entries are live, with the four phantom
    /// points last; the buffer is reused by the next glyph program.
    /// </summary>
    public ControlPoint[] GlyphZonePoints => this.glyphZoneCurrent;

    /// <summary>
    /// Gets the number of live points in <see cref="GlyphZonePoints"/>, including the four
    /// phantom points.
    /// </summary>
    public int GlyphZonePointCount { get; private set; }

    /// <summary>
    /// Gets a value indicating whether point movement is free of the v40 backward
    /// compatibility restrictions. Movement is unrestricted in full hinting mode and
    /// when a font has executed the INSTCTRL backward-compatibility waiver, which may be
    /// toggled mid glyph program, so this must remain a dynamic check.
    /// </summary>
    private bool IsMovementUnrestricted => this.hintingMode == HintingMode.Full || (this.state.InstructionControl & InstructionControlFlags.NativeClearType) != 0;

    /// <summary>
    /// Sets the normalized axis coordinates for variable font hinting.
    /// These are used by the GETVARIATION and GETINFO instructions.
    /// </summary>
    /// <param name="coordinates">Normalized axis coordinates in the range [-1, 1], or <see langword="null"/> for non-variable fonts.</param>
    public void SetNormalizedAxisCoordinates(float[]? coordinates)
        => this.normalizedAxisCoordinates = coordinates;

    /// <summary>
    /// Executes the font program (fpgm) to populate function definitions (FDEF/IDEF).
    /// This must be called once per font before any CVT or glyph programs are executed.
    /// The font program runs once per interpreter instance regardless of hinting mode:
    /// FDEF bodies are static bytecode and any storage or twilight side effects are
    /// discarded before each prep execution, so no mode dependent state can leak from it.
    /// </summary>
    /// <param name="instructions">The raw font program bytecode.</param>
    public void InitializeFunctionDefs(byte[] instructions)
        => this.Execute(new StackInstructionStream(instructions, 0), false, true);

    /// <summary>
    /// Scales the Control Value Table and executes the prep (CVT) program.
    /// The prep program typically sets up the graphics state and may modify CVT entries
    /// for the current pixel size. The resulting state is saved and restored for each
    /// subsequent glyph program execution.
    /// </summary>
    /// <param name="cvt">The raw CVT entries from the font, or <see langword="null"/> if absent.</param>
    /// <param name="scale">The scale factor to apply to CVT entries (units-per-em to pixels).</param>
    /// <param name="ppem">The pixels-per-em value at the current size.</param>
    /// <param name="unitsPerEm">The font's design units per em.</param>
    /// <param name="cvProgram">The raw prep program bytecode, or <see langword="null"/> if absent.</param>
    /// <param name="mode">The hinting mode governing movement restrictions and the GETINFO identity.</param>
    public void SetControlValueTable(short[]? cvt, float scale, float ppem, int unitsPerEm, byte[]? cvProgram, HintingMode mode)
    {
        if (this.scale == scale && this.hintingMode == mode)
        {
            return;
        }

        this.hintingMode = mode;
        this.trueTypeScaler = new GlyphVector.TrueTypeScaler((int)Math.Round(ppem * 65536F), unitsPerEm << 16);

        // A missing CVT table must not skip the prep program: fonts may carry a prep
        // program without control values, and prep still establishes the graphics state,
        // storage, and twilight points that glyph programs build on.
        if (cvt != null)
        {
            if (this.controlValueTable.Length == 0 && cvt.Length > 0)
            {
                this.controlValueTable = new float[cvt.Length];
            }

            for (int i = 0; i < cvt.Length; i++)
            {
                // CVT entries are integral design coordinates. Scale them with the same
                // reduced integer ratio used for outline coordinates so both data sets land
                // on one 26.6 grid and use the same tie direction for negative values.
                this.controlValueTable[i] = GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale(cvt[i]));
            }
        }
        else
        {
            this.controlValueTable = [];
        }

        this.scale = scale;
        this.ppem = (int)Math.Round(ppem);
        this.state.Reset();
        this.stack.Clear();

        // Restore the interpreter to the same state a freshly created interpreter would be in
        // immediately before running the prep program. A pooled interpreter may have been used
        // to hint glyphs at a previous size, leaving behind storage writes, twilight points,
        // rounding state and zone pointers. The prep program reads and builds on this state, so
        // without restoring it the prep result — and therefore the hinted outline — depends on
        // the interpreter's history. That made hinting non-deterministic when a font family was
        // rendered concurrently from a shared interpreter pool (see issue #484).
        // Prep begins with zeroed twilight coordinates and storage. In particular, storage
        // writes made while defining font functions are not part of the per-size prep state.
        this.ResetTwilightZone();
        Array.Clear(this.storage, 0, this.storage.Length);
        this.prepStorage = null;
        this.inGlyphProgram = false;
        this.callStackSize = 0;
        this.fdotp = 0;
        this.roundThreshold = 0;
        this.roundPhase = 0;
        this.roundPeriod = 0;
        this.iupXCalled = false;
        this.iupYCalled = false;
        this.isComposite = false;
        this.contours = [];
        this.points = default;
        this.zp0 = this.zp1 = this.zp2 = this.points;

        if (cvProgram != null)
        {
            // With no glyph points, the prep limit is 300 + 22 per CVT entry. Backward
            // jumps and LOOPCALL share that data-dependent budget independently.
            this.insCounter = 0;
            this.loopcallCounter = 0;
            this.negJumpCounter = 0;
            int cvtSize = this.controlValueTable.Length;
            this.loopcallCounterMax = 300 + (22 * (long)cvtSize);
            this.negJumpCounterMax = this.loopcallCounterMax;

            this.Execute(new StackInstructionStream(cvProgram, 0), false, false);

            // Retain the completed prep storage as the immutable baseline for glyph programs.
            // WS creates a private copy only when a glyph actually changes an entry.
            this.prepStorage = this.storage;

            // Save the per-size graphics state that every glyph program starts from.
            if ((this.state.InstructionControl & InstructionControlFlags.UseDefaultGraphicsState) != 0)
            {
                this.cvtState.Reset();
            }
            else
            {
                // Reference points and most scalar controls carry across from prep, while
                // vectors, rounding, and loop count have defined glyph-program defaults.
                this.cvtState = this.state;
                this.cvtState.Freedom = Vector2.UnitX;
                this.cvtState.Projection = Vector2.UnitX;
                this.cvtState.DualProjection = Vector2.UnitX;
                this.cvtState.RoundState = RoundMode.ToGrid;
                this.cvtState.Loop = 1;
            }
        }

        if (this.controlValueTable.Length > 0)
        {
            if (this.baseControlValueTable.Length != this.controlValueTable.Length)
            {
                this.baseControlValueTable = new float[this.controlValueTable.Length];
            }

            Array.Copy(this.controlValueTable, this.baseControlValueTable, this.controlValueTable.Length);
        }
        else
        {
            this.baseControlValueTable = [];
        }
    }

    /// <summary>
    /// Attempts to apply TrueType hinting instructions to the specified glyph outline.
    /// </summary>
    /// <remarks>
    /// Hinting will not be applied if the instructions buffer is empty or if grid fitting is
    /// inhibited by the current interpreter state. If the instructions are malformed or an error occurs during
    /// execution, the method returns <see langword="false"/> and the glyph outline remains unhinted.
    /// </remarks>
    /// <param name="glyphPoints">The glyph's outline control points, excluding phantom points.</param>
    /// <param name="unscaledPoints">The same control points in font units, which IP interpolates from.</param>
    /// <param name="pp1">The first phantom point at the true fractional metrics.</param>
    /// <param name="pp2">The second phantom point at the true fractional metrics.</param>
    /// <param name="pp3">The third phantom point at the true fractional metrics.</param>
    /// <param name="pp4">The fourth phantom point at the true fractional metrics.</param>
    /// <param name="unscaledPp1">The first phantom point in font units.</param>
    /// <param name="unscaledPp2">The second phantom point in font units.</param>
    /// <param name="unscaledPp3">The third phantom point in font units.</param>
    /// <param name="unscaledPp4">The fourth phantom point in font units.</param>
    /// <param name="endPoints">A read-only list of indices indicating the end points of each contour in the glyph.</param>
    /// <param name="instructions">A read-only memory buffer containing the TrueType hinting instructions to execute.</param>
    /// <param name="isComposite">Indicates whether the glyph is a composite glyph. Set to <see langword="true"/> for composite glyphs; otherwise, <see langword="false"/>.</param>
    /// <returns><see langword="true"/> if hinting was successfully applied; otherwise, <see langword="false"/>.</returns>
    public bool TryHintGlyph(IList<ControlPoint> glyphPoints, IList<ControlPoint> unscaledPoints, Vector2 pp1, Vector2 pp2, Vector2 pp3, Vector2 pp4, Vector2 unscaledPp1, Vector2 unscaledPp2, Vector2 unscaledPp3, Vector2 unscaledPp4, IReadOnlyList<ushort> endPoints, ReadOnlyMemory<byte> instructions, bool isComposite)
    {
        this.LastError = null;
        this.LastRunTouchedX = false;
        this.LastRunTouchedY = false;
        this.LastRunInhibited = false;

        if (instructions.Length == 0)
        {
            return false;
        }

        // Check if the CVT program disabled hinting
        if ((this.state.InstructionControl & InstructionControlFlags.InhibitGridFitting) != 0)
        {
            this.LastRunInhibited = true;
            return false;
        }

        try
        {
            // Stage the outline and phantom points into the interpreter owned glyph zone
            // buffers, which grow once and are reused for every subsequent glyph.
            int count = glyphPoints.Count + 4;
            this.EnsureGlyphZoneCapacity(count);
            ControlPoint[] current = this.glyphZoneCurrent;
            ControlPoint[] original = this.glyphZoneOriginal;
            ControlPoint[] unscaled = this.glyphZoneUnscaled;
            for (int i = 0; i < glyphPoints.Count; i++)
            {
                current[i] = glyphPoints[i];
                unscaled[i] = unscaledPoints[i];
            }

            unscaled[count - 4] = new ControlPoint(unscaledPp1, false);
            unscaled[count - 3] = new ControlPoint(unscaledPp2, false);
            unscaled[count - 2] = new ControlPoint(unscaledPp3, false);
            unscaled[count - 1] = new ControlPoint(unscaledPp4, false);

            // Phantom coordinates are design-unit points, so scale them through the same
            // integer ratio as the outline. Reconstructing them from independently rounded
            // bounds and floating-point metrics can put PP1 on a different 26.6 value.
            current[count - 4] = new ControlPoint(
                new Vector2(
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp1.X)),
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp1.Y))),
                false);

            current[count - 3] = new ControlPoint(
                new Vector2(
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp2.X)),
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp2.Y))),
                false);

            current[count - 2] = new ControlPoint(
                new Vector2(
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp3.X)),
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp3.Y))),
                false);

            current[count - 1] = new ControlPoint(
                new Vector2(
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp4.X)),
                    GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale((int)unscaledPp4.Y))),
                false);

            current.AsSpan(0, count).CopyTo(original);

            // Let p be PP1.X in signed 26.6. The shared horizontal-origin adjustment is
            // roundToGrid(p) - p, where roundToGrid(p) = (p + 32) & ~63. Apply it to every
            // original point before initializing the current array. It cancels at emission
            // for untouched points, but remains significant when an angled instruction
            // converts a projected distance back into X and Y movement.
            int pp1Index = count - 4;
            int originalPp1X = FloatToF26Dot6(original[pp1Index].Point.X);
            int roundedPp1X = unchecked(originalPp1X + 0x20) & ~0x3F;
            int horizontalOriginDelta = roundedPp1X - originalPp1X;
            if (horizontalOriginDelta != 0)
            {
                float delta = F26Dot6ToFloat(horizontalOriginDelta);
                for (int i = 0; i < count; i++)
                {
                    original[i].Point.X += delta;
                }
            }

            original.AsSpan(0, count).CopyTo(current);

            // Each working side-bearing phantom then rounds on its metric axis. PP1 and PP2
            // round X; PP3 and PP4 round Y. The unscaled and original arrays stay unchanged.
            current[count - 4].Point.X = MathF.Floor(current[count - 4].Point.X + 0.5F);
            current[count - 3].Point.X = MathF.Floor(current[count - 3].Point.X + 0.5F);
            current[count - 2].Point.Y = MathF.Floor(current[count - 2].Point.Y + 0.5F);
            current[count - 1].Point.Y = MathF.Floor(current[count - 1].Point.Y + 0.5F);
            Array.Clear(this.glyphZoneTouch, 0, count);
            this.GlyphZonePointCount = count;

            // Save contours and points
            this.contours = endPoints;
            this.zp0 = this.zp1 = this.zp2 = this.points = new Zone(current, this.glyphZoneOriginal, unscaled, this.glyphZoneTouch, count);

            // reset all of our shared state
            this.state = this.cvtState;
            this.callStackSize = 0;

            // Restore the shared prep baseline. A later WS instruction copies it before the
            // first glyph-local write, so one glyph cannot alter another glyph's start state.
            if (this.prepStorage != null)
            {
                this.storage = this.prepStorage;
            }
            else
            {
                Array.Clear(this.storage, 0, this.storage.Length);
            }

            this.inGlyphProgram = true;

            if (this.baseControlValueTable.Length > 0)
            {
                if (this.controlValueTable.Length != this.baseControlValueTable.Length)
                {
                    this.controlValueTable = new float[this.baseControlValueTable.Length];
                }

                Array.Copy(this.baseControlValueTable, this.controlValueTable, this.baseControlValueTable.Length);
            }
            else
            {
                this.controlValueTable = [];
            }

            this.ResetTwilightZone();

#if DEBUG
            this.debugList.Clear();
#endif

            this.stack.Clear();
            this.OnVectorsUpdated();
            this.iupXCalled = false;
            this.iupYCalled = false;
            this.isComposite = isComposite;

            // For glyph programs the repeated-call and backward-jump budget is
            // max(50, 10 * pointCount) + max(50, cvtCount / 10).
            this.insCounter = 0;
            this.loopcallCounter = 0;
            this.negJumpCounter = 0;
            int nPoints = count;
            int cvtSize = this.controlValueTable.Length;
            if (nPoints > 0)
            {
                this.loopcallCounterMax = Math.Max(50, 10 * (long)nPoints) + Math.Max(50, cvtSize / 10);
            }
            else
            {
                this.loopcallCounterMax = 300 + (22 * (long)cvtSize);
            }

            this.negJumpCounterMax = this.loopcallCounterMax;

            // normalize the round state settings
            switch (this.state.RoundState)
            {
                case RoundMode.Super:
                    this.SetSuperRound(1.0f);
                    break;
                case RoundMode.Super45:
                    this.SetSuperRound(Sqrt2Over2);
                    break;
            }

            this.Execute(new StackInstructionStream(instructions, 0), false, false);

            // Record which axes the program marked touched on the outline itself. The four
            // appended phantom points are excluded: advance adjustments alone do not
            // constitute outline grid fitting.
            TouchState[] touchStates = this.points.TouchState;
            int outlinePointCount = count - 4;
            bool touchedX = false;
            bool touchedY = false;
            for (int i = 0; i < outlinePointCount && !(touchedX && touchedY); i++)
            {
                touchedX |= (touchStates[i] & TouchState.X) != 0;
                touchedY |= (touchStates[i] & TouchState.Y) != 0;
            }

            this.LastRunTouchedX = touchedX;
            this.LastRunTouchedY = touchedY;

            return true;
        }
        catch (Exception ex)
        {
            this.LastError = ex;
            return false;
        }
    }

    /// <summary>
    /// Grows the interpreter owned glyph zone buffers to hold at least the given point
    /// count. Growth doubles so repeated hinting reaches a steady state with no
    /// per-glyph allocation.
    /// </summary>
    /// <param name="count">The number of points the zone must hold, including phantoms.</param>
    private void EnsureGlyphZoneCapacity(int count)
    {
        if (this.glyphZoneCurrent.Length < count)
        {
            int capacity = Math.Max(count, Math.Max(64, this.glyphZoneCurrent.Length * 2));
            this.glyphZoneCurrent = new ControlPoint[capacity];
            this.glyphZoneOriginal = new ControlPoint[capacity];
            this.glyphZoneUnscaled = new ControlPoint[capacity];
            this.glyphZoneTouch = new TouchState[capacity];
        }
    }

    /// <summary>
    /// Resets all twilight zone points to the origin and clears their touch state,
    /// preventing stale data from leaking between glyph programs.
    /// </summary>
    private void ResetTwilightZone()
    {
        // Twilight reference points begin at (0,0). Reset original and current coordinates
        // together with touch state so a previous glyph cannot affect the next program.
        ControlPoint[] twCurrent = this.twilight.Current;
        ControlPoint[] twOriginal = this.twilight.Original;

        int len = twCurrent.Length;
        for (int i = 0; i < len; i++)
        {
            twCurrent[i].Point = default;
            twOriginal[i].Point = default;
        }

        Array.Clear(this.twilight.TouchState, 0, this.twilight.Count);
    }

    /// <summary>
    /// Core instruction dispatch loop. Reads and executes opcodes from the given
    /// instruction stream until the stream is exhausted or an error terminates execution.
    /// </summary>
    /// <param name="stream">The instruction stream to execute.</param>
    /// <param name="inFunction">
    /// <see langword="true"/> when executing inside a CALL/LOOPCALL function body.
    /// Controls whether ENDF returns to the caller or exits execution.
    /// </param>
    /// <param name="allowFunctionDefs">
    /// <see langword="true"/> when executing the font program (fpgm), which permits
    /// FDEF and IDEF instructions. Glyph and prep programs set this to <see langword="false"/>.
    /// </param>
    private void Execute(StackInstructionStream stream, bool inFunction, bool allowFunctionDefs)
    {
        while (!stream.Done)
        {
            int rawOpcode = stream.NextByte();
            OpCode opcode = (OpCode)rawOpcode;

#if DEBUG
            this.debugList.Add(opcode);
#endif

            // Count every dispatched opcode, including opcodes reached through functions.
            // Exceeding the fixed ceiling terminates the program without further mutation.
            if (++this.insCounter > MaxRunnableOpcodes)
            {
                return;
            }

            // Each table entry packs the required pop count in its high nibble and the
            // resulting push count in its low nibble, allowing one bounds check per opcode.
            byte popPush = PopPushCount[rawOpcode];
            int pops = popPush >> 4;
            int pushes = popPush & 0xF;

            // A malformed underflow discards the incomplete operand set and supplies the
            // required number of zero operands, preserving deterministic stack depth.
            if (this.stack.Count < pops)
            {
                int missing = pops - this.stack.Count;
                this.stack.Clear();
                for (int z = 0; z < pops; z++)
                {
                    this.stack.Push(0);
                }
            }

            // Stop before an opcode whose net stack effect would exceed maxStack.
            if (this.stack.Count - pops + pushes > this.stack.Capacity)
            {
                return;
            }

            switch (opcode)
            {
                // ==== PUSH INSTRUCTIONS ====
                case OpCode.NPUSHB:
                case OpCode.PUSHB1:
                case OpCode.PUSHB2:
                case OpCode.PUSHB3:
                case OpCode.PUSHB4:
                case OpCode.PUSHB5:
                case OpCode.PUSHB6:
                case OpCode.PUSHB7:
                case OpCode.PUSHB8:
                {
                    int count = opcode == OpCode.NPUSHB ? stream.NextByte() : opcode - OpCode.PUSHB1 + 1;
                    for (int i = 0; i < count; i++)
                    {
                        this.stack.Push(stream.NextByte());
                    }
                }

                break;
                case OpCode.NPUSHW:
                case OpCode.PUSHW1:
                case OpCode.PUSHW2:
                case OpCode.PUSHW3:
                case OpCode.PUSHW4:
                case OpCode.PUSHW5:
                case OpCode.PUSHW6:
                case OpCode.PUSHW7:
                case OpCode.PUSHW8:
                {
                    int count = opcode == OpCode.NPUSHW ? stream.NextByte() : opcode - OpCode.PUSHW1 + 1;
                    for (int i = 0; i < count; i++)
                    {
                        this.stack.Push(stream.NextWord());
                    }
                }

                break;

                // ==== STORAGE MANAGEMENT ====
                case OpCode.RS:
                {
                    int loc = this.stack.Pop();
                    if ((uint)loc >= (uint)this.storage.Length)
                    {
                        this.stack.Push(0);
                    }
                    else
                    {
                        this.stack.Push(this.storage[loc]);
                    }
                }

                break;
                case OpCode.WS:
                {
                    int value = this.stack.Pop();
                    int loc = this.stack.Pop();
                    if ((uint)loc < (uint)this.storage.Length)
                    {
                        // Glyph programs initially share the completed prep storage. Copy on
                        // the first write so later glyphs still observe the same prep baseline.
                        if (this.inGlyphProgram && this.storage == this.prepStorage)
                        {
                            int[] glyphStorage = new int[this.storage.Length];
                            Array.Copy(this.storage, glyphStorage, this.storage.Length);
                            this.storage = glyphStorage;
                        }

                        this.storage[loc] = value;
                    }
                }

                break;

                // ==== CONTROL VALUE TABLE ====
                case OpCode.WCVTP:
                {
                    float value = this.stack.PopFloat();
                    int loc = this.stack.Pop();
                    if ((uint)loc < (uint)this.controlValueTable.Length)
                    {
                        this.controlValueTable[loc] = value;
                    }
                }

                break;
                case OpCode.WCVTF:
                {
                    int value = this.stack.Pop();
                    int loc = this.stack.Pop();
                    if ((uint)loc < (uint)this.controlValueTable.Length)
                    {
                        // WCVTF supplies an integral font-unit value. Apply the active
                        // font-unit-to-26.6 ratio so runtime writes use the same scale and
                        // signed tie handling as CVT entries initialized before prep.
                        this.controlValueTable[loc] = GlyphVector.TrueTypeScaler.ToFloat(this.trueTypeScaler.Scale(value));
                    }
                }

                break;
                case OpCode.RCVT:
                {
                    int loc = this.stack.Pop();
                    if ((uint)loc >= (uint)this.controlValueTable.Length)
                    {
                        this.stack.Push(0);
                    }
                    else
                    {
                        this.stack.Push(this.controlValueTable[loc]);
                    }

                    break;
                }

                // ==== STATE VECTORS ====
                case OpCode.SVTCA0:
                case OpCode.SVTCA1:
                {
                    byte axis = opcode - OpCode.SVTCA0;
                    this.SetFreedomVectorToAxis(axis);
                    this.SetProjectionVectorToAxis(axis);
                }

                break;
                case OpCode.SFVTPV:
                {
                    this.state.Freedom = this.state.Projection;

                    // Copying projection to freedom makes their dot product exactly 1.0,
                    // represented by 0x4000 in F2.14. Recomputing it from float components
                    // could instead produce an adjacent fixed-point value.
                    this.fdotp = 1F;
                    break;
                }

                case OpCode.SPVTCA0:
                case OpCode.SPVTCA1:
                {
                    this.SetProjectionVectorToAxis(opcode - OpCode.SPVTCA0);
                    break;
                }

                case OpCode.SFVTCA0:
                case OpCode.SFVTCA1:
                {
                    this.SetFreedomVectorToAxis(opcode - OpCode.SFVTCA0);
                    break;
                }

                case OpCode.SPVTL0:
                case OpCode.SPVTL1:
                case OpCode.SFVTL0:
                case OpCode.SFVTL1:
                {
                    this.SetVectorToLine(opcode - OpCode.SPVTL0, false);
                    break;
                }

                case OpCode.SDPVTL0:
                case OpCode.SDPVTL1:
                {
                    this.SetVectorToLine(opcode - OpCode.SDPVTL0, true);
                    break;
                }

                case OpCode.SPVFS:
                case OpCode.SFVFS:
                {
                    int y = this.stack.Pop();
                    int x = this.stack.Pop();

                    // The bytecode operands already encode signed F2.14 components. Preserve
                    // their low sixteen bits directly; SPVFS and SFVFS do not normalize them.
                    Vector2 vec = new(F2Dot14ToFloat(x), F2Dot14ToFloat(y));
                    if (opcode == OpCode.SFVFS)
                    {
                        this.state.Freedom = vec;
                    }
                    else
                    {
                        this.state.Projection = vec;
                        this.state.DualProjection = vec;
                    }

                    this.OnVectorsUpdated();
                }

                break;
                case OpCode.GPV:
                case OpCode.GFV:
                {
                    Vector2 vec = opcode == OpCode.GPV ? this.state.Projection : this.state.Freedom;
                    this.stack.Push(FloatToF2Dot14(vec.X));
                    this.stack.Push(FloatToF2Dot14(vec.Y));
                }

                break;

                // ==== GRAPHICS STATE ====
                case OpCode.SRP0:
                {
                    this.state.Rp0 = this.stack.Pop();
                    break;
                }

                case OpCode.SRP1:
                {
                    this.state.Rp1 = this.stack.Pop();
                    break;
                }

                case OpCode.SRP2:
                {
                    this.state.Rp2 = this.stack.Pop();
                    break;
                }

                case OpCode.SZP0:
                {
                    if (this.TryGetZoneFromStack(out Zone szp0Zone))
                    {
                        this.zp0 = szp0Zone;
                    }

                    break;
                }

                case OpCode.SZP1:
                {
                    if (this.TryGetZoneFromStack(out Zone szp1Zone))
                    {
                        this.zp1 = szp1Zone;
                    }

                    break;
                }

                case OpCode.SZP2:
                {
                    if (this.TryGetZoneFromStack(out Zone szp2Zone))
                    {
                        this.zp2 = szp2Zone;
                    }

                    break;
                }

                case OpCode.SZPS:
                {
                    if (this.TryGetZoneFromStack(out Zone szpsZone))
                    {
                        this.zp0 = this.zp1 = this.zp2 = szpsZone;
                    }

                    break;
                }

                case OpCode.RTHG:
                {
                    this.state.RoundState = RoundMode.ToHalfGrid;
                    break;
                }

                case OpCode.RTG:
                {
                    this.state.RoundState = RoundMode.ToGrid;
                    break;
                }

                case OpCode.RTDG:
                {
                    this.state.RoundState = RoundMode.ToDoubleGrid;
                    break;
                }

                case OpCode.RDTG:
                {
                    this.state.RoundState = RoundMode.DownToGrid;
                    break;
                }

                case OpCode.RUTG:
                {
                    this.state.RoundState = RoundMode.UpToGrid;
                    break;
                }

                case OpCode.ROFF:
                {
                    this.state.RoundState = RoundMode.Off;
                    break;
                }

                case OpCode.SROUND:
                {
                    this.state.RoundState = RoundMode.Super;
                    this.SetSuperRound(1.0f);
                    break;
                }

                case OpCode.S45ROUND:
                {
                    this.state.RoundState = RoundMode.Super45;
                    this.SetSuperRound(Sqrt2Over2);
                    break;
                }

                case OpCode.INSTCTRL:
                {
                    // Always consume both operands, including selectors that cannot change
                    // state in the current program range.
                    int selector = this.stack.Pop();
                    int value = this.stack.Pop();

                    // Selectors 1 and 2 alter the saved per-size execution policy and are
                    // therefore accepted only during prep. Selector 3 controls movement
                    // restrictions and may also be changed inside a glyph; restoring the
                    // prep graphics state before each glyph makes that change temporary.
                    if (selector is >= 1 and <= 3 && (!this.inGlyphProgram || selector == 3))
                    {
                        int bit = 1 << (selector - 1);

                        // Zero clears the selected bit. A nonzero value sets it only when it
                        // equals that selector's one-hot bit, rejecting ambiguous masks.
                        if (value == 0)
                        {
                            this.state.InstructionControl = (InstructionControlFlags)((int)this.state.InstructionControl & ~bit);
                        }
                        else if (value == bit)
                        {
                            this.state.InstructionControl = (InstructionControlFlags)((int)this.state.InstructionControl | bit);
                        }
                    }
                }

                break;
                case OpCode.SCANCTRL:
                {
                    // Records the font's dropout control request. The low byte is a pixels per
                    // em threshold and the upper bits select which conditions enable dropout,
                    // so the decision cannot be made until the glyph is rendered at a known
                    // size. Only the low sixteen bits are meaningful.
                    this.state.ScanControl = (this.state.ScanControl & ~0xFFFF) | (this.stack.Pop() & 0xFFFF);
                    break;
                }

                case OpCode.SCANTYPE:
                {
                    // Selects which rule the rasterizer applies once dropout is enabled.
                    this.state.ScanType = this.stack.Pop();
                    break;
                }

                case OpCode.SANGW: /* instruction unspported */
                {
                    this.stack.Pop();
                    break;
                }

                case OpCode.SLOOP:
                {
                    int loop = this.stack.Pop();
                    if (loop < 0)
                    {
                        // A negative loop count is invalid and leaves the previous count intact.
                        break;
                    }

                    // Loop count is stored as an unsigned sixteen-bit quantity; larger values
                    // saturate at 65535 rather than wrapping.
                    this.state.Loop = loop > 0xFFFF ? 0xFFFF : loop;
                    break;
                }

                case OpCode.SMD:
                {
                    this.state.MinDistance = this.stack.PopFloat();
                    break;
                }

                case OpCode.SCVTCI:
                {
                    this.state.ControlValueCutIn = this.stack.PopFloat();
                    break;
                }

                case OpCode.SSWCI:
                {
                    this.state.SingleWidthCutIn = this.stack.PopFloat();
                    break;
                }

                case OpCode.SSW:
                {
                    this.state.SingleWidthValue = this.stack.Pop() * this.scale;
                    break;
                }

                case OpCode.FLIPON:
                {
                    this.state.AutoFlip = true;
                    break;
                }

                case OpCode.FLIPOFF:
                {
                    this.state.AutoFlip = false;
                    break;
                }

                case OpCode.SDB:
                {
                    this.state.DeltaBase = this.stack.Pop();
                    break;
                }

                case OpCode.SDS:
                {
                    this.state.DeltaShift = this.stack.Pop();
                    break;
                }

                // ==== POINT MEASUREMENT ====
                case OpCode.GC0:
                {
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp2.Count)
                    {
                        this.stack.Push(0);
                        break;
                    }

                    this.stack.Push(this.Project(this.zp2.GetCurrent(pointIndex)));
                    break;
                }

                case OpCode.GC1:
                {
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp2.Count)
                    {
                        this.stack.Push(0);
                        break;
                    }

                    this.stack.Push(this.DualProject(this.zp2.GetOriginal(pointIndex)));
                    break;
                }

                case OpCode.SCFS:
                {
                    float value = this.stack.PopFloat();
                    int index = this.stack.Pop();
                    if ((uint)index >= (uint)this.zp2.Count)
                    {
                        break;
                    }

                    Vector2 point = this.zp2.GetCurrent(index);
                    this.MovePoint(this.zp2, index, value - this.Project(point));

                    // Moving twilight points moves their "original" value also
                    if (this.zp2.IsTwilight)
                    {
                        this.zp2.Original[index].Point = this.zp2.Current[index].Point;
                    }
                }

                break;
                case OpCode.MD0:
                {
                    int i0 = this.stack.Pop();
                    int i1 = this.stack.Pop();
                    if ((uint)i0 >= (uint)this.zp1.Count ||
                        (uint)i1 >= (uint)this.zp0.Count)
                    {
                        this.stack.Push(0);
                        break;
                    }

                    // MD[0] projects the current coordinate difference. The opcode's low bit
                    // distinguishes the instruction encoding; it does not select originals.
                    this.stack.Push(this.Project(this.zp0.GetCurrent(i1) - this.zp1.GetCurrent(i0)));
                }

                break;
                case OpCode.MD1:
                {
                    int i0 = this.stack.Pop();
                    int i1 = this.stack.Pop();
                    if ((uint)i0 >= (uint)this.zp0.Count ||
                        (uint)i1 >= (uint)this.zp1.Count)
                    {
                        this.stack.Push(0);
                        break;
                    }

                    float distance = this.hintingMode == HintingMode.Full && !this.zp0.IsTwilight && !this.zp1.IsTwilight
                        ? this.DualProjectUnscaled(this.zp1.GetUnscaled(i1) - this.zp0.GetUnscaled(i0))
                        : this.DualProject(this.zp1.GetOriginal(i1) - this.zp0.GetOriginal(i0));

                    this.stack.Push(distance);
                }

                break;
                case OpCode.MPS: // MPS should return point size, but we assume DPI so it's the same as pixel size
                case OpCode.MPPEM:
                {
                    this.stack.Push(this.ppem);
                    break;
                }

                case OpCode.AA: /* deprecated instruction */
                {
                    this.stack.Pop();
                    break;
                }

                // ==== POINT MODIFICATION ====
                case OpCode.FLIPPT:
                {
                    // Once both IUP axes have completed under restricted movement, FLIP no
                    // longer changes contour topology.
                    bool blocked = !this.IsMovementUnrestricted && this.iupXCalled && this.iupYCalled;
                    for (int i = 0; i < this.state.Loop; i++)
                    {
                        int index = this.stack.Pop();
                        if (blocked || (uint)index >= (uint)this.points.Count)
                        {
                            continue;
                        }

                        this.points.Current[index].OnCurve ^= true;
                    }

                    this.state.Loop = 1;
                }

                break;
                case OpCode.FLIPRGON:
                {
                    bool blocked = !this.IsMovementUnrestricted && this.iupXCalled && this.iupYCalled;
                    int end = this.stack.Pop();
                    int start = this.stack.Pop();
                    if (blocked ||
                        (uint)end >= (uint)this.points.Count ||
                        (uint)start >= (uint)this.points.Count)
                    {
                        break;
                    }

                    for (int i = start; i <= end; i++)
                    {
                        this.points.Current[i].OnCurve = true;
                    }
                }

                break;
                case OpCode.FLIPRGOFF:
                {
                    bool blocked = !this.IsMovementUnrestricted && this.iupXCalled && this.iupYCalled;
                    int end = this.stack.Pop();
                    int start = this.stack.Pop();
                    if (blocked ||
                        (uint)end >= (uint)this.points.Count ||
                        (uint)start >= (uint)this.points.Count)
                    {
                        break;
                    }

                    for (int i = start; i <= end; i++)
                    {
                        this.points.Current[i].OnCurve = false;
                    }
                }

                break;
                case OpCode.SHP0:
                case OpCode.SHP1:
                {
                    // Compute the reference displacement once, then apply that same vector to
                    // each of the Loop target points in ZP2.
                    if (!this.TryComputeDisplacement((int)opcode, out _, out _, out int displacementX, out int displacementY))
                    {
                        // An invalid reference consumes the pending target operands and restores
                        // Loop to its one-operation default without moving any point.
                        for (int i = 0; i < this.state.Loop; i++)
                        {
                            this.stack.Pop();
                        }

                        this.state.Loop = 1;
                        break;
                    }

                    for (int i = 0; i < this.state.Loop; i++)
                    {
                        int pointIndex = this.stack.Pop();
                        if ((uint)pointIndex < (uint)this.zp2.Count)
                        {
                            this.MoveZp2Point(this.zp2, pointIndex, displacementX, displacementY, true);
                        }
                    }

                    this.state.Loop = 1;
                }

                break;
                case OpCode.SHPIX:
                {
                    // SHPIX supplies a scalar 26.6 distance along the freedom vector.
                    int magnitude = this.stack.Pop();
                    short freedomX = (short)FloatToF2Dot14(this.state.Freedom.X);
                    short freedomY = (short)FloatToF2Dot14(this.state.Freedom.Y);

                    // For each component, delta = (((distance * freedom) >> 13) + 1) >> 1.
                    // The two arithmetic shifts preserve signed rounding in the 26.6 result.
                    int dx = freedomX == 0 ? 0 : MultiplyF26Dot6ByF2Dot14(magnitude, freedomX);
                    int dy = freedomY == 0 ? 0 : MultiplyF26Dot6ByF2Dot14(magnitude, freedomY);
                    bool unrestricted = this.IsMovementUnrestricted;
                    bool postIUP = this.iupXCalled && this.iupYCalled;
                    bool inTwilight = this.zp0.IsTwilight || this.zp1.IsTwilight || this.zp2.IsTwilight;

                    for (int i = 0; i < this.state.Loop; i++)
                    {
                        int pointIndex = this.stack.Pop();
                        if ((uint)pointIndex >= (uint)this.zp2.Count)
                        {
                            continue;
                        }

                        if (!unrestricted)
                        {
                            // Backward compat mode: gated Y-only movement.
                            // Twilight zone always allowed; otherwise need composite+freeY or Y-touched.
                            // Post-IUP (0x7): nothing moves (MoveZp2Point blocks Y at post-IUP).
                            if (inTwilight ||
                                (!postIUP &&
                                 ((this.isComposite && this.state.Freedom.Y != 0) ||
                                  ((this.zp2.TouchState[pointIndex] & TouchState.Y) == TouchState.Y))))
                            {
                                this.MoveZp2Point(this.zp2, pointIndex, 0, dy, true);
                            }
                        }
                        else
                        {
                            // With the compatibility restrictions waived, both nonzero freedom
                            // components contribute to the movement.
                            this.MoveZp2Point(this.zp2, pointIndex, dx, dy, true);
                        }
                    }

                    this.state.Loop = 1;
                    break;
                }

                case OpCode.SHC0:
                case OpCode.SHC1:
                {
                    if (!this.TryComputeDisplacement((int)opcode, out Zone zone, out int point, out int displacementX, out int displacementY))
                    {
                        this.stack.Pop();
                        break;
                    }

                    int contour = this.stack.Pop();
                    int bounds = this.zp2.IsTwilight ? 1 : this.contours.Count;
                    if ((uint)contour >= (uint)bounds)
                    {
                        break;
                    }

                    int start = contour == 0 ? 0 : this.contours[contour - 1] + 1;
                    int count = this.zp2.IsTwilight ? this.zp2.Count : this.contours[contour] + 1;
                    ControlPoint[] current = this.zp2.Current;
                    TouchState[] states = this.zp2.TouchState;

                    for (int i = start; i < count; i++)
                    {
                        // Don't move the reference point
                        if (zone.Current != current || point != i)
                        {
                            this.MoveZp2Point(this.zp2, i, displacementX, displacementY, true);
                        }
                    }
                }

                break;
                case OpCode.SHZ0:
                case OpCode.SHZ1:
                {
                    // SHZ consumes the target-zone selector before resolving the reference
                    // displacement shared by every point in that zone.
                    int shzZone = this.stack.Pop();
                    if ((uint)shzZone >= 2)
                    {
                        break;
                    }

                    if (!this.TryComputeDisplacement((int)opcode, out Zone zone, out int point, out int displacementX, out int displacementY))
                    {
                        break;
                    }

                    int count = 0;
                    if (this.zp2.IsTwilight)
                    {
                        count = this.zp2.Count;
                    }
                    else if (this.contours.Count > 0)
                    {
                        count = this.contours[this.contours.Count - 1] + 1;
                    }

                    ControlPoint[] current = this.zp2.Current;
                    for (int i = 0; i < count; i++)
                    {
                        // Don't move the reference point
                        if (zone.Current != current || point != i)
                        {
                            this.MoveZp2Point(this.zp2, i, displacementX, displacementY, false);
                        }
                    }
                }

                break;
                case OpCode.MIAP0:
                case OpCode.MIAP1:
                {
                    float distance = this.ReadCvt();
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp0.Count)
                    {
                        // MIAP updates both reference-point registers even when the requested
                        // point lies outside ZP0.
                        this.state.Rp0 = pointIndex;
                        this.state.Rp1 = pointIndex;
                        break;
                    }

                    // this instruction is used in the CVT to set up twilight points with original values
                    if (this.zp0.IsTwilight)
                    {
                        Vector2 original = this.state.Freedom * distance;
                        this.zp0.Original[pointIndex].Point = original;
                        this.zp0.Current[pointIndex].Point = original;
                    }

                    // current position of the point along the projection vector
                    Vector2 point = this.zp0.GetCurrent(pointIndex);
                    float currentPos = this.Project(point);
                    if (opcode == OpCode.MIAP1)
                    {
                        // only use the CVT if we are above the cut-in point
                        if (Math.Abs(distance - currentPos) > this.state.ControlValueCutIn)
                        {
                            distance = currentPos;
                        }

                        distance = this.Round(distance);
                    }

                    this.MovePoint(this.zp0, pointIndex, distance - currentPos);
                    this.state.Rp0 = pointIndex;
                    this.state.Rp1 = pointIndex;
                }

                break;
                case OpCode.MDAP0:
                case OpCode.MDAP1:
                {
                    // An invalid MDAP point performs no movement and leaves references unchanged.
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp0.Count)
                    {
                        break;
                    }

                    Vector2 point = this.zp0.GetCurrent(pointIndex);
                    float distance = 0.0f;
                    if (opcode == OpCode.MDAP1)
                    {
                        distance = this.Project(point);
                        distance = this.Round(distance) - distance;
                    }

                    this.MovePoint(this.zp0, pointIndex, distance);
                    this.state.Rp0 = pointIndex;
                    this.state.Rp1 = pointIndex;
                }

                break;
                case OpCode.MSIRP0:
                case OpCode.MSIRP1:
                {
                    float targetDistance = this.stack.PopFloat();
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp1.Count ||
                        (uint)this.state.Rp0 >= (uint)this.zp0.Count)
                    {
                        break;
                    }

                    // if we're operating on the twilight zone, initialize the points
                    if (this.zp1.IsTwilight)
                    {
                        ControlPoint[] zp0Original = this.zp0.Original;
                        ControlPoint[] zp1Current = this.zp1.Current;
                        ControlPoint[] zp1Original = this.zp1.Original;
                        zp1Original[pointIndex].Point = zp0Original[this.state.Rp0].Point + (targetDistance * this.state.Freedom / this.fdotp);
                        zp1Current[pointIndex].Point = zp1Original[pointIndex].Point;
                    }

                    float currentDistance = this.Project(this.zp1.GetCurrent(pointIndex) - this.zp0.GetCurrent(this.state.Rp0));
                    this.MovePoint(this.zp1, pointIndex, targetDistance - currentDistance);

                    this.state.Rp1 = this.state.Rp0;
                    this.state.Rp2 = pointIndex;
                    if (opcode == OpCode.MSIRP1)
                    {
                        this.state.Rp0 = pointIndex;
                    }
                }

                break;
                case OpCode.IP:
                {
                    // RP1 is the interpolation origin. If it is invalid, consume every Loop
                    // target and restore Loop to one without changing coordinates.
                    if ((uint)this.state.Rp1 >= (uint)this.zp0.Count)
                    {
                        // The invalid-reference path still drains all target operands.
                        for (int i = 0; i < this.state.Loop; i++)
                        {
                            this.stack.Pop();
                        }

                        this.state.Loop = 1;
                        break;
                    }

                    bool twilight = this.zp0.IsTwilight || this.zp1.IsTwilight || this.zp2.IsTwilight;
                    bool useScaledOriginal = twilight || this.isComposite;
                    bool moveXDirect = this.state.Freedom == Vector2.UnitX &&
                        this.state.Projection == Vector2.UnitX &&
                        this.state.DualProjection == Vector2.UnitX;
                    bool moveYDirect = this.state.Freedom == Vector2.UnitY &&
                        this.state.Projection == Vector2.UnitY &&
                        this.state.DualProjection == Vector2.UnitY;

                    // Axis-aligned freedom, projection, and dual-projection vectors allow IP
                    // to interpolate the selected coordinate directly in integer units. This
                    // avoids two fixed-point projections and their independent rounding.
                    if ((moveXDirect || moveYDirect) && (uint)this.state.Rp2 < (uint)this.zp1.Count)
                    {
                        bool xAxis = moveXDirect;
                        int originalBaseCoordinate = ReadInterpolationCoordinate(in this.zp0, this.state.Rp1, xAxis, useScaledOriginal);
                        int originalRangeCoordinate = ReadInterpolationCoordinate(in this.zp1, this.state.Rp2, xAxis, useScaledOriginal);
                        int directOriginalRange = originalRangeCoordinate - originalBaseCoordinate;
                        if (directOriginalRange != 0)
                        {
                            int currentBaseCoordinate = ReadCurrentCoordinate(in this.zp0, this.state.Rp1, xAxis);
                            int currentRangeCoordinate = ReadCurrentCoordinate(in this.zp1, this.state.Rp2, xAxis);
                            int directCurrentRange = currentRangeCoordinate - currentBaseCoordinate;

                            for (int i = 0; i < this.state.Loop; i++)
                            {
                                int pointIndex = this.stack.Pop();
                                if ((uint)pointIndex >= (uint)this.zp2.Count)
                                {
                                    continue;
                                }

                                int originalCoordinate = ReadInterpolationCoordinate(in this.zp2, pointIndex, xAxis, useScaledOriginal);
                                int coordinate = currentBaseCoordinate + MultiplyDivideRounded(originalCoordinate - originalBaseCoordinate, directCurrentRange, directOriginalRange);
                                WriteCurrentCoordinate(ref this.zp2, pointIndex, xAxis, coordinate);
                            }

                            this.state.Loop = 1;
                            break;
                        }
                    }

                    Vector2 originalBase = useScaledOriginal ? this.zp0.GetOriginal(this.state.Rp1) : this.zp0.GetUnscaled(this.state.Rp1);
                    Vector2 currentBase = this.zp0.GetCurrent(this.state.Rp1);

                    // An invalid RP2 defines zero original and current ranges. Targets are
                    // still consumed and take the zero-range behavior below.
                    float originalRange = 0;
                    float currentRange = 0;
                    if ((uint)this.state.Rp2 < (uint)this.zp1.Count)
                    {
                        Vector2 rangeOriginal = useScaledOriginal ? this.zp1.GetOriginal(this.state.Rp2) : this.zp1.GetUnscaled(this.state.Rp2);
                        originalRange = this.DualProject(rangeOriginal - originalBase);
                        currentRange = this.Project(this.zp1.GetCurrent(this.state.Rp2) - currentBase);
                    }

                    for (int i = 0; i < this.state.Loop; i++)
                    {
                        int pointIndex = this.stack.Pop();
                        if ((uint)pointIndex >= (uint)this.zp2.Count)
                        {
                            continue;
                        }

                        Vector2 point = this.zp2.GetCurrent(pointIndex);
                        float currentDistance = this.Project(point - currentBase);
                        Vector2 pointOriginal = useScaledOriginal ? this.zp2.GetOriginal(pointIndex) : this.zp2.GetUnscaled(pointIndex);
                        float originalDistance = this.DualProject(pointOriginal - originalBase);

                        float newDistance = 0.0f;
                        if (originalDistance != 0.0f)
                        {
                            // a range of 0.0f is invalid according to the spec (would result in a div by zero)
                            if (originalRange == 0.0f)
                            {
                                newDistance = originalDistance;
                            }
                            else
                            {
                                newDistance = MulDivRound(originalDistance, currentRange, originalRange);
                            }
                        }

                        this.MovePoint(this.zp2, pointIndex, newDistance - currentDistance);
                    }

                    this.state.Loop = 1;
                }

                break;
                case OpCode.ALIGNRP:
                {
                    // RP0 supplies the alignment coordinate. An invalid reference consumes
                    // every Loop target and restores Loop without moving a point.
                    if ((uint)this.state.Rp0 >= (uint)this.zp0.Count)
                    {
                        for (int i = 0; i < this.state.Loop; i++)
                        {
                            this.stack.Pop();
                        }

                        this.state.Loop = 1;
                        break;
                    }

                    for (int i = 0; i < this.state.Loop; i++)
                    {
                        int pointIndex = this.stack.Pop();
                        if ((uint)pointIndex >= (uint)this.zp1.Count)
                        {
                            continue;
                        }

                        Vector2 p1 = this.zp1.GetCurrent(pointIndex);
                        Vector2 p2 = this.zp0.GetCurrent(this.state.Rp0);
                        this.MovePoint(this.zp1, pointIndex, -this.Project(p1 - p2));
                    }

                    this.state.Loop = 1;
                }

                break;
                case OpCode.ALIGNPTS:
                {
                    // The upper stack operand names p2 in ZP0 and the lower names p1 in ZP1.
                    // Moving each by half the projected separation meets them at the midpoint.
                    int p2 = this.stack.Pop();
                    int p1 = this.stack.Pop();
                    if ((uint)p1 >= (uint)this.zp1.Count ||
                        (uint)p2 >= (uint)this.zp0.Count)
                    {
                        break;
                    }

                    float distance = this.Project(this.zp0.GetCurrent(p2) - this.zp1.GetCurrent(p1)) / 2;
                    this.MovePoint(this.zp1, p1, distance);
                    this.MovePoint(this.zp0, p2, -distance);
                }

                break;
                case OpCode.UTP:
                {
                    int pointIndex = this.stack.Pop();
                    if ((uint)pointIndex >= (uint)this.zp0.Count)
                    {
                        break;
                    }

                    this.zp0.TouchState[pointIndex] &= ~this.GetTouchState();
                    break;
                }

                case OpCode.IUP0:
                case OpCode.IUP1:
                {
                    // Restricted mode freezes interpolation after both axes have completed.
                    // Unrestricted mode permits another IUP because intervening instructions
                    // may have touched additional points.
                    if (!this.IsMovementUnrestricted && this.iupXCalled && this.iupYCalled)
                    {
                        break;
                    }

                    unsafe
                    {
                        // bail if no contours (empty outline)
                        if (this.contours.Count == 0)
                        {
                            break;
                        }

                        fixed (ControlPoint* currentPtr = this.points.Current)
                        {
                            fixed (ControlPoint* originalPtr = this.points.Original)
                            {
                                fixed (ControlPoint* unscaledPtr = this.points.Unscaled)
                                {
                                    // opcode controls whether we care about X or Y direction
                                    // do some pointer trickery so we can operate on the
                                    // points in a direction-agnostic manner
                                    TouchState touchMask;
                                    byte* current;
                                    byte* original;
                                    byte* interpolationDomain;
                                    if (opcode == OpCode.IUP0)
                                    {
                                        this.iupYCalled = true;
                                        touchMask = TouchState.Y;
                                        current = (byte*)&currentPtr->Point.Y;
                                        original = (byte*)&originalPtr->Point.Y;
                                        interpolationDomain = this.isComposite ? original : (byte*)&unscaledPtr->Point.Y;
                                    }
                                    else
                                    {
                                        this.iupXCalled = true;
                                        touchMask = TouchState.X;
                                        current = (byte*)&currentPtr->Point.X;
                                        original = (byte*)&originalPtr->Point.X;
                                        interpolationDomain = this.isComposite ? original : (byte*)&unscaledPtr->Point.X;
                                    }

                                    // Composite components have already undergone component
                                    // transforms, so their interpolation domain is the scaled 26.6
                                    // original array. A simple glyph instead uses integral font
                                    // units, avoiding ratio distortion from pre-rounded coordinates.
                                    bool interpolationDomainIsF26Dot6 = this.isComposite;

                                    int point = 0;
                                    for (int i = 0; i < this.contours.Count; i++)
                                    {
                                        ushort endPoint = this.contours[i];
                                        int firstPoint = point;
                                        int firstTouched = -1;
                                        int lastTouched = -1;

                                        for (; point <= endPoint; point++)
                                        {
                                            // check whether this point has been touched
                                            if ((this.points.TouchState[point] & touchMask) != 0)
                                            {
                                                // if this is the first touched point in the contour, note it and continue
                                                if (firstTouched < 0)
                                                {
                                                    firstTouched = point;
                                                    lastTouched = point;
                                                    continue;
                                                }

                                                // otherwise, interpolate all untouched points
                                                // between this point and our last touched point
                                                InterpolatePoints(current, original, interpolationDomain, interpolationDomainIsF26Dot6, lastTouched + 1, point - 1, lastTouched, point);
                                                lastTouched = point;
                                            }
                                        }

                                        // check if we had any touched points at all in this contour
                                        if (firstTouched >= 0)
                                        {
                                            // there are two cases left to handle:
                                            // 1. there was only one touched point in the whole contour, in
                                            //    which case we want to shift everything relative to that one
                                            // 2. several touched points, in which case handle the gap from the
                                            //    beginning to the first touched point and the gap from the last
                                            //    touched point to the end of the contour
                                            if (lastTouched == firstTouched)
                                            {
                                                int delta = ReadF26Dot6(current, lastTouched) - ReadF26Dot6(original, lastTouched);
                                                if (delta != 0)
                                                {
                                                    for (int j = firstPoint; j < lastTouched; j++)
                                                    {
                                                        WriteF26Dot6(current, j, ReadF26Dot6(current, j) + delta);
                                                    }

                                                    for (int j = lastTouched + 1; j <= endPoint; j++)
                                                    {
                                                        WriteF26Dot6(current, j, ReadF26Dot6(current, j) + delta);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                InterpolatePoints(current, original, interpolationDomain, interpolationDomainIsF26Dot6, lastTouched + 1, endPoint, lastTouched, firstTouched);
                                                if (firstTouched > 0)
                                                {
                                                    InterpolatePoints(current, original, interpolationDomain, interpolationDomainIsF26Dot6, firstPoint, firstTouched - 1, lastTouched, firstTouched);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    break;
                }

                case OpCode.ISECT:
                {
                    int ib1 = this.stack.Pop();
                    int ib0 = this.stack.Pop();
                    int ia1 = this.stack.Pop();
                    int ia0 = this.stack.Pop();
                    int index = this.stack.Pop();
                    if ((uint)ib0 >= (uint)this.zp0.Count ||
                        (uint)ib1 >= (uint)this.zp0.Count ||
                        (uint)ia0 >= (uint)this.zp1.Count ||
                        (uint)ia1 >= (uint)this.zp1.Count ||
                        (uint)index >= (uint)this.zp2.Count)
                    {
                        break;
                    }

                    Vector2 b1 = this.zp0.GetCurrent(ib1);
                    Vector2 b0 = this.zp0.GetCurrent(ib0);
                    Vector2 a1 = this.zp1.GetCurrent(ia1);
                    Vector2 a0 = this.zp1.GetCurrent(ia0);

                    int b0X = FloatToF26Dot6(b0.X);
                    int b0Y = FloatToF26Dot6(b0.Y);
                    int bDx = unchecked(FloatToF26Dot6(b1.X) - b0X);
                    int bDy = unchecked(FloatToF26Dot6(b1.Y) - b0Y);
                    int a0X = FloatToF26Dot6(a0.X);
                    int a0Y = FloatToF26Dot6(a0.Y);
                    int aDx = unchecked(FloatToF26Dot6(a1.X) - a0X);
                    int aDy = unchecked(FloatToF26Dot6(a1.Y) - a0Y);
                    int intersectionX;
                    int intersectionY;

                    // Keep both lines in signed 26.6. Perpendicular axis-aligned lines meet at
                    // (vertical.X, horizontal.Y), so these cases avoid division entirely.
                    if (bDy == 0 && aDx == 0)
                    {
                        intersectionX = a0X;
                        intersectionY = b0Y;
                    }
                    else if (bDy != 0 && bDx == 0 && aDy == 0)
                    {
                        intersectionX = b0X;
                        intersectionY = a0Y;
                    }
                    else
                    {
                        int numerator;
                        int denominator;

                        if (bDy == 0)
                        {
                            numerator = unchecked(a0Y - b0Y);
                            denominator = unchecked(-aDy);
                        }
                        else if (bDx == 0)
                        {
                            numerator = unchecked(a0X - b0X);
                            denominator = unchecked(-aDx);
                        }
                        else
                        {
                            int absoluteBDx = bDx < 0 ? unchecked(-bDx) : bDx;
                            int absoluteBDy = bDy < 0 ? unchecked(-bDy) : bDy;

                            // Divide through B's larger-magnitude component to limit the two
                            // cross-product intermediates. Each quotient uses sign-aware
                            // half-divisor rounding before the final intersection division.
                            if (absoluteBDx < absoluteBDy)
                            {
                                int yDifference = unchecked(a0Y - b0Y);
                                int xOffset = unchecked((int)CompensatedDivide(bDy, (long)yDifference * bDx));
                                int projectedADy = unchecked((int)CompensatedDivide(bDy, (long)aDy * bDx));
                                numerator = unchecked((b0X - a0X) + xOffset);
                                denominator = unchecked(aDx - projectedADy);
                            }
                            else
                            {
                                int xDifference = unchecked(a0X - b0X);
                                int yOffset = unchecked((int)CompensatedDivide(bDx, (long)xDifference * bDy));
                                int projectedADx = unchecked((int)CompensatedDivide(bDx, (long)aDx * bDy));
                                numerator = unchecked((a0Y - b0Y) - yOffset);
                                denominator = unchecked(projectedADx - aDy);
                            }
                        }

                        if (denominator == 0)
                        {
                            // Parallel lines use the average of their integer midpoints. Each
                            // direction component is halved by arithmetic shift before the
                            // midpoint coordinates are added, fixing negative odd-value rounding.
                            intersectionX = unchecked(((aDx >> 1) + b0X + (bDx >> 1) + a0X) >> 1);
                            intersectionY = unchecked(((aDy >> 1) + b0Y + (bDy >> 1) + a0Y) >> 1);
                        }
                        else
                        {
                            intersectionX = unchecked(a0X + (int)CompensatedDivide(denominator, (long)aDx * numerator));
                            intersectionY = unchecked(a0Y + (int)CompensatedDivide(denominator, (long)aDy * numerator));
                        }
                    }

                    this.zp2.Current[index].Point = new Vector2(F26Dot6ToFloat(intersectionX), F26Dot6ToFloat(intersectionY));
                    this.zp2.TouchState[index] = TouchState.Both;
                }

                break;

                // ==== STACK MANAGEMENT ====
                case OpCode.DUP:
                {
                    this.stack.Duplicate();
                    break;
                }

                case OpCode.POP:
                {
                    this.stack.Pop();
                    break;
                }

                case OpCode.CLEAR:
                {
                    this.stack.Clear();
                    break;
                }

                case OpCode.SWAP:
                {
                    this.stack.Swap();
                    break;
                }

                case OpCode.DEPTH:
                {
                    this.stack.Depth();
                    break;
                }

                case OpCode.CINDEX:
                {
                    this.stack.Copy();
                    break;
                }

                case OpCode.MINDEX:
                {
                    this.stack.Move();
                    break;
                }

                case OpCode.ROLL:
                {
                    this.stack.Roll();
                    break;
                }

                // ==== FLOW CONTROL ====
                case OpCode.IF:
                {
                    // value is false; jump to the next else block or endif marker
                    // otherwise, we don't have to do anything; we'll keep executing this block
                    if (!this.stack.PopBool())
                    {
                        int indent = 1;
                        while (indent > 0)
                        {
                            opcode = SkipNext(ref stream);
                            switch (opcode)
                            {
                                case OpCode.IF:
                                    indent++;
                                    break;
                                case OpCode.EIF:
                                    indent--;
                                    break;
                                case OpCode.ELSE:
                                    if (indent == 1)
                                    {
                                        indent = 0;
                                    }

                                    break;
                            }
                        }
                    }
                }

                break;
                case OpCode.ELSE:
                {
                    // assume we hit the true statement of some previous if block
                    // if we had hit false, we would have jumped over this
                    int indent = 1;
                    while (indent > 0)
                    {
                        opcode = SkipNext(ref stream);
                        switch (opcode)
                        {
                            case OpCode.IF:
                                indent++;
                                break;
                            case OpCode.EIF:
                                indent--;
                                break;
                        }
                    }
                }

                break;
                case OpCode.EIF: /* nothing to do */
                {
                    break;
                }

                case OpCode.JROT:
                case OpCode.JROF:
                {
                    if (this.stack.PopBool() == (opcode == OpCode.JROT))
                    {
                        int offset = this.stack.Pop();
                        if (offset < 0 && ++this.negJumpCounter > this.negJumpCounterMax)
                        {
                            return;
                        }

                        stream.Jump(offset - 1);
                    }
                    else
                    {
                        this.stack.Pop();    // ignore the offset
                    }
                }

                break;
                case OpCode.JMPR:
                {
                    int offset = this.stack.Pop();
                    if (offset < 0 && ++this.negJumpCounter > this.negJumpCounterMax)
                    {
                        // A backward jump consumes its data-dependent budget; exceeding it
                        // terminates the current instruction stream.
                        return;
                    }

                    stream.Jump(offset - 1);
                    break;
                }

                // ==== LOGICAL OPS ====
                case OpCode.LT:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a < b);
                }

                break;
                case OpCode.LTEQ:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a <= b);
                }

                break;
                case OpCode.GT:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a > b);
                }

                break;
                case OpCode.GTEQ:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a >= b);
                }

                break;
                case OpCode.EQ:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a == b);
                }

                break;
                case OpCode.NEQ:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a != b);
                }

                break;
                case OpCode.AND:
                {
                    bool b = this.stack.PopBool();
                    bool a = this.stack.PopBool();
                    this.stack.Push(a && b);
                }

                break;
                case OpCode.OR:
                {
                    bool b = this.stack.PopBool();
                    bool a = this.stack.PopBool();
                    this.stack.Push(a || b);
                }

                break;
                case OpCode.NOT:
                {
                    this.stack.Push(!this.stack.PopBool());
                    break;
                }

                case OpCode.ODD:
                {
                    int value = (int)this.Round(this.stack.PopFloat());
                    this.stack.Push(value % 2 != 0);
                }

                break;
                case OpCode.EVEN:
                {
                    int value = (int)this.Round(this.stack.PopFloat());
                    this.stack.Push(value % 2 == 0);
                }

                break;

                // ==== ARITHMETIC ====
                case OpCode.ADD:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a + b);
                }

                break;
                case OpCode.SUB:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    this.stack.Push(a - b);
                }

                break;
                case OpCode.DIV:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    if (b == 0)
                    {
                        // DIV by zero terminates the current instruction stream.
                        return;
                    }

                    long result = ((long)a << 6) / b;
                    this.stack.Push((int)result);
                }

                break;
                case OpCode.MUL:
                {
                    int b = this.stack.Pop();
                    int a = this.stack.Pop();
                    int result;
                    if (a is >= -0xB504 and <= 0xB504 &&
                        b is >= -0xB504 and <= 0xB504)
                    {
                        // These operands cannot overflow a signed 32-bit product. Convert the
                        // 52.12 product back to 26.6 as (a*b + 32) >> 6; the arithmetic shift
                        // sends exact negative halves toward positive infinity.
                        result = unchecked((a * b) + 0x20) >> 6;
                    }
                    else
                    {
                        // The large-operand path forms the unsigned product from 16-bit
                        // limbs, rounds its magnitude, then reapplies the product sign.
                        bool negative = (a < 0) != (b < 0);
                        uint magnitudeA = a < 0 ? unchecked((uint)-a) : (uint)a;
                        uint magnitudeB = b < 0 ? unchecked((uint)-b) : (uint)b;
                        uint magnitude = unchecked((uint)((((ulong)magnitudeA * magnitudeB) + 0x20) >> 6));
                        result = negative ? unchecked((int)(0U - magnitude)) : unchecked((int)magnitude);
                    }

                    this.stack.Push(result);
                }

                break;
                case OpCode.ABS:
                {
                    this.stack.Push(Math.Abs(this.stack.Pop()));
                    break;
                }

                case OpCode.NEG:
                {
                    this.stack.Push(-this.stack.Pop());
                    break;
                }

                case OpCode.FLOOR:
                {
                    this.stack.Push(this.stack.Pop() & ~63);
                    break;
                }

                case OpCode.CEILING:
                {
                    this.stack.Push((this.stack.Pop() + 63) & ~63);
                    break;
                }

                case OpCode.MAX:
                {
                    this.stack.Push(Math.Max(this.stack.Pop(), this.stack.Pop()));
                    break;
                }

                case OpCode.MIN:
                {
                    this.stack.Push(Math.Min(this.stack.Pop(), this.stack.Pop()));
                    break;
                }

                // ==== FUNCTIONS ====
                case OpCode.FDEF:
                {
                    if (!allowFunctionDefs || inFunction)
                    {
                        return;
                    }

                    this.functions[this.stack.Pop()] = stream.ToMemory();
                    while (SkipNext(ref stream) != OpCode.ENDF)
                    {
                    }
                }

                break;
                case OpCode.IDEF:
                {
                    if (!allowFunctionDefs || inFunction)
                    {
                        return;
                    }

                    this.instructionDefs[this.stack.Pop()] = stream.ToMemory();
                    while (SkipNext(ref stream) != OpCode.ENDF)
                    {
                    }
                }

                break;
                case OpCode.ENDF:
                {
                    if (!inFunction)
                    {
                        return;
                    }

                    return;
                }

                case OpCode.CALL:
                case OpCode.LOOPCALL:
                {
                    this.callStackSize++;
                    if (this.callStackSize > MaxCallStack)
                    {
                        // Function nesting beyond the fixed call-stack limit terminates execution.
                        return;
                    }

                    int funcIndex = this.stack.Pop();
                    if ((uint)funcIndex >= (uint)this.functions.Length)
                    {
                        // A function index outside the maxp-defined table terminates execution.
                        return;
                    }

                    InstructionStream function = this.functions[funcIndex];
                    int count = opcode == OpCode.LOOPCALL ? this.stack.Pop() : 1;

                    // CALL contributes only to nesting depth. LOOPCALL additionally consumes
                    // one repeated-call budget unit per requested invocation.
                    if (opcode == OpCode.LOOPCALL)
                    {
                        this.loopcallCounter += count;
                        if (this.loopcallCounter > this.loopcallCounterMax)
                        {
                            // Reject the whole repeated call before executing any body when its
                            // count would exceed the program's data-dependent budget.
                            return;
                        }
                    }

                    if (count > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            this.Execute(function.ToStack(), true, false);
                        }
                    }

                    this.callStackSize--;
                }

                break;

                // ==== ROUNDING ====
                // we don't have "engine compensation" so the variants are unnecessary
                case OpCode.ROUND0:
                case OpCode.ROUND1:
                case OpCode.ROUND2:
                case OpCode.ROUND3:
                {
                    this.stack.Push(this.Round(this.stack.PopFloat()));
                    break;
                }

                case OpCode.NROUND0:
                case OpCode.NROUND1:
                case OpCode.NROUND2:
                case OpCode.NROUND3:
                {
                    break;
                }

                // ==== DELTA EXCEPTIONS ====
                case OpCode.DELTAC1:
                case OpCode.DELTAC2:
                case OpCode.DELTAC3:
                {
                    int last = this.stack.Pop();
                    for (int i = 1; i <= last; i++)
                    {
                        int cvtIndex = this.stack.Pop();
                        int arg = this.stack.Pop();

                        // upper 4 bits of the 8-bit arg is the relative ppem
                        // the opcode specifies the base to add to the ppem
                        int triggerPpem = (arg >> 4) & 0xF;
                        triggerPpem += (opcode - OpCode.DELTAC1) * 16;
                        triggerPpem += this.state.DeltaBase;

                        // if the current ppem matches the trigger, apply the exception
                        if (this.ppem == triggerPpem)
                        {
                            // the lower 4 bits of the arg is the amount to shift
                            // it's encoded such that 0 isn't an allowable value (who wants to shift by 0 anyway?)
                            int amount = (arg & 0xF) - 8;
                            if (amount >= 0)
                            {
                                amount++;
                            }

                            amount *= 1 << (6 - this.state.DeltaShift);

                            // Invalid CVT indices are ignored after their encoded operands have
                            // been consumed, preserving stack and loop progress.
                            if ((uint)cvtIndex < (uint)this.controlValueTable.Length)
                            {
                                this.controlValueTable[cvtIndex] += F26Dot6ToFloat(amount);
                            }
                        }
                    }
                }

                break;
                case OpCode.DELTAP1:
                case OpCode.DELTAP2:
                case OpCode.DELTAP3:
                {
                    // In restricted mode DELTAP may move only before both IUP passes and only
                    // when either a composite uses a nonzero Y freedom component or the target
                    // was already Y-touched. Unrestricted mode applies the encoded movement.
                    bool postIUP = this.iupXCalled && this.iupYCalled;
                    bool composite = this.isComposite;
                    int last = this.stack.Pop();
                    for (int i = 1; i <= last; i++)
                    {
                        int pointIndex = this.stack.Pop();
                        int arg = this.stack.Pop();
                        if ((uint)pointIndex >= (uint)this.zp0.Count)
                        {
                            continue;
                        }

                        // upper 4 bits of the 8-bit arg is the relative ppem
                        // the opcode specifies the base to add to the ppem
                        int triggerPpem = (arg >> 4) & 0xF;
                        triggerPpem += this.state.DeltaBase;
                        if (opcode != OpCode.DELTAP1)
                        {
                            triggerPpem += (opcode - OpCode.DELTAP2 + 1) * 16;
                        }

                        // if the current ppem matches the trigger, apply the exception
                        if (this.ppem == triggerPpem)
                        {
                            // the lower 4 bits of the arg is the amount to shift
                            // it's encoded such that 0 isn't an allowable value (who wants to shift by 0 anyway?)
                            int amount = (arg & 0xF) - 8;
                            if (amount >= 0)
                            {
                                amount++;
                            }

                            amount *= 1 << (6 - this.state.DeltaShift);

                            if (this.IsMovementUnrestricted)
                            {
                                this.MovePoint(this.zp0, pointIndex, F26Dot6ToFloat(amount));
                            }
                            else
                            {
                                // Compat mode: gate on !postIUP AND (composite+freeY or Y-touched).
                                TouchState state = this.zp0.TouchState[pointIndex];
                                if (!postIUP &&
                                    ((composite && this.state.Freedom.Y != 0) ||
                                     ((state & TouchState.Y) == TouchState.Y)))
                                {
                                    this.MovePoint(this.zp0, pointIndex, F26Dot6ToFloat(amount));
                                }
                            }
                        }
                    }
                }

                break;

                // ==== MISCELLANEOUS ====
                case OpCode.DEBUG:
                {
                    this.stack.Pop();
                    break;
                }

                case OpCode.GETINFO:
                {
                    // Report the interpreter identity for the active mode. A font's prep and
                    // glyph programs branch on the reported version to decide which grid
                    // fitting path to run: a higher version signals a full interpreter that
                    // executes every instruction on both axes, so the font runs its classic
                    // bidirectional path; the lower version signals the lean interpreter that
                    // restricts point movement, so the font runs its reduced path and may skip
                    // horizontal hinting and small size delta exceptions. Full mode reports the
                    // higher version to unlock the classic path; Standard mode reports the lean
                    // version it was designed against.
                    bool full = this.hintingMode == HintingMode.Full;
                    int selector = this.stack.Pop();
                    int result = 0;

                    // Selector bit 0: interpreter version.
                    if ((selector & 0x1) != 0)
                    {
                        result = full ? 42 : 40;
                    }

                    // Selector bits 1-2: rotation/stretching. Reported only under a rotating or
                    // stretching transform, which this interpreter never applies.

                    // Selector bit 3 requests variation-font status. Set result bit 10 when
                    // normalized variation coordinates are present.
                    if ((selector & 0x8) != 0 && this.normalizedAxisCoordinates is not null)
                    {
                        result |= 1 << 10;
                    }

                    // Selector bit 5: grayscale rendering. Full mode reports FALSE, the
                    // classic bi-level identity. A font gates its per size delta exceptions on
                    // this bit, applying the crisp bi-level tweaks when it is clear and
                    // suppressing them when it is set. Full mode reproduces bi-level grid
                    // fitting, so the exceptions must fire; reporting grayscale here would
                    // suppress them and leave features such as open terminals a pixel off the
                    // bi-level result. Standard mode also reports false.
                    if (!full)
                    {
                        // Selector bit 6: subpixel hinting is available (v40 default).
                        if ((selector & 0x40) != 0)
                        {
                            result |= 1 << 13;
                        }

                        // Selector bit 10: subpixel positioned.
                        if ((selector & 0x400) != 0)
                        {
                            result |= 1 << 17;
                        }

                        // Selector bit 11: symmetrical smoothing.
                        if ((selector & 0x800) != 0)
                        {
                            result |= 1 << 18;
                        }

                        // Selector bit 12: ClearType hinting and grayscale rendering.
                        // Standard mode uses symmetric grayscale rendering, so the
                        // ClearType-and-grayscale capability bit is always reported when asked.
                        // ClearType-era prep programs branch on this to select grayscale-safe
                        // hinting instead of LCD-specific pixel tweaks.
                        if ((selector & 0x1000) != 0)
                        {
                            result |= 1 << 19;
                        }
                    }

                    this.stack.Push(result);
                }

                break;

                case OpCode.GETVARIATION:
                {
                    // Push each normalized [-1,1] axis coordinate as a signed F2.14 integer:
                    // round(coordinate * 2^14).
                    if (this.normalizedAxisCoordinates is not null)
                    {
                        for (int i = 0; i < this.normalizedAxisCoordinates.Length; i++)
                        {
                            this.stack.Push((int)Math.Round(this.normalizedAxisCoordinates[i] * 16384));
                        }
                    }

                    break;
                }

                case OpCode.GETDATA:
                {
                    // GETDATA's defined compatibility value is 17.
                    this.stack.Push(17);
                    break;
                }

                default:
                {
                    if (opcode >= OpCode.MIRP)
                    {
                        this.MoveIndirectRelative(opcode - OpCode.MIRP);
                    }
                    else if (opcode >= OpCode.MDRP)
                    {
                        this.MoveDirectRelative(opcode - OpCode.MDRP);
                    }
                    else
                    {
                        // check if this is a runtime-defined opcode
                        int index = (int)opcode;
                        if (index > this.instructionDefs.Length || !this.instructionDefs[index].IsValid)
                        {
                            // An undefined opcode terminates the current instruction stream.
                            return;
                        }

                        this.callStackSize++;
                        if (this.callStackSize > MaxCallStack)
                        {
                            return;
                        }

                        this.Execute(this.instructionDefs[index].ToStack(), true, false);
                        this.callStackSize--;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Pops a CVT index from the stack and returns the corresponding value.
    /// Returns 0 for out-of-bounds indices after consuming the index operand.
    /// </summary>
    /// <returns>The selected CVT value in device pixels, or zero for an invalid index.</returns>
    private float ReadCvt()
    {
        int loc = this.stack.Pop();
        if ((uint)loc >= (uint)this.controlValueTable.Length)
        {
            return 0;
        }

        return this.controlValueTable[loc];
    }

    /// <summary>
    /// Recomputes the cached dot product of the freedom and projection vectors.
    /// Must be called whenever either vector changes.
    /// </summary>
    private void OnVectorsUpdated()
    {
        short freedomX = (short)FloatToF2Dot14(this.state.Freedom.X);
        short freedomY = (short)FloatToF2Dot14(this.state.Freedom.Y);
        short projectionX = (short)FloatToF2Dot14(this.state.Projection.X);
        short projectionY = (short)FloatToF2Dot14(this.state.Projection.Y);

        // Each F2.14 component product rounds independently as (a*b + 0x2000) >> 14.
        // Their sum is narrowed to sixteen bits before the near-perpendicular test.
        int xProduct = ((freedomX * projectionX) + 0x2000) >> 14;
        int yProduct = ((freedomY * projectionY) + 0x2000) >> 14;
        short dot = unchecked((short)(xProduct + yProduct));
        if ((ushort)(dot + 0x3FF) <= 0x7FE)
        {
            // A dot product within 0x3FF of zero would amplify movement excessively when
            // used as a divisor. Preserve its sign and clamp its magnitude to 0x4000 (1.0).
            dot = dot < 0 ? (short)-0x4000 : (short)0x4000;
        }

        this.fdotp = F2Dot14ToFloat(dot);
    }

    /// <summary>
    /// Sets the freedom vector to one of the coordinate axes (SFVTCA).
    /// </summary>
    /// <param name="axis">0 for the Y-axis, 1 for the X-axis.</param>
    private void SetFreedomVectorToAxis(int axis)
    {
        this.state.Freedom = axis == 0 ? Vector2.UnitY : Vector2.UnitX;
        this.OnVectorsUpdated();
    }

    /// <summary>
    /// Sets the projection and dual-projection vectors to one of the coordinate axes (SPVTCA).
    /// </summary>
    /// <param name="axis">0 for the Y-axis, 1 for the X-axis.</param>
    private void SetProjectionVectorToAxis(int axis)
    {
        this.state.Projection = axis == 0 ? Vector2.UnitY : Vector2.UnitX;
        this.state.DualProjection = this.state.Projection;

        this.OnVectorsUpdated();
    }

    /// <summary>
    /// Sets a projection or freedom vector to the direction of a line between two points
    /// (SPVTL/SFVTL/SDPVTL). The mode's low bit selects the perpendicular direction.
    /// </summary>
    /// <param name="mode">0=SPVTL0, 1=SPVTL1, 2=SFVTL0, 3=SFVTL1.</param>
    /// <param name="dual">When <see langword="true"/>, also sets the dual-projection vector from original coordinates.</param>
    private void SetVectorToLine(int mode, bool dual)
    {
        int index1 = this.stack.Pop();
        int index2 = this.stack.Pop();
        Vector2 p1 = this.zp2.GetCurrent(index1);
        Vector2 p2 = this.zp1.GetCurrent(index2);

        Vector2 line = p2 - p1;

        // The low mode bit rotates (x,y) to (-y,x), selecting the perpendicular direction,
        // before the 26.6 line is normalized into signed F2.14 components.
        if ((mode & 0x1) != 0)
        {
            line = new Vector2(-line.Y, line.X);
        }

        line = NormalizeF26Dot6(line);

        if (mode >= 2)
        {
            this.state.Freedom = line;
        }
        else
        {
            this.state.Projection = line;
            this.state.DualProjection = line;
        }

        // set the dual projection vector using original points
        if (dual)
        {
            p1 = this.zp2.GetOriginal(index1);
            p2 = this.zp1.GetOriginal(index2);
            line = p2 - p1;

            if ((mode & 0x1) != 0)
            {
                line = new Vector2(-line.Y, line.X);
            }

            this.state.DualProjection = NormalizeF26Dot6(line);
        }

        this.OnVectorsUpdated();
    }

    /// <summary>
    /// Pops a zone index from the stack and returns the corresponding zone.
    /// Returns <see langword="false"/> for indices other than glyph zone 1 or twilight zone 0.
    /// </summary>
    /// <param name="zone">Receives the selected point zone when the index is valid.</param>
    /// <returns><see langword="true"/> for zone index 0 or 1; otherwise <see langword="false"/>.</returns>
    private bool TryGetZoneFromStack(out Zone zone)
    {
        int zoneIndex = this.stack.Pop();
        switch (zoneIndex)
        {
            case 0:
                zone = this.twilight;
                return true;
            case 1:
                zone = this.points;
                return true;
            default:
                // Invalid zone pointers consume their operand but leave the destination
                // zone register unchanged.
                zone = default;
                return false;
        }
    }

    /// <summary>
    /// Configures super-rounding parameters from a packed mode byte (SROUND/S45ROUND).
    /// Bits 7-6 select the period multiplier, bits 5-4 the phase, and bits 3-0 the threshold.
    /// </summary>
    /// <param name="period">Base period: 1.0 for SROUND, sqrt(2)/2 for S45ROUND.</param>
    private void SetSuperRound(float period)
    {
        int mode = this.stack.Pop();
        this.roundPeriod = (mode & 0xC0) switch
        {
            0 => period / 2,
            0x40 => period,
            0x80 => period * 2,
            _ => period * 2, // The reserved encoding uses the largest period.
        };

        // bits 5-4 are the phase
        switch (mode & 0x30)
        {
            case 0:
                this.roundPhase = 0;
                break;
            case 0x10:
                this.roundPhase = this.roundPeriod / 4;
                break;
            case 0x20:
                this.roundPhase = this.roundPeriod / 2;
                break;
            case 0x30:
                this.roundPhase = this.roundPeriod * 3 / 4;
                break;
        }

        // bits 3-0 are the threshold
        if ((mode & 0xF) == 0)
        {
            this.roundThreshold = this.roundPeriod - 1;
        }
        else
        {
            this.roundThreshold = ((mode & 0xF) - 4) * this.roundPeriod / 8;
        }
    }

    /// <summary>
    /// Move Indirect Relative Point (MIRP). Moves a point so that its distance from RP0
    /// matches a CVT value, subject to rounding, cut-in, and minimum distance constraints
    /// controlled by the instruction's flag bits.
    /// </summary>
    /// <param name="flags">MIRP flag bits: bit 4=set RP0, bit 3=minimum distance, bit 2=round, bits 1-0=engine compensation.</param>
    private void MoveIndirectRelative(int flags)
    {
        float cvt = this.ReadCvt();
        int pointIndex = this.stack.Pop();
        if ((uint)pointIndex >= (uint)this.zp1.Count ||
            (uint)this.state.Rp0 >= (uint)this.zp0.Count)
        {
            // An invalid target still advances the reference-point state exactly as a
            // completed MIRP would, but performs no coordinate or CVT work.
            this.state.Rp1 = this.state.Rp0;
            this.state.Rp2 = pointIndex;
            if ((flags & 0x10) != 0)
            {
                this.state.Rp0 = pointIndex;
            }

            return;
        }

        if (Math.Abs(cvt - this.state.SingleWidthValue) < this.state.SingleWidthCutIn)
        {
            if (cvt >= 0)
            {
                cvt = this.state.SingleWidthValue;
            }
            else
            {
                cvt = -this.state.SingleWidthValue;
            }
        }

        // if we're looking at the twilight zone we need to prepare the points there
        Vector2 originalReference = this.zp0.GetOriginal(this.state.Rp0);
        if (this.zp1.IsTwilight)
        {
            Vector2 initialValue = originalReference + (this.state.Freedom * cvt);
            this.zp1.Original[pointIndex].Point = initialValue;
            this.zp1.Current[pointIndex].Point = initialValue;
        }

        Vector2 point = this.zp1.GetCurrent(pointIndex);
        float originalDistance = this.hintingMode == HintingMode.Full && !this.zp0.IsTwilight && !this.zp1.IsTwilight
            ? this.DualProjectUnscaled(this.zp1.GetUnscaled(pointIndex) - this.zp0.GetUnscaled(this.state.Rp0))
            : this.DualProject(this.zp1.GetOriginal(pointIndex) - originalReference);
        float currentDistance = this.Project(point - this.zp0.GetCurrent(this.state.Rp0));

        if (this.state.AutoFlip && Math.Sign(originalDistance) != Math.Sign(cvt))
        {
            cvt = -cvt;
        }

        // if bit 2 is set, round the distance and look at the cut-in value
        float distance = cvt;
        if ((flags & 0x4) != 0)
        {
            // only perform cut-in tests when both points are in the same zone
            if (this.zp0.IsTwilight == this.zp1.IsTwilight && Math.Abs(cvt - originalDistance) > this.state.ControlValueCutIn)
            {
                cvt = originalDistance;
            }

            distance = this.Round(cvt);
        }

        // if bit 3 is set, constrain to the minimum distance
        if ((flags & 0x8) != 0)
        {
            if (originalDistance >= 0)
            {
                distance = Math.Max(distance, this.state.MinDistance);
            }
            else
            {
                distance = Math.Min(distance, -this.state.MinDistance);
            }
        }

        // move the point
        this.MovePoint(this.zp1, pointIndex, distance - currentDistance);
        this.state.Rp1 = this.state.Rp0;
        this.state.Rp2 = pointIndex;
        if ((flags & 0x10) != 0)
        {
            this.state.Rp0 = pointIndex;
        }
    }

    /// <summary>
    /// Move Direct Relative Point (MDRP). Moves a point so that its distance from RP0
    /// matches the original outline distance, subject to rounding and minimum distance
    /// constraints controlled by the instruction's flag bits.
    /// </summary>
    /// <param name="flags">MDRP flag bits: bit 4=set RP0, bit 3=minimum distance, bit 2=round, bits 1-0=engine compensation.</param>
    private void MoveDirectRelative(int flags)
    {
        int pointIndex = this.stack.Pop();
        if ((uint)pointIndex >= (uint)this.zp1.Count ||
            (uint)this.state.Rp0 >= (uint)this.zp0.Count)
        {
            // An invalid target still advances the reference-point state exactly as a
            // completed MDRP would, but performs no coordinate work.
            this.state.Rp1 = this.state.Rp0;
            this.state.Rp2 = pointIndex;
            if ((flags & 0x10) != 0)
            {
                this.state.Rp0 = pointIndex;
            }

            return;
        }

        Vector2 p1 = this.zp0.GetOriginal(this.state.Rp0);
        Vector2 p2 = this.zp1.GetOriginal(pointIndex);
        float originalDistance = this.hintingMode == HintingMode.Full && !this.zp0.IsTwilight && !this.zp1.IsTwilight
            ? this.DualProjectUnscaled(this.zp1.GetUnscaled(pointIndex) - this.zp0.GetUnscaled(this.state.Rp0))
            : this.DualProject(p2 - p1);

        // single width cut-in test
        if (Math.Abs(originalDistance - this.state.SingleWidthValue) < this.state.SingleWidthCutIn)
        {
            if (originalDistance >= 0)
            {
                originalDistance = this.state.SingleWidthValue;
            }
            else
            {
                originalDistance = -this.state.SingleWidthValue;
            }
        }

        // if bit 2 is set, perform rounding
        float distance = originalDistance;
        if ((flags & 0x4) != 0)
        {
            distance = this.Round(distance);
        }

        // if bit 3 is set, constrain to the minimum distance
        if ((flags & 0x8) != 0)
        {
            if (originalDistance >= 0)
            {
                distance = Math.Max(distance, this.state.MinDistance);
            }
            else
            {
                distance = Math.Min(distance, -this.state.MinDistance);
            }
        }

        // move the point
        originalDistance = this.Project(this.zp1.GetCurrent(pointIndex) - this.zp0.GetCurrent(this.state.Rp0));

        this.MovePoint(this.zp1, pointIndex, distance - originalDistance);
        this.state.Rp1 = this.state.Rp0;
        this.state.Rp2 = pointIndex;
        if ((flags & 0x10) != 0)
        {
            this.state.Rp0 = pointIndex;
        }
    }

    /// <summary>
    /// Computes the displacement vector for SHP/SHC/SHZ instructions by projecting the
    /// movement of the reference point (RP1 or RP2 depending on mode) from its original
    /// to its current position onto the freedom vector.
    /// </summary>
    /// <param name="mode">Opcode value; bit 0 selects RP1 in ZP0 (1) or RP2 in ZP1 (0).</param>
    /// <param name="zone">Receives the reference zone.</param>
    /// <param name="point">Receives the reference point index.</param>
    /// <param name="displacementX">Receives the computed X displacement in signed 26.6 units.</param>
    /// <param name="displacementY">Receives the computed Y displacement in signed 26.6 units.</param>
    /// <returns><see langword="true"/> if the reference point is valid; otherwise <see langword="false"/>.</returns>
    private bool TryComputeDisplacement(int mode, out Zone zone, out int point, out int displacementX, out int displacementY)
    {
        if ((mode & 1) == 0)
        {
            zone = this.zp1;
            point = this.state.Rp2;
        }
        else
        {
            zone = this.zp0;
            point = this.state.Rp1;
        }

        if ((uint)point >= (uint)zone.Count)
        {
            displacementX = 0;
            displacementY = 0;
            return false;
        }

        Vector2 current = zone.GetCurrent(point);
        Vector2 original = zone.GetOriginal(point);
        int distance = ProjectF26Dot6(
            FloatToF26Dot6(current.X) - FloatToF26Dot6(original.X),
            FloatToF26Dot6(current.Y) - FloatToF26Dot6(original.Y),
            this.state.Projection);

        short freedomX = (short)FloatToF2Dot14(this.state.Freedom.X);
        short freedomY = (short)FloatToF2Dot14(this.state.Freedom.Y);
        short projectionDot = (short)FloatToF2Dot14(this.fdotp);

        // A projection dot product of 0x4000 is exactly 1.0, so each coordinate is the
        // signed two-stage product of the 26.6 distance and its F2.14 freedom component.
        // Other dot products divide that product by the dot value with sign-aware rounding.
        if (projectionDot == 0x4000)
        {
            displacementX = freedomX == 0 ? 0 : MultiplyF26Dot6ByF2Dot14(distance, freedomX);
            displacementY = freedomY == 0 ? 0 : MultiplyF26Dot6ByF2Dot14(distance, freedomY);
        }
        else
        {
            displacementX = freedomX == 0
                ? 0
                : unchecked((int)CompensatedDivide(projectionDot, (long)freedomX * distance));
            displacementY = freedomY == 0
                ? 0
                : unchecked((int)CompensatedDivide(projectionDot, (long)freedomY * distance));
        }

        return true;
    }

    /// <summary>
    /// Returns the touch state flags corresponding to the current freedom vector axes.
    /// Used by UTP to selectively clear touch bits.
    /// </summary>
    /// <returns>The touch-state mask selected by the nonzero freedom-vector components.</returns>
    private TouchState GetTouchState()
    {
        TouchState touch = TouchState.None;
        if (this.state.Freedom.X != 0)
        {
            touch = TouchState.X;
        }

        if (this.state.Freedom.Y != 0)
        {
            touch |= TouchState.Y;
        }

        return touch;
    }

    /// <summary>
    /// Moves a point along the freedom vector by the given distance, applying v40
    /// backward compatibility restrictions: X movement is always blocked in compat mode,
    /// Y movement is blocked only after both IUP passes have completed (post-IUP).
    /// Touch bits follow the nonzero freedom-vector components even when compatibility
    /// restrictions suppress the corresponding coordinate update.
    /// </summary>
    /// <param name="zone">The point zone containing the target.</param>
    /// <param name="index">The target point index.</param>
    /// <param name="distance">The projected movement distance in device pixels.</param>
    private void MovePoint(Zone zone, int index, float distance)
    {
        // X is always blocked in backward compat mode.
        // Y is blocked only after both IUP axes have completed.
        bool unrestricted = this.IsMovementUnrestricted;
        bool postIUP = this.iupXCalled && this.iupYCalled;
        short freedomX = (short)FloatToF2Dot14(this.state.Freedom.X);
        short freedomY = (short)FloatToF2Dot14(this.state.Freedom.Y);
        short projectionDot = (short)FloatToF2Dot14(this.fdotp);
        int distanceF26Dot6 = FloatToF26Dot6(distance);

        if (freedomX != 0)
        {
            if (unrestricted)
            {
                int dx;
                if (projectionDot == 0x4000)
                {
                    dx = MultiplyF26Dot6ByF2Dot14(distanceF26Dot6, freedomX);
                }
                else if (projectionDot == freedomX)
                {
                    dx = distanceF26Dot6;
                }
                else
                {
                    // Compute round(freedomX * distance / projectionDot) by adding the
                    // divisor's signed half before truncating division. A zero divisor
                    // saturates according to the numerator's sign.
                    long numerator = (long)freedomX * distanceF26Dot6;
                    int halfDivisor = projectionDot / 2;
                    numerator = projectionDot < 0 ? numerator - halfDivisor : numerator + halfDivisor;
                    dx = projectionDot == 0
                        ? (numerator < 0 ? int.MinValue : int.MaxValue)
                        : unchecked((int)(numerator / projectionDot));
                }

                int x = unchecked(FloatToF26Dot6(zone.Current[index].Point.X) + dx);
                zone.Current[index].Point.X = F26Dot6ToFloat(x);
            }

            zone.TouchState[index] |= TouchState.X;
        }

        if (freedomY != 0)
        {
            if (unrestricted || !postIUP)
            {
                int dy;
                if (projectionDot == 0x4000)
                {
                    dy = MultiplyF26Dot6ByF2Dot14(distanceF26Dot6, freedomY);
                }
                else if (projectionDot == freedomY)
                {
                    dy = distanceF26Dot6;
                }
                else
                {
                    // Y uses the same quotient with a half-divisor whose sign is positive
                    // only when numerator and divisor have the same sign.
                    long numerator = (long)freedomY * distanceF26Dot6;
                    dy = unchecked((int)CompensatedDivide(projectionDot, numerator));
                }

                int y = unchecked(FloatToF26Dot6(zone.Current[index].Point.Y) + dy);
                zone.Current[index].Point.Y = F26Dot6ToFloat(y);
            }

            zone.TouchState[index] |= TouchState.Y;
        }
    }

    /// <summary>
    /// Moves a ZP2 point by explicit (dx, dy) deltas with the same v40 backward
    /// compatibility restrictions as <see cref="MovePoint"/>. Used by SHP, SHC, SHZ,
    /// and SHPIX where the displacement is pre-computed rather than derived from a scalar distance.
    /// Touch bits follow the nonzero freedom-vector components independently of whether
    /// compatibility restrictions suppress the coordinate update.
    /// </summary>
    /// <param name="zone">The ZP2 point zone containing the target.</param>
    /// <param name="index">The target point index.</param>
    /// <param name="dx">The X displacement in signed 26.6.</param>
    /// <param name="dy">The Y displacement in signed 26.6.</param>
    /// <param name="touch">Whether moved freedom-vector axes are marked as touched.</param>
    private void MoveZp2Point(Zone zone, int index, int dx, int dy, bool touch)
    {
        // X is always blocked in compat mode.
        // Y is blocked only after both IUP axes have completed.
        bool unrestricted = this.IsMovementUnrestricted;
        bool postIUP = this.iupXCalled && this.iupYCalled;

        if (this.state.Freedom.X != 0)
        {
            if (unrestricted)
            {
                int x = unchecked(FloatToF26Dot6(zone.Current[index].Point.X) + dx);
                zone.Current[index].Point.X = F26Dot6ToFloat(x);
            }

            if (touch)
            {
                zone.TouchState[index] |= TouchState.X;
            }
        }

        if (this.state.Freedom.Y != 0)
        {
            if (unrestricted || !postIUP)
            {
                int y = unchecked(FloatToF26Dot6(zone.Current[index].Point.Y) + dy);
                zone.Current[index].Point.Y = F26Dot6ToFloat(y);
            }

            if (touch)
            {
                zone.TouchState[index] |= TouchState.Y;
            }
        }
    }

    /// <summary>
    /// Rounds a distance value according to the current round state.
    /// Engine compensation is zero, so every mode depends only on the signed distance and
    /// the configured grid period, phase, and threshold.
    /// </summary>
    /// <param name="value">The signed distance in device pixels.</param>
    /// <returns>The distance rounded according to the current graphics state.</returns>
    private float Round(float value)
    {
        switch (this.state.RoundState)
        {
            case RoundMode.Off:
                // No rounding or compensation.
                return value;

            case RoundMode.ToGrid:
            {
                // Nearest whole pixel, with sign-symmetric magnitude rounding.
                if (value >= 0F)
                {
                    float val = (float)Math.Floor(value + 0.5F);
                    if (val < 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
                else
                {
                    float val = -(float)Math.Floor(-value + 0.5F);
                    if (val > 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
            }

            case RoundMode.ToHalfGrid:
            {
                // Nearest half-integer pixel, preserving the input sign.
                if (value >= 0F)
                {
                    float val = (float)Math.Floor(value) + 0.5F;
                    if (val < 0F)
                    {
                        val = 0.5F;
                    }

                    return val;
                }
                else
                {
                    float val = -((float)Math.Floor(-value) + 0.5F);
                    if (val > 0F)
                    {
                        val = -0.5F;
                    }

                    return val;
                }
            }

            case RoundMode.DownToGrid:
            {
                // Truncate the magnitude toward the preceding whole-pixel boundary.
                if (value >= 0F)
                {
                    float val = (float)Math.Floor(value);
                    if (val < 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
                else
                {
                    float val = -(float)Math.Floor(-value);
                    if (val > 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
            }

            case RoundMode.UpToGrid:
            {
                // Expand the magnitude to the next whole-pixel boundary.
                if (value >= 0F)
                {
                    float val = (float)Math.Ceiling(value);
                    if (val < 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
                else
                {
                    float val = -(float)Math.Ceiling(-value);
                    if (val > 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
            }

            case RoundMode.ToDoubleGrid:
            {
                // Nearest multiple of 1/2 pixel.
                const float step = 0.5F;

                if (value >= 0F)
                {
                    float val = step * (float)Math.Floor((value / step) + 0.5F);
                    if (val < 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
                else
                {
                    float val = -step * (float)Math.Floor((-value / step) + 0.5F);
                    if (val > 0F)
                    {
                        val = 0F;
                    }

                    return val;
                }
            }

            case RoundMode.Super:
            case RoundMode.Super45:
            {
                // Quantize (abs(value) - phase + threshold) to the configured period,
                // restore the phase, then reapply the original sign.
                float period = this.roundPeriod;
                float phase = this.roundPhase;
                float threshold = this.roundThreshold;

                if (value >= 0F)
                {
                    float val = value - phase + threshold;
                    val = (float)Math.Floor(val / period) * period;
                    val += phase;

                    if (val < 0F)
                    {
                        val = phase;
                    }

                    return val;
                }
                else
                {
                    float val = -value - phase + threshold;
                    val = (float)Math.Floor(val / period) * period;
                    val = -val - phase;

                    if (val > 0F)
                    {
                        val = -phase;
                    }

                    return val;
                }
            }

            default:
                return value;
        }
    }

    /// <summary>
    /// Projects a 26.6 point difference onto the current projection vector.
    /// </summary>
    /// <param name="point">The point difference in exact 26.6 float storage.</param>
    /// <returns>The projected distance in exact 26.6 float storage.</returns>
    private float Project(Vector2 point) => ProjectF26Dot6(point, this.state.Projection);

    /// <summary>
    /// Projects a 26.6 point difference onto the dual-projection vector used for original coordinates.
    /// </summary>
    /// <param name="point">The original point difference in exact 26.6 float storage.</param>
    /// <returns>The projected distance in exact 26.6 float storage.</returns>
    private float DualProject(Vector2 point) => ProjectF26Dot6(point, this.state.DualProjection);

    /// <summary>
    /// Scales an unrounded font-unit difference once and projects it through the dual vector,
    /// matching MIRP/MDRP's normal-glyph path over the element's original coordinate arrays.
    /// </summary>
    /// <param name="point">The point difference in integral font units.</param>
    /// <returns>The projected distance in exact 26.6 float storage.</returns>
    private float DualProjectUnscaled(Vector2 point)
    {
        // MDRP and MIRP project the integral font-unit difference first, then scale that
        // single distance. Scaling the components independently changes rounding order.
        int projected = ProjectF26Dot6((int)point.X, (int)point.Y, this.state.DualProjection);
        return F26Dot6ToFloat(this.trueTypeScaler.Scale(projected));
    }

    /// <summary>
    /// Reads and skips the next instruction in the stream, advancing past any inline
    /// data bytes for push instructions. Used by FDEF/IDEF to scan for ENDF and by
    /// IF/ELSE to skip over conditional blocks.
    /// </summary>
    /// <param name="stream">The instruction stream whose next opcode is consumed.</param>
    /// <returns>The opcode that was skipped.</returns>
    private static OpCode SkipNext(ref StackInstructionStream stream)
    {
        OpCode opcode = stream.NextOpCode();
        switch (opcode)
        {
            case OpCode.NPUSHB:
            case OpCode.PUSHB1:
            case OpCode.PUSHB2:
            case OpCode.PUSHB3:
            case OpCode.PUSHB4:
            case OpCode.PUSHB5:
            case OpCode.PUSHB6:
            case OpCode.PUSHB7:
            case OpCode.PUSHB8:
            {
                int count = opcode == OpCode.NPUSHB ? stream.NextByte() : opcode - OpCode.PUSHB1 + 1;
                stream.Skip(count);
            }

            break;
            case OpCode.NPUSHW:
            case OpCode.PUSHW1:
            case OpCode.PUSHW2:
            case OpCode.PUSHW3:
            case OpCode.PUSHW4:
            case OpCode.PUSHW5:
            case OpCode.PUSHW6:
            case OpCode.PUSHW7:
            case OpCode.PUSHW8:
            {
                int count = opcode == OpCode.NPUSHW ? stream.NextByte() : opcode - OpCode.PUSHW1 + 1;
                stream.SkipWord(count);
            }

            break;
        }

        return opcode;
    }

    /// <summary>
    /// Interpolates untouched points between two references using integer IUP arithmetic.
    /// Raw byte pointers let the same loop address either coordinate axis without branches
    /// at each point.
    /// </summary>
    /// <param name="current">The first coordinate field in the current point array.</param>
    /// <param name="original">The first coordinate field in the scaled original point array.</param>
    /// <param name="interpolationDomain">The first coordinate field in the font-unit or scaled interpolation array.</param>
    /// <param name="interpolationDomainIsF26Dot6">Whether interpolation-domain coordinates use signed 26.6 rather than integral font units.</param>
    /// <param name="start">The first target point index, inclusive.</param>
    /// <param name="end">The final target point index, inclusive.</param>
    /// <param name="ref1">The first touched reference point index.</param>
    /// <param name="ref2">The second touched reference point index.</param>
    private static unsafe void InterpolatePoints(byte* current, byte* original, byte* interpolationDomain, bool interpolationDomainIsF26Dot6, int start, int end, int ref1, int ref2)
    {
        if (start > end)
        {
            return;
        }

        int firstDomain = ReadInterpolationDomain(interpolationDomain, interpolationDomainIsF26Dot6, ref1);
        int secondDomain = ReadInterpolationDomain(interpolationDomain, interpolationDomainIsF26Dot6, ref2);
        int lowerReference = ref2;
        int upperReference = ref1;
        int lowerDomain = secondDomain;
        int upperDomain = firstDomain;
        if (firstDomain < secondDomain)
        {
            lowerReference = ref1;
            upperReference = ref2;
            lowerDomain = firstDomain;
            upperDomain = secondDomain;
        }

        int domainDistance = upperDomain - lowerDomain;
        int lowerOriginal = ReadF26Dot6(original, lowerReference);
        int lowerCurrent = ReadF26Dot6(current, lowerReference);
        int lowerDelta = lowerCurrent - lowerOriginal;

        // Coincident interpolation-domain references cannot define a ratio, so every target
        // receives the lower reference's current-minus-original displacement. This also
        // handles a contour with only one touched point when the circular walk presents it
        // as both references.
        if (domainDistance == 0)
        {
            for (int i = start; i <= end; i++)
            {
                WriteF26Dot6(current, i, ReadF26Dot6(current, i) + lowerDelta);
            }

            return;
        }

        int upperOriginal = ReadF26Dot6(original, upperReference);
        int upperCurrent = ReadF26Dot6(current, upperReference);
        int upperDelta = upperCurrent - upperOriginal;
        int currentSpan = upperCurrent - lowerCurrent;

        // When both the domain span and signed current span are below 0x8000, the product
        // fits the intended 32-bit path. The test is signed: a negative current span always
        // qualifies. Interior points use
        // lowerCurrent + round((domain-lowerDomain) * currentSpan / domainDistance).
        if (domainDistance < 0x8000 && currentSpan < 0x8000)
        {
            for (int i = start; i <= end; i++)
            {
                int pointOriginal = ReadF26Dot6(original, i);
                int pointCurrent;
                if (lowerOriginal < pointOriginal)
                {
                    if (pointOriginal < upperOriginal)
                    {
                        int pointDomain = ReadInterpolationDomain(interpolationDomain, interpolationDomainIsF26Dot6, i);
                        int numerator = unchecked(((pointDomain - lowerDomain) * currentSpan) + (domainDistance >> 1));
                        pointCurrent = unchecked((numerator / domainDistance) + lowerCurrent);
                    }
                    else
                    {
                        pointCurrent = unchecked(pointOriginal + upperDelta);
                    }
                }
                else if (upperOriginal <= pointOriginal)
                {
                    pointCurrent = unchecked(pointOriginal + upperDelta);
                }
                else
                {
                    pointCurrent = unchecked(pointOriginal + lowerDelta);
                }

                WriteF26Dot6(current, i, pointCurrent);
            }

            return;
        }

        int scale = DivideF16Dot16(currentSpan, domainDistance);
        for (int i = start; i <= end; i++)
        {
            int pointOriginal = ReadF26Dot6(original, i);
            int pointCurrent;
            if (lowerOriginal < pointOriginal)
            {
                if (pointOriginal < upperOriginal)
                {
                    int pointDomain = ReadInterpolationDomain(interpolationDomain, interpolationDomainIsF26Dot6, i);
                    long product = (long)(pointDomain - lowerDomain) * scale;
                    int interpolated = checked((int)((product + (product >> 63) + 0x8000) >> 16));
                    pointCurrent = unchecked(interpolated + lowerCurrent);
                }
                else
                {
                    pointCurrent = unchecked(pointOriginal + upperDelta);
                }
            }
            else
            {
                pointCurrent = unchecked(pointOriginal + lowerDelta);
            }

            WriteF26Dot6(current, i, pointCurrent);
        }
    }

    /// <summary>
    /// Divides two integers into a signed 16.16 result. Half the denominator is added when
    /// numerator and denominator share a sign and subtracted otherwise; overflow saturates
    /// to the signed 32-bit range.
    /// </summary>
    /// <param name="numerator">The signed integer numerator.</param>
    /// <param name="denominator">The signed integer denominator.</param>
    /// <returns>The rounded, saturated signed 16.16 quotient.</returns>
    private static int DivideF16Dot16(int numerator, int denominator)
    {
        int halfDenominator = denominator / 2;
        long rounding = (denominator < 0) == (numerator < 0) ? halfDenominator : -halfDenominator;
        if (denominator == 0)
        {
            return int.MaxValue;
        }

        long quotient = (((long)numerator * 0x10000) + rounding) / denominator;
        if (quotient >= 0x80000000L)
        {
            return int.MaxValue;
        }

        if (quotient < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)quotient;
    }

    /// <summary>
    /// Multiplies and divides signed integers with sign-aware half-divisor rounding.
    /// </summary>
    /// <param name="value">The signed value to scale.</param>
    /// <param name="multiplier">The signed scale numerator.</param>
    /// <param name="divisor">The signed scale denominator.</param>
    /// <returns>The rounded signed integer quotient.</returns>
    private static int MultiplyDivideRounded(int value, int multiplier, int divisor)
    {
        long product = (long)value * multiplier;
        int halfDivisor = divisor / 2;
        long rounding = (divisor < 0) == (product < 0) ? halfDivisor : -halfDivisor;
        return unchecked((int)((product + rounding) / divisor));
    }

    /// <summary>
    /// Reads an IP coordinate from either font units or scaled originals.
    /// </summary>
    /// <param name="zone">The point zone containing both coordinate domains.</param>
    /// <param name="index">The point index.</param>
    /// <param name="xAxis">Whether to read X; otherwise Y is read.</param>
    /// <param name="isF26Dot6">Whether to read the scaled original rather than the font-unit coordinate.</param>
    /// <returns>The coordinate as signed 26.6 or integral font units, according to <paramref name="isF26Dot6"/>.</returns>
    private static int ReadInterpolationCoordinate(in Zone zone, int index, bool xAxis, bool isF26Dot6)
    {
        Vector2 point = isF26Dot6 ? zone.GetOriginal(index) : zone.GetUnscaled(index);
        float coordinate = xAxis ? point.X : point.Y;
        return isF26Dot6 ? FloatToF26Dot6(coordinate) : (int)coordinate;
    }

    /// <summary>
    /// Reads a current IP coordinate as a signed 26.6 integer.
    /// </summary>
    /// <param name="zone">The point zone containing the current coordinates.</param>
    /// <param name="index">The point index.</param>
    /// <param name="xAxis">Whether to read X; otherwise Y is read.</param>
    /// <returns>The selected coordinate in signed 26.6.</returns>
    private static int ReadCurrentCoordinate(in Zone zone, int index, bool xAxis)
    {
        Vector2 point = zone.GetCurrent(index);
        return FloatToF26Dot6(xAxis ? point.X : point.Y);
    }

    /// <summary>
    /// Writes a direct IP coordinate and marks the corresponding touch axis.
    /// </summary>
    /// <param name="zone">The point zone receiving the coordinate.</param>
    /// <param name="index">The point index.</param>
    /// <param name="xAxis">Whether to write X; otherwise Y is written.</param>
    /// <param name="coordinate">The new coordinate in signed 26.6.</param>
    private static void WriteCurrentCoordinate(ref Zone zone, int index, bool xAxis, int coordinate)
    {
        ControlPoint point = zone.Current[index];
        if (xAxis)
        {
            point.Point.X = F26Dot6ToFloat(coordinate);
            zone.TouchState[index] |= TouchState.X;
        }
        else
        {
            point.Point.Y = F26Dot6ToFloat(coordinate);
            zone.TouchState[index] |= TouchState.Y;
        }

        zone.Current[index] = point;
    }

    /// <summary>
    /// Reads a coordinate from a scaled 26.6 point array.
    /// </summary>
    /// <param name="points">A pointer to the first selected-axis coordinate.</param>
    /// <param name="index">The point index.</param>
    /// <returns>The coordinate in signed 26.6.</returns>
    private static unsafe int ReadF26Dot6(byte* points, int index) => FloatToF26Dot6(*GetPoint(points, index));

    /// <summary>
    /// Writes a coordinate to a scaled 26.6 point array.
    /// </summary>
    /// <param name="points">A pointer to the first selected-axis coordinate.</param>
    /// <param name="index">The point index.</param>
    /// <param name="value">The coordinate in signed 26.6.</param>
    private static unsafe void WriteF26Dot6(byte* points, int index, int value) => *GetPoint(points, index) = F26Dot6ToFloat(value);

    /// <summary>
    /// Reads either a font-unit or scaled-original IUP coordinate.
    /// </summary>
    /// <param name="points">A pointer to the first selected-axis coordinate.</param>
    /// <param name="isF26Dot6">Whether the pointed array stores scaled 26.6 coordinates.</param>
    /// <param name="index">The point index.</param>
    /// <returns>The coordinate in signed 26.6 or integral font units.</returns>
    private static unsafe int ReadInterpolationDomain(byte* points, bool isF26Dot6, int index)
        => isF26Dot6 ? ReadF26Dot6(points, index) : (int)*GetPoint(points, index);

    // Fixed-point conversion helpers.
    // F2Dot14: 2-bit integer + 14-bit fraction, range [-2, ~2). Used for unit vectors.
    // F26Dot6: 26-bit integer + 6-bit fraction, the point-coordinate format defined by the
    // TrueType instruction set. Float storage is exact for the working range; conversions at
    // arithmetic boundaries preserve the instruction set's integer rounding.

    /// <summary>
    /// Quantizes a scaled point to the nearest sixty-fourth of a pixel, with ties away from zero.
    /// </summary>
    /// <param name="value">The point in device pixels.</param>
    /// <returns>The point on the signed 26.6 grid.</returns>
    private static Vector2 QuantizeF26Dot6(Vector2 value) => new(MathF.Round(value.X * 64F, MidpointRounding.AwayFromZero) / 64F, MathF.Round(value.Y * 64F, MidpointRounding.AwayFromZero) / 64F);

    /// <summary>
    /// Converts the low sixteen bits of an F2.14 value to the interpreter's exact float representation.
    /// </summary>
    /// <param name="value">The value whose low sixteen bits contain signed F2.14.</param>
    /// <returns>The represented floating-point value.</returns>
    private static float F2Dot14ToFloat(int value) => (short)value / 16384.0f;

    /// <summary>
    /// Converts an exact F2.14 float to its sign-extended sixteen-bit representation.
    /// </summary>
    /// <param name="value">The floating-point vector component.</param>
    /// <returns>The signed F2.14 bit pattern in a 32-bit stack value.</returns>
    private static int FloatToF2Dot14(float value) => (int)(uint)(short)Math.Round(value * 16384.0f);

    /// <summary>
    /// Converts a signed 26.6 value to the interpreter's exact float representation.
    /// </summary>
    /// <param name="value">The signed 26.6 integer.</param>
    /// <returns>The represented device-pixel value.</returns>
    private static float F26Dot6ToFloat(int value) => value / 64.0f;

    /// <summary>
    /// Converts an exact 26.6 float to its signed integer representation.
    /// </summary>
    /// <param name="value">The device-pixel value.</param>
    /// <returns>The rounded signed 26.6 integer.</returns>
    private static int FloatToF26Dot6(float value) => (int)Math.Round(value * 64.0f);

    /// <summary>
    /// Projects a 26.6 vector through a signed F2.14 projection vector. The X and Y
    /// products round independently before their signed 26.6 results are added.
    /// </summary>
    /// <param name="point">The point difference in 26.6 device coordinates.</param>
    /// <param name="projection">The projection vector in signed F2.14 coordinates.</param>
    /// <returns>The projected distance in exact 26.6 float storage.</returns>
    private static float ProjectF26Dot6(Vector2 point, Vector2 projection)
    {
        int projected = ProjectF26Dot6(FloatToF26Dot6(point.X), FloatToF26Dot6(point.Y), projection);
        return F26Dot6ToFloat(projected);
    }

    /// <summary>
    /// Projects an integer 26.6 pair through a signed F2.14 vector.
    /// </summary>
    /// <param name="x">The X coordinate in signed 26.6 units.</param>
    /// <param name="y">The Y coordinate in signed 26.6 units.</param>
    /// <param name="projection">The projection vector in signed F2.14 coordinates.</param>
    /// <returns>The projected signed 26.6 distance.</returns>
    private static int ProjectF26Dot6(int x, int y, Vector2 projection)
    {
        int projectedX = MultiplyF26Dot6ByF2Dot14(x, (short)FloatToF2Dot14(projection.X));
        int projectedY = MultiplyF26Dot6ByF2Dot14(y, (short)FloatToF2Dot14(projection.Y));
        return unchecked(projectedX + projectedY);
    }

    /// <summary>
    /// Multiplies one signed 26.6 coordinate by a signed F2.14 component using the
    /// two arithmetic shifts and a positive half-unit increment.
    /// </summary>
    /// <param name="value">The coordinate in signed 26.6 units.</param>
    /// <param name="component">The vector component in signed F2.14 units.</param>
    /// <returns>The rounded signed 26.6 product.</returns>
    private static int MultiplyF26Dot6ByF2Dot14(int value, short component)
    {
        long product = (long)value * component;
        return unchecked((int)(((product >> 13) + 1) >> 1));
    }

    /// <summary>
    /// Divides a signed numerator using sign-aware half-divisor rounding.
    /// </summary>
    /// <param name="denominator">The signed divisor.</param>
    /// <param name="numerator">The signed numerator.</param>
    /// <returns>The rounded quotient, or signed saturation when the divisor is zero.</returns>
    private static long CompensatedDivide(int denominator, long numerator)
    {
        long halfDenominator = denominator / 2;
        long adjusted = numerator + ((denominator < 0) == (numerator < 0) ? halfDenominator : -halfDenominator);
        if (denominator != 0)
        {
            return adjusted / denominator;
        }

        return adjusted < 0 ? int.MinValue : int.MaxValue;
    }

    /// <summary>
    /// Normalizes a 26.6 line into signed F2.14 components using integer scaling and an
    /// integer square root. A zero-length line resolves to the positive X unit vector.
    /// </summary>
    /// <param name="value">The line vector in exact 26.6 float storage.</param>
    /// <returns>The normalized signed F2.14 vector in exact float storage.</returns>
    private static Vector2 NormalizeF26Dot6(Vector2 value)
    {
        int x = FloatToF26Dot6(value.X);
        int y = FloatToF26Dot6(value.Y);
        if (x == 0 && y == 0)
        {
            return Vector2.UnitX;
        }

        int magnitudeSquared;
        if (x > -0x8000 && x < 0x7FFF && y > -0x8000 && y < 0x7FFF)
        {
            // Small inputs can be squared in 32 bits. Shift the squared magnitude by two
            // bits per iteration and each component by one, preserving their ratio while
            // moving the magnitude into the square-root routine's high-precision range.
            magnitudeSquared = unchecked((x * x) + (y * y));
            int shift = 0xF;
            while (magnitudeSquared < 0x20000000)
            {
                magnitudeSquared = unchecked(magnitudeSquared << 2);
                shift++;
            }

            x = unchecked(x << (shift & 0x1F));
            y = unchecked(y << (shift & 0x1F));
        }
        else
        {
            // Double large inputs only while both remain within the stated signed bounds.
            // Their 2.60 squares then round down to signed 2.30 terms before addition.
            while (x < 0x20000000
                && x > -0x20000000
                && (uint)(y + 0x1FFFFFFF) <= 0x3FFFFFFE)
            {
                x = unchecked(x * 2);
                y = unchecked(y * 2);
            }

            long xSquared = (long)x * x;
            long ySquared = (long)y * y;
            int xTerm = SaturateToInt((xSquared + (xSquared >> 63) + 0x20000000) >> 30);
            int yTerm = SaturateToInt((ySquared + (ySquared >> 63) + 0x20000000) >> 30);
            magnitudeSquared = unchecked(xTerm + yTerm);
        }

        int magnitude = FractionalSquareRoot(magnitudeSquared);
        short normalizedX = NormalizeComponent(x, magnitude);
        short normalizedY = NormalizeComponent(y, magnitude);
        return new Vector2(F2Dot14ToFloat(normalizedX), F2Dot14ToFloat(normalizedY));
    }

    /// <summary>
    /// Converts one scaled normalization component to signed F2.14.
    /// </summary>
    /// <param name="value">The scaled component.</param>
    /// <param name="magnitude">The fractional square root shared by both components.</param>
    /// <returns>The normalized signed F2.14 component.</returns>
    private static short NormalizeComponent(int value, int magnitude)
    {
        long quotient = CompensatedDivide(magnitude, (long)value << 30);
        int saturated = SaturateToInt(quotient);
        int rounded = unchecked(saturated + 0x8000);
        return unchecked((short)(rounded >> 16));
    }

    /// <summary>
    /// Returns a rounded fixed-point square root using a restoring bit-by-bit algorithm.
    /// </summary>
    /// <param name="value">The nonnegative fixed-point radicand.</param>
    /// <returns>The rounded fixed-point square root, or <see cref="int.MinValue"/> for a negative input.</returns>
    private static int FractionalSquareRoot(int value)
    {
        if (value < 0)
        {
            return int.MinValue;
        }

        // A raw Q2.30 value v represents v / 2^30. Returning its square root in
        // the same format therefore requires round(sqrt(v * 2^30)). The restoring
        // algorithm evaluates that integer square root without constructing the
        // 61-bit product v * 2^30.
        uint radicand = (uint)value;

        // 0x40000000 is 1.0 in Q2.30. A nonnegative signed input is less than 2.0,
        // so the square root's leading bit is 1.0 exactly when the radicand is at
        // least 1.0. Subtracting 1.0 squared leaves the remainder for lower bits.
        uint root = radicand < 0x40000000U ? 0U : 0x40000000U;
        uint remainder = radicand < 0x40000000U ? radicand : radicand - 0x40000000U;

        // Let N = v * 2^30. At the start of each iteration the invariant is
        // remainder = (N - root^2) / (4 * bit). The candidate result bit is
        // d = 2 * bit, and (root + d)^2 - root^2 = 4 * bit * (root + bit).
        // The candidate therefore fits precisely when remainder >= root + bit.
        // The first candidate bit is 0x20000000 (0.5 in Q2.30); shifting bit right
        // tests each lower result bit in turn.
        uint bit = 0x10000000U;
        do
        {
            uint trial = unchecked(bit + root);
            if (remainder >= trial)
            {
                remainder -= trial;
                root = unchecked(root + (bit * 2));
            }

            // Halving bit changes the invariant's denominator from 4 * bit to
            // 2 * bit, so doubling the residual quotient preserves the invariant
            // for the next result bit.
            remainder = unchecked(remainder * 2);
            bit >>= 1;
        }
        while (bit != 0);

        // The loop resolves every result bit except bit zero. Root is consequently
        // even and N - root^2 = 2 * remainder. Advancing to root + 1 costs
        // (root + 1)^2 - root^2 = 2 * root + 1, which fits exactly when
        // remainder > root. Both branches convert remainder to N - root^2 for the
        // completed integer root.
        if (remainder > root)
        {
            remainder = unchecked(((remainder - root) * 2) - 1);
            root++;
        }
        else
        {
            remainder = unchecked(remainder * 2);
        }

        // Root is now floor(sqrt(N)) and remainder is N - root^2. The exact root
        // is nearer root + 1 when remainder > root: squaring root + 0.5 places the
        // threshold at root^2 + root + 0.25, and the integer N cannot equal it.
        return unchecked((int)(root + (root < remainder ? 1U : 0U)));
    }

    /// <summary>
    /// Clamps a signed 64-bit intermediate to the signed 32-bit range.
    /// </summary>
    /// <param name="value">The value to clamp.</param>
    /// <returns>The value represented as a saturated signed 32-bit integer.</returns>
    private static int SaturateToInt(long value)
        => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    /// <summary>
    /// Multiplies and divides the way both reference engines do, on 26.6 values, adding half
    /// the divisor before dividing so the result rounds away from zero at the halfway point.
    /// A plain division leaves a residue that a later grid rounding can magnify to one pixel.
    /// </summary>
    /// <param name="a">The value to scale.</param>
    /// <param name="b">The multiplier.</param>
    /// <param name="c">The divisor.</param>
    /// <returns>The rounded result in pixels.</returns>
    private static float MulDivRound(float a, float b, float c)
    {
        long numerator = (long)MathF.Round(a * 64F) * (long)MathF.Round(b * 64F);
        long divisor = (long)MathF.Round(c * 64F);
        if (divisor == 0)
        {
            return 0F;
        }

        int sign = 1;
        if (numerator < 0)
        {
            numerator = -numerator;
            sign = -sign;
        }

        if (divisor < 0)
        {
            divisor = -divisor;
            sign = -sign;
        }

        long result = ((numerator * 2) + divisor) / (divisor * 2);
        return sign * result / 64F;
    }

    /// <summary>
    /// Locates one selected-axis coordinate in a packed control-point array.
    /// </summary>
    /// <param name="data">A pointer to the selected-axis coordinate of the first point.</param>
    /// <param name="index">The point index.</param>
    /// <returns>A pointer to the selected coordinate.</returns>
    private static unsafe float* GetPoint(byte* data, int index) => (float*)(data + (sizeof(ControlPoint) * index));

#pragma warning disable SA1201 // Elements should appear in the correct order
    /// <summary>
    /// Specifies the rounding mode used by the TrueType interpreter.
    /// </summary>
    private enum RoundMode
#pragma warning restore SA1201 // Elements should appear in the correct order
    {
        /// <summary>
        /// Round to the nearest half-grid line.
        /// </summary>
        ToHalfGrid,

        /// <summary>
        /// Round to the nearest grid line.
        /// </summary>
        ToGrid,

        /// <summary>
        /// Round to the nearest double-grid line.
        /// </summary>
        ToDoubleGrid,

        /// <summary>
        /// Round down to the nearest grid line.
        /// </summary>
        DownToGrid,

        /// <summary>
        /// Round up to the nearest grid line.
        /// </summary>
        UpToGrid,

        /// <summary>
        /// No rounding.
        /// </summary>
        Off,

        /// <summary>
        /// Super-rounding with a period of 1.0.
        /// </summary>
        Super,

        /// <summary>
        /// Super-rounding with a period of sqrt(2)/2.
        /// </summary>
        Super45
    }

    /// <summary>
    /// Flags controlling instruction execution behavior, set by the INSTCTRL instruction.
    /// </summary>
    [Flags]
    private enum InstructionControlFlags
    {
        /// <summary>
        /// No special instruction control.
        /// </summary>
        None,

        /// <summary>
        /// Inhibit grid fitting (disables hinting).
        /// </summary>
        InhibitGridFitting = 0x1,

        /// <summary>
        /// Use the default graphics state instead of the state saved by the prep program.
        /// </summary>
        UseDefaultGraphicsState = 0x2,

        /// <summary>
        /// Backward-compatibility movement restrictions are waived.
        /// </summary>
        NativeClearType = 0x4
    }

    /// <summary>
    /// Tracks which axes a point has been touched (moved) along by hinting instructions.
    /// Used by IUP (Interpolate Untouched Points) to determine which points need interpolation.
    /// </summary>
    [Flags]
    private enum TouchState
    {
        /// <summary>
        /// The point has not been touched.
        /// </summary>
        None = 0,

        /// <summary>
        /// The point has been touched along the X axis.
        /// </summary>
        X = 0x1,

        /// <summary>
        /// The point has been touched along the Y axis.
        /// </summary>
        Y = 0x2,

        /// <summary>
        /// The point has been touched along both axes.
        /// </summary>
        Both = X | Y
    }

    /// <summary>
    /// An immutable snapshot of an instruction stream position, used to store function
    /// and instruction definitions (FDEF/IDEF) for later execution via CALL/LOOPCALL.
    /// </summary>
    private readonly struct InstructionStream
    {
        private readonly ReadOnlyMemory<byte> instructions;
        private readonly int ip;

        /// <summary>
        /// Initializes a new instance of the <see cref="InstructionStream"/> struct.
        /// </summary>
        /// <param name="instructions">The instruction bytecode buffer.</param>
        /// <param name="offset">The byte offset into the buffer.</param>
        public InstructionStream(ReadOnlyMemory<byte> instructions, int offset)
        {
            this.instructions = instructions;
            this.ip = offset;
        }

        /// <summary>
        /// Gets a value indicating whether this stream references a valid instruction buffer.
        /// </summary>
        public bool IsValid => !this.instructions.IsEmpty;

        /// <summary>
        /// Creates a mutable <see cref="StackInstructionStream"/> positioned at this stream's offset.
        /// </summary>
        /// <returns>A new <see cref="StackInstructionStream"/>.</returns>
        public StackInstructionStream ToStack() => new(this.instructions, this.ip);
    }

    /// <summary>
    /// A mutable, stack-allocated instruction stream that reads TrueType bytecode
    /// sequentially and supports forward/backward jumps.
    /// </summary>
    private ref struct StackInstructionStream
    {
        private readonly ReadOnlyMemory<byte> origin;
        private readonly ReadOnlySpan<byte> instructions;
        private int ip;

        /// <summary>
        /// Initializes a new instance of the <see cref="StackInstructionStream"/> struct.
        /// </summary>
        /// <param name="instructions">The instruction bytecode buffer.</param>
        /// <param name="offset">The byte offset to start reading from.</param>
        public StackInstructionStream(ReadOnlyMemory<byte> instructions, int offset)
        {
            this.origin = instructions;
            this.instructions = instructions.Span;
            this.ip = offset;
        }

        /// <summary>
        /// Gets a value indicating whether this stream references a valid instruction buffer.
        /// </summary>
        public readonly bool IsValid => !this.instructions.IsEmpty;

        /// <summary>
        /// Gets a value indicating whether the instruction pointer has reached the end of the buffer.
        /// </summary>
        public readonly bool Done => this.ip >= this.instructions.Length;

        /// <summary>
        /// Reads the next byte from the stream and advances the instruction pointer.
        /// </summary>
        /// <returns>The byte value.</returns>
        public int NextByte()
        {
            ReadOnlySpan<byte> span = this.instructions;
            int offset = this.ip;
            if ((uint)offset >= (uint)span.Length)
            {
                ThrowEndOfInstructions();
            }

            byte b = span[offset];
            this.ip++;
            return b;
        }

        /// <summary>
        /// Skips the specified number of bytes in the stream.
        /// </summary>
        /// <param name="count">The number of bytes to skip.</param>
        public void Skip(int count)
        {
            this.ip += count;
            if ((uint)this.ip >= (uint)this.instructions.Length)
            {
                ThrowEndOfInstructions();
            }
        }

        /// <summary>
        /// Reads the next byte as an <see cref="OpCode"/>.
        /// </summary>
        /// <returns>The opcode.</returns>
        public OpCode NextOpCode() => (OpCode)this.NextByte();

        /// <summary>
        /// Reads the next two bytes as a signed 16-bit word (big-endian).
        /// </summary>
        /// <returns>The signed word value.</returns>
        public int NextWord() => (short)(ushort)((this.NextByte() << 8) | this.NextByte());

        /// <summary>
        /// Skips the specified number of 16-bit words in the stream.
        /// </summary>
        /// <param name="count">The number of words to skip.</param>
        public void SkipWord(int count) => this.Skip(count * 2);

        /// <summary>
        /// Moves the instruction pointer by the specified byte offset (can be negative for backward jumps).
        /// </summary>
        /// <param name="offset">The byte offset to jump.</param>
        public void Jump(int offset) => this.ip += offset;

        /// <summary>
        /// Creates an immutable <see cref="InstructionStream"/> snapshot at the current position.
        /// </summary>
        /// <returns>A new <see cref="InstructionStream"/>.</returns>
        public readonly InstructionStream ToMemory() => new(this.origin, this.ip);

        /// <summary>
        /// Throws when an instruction attempts to read beyond the available bytecode.
        /// </summary>
        private static void ThrowEndOfInstructions() => throw new FontException("no more instructions");
    }

    /// <summary>
    /// Holds the TrueType graphics state registers used during instruction execution.
    /// This includes vector directions, rounding settings, reference points, and control flags.
    /// </summary>
    private struct GraphicsState
    {
        /// <summary>
        /// The freedom vector direction.
        /// </summary>
        public Vector2 Freedom;

        /// <summary>
        /// The dual projection vector, used for original outline measurements.
        /// </summary>
        public Vector2 DualProjection;

        /// <summary>
        /// The projection vector direction.
        /// </summary>
        public Vector2 Projection;

        /// <summary>
        /// The instruction control flags set by the INSTCTRL instruction.
        /// </summary>
        public InstructionControlFlags InstructionControl;

        /// <summary>
        /// The dropout control request set by the SCANCTRL instruction. The low byte is a
        /// pixels per em threshold and the upper bits select the conditions under which
        /// dropout applies, so the value alone does not decide anything.
        /// </summary>
        public int ScanControl;

        /// <summary>
        /// The dropout rule selected by the SCANTYPE instruction.
        /// </summary>
        public int ScanType;

        /// <summary>
        /// The current rounding mode.
        /// </summary>
        public RoundMode RoundState;

        /// <summary>
        /// The minimum distance value (in pixels, F26Dot6).
        /// </summary>
        public float MinDistance;

        /// <summary>
        /// The control value cut-in threshold.
        /// </summary>
        public float ControlValueCutIn;

        /// <summary>
        /// The single width cut-in threshold.
        /// </summary>
        public float SingleWidthCutIn;

        /// <summary>
        /// The single width value.
        /// </summary>
        public float SingleWidthValue;

        /// <summary>
        /// The delta base value for DELTAP/DELTAC instructions.
        /// </summary>
        public int DeltaBase;

        /// <summary>
        /// The delta shift value for DELTAP/DELTAC instructions.
        /// </summary>
        public int DeltaShift;

        /// <summary>
        /// The loop variable controlling repeated instruction execution.
        /// </summary>
        public int Loop;

        /// <summary>
        /// Reference point 0.
        /// </summary>
        public int Rp0;

        /// <summary>
        /// Reference point 1.
        /// </summary>
        public int Rp1;

        /// <summary>
        /// Reference point 2.
        /// </summary>
        public int Rp2;

        /// <summary>
        /// Whether auto-flip is enabled for MIAP and MIRP instructions.
        /// </summary>
        public bool AutoFlip;

        /// <summary>
        /// Resets all graphics state fields to their default values.
        /// </summary>
        public void Reset()
        {
            this.Freedom = Vector2.UnitX;
            this.Projection = Vector2.UnitX;
            this.DualProjection = Vector2.UnitX;
            this.InstructionControl = InstructionControlFlags.None;
            this.RoundState = RoundMode.ToGrid;
            this.MinDistance = 1.0f;
            this.ControlValueCutIn = 17.0f / 16.0f;
            this.SingleWidthCutIn = 0.0f;
            this.SingleWidthValue = 0.0f;
            this.DeltaBase = 9;
            this.DeltaShift = 3;
            this.Loop = 1;
            this.Rp0 = this.Rp1 = this.Rp2 = 0;
            this.AutoFlip = true;
        }
    }

    /// <summary>
    /// Represents a point zone in the TrueType interpreter. There are two zones:
    /// the glyph zone (containing the glyph's outline points) and the twilight zone
    /// (containing points created by instructions for reference purposes).
    /// </summary>
    private struct Zone
    {
        /// <summary>
        /// The current (hinted) control points.
        /// </summary>
        public ControlPoint[] Current;

        /// <summary>
        /// The original (unhinted) control points.
        /// </summary>
        public ControlPoint[] Original;

        /// <summary>
        /// The outline in font units, before scaling and before the 26.6 quantization. IP
        /// forms its ratio from these, because the scaled originals carry the quantization
        /// error and the ratio magnifies it into a whole pixel once a later instruction
        /// rounds the point to the grid.
        /// </summary>
        public ControlPoint[] Unscaled;

        /// <summary>
        /// Per-point touch state tracking for IUP interpolation.
        /// </summary>
        public TouchState[] TouchState;

        /// <summary>
        /// The number of live points; the backing arrays may be longer.
        /// </summary>
        public int Count;

        /// <summary>
        /// Whether this is the twilight zone.
        /// </summary>
        public bool IsTwilight;

        /// <summary>
        /// Initializes a new instance of the <see cref="Zone"/> struct for the twilight zone.
        /// </summary>
        /// <param name="maxTwilightPoints">The maximum number of twilight points.</param>
        /// <param name="isTwilight">Whether this is the twilight zone.</param>
        public Zone(int maxTwilightPoints, bool isTwilight)
        {
            this.IsTwilight = isTwilight;
            this.Current = new ControlPoint[maxTwilightPoints];
            this.Original = new ControlPoint[maxTwilightPoints];
            this.Unscaled = this.Original;
            this.TouchState = new TouchState[maxTwilightPoints];
            this.Count = maxTwilightPoints;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Zone"/> struct for the glyph zone
        /// as a view over interpreter owned buffers. The buffers may exceed the live point
        /// count; every consumer bounds against <see cref="Count"/>.
        /// </summary>
        /// <param name="current">The buffer holding the points being hinted.</param>
        /// <param name="original">The buffer holding the unhinted point copies.</param>
        /// <param name="unscaled">The buffer holding the outline in font units.</param>
        /// <param name="touchState">The per point touch state buffer.</param>
        /// <param name="count">The number of live points.</param>
        public Zone(ControlPoint[] current, ControlPoint[] original, ControlPoint[] unscaled, TouchState[] touchState, int count)
        {
            this.IsTwilight = false;
            this.Current = current;
            this.Original = original;
            this.Unscaled = unscaled;
            this.TouchState = touchState;
            this.Count = count;
        }

        /// <summary>
        /// Gets the font unit position of the point at the specified index.
        /// </summary>
        /// <param name="index">The point index.</param>
        /// <returns>The position in font units.</returns>
        public readonly Vector2 GetUnscaled(int index) => this.Unscaled[index].Point;

        /// <summary>
        /// Gets the current (hinted) position of the point at the specified index.
        /// </summary>
        /// <param name="index">The point index.</param>
        /// <returns>The current position.</returns>
        public readonly Vector2 GetCurrent(int index) => this.Current[index].Point;

        /// <summary>
        /// Gets the original (unhinted) position of the point at the specified index.
        /// </summary>
        /// <param name="index">The point index.</param>
        /// <returns>The original position.</returns>
        public readonly Vector2 GetOriginal(int index) => this.Original[index].Point;
    }

    /// <summary>
    /// A fixed-capacity integer stack used by the TrueType bytecode interpreter.
    /// Values are stored as 32-bit integers; F26Dot6 and F2Dot14 conversions are handled at push/pop time.
    /// </summary>
    private class ExecutionStack
    {
        private readonly int[] s;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecutionStack"/> class.
        /// </summary>
        /// <param name="maxStack">The maximum stack depth.</param>
        public ExecutionStack(int maxStack) => this.s = new int[maxStack];

        /// <summary>
        /// Gets the current number of elements on the stack.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the maximum capacity of the stack.
        /// </summary>
        public int Capacity => this.s.Length;

        /// <summary>
        /// Peeks at the top element without removing it.
        /// </summary>
        /// <returns>The top element value.</returns>
        public int Peek() => this.Peek(0);

        /// <summary>
        /// Pops the top element and returns it as a boolean (non-zero is <see langword="true"/>).
        /// </summary>
        /// <returns>The boolean value.</returns>
        public bool PopBool() => this.Pop() != 0;

        /// <summary>
        /// Pops the top element and converts it from F26Dot6 to a float.
        /// </summary>
        /// <returns>The float value.</returns>
        public float PopFloat() => F26Dot6ToFloat(this.Pop());

        /// <summary>
        /// Pushes a boolean value onto the stack (1 for <see langword="true"/>, 0 for <see langword="false"/>).
        /// </summary>
        /// <param name="value">The boolean value to push.</param>
        public void Push(bool value) => this.Push(value ? 1 : 0);

        /// <summary>
        /// Pushes a float value onto the stack, converting it to F26Dot6 format.
        /// </summary>
        /// <param name="value">The float value to push.</param>
        public void Push(float value) => this.Push(FloatToF26Dot6(value));

        /// <summary>
        /// Clears all elements from the stack.
        /// </summary>
        public void Clear() => this.Count = 0;

        /// <summary>
        /// Pushes the current stack depth onto the stack.
        /// </summary>
        public void Depth() => this.Push(this.Count);

        /// <summary>
        /// Duplicates the top element on the stack.
        /// </summary>
        public void Duplicate() => this.Push(this.Peek());

        /// <summary>
        /// Copies the element at the index specified by the top stack value.
        /// </summary>
        public void Copy() => this.Copy(this.Pop() - 1);

        /// <summary>
        /// Copies the element at the specified index (from top) and pushes it.
        /// </summary>
        /// <param name="index">The zero-based index from the top of the stack.</param>
        public void Copy(int index) => this.Push(this.Peek(index));

        /// <summary>
        /// Moves the element at the index specified by the top stack value to the top.
        /// </summary>
        public void Move() => this.Move(this.Pop() - 1);

        /// <summary>
        /// Rolls the top three elements (equivalent to Move(2)).
        /// </summary>
        public void Roll() => this.Move(2);

        /// <summary>
        /// Moves the element at the specified index to the top of the stack,
        /// shifting elements above it down by one position.
        /// </summary>
        /// <param name="index">The zero-based index from the top of the stack.</param>
        public void Move(int index)
        {
            int c = this.Count;
            int[] a = this.s;
            int val = this.Peek(index);
            for (int i = c - index - 1; i < c - 1; i++)
            {
                a[i] = a[i + 1];
            }

            a[c - 1] = val;
        }

        /// <summary>
        /// Swaps the top two elements on the stack.
        /// </summary>
        public void Swap()
        {
            int c = this.Count;
            if (c < 2)
            {
                ThrowStackOverflow();
            }

            int[] a = this.s;
            (a[c - 2], a[c - 1]) = (a[c - 1], a[c - 2]);
        }

        /// <summary>
        /// Pushes an integer value onto the stack.
        /// </summary>
        /// <param name="value">The integer value to push.</param>
        public void Push(int value)
        {
            if (this.Count == this.s.Length)
            {
                ThrowStackOverflow();
            }

            this.s[this.Count++] = value;
        }

        /// <summary>
        /// Pops and returns the top element from the stack.
        /// </summary>
        /// <returns>The popped integer value.</returns>
        public int Pop()
        {
            if (this.Count == 0)
            {
                ThrowStackOverflow();
            }

            return this.s[--this.Count];
        }

        /// <summary>
        /// Peeks at the element at the specified index from the top of the stack without removing it.
        /// </summary>
        /// <param name="index">The zero-based index from the top of the stack.</param>
        /// <returns>The integer value at the specified position.</returns>
        public int Peek(int index)
        {
            if (index < 0 || index >= this.Count)
            {
                ThrowStackOverflow();
            }

            return this.s[this.Count - index - 1];
        }

        /// <summary>
        /// Throws when an instruction accesses the stack outside its valid range.
        /// </summary>
        private static void ThrowStackOverflow() => throw new FontException("stack overflow");
    }
}
