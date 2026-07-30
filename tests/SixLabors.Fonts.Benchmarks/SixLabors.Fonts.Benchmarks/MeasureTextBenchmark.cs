// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using BenchmarkDotNet.Attributes;

namespace SixLabors.Fonts.Benchmarks;

/// <summary>
/// Measures complete text bounds calculation, including shaping, segmentation, wrapping,
/// line layout, and positioned glyph bounds.
/// </summary>
/// <remarks>
/// SkiaSharp has no equivalent paragraph measurement operation. The comparable single-run
/// shaping and bounds work is measured by <see cref="MeasureShapedRunBenchmark"/>.
/// </remarks>
[MediumRunJob]
public class MeasureTextBenchmark
{
    private readonly TextOptions textOptions;

    private const string LoremIpsum = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. "
   + "Sed non risus. Suspendisse lectus tortor, dignissim sit amet, adipiscing nec, ultricies sed, dolor. "
   + "Cras elementum ultrices diam. Maecenas ligula massa, varius a, semper congue, euismod non, mi. "
   + "Proin porttitor, orci nec nonummy molestie, enim est eleifend mi, non fermentum diam nisl sit amet erat. "
   + "Duis semper. Duis arcu massa, scelerisque vitae, consequat in, pretium a, enim. Pellentesque congue. "
   + "Ut in risus volutpat libero pharetra tempor. Cras vestibulum bibendum augue. Praesent egestas leo in pede. "
   + "Praesent blandit odio eu enim. Pellentesque sed dui ut augue blandit sodales. "
   + "Vestibulum ante ipsum primis in faucibus orci luctus et ultrices posuere cubilia Curae; "
   + "Aliquam nibh. Mauris ac mauris sed pede pellentesque fermentum. Maecenas adipiscing ante non diam sodales hendrerit. "
   + "\n\n"
   + "Ut velit mauris, egestas sed, gravida nec, ornare ut, mi. Aenean ut orci vel massa suscipit pulvinar. "
   + "Nulla sollicitudin. Fusce varius, ligula non tempus aliquam, nunc turpis ullamcorper nibh, in tempus sapien eros vitae ligula. "
   + "Pellentesque rhoncus nunc et augue. Integer id felis. Curabitur aliquet pellentesque diam. "
   + "Integer quis metus vitae elit lobortis egestas. Lorem ipsum dolor sit amet, consectetuer adipiscing elit. "
   + "Morbi vel erat non mauris convallis vehicula. Nulla et sapien. Integer tortor tellus, aliquam faucibus, "
   + "convallis id, congue eu, quam. Mauris ullamcorper felis vitae erat. Proin feugiat, augue non elementum posuere, "
   + "metus purus iaculis lectus, et tristique ligula justo vitae magna.\n\n"
   + "Aliquam convallis sollicitudin purus. Praesent aliquam, enim at fermentum mollis, ligula massa adipiscing nisl, "
   + "ac euismod nibh nisl eu lectus. Fusce vulputate eleifend sapien. Vestibulum purus quam, scelerisque ut, mollis sed, "
   + "nonummy id, metus. Nullam accumsan lorem in dui. Cras ultricies mi eu turpis hendrerit fringilla. "
   + "Vestibulum ante ipsum primis in faucibus orci luctus et ultrices posuere cubilia Curae; In ac dui quis mi consectetuer lacinia. "
   + "Nam pretium turpis et arcu. Duis arcu tortor, suscipit eget, imperdiet nec, imperdiet iaculis, ipsum. "
   + "Sed aliquam ultrices mauris. Integer ante arcu, accumsan a, consectetuer eget, posuere ut, mauris. "
   + "Praesent adipiscing. Phasellus ullamcorper ipsum rutrum nunc. Nunc nonummy metus. "
   + "Vestibulum volutpat pretium libero. Cras id dui. Aenean ut eros et nisl sagittis vestibulum. "
   + "Nullam nulla eros, ultricies sit amet, nonummy id, imperdiet feugiat, pede. Sed lectus.";

    public MeasureTextBenchmark()
    {
        const string fontFamilyName = "Arial";
        const int fontSize = 16;

        this.textOptions = new TextOptions(SystemFonts.Get(fontFamilyName).CreateFont(fontSize, FontStyle.Regular))
        {
            WrappingLength = 400
        };
    }

    /// <summary>
    /// Gets or sets the text measured by the benchmark.
    /// </summary>
    [Params("a", "Hello world", "The quick brown fox jumps over the lazy dog", LoremIpsum)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Measures the rendered bounds after complete text layout.
    /// </summary>
    /// <returns>The rendered bounds.</returns>
    [Benchmark]
    public FontRectangle SixLaborsFonts() => TextMeasurer.MeasureBounds(this.Text, this.textOptions);
}
