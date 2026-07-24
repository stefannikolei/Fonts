// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using BenchmarkDotNet.Attributes;
using HarfBuzzSharp;
using HBBuffer = HarfBuzzSharp.Buffer;
using HBFace = HarfBuzzSharp.Face;
using HBFont = HarfBuzzSharp.Font;

namespace SixLabors.Fonts.Benchmarks;

/// <summary>
/// Defines the text shape used by <see cref="ShapeTextBenchmark"/>.
/// </summary>
public enum ShapeTextBenchmarkScenario
{
    /// <summary>
    /// Latin text with standard ligature opportunities, shaped with Open Sans.
    /// </summary>
    Latin,

    /// <summary>
    /// Arabic text exercising joining forms and mandatory ligatures, shaped with Dubai.
    /// </summary>
    Arabic,

    /// <summary>
    /// Devanagari text exercising conjuncts, matras, and reordering, shaped with
    /// Noto Sans Devanagari.
    /// </summary>
    Devanagari
}

/// <summary>
/// Compares end to end single run text shaping between <see cref="TextShaper"/> and
/// HarfBuzz via HarfBuzzSharp. Both sides shape identical font file bytes and walk the
/// resulting glyph stream, summing the advances so the output is fully consumed.
/// </summary>
/// <remarks>
/// The operations are not identical: <see cref="TextShaper"/> runs bidi analysis and run
/// itemization internally, while HarfBuzz expects the caller to have segmented the text
/// and only guesses the buffer's script, direction, and language. The comparison measures
/// what a consumer pays for a shaped glyph stream through each API.
/// </remarks>
[Config(typeof(Config.Short))]
public class ShapeTextBenchmark : IDisposable
{
    private string text = string.Empty;
    private TextOptions textOptions = null!;
    private Blob? blob;
    private HBFace? face;
    private HBFont? hbFont;
    private HBBuffer buffer = null!;

    /// <summary>
    /// Gets or sets the text scenario used by the benchmark.
    /// </summary>
    [Params(ShapeTextBenchmarkScenario.Latin, ShapeTextBenchmarkScenario.Arabic, ShapeTextBenchmarkScenario.Devanagari)]
    public ShapeTextBenchmarkScenario Scenario { get; set; }

    /// <summary>
    /// Initializes the input text, fonts, and HarfBuzz state for each scenario.
    /// </summary>
    [GlobalSetup]
    public void SetUp()
    {
        string fontPath;
        if (this.Scenario == ShapeTextBenchmarkScenario.Latin)
        {
            fontPath = GetFontPath("OpenSans-Regular.ttf");
            this.text = "The quick brown fox jumps over the lazy dog; fifty fluffy waffles.";
        }
        else if (this.Scenario == ShapeTextBenchmarkScenario.Arabic)
        {
            fontPath = GetFontPath("Dubai-Regular.ttf");
            this.text = "سلام عليكم ورحمة الله وبركاته لا إله إلا الله";
        }
        else
        {
            fontPath = GetFontPath("NotoSansDevanagari-Regular.ttf");
            this.text = "क्षत्रिय द्वारा प्रकृति की रक्षा कर्तव्य है";
        }

        Font font = new FontCollection().Add(fontPath).CreateFont(16);
        this.textOptions = new TextOptions(font);

        this.blob = Blob.FromFile(fontPath);
        this.face = new HBFace(this.blob, 0);
        this.hbFont = new HBFont(this.face);
        this.hbFont.SetFunctionsOpenType();
        this.buffer = new HBBuffer();
    }

    /// <summary>
    /// Shapes the text with <see cref="TextShaper"/> and sums the resulting advances.
    /// </summary>
    /// <returns>The advance sum, returned so the shaped stream is fully consumed.</returns>
    [Benchmark]
    public int ShapeSixLaborsFonts()
    {
        IReadOnlyList<ShapedGlyph> glyphs = TextShaper.Shape(this.text, this.textOptions);

        int advanceSum = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            advanceSum += glyphs[i].AdvanceWidth;
        }

        return advanceSum;
    }

    /// <summary>
    /// Shapes the text with HarfBuzz, reusing one buffer as production text stacks do,
    /// and sums the resulting advances.
    /// </summary>
    /// <returns>The advance sum, returned so the shaped stream is fully consumed.</returns>
    [Benchmark(Baseline = true)]
    public int ShapeHarfBuzz()
    {
        this.buffer.Reset();
        this.buffer.AddUtf16(this.text);
        this.buffer.GuessSegmentProperties();
        this.hbFont!.Shape(this.buffer);

        ReadOnlySpan<GlyphPosition> positions = this.buffer.GetGlyphPositionSpan();

        int advanceSum = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            advanceSum += positions[i].XAdvance;
        }

        return advanceSum;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.buffer?.Dispose();
        this.hbFont?.Dispose();
        this.face?.Dispose();
        this.blob?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resolves a test font path by walking up from the benchmark output directory to
    /// the repository root.
    /// </summary>
    /// <param name="fileName">The font file name within tests/Fonts.</param>
    /// <returns>The full font path.</returns>
    private static string GetFontPath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SixLabors.Fonts.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new IOException("Unable to locate the repository root.");
        }

        return Path.Combine(directory.FullName, "tests", "Fonts", fileName);
    }
}
