// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using BenchmarkDotNet.Attributes;
using SixLabors.Fonts.Rendering;

namespace SixLabors.Fonts.Benchmarks;

/// <summary>
/// Measures glyph outline rendering into a no-op renderer across hinting modes, separating
/// the steady state path that reuses cached per size outlines from the first render path
/// that scales, hints and grid fits a fresh outline for every size.
/// </summary>
[Config(typeof(Config.Short))]
public class RenderGlyphsBenchmark
{
    private const string Text = "The quick brown fox jumps over the lazy dog. 0123456789";
    private const int ColdSizeCount = 256;

    private readonly NoOpGlyphRenderer renderer = new();
    private TextOptions warmOptions = null!;
    private Font coldFont = null!;

    /// <summary>
    /// Gets or sets the hinting mode applied to every render.
    /// </summary>
    [Params(HintingMode.None, HintingMode.Standard, HintingMode.Full)]
    public HintingMode Hinting { get; set; }

    /// <summary>
    /// Creates the shared font and options for the steady state benchmark.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        Font font = SystemFonts.Get("Arial").CreateFont(10, FontStyle.Regular);
        this.warmOptions = new TextOptions(font)
        {
            HintingMode = this.Hinting,
        };
    }

    /// <summary>
    /// Creates a pristine font instance so the per size outline caches start empty.
    /// </summary>
    [IterationSetup(Target = nameof(RenderNewSizePerRender))]
    public void ColdSetup() => this.coldFont = SystemFonts.Get("Arial").CreateFont(10, FontStyle.Regular);

    /// <summary>
    /// Renders repeatedly at one size, exercising the cached outline path that dominates
    /// real workloads.
    /// </summary>
    [Benchmark]
    public void RenderCachedSize() => TextRenderer.RenderTo(this.renderer, Text, this.warmOptions);

    /// <summary>
    /// Renders once at each of many distinct sizes so every glyph is scaled, hinted and
    /// grid fitted from scratch, isolating the per size preparation cost.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ColdSizeCount)]
    public void RenderNewSizePerRender()
    {
        for (int i = 0; i < ColdSizeCount; i++)
        {
            float size = 8F + (i * (1F / 64F));
            TextOptions options = new(new Font(this.coldFont, size))
            {
                HintingMode = this.Hinting,
            };

            TextRenderer.RenderTo(this.renderer, Text, options);
        }
    }

    private sealed class NoOpGlyphRenderer : IGlyphRenderer
    {
        public void ArcTo(float radiusX, float radiusY, float rotation, bool largeArc, bool sweep, Vector2 point)
        {
        }

        public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters) => true;

        public void BeginFigure()
        {
        }

        public void BeginLayer(Paint? paint, FillRule fillRule, ClipQuad? clipBounds)
        {
        }

        public void BeginText(in FontRectangle bounds)
        {
        }

        public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point)
        {
        }

        public TextDecorations EnabledDecorations() => TextDecorations.None;

        public void EndFigure()
        {
        }

        public void EndGlyph()
        {
        }

        public void EndLayer()
        {
        }

        public void EndText()
        {
        }

        public void LineTo(Vector2 point)
        {
        }

        public void MoveTo(Vector2 point)
        {
        }

        public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point)
        {
        }

        public void SetDecoration(TextDecorations textDecorations, Vector2 start, Vector2 end, float thickness, ReadOnlyMemory<float> intersections)
        {
        }
    }
}
