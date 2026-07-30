// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Runtime.CompilerServices;
using SixLabors.Fonts.Rendering;
using SixLabors.Fonts.Unicode;

namespace SixLabors.Fonts.Tests.Issues;

public class Issues_462
{
    private readonly FontFamily emoji = TestFonts.GetFontFamily(TestFonts.NotoColorEmojiRegular);
    private readonly FontFamily noto = TestFonts.GetFontFamily(TestFonts.NotoSansRegular);

    [Fact]
    public void CanRenderEmojiFont_With_COLRv1()
    {
        Font font = this.emoji.CreateFont(100);
        const string text = "a😨 b😅\r\nc🥲 d🤩";

        TextOptions options = new(font)
        {
            ColorFontSupport = ColorFontSupport.ColrV1,
            LineSpacing = 1.8F,
            FallbackFontFamilies = new[] { this.noto },
            TextRuns = new List<TextRun>
                {
                    new()
                    {
                        Start = 0,
                        End = text.GetGraphemeCount(),
                        TextDecorations = TextDecorations.Strikeout | TextDecorations.Underline | TextDecorations.Overline
                    }
                }
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        Assert.Equal(10, renderer.GlyphKeys.Count);

        // There are too many metrics to validate here so we just ensure no exceptions are thrown
        // and the rendering looks correct by inspecting the snapshot.
        TextLayoutTestUtilities.TestLayout(
            text,
            options,
            includeGeometry: true,
            customDecorations: true);
    }

    [Fact]
    public void CanRenderEmojiFont_With_SVG()
    {
        Font font = this.emoji.CreateFont(100);
        const string text = "a😨 b😅\r\nc🥲 d🤩";

        TextOptions options = new(font)
        {
            ColorFontSupport = ColorFontSupport.Svg,
            LineSpacing = 1.8F,
            FallbackFontFamilies = new[] { this.noto },
            TextRuns = new List<TextRun>
                {
                    new()
                    {
                        Start = 0,
                        End = text.GetGraphemeCount(),
                        TextDecorations = TextDecorations.Strikeout | TextDecorations.Underline | TextDecorations.Overline
                    }
                }
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        Assert.Equal(10, renderer.GlyphKeys.Count);

        TextLayoutTestUtilities.TestLayout(
            text,
            options,
            includeGeometry: true,
            customDecorations: true);
    }

    [Theory]
    [InlineData("robot", "🤖", 1)]
    [InlineData("clown", "🤡", 1)]
    [InlineData("leg", "🦿", 1)]
    [InlineData("mending-heart", "❤️‍🩹", 2)]
    [InlineData("heart-on-fire", "❤️‍🔥", 2)]
    public void CanRenderProblemEmojiTransforms_With_COLRv1(string name, string text, int glyphCount)
        => this.AssertCanRenderProblemEmojiTransforms(name, text, ColorFontSupport.ColrV1, glyphCount);

    [Theory]
    [InlineData("robot", "🤖", 1)]
    [InlineData("clown", "🤡", 1)]
    [InlineData("leg", "🦿", 1)]
    [InlineData("mending-heart", "❤️‍🩹", 2)]
    [InlineData("heart-on-fire", "❤️‍🔥", 2)]
    public void CanRenderProblemEmojiTransforms_With_SVG(string name, string text, int glyphCount)
        => this.AssertCanRenderProblemEmojiTransforms(name, text, ColorFontSupport.Svg, glyphCount);

    [Fact]
    public void CanRenderEmojiSanityMatrix_With_COLRv1()
        => this.AssertCanRenderEmojiSanityMatrix(ColorFontSupport.ColrV1);

    [Fact]
    public void CanRenderEmojiSanityMatrix_With_SVG()
        => this.AssertCanRenderEmojiSanityMatrix(ColorFontSupport.Svg);

    [Fact]
    public void Svg_UsesDefaultBlackFillForUnspecifiedCatFaceDetails()
    {
        Font font = this.emoji.CreateFont(256);

        TextOptions options = new(font)
        {
            ColorFontSupport = ColorFontSupport.Svg,
            FallbackFontFamilies = new[] { this.noto },
        };

        LayerCaptureRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "😸", options);

        Assert.Single(renderer.GlyphKeys);
        Assert.True(renderer.SolidLayers.Count(x => x.Color == GlyphColor.Black && Math.Abs(x.Opacity - 1F) < 0.001F) >= 9);
    }

    [Fact]
    public void Svg_PropagatesUseOpacityToReferencedGeometry()
    {
        Font font = this.emoji.CreateFont(256);

        TextOptions options = new(font)
        {
            ColorFontSupport = ColorFontSupport.Svg,
            FallbackFontFamilies = new[] { this.noto },
        };

        LayerCaptureRenderer renderer = new();
        TextRenderer.RenderTo(renderer, "🧐", options);

        Assert.Single(renderer.GlyphKeys);
        Assert.True(GlyphColor.TryParseHex("#CCCCCC", out GlyphColor monocleColor));
        Assert.Contains(renderer.SolidLayers, x => x.Color == monocleColor && Math.Abs(x.Opacity - 0.5F) < 0.001F);
    }

    private void AssertCanRenderProblemEmojiTransforms(
        string name,
        string text,
        ColorFontSupport support,
        int glyphCount,
        [CallerMemberName] string test = "")
    {
        Font font = this.emoji.CreateFont(256);

        TextOptions options = new(font)
        {
            ColorFontSupport = support,
            FallbackFontFamilies = new[] { this.noto },
        };

        // A joined sequence ligates to one visible glyph, but a skipped default
        // ignorable that survives the ligature remains in the shaped stream as an
        // ink-free invisible glyph, exactly as HarfBuzz and browsers keep it, so
        // the joined sequences render one more glyph than they show.
        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        Assert.Equal(glyphCount, renderer.GlyphKeys.Count);

        TextLayoutTestUtilities.TestLayout(text, options, test: test, properties: name);
    }

    private void AssertCanRenderEmojiSanityMatrix(
        ColorFontSupport support,
        [CallerMemberName] string test = "")
    {
        Font font = this.emoji.CreateFont(64);
        const string text =
            "😀😃😄😁😆😅😂🤣😭😉😗😙\n" +
            "😚😘🥰😍🤩🥳🙃🙂🥲🥹😋😛\n" +
            "😝😜🤪😇😊☺️😏😌😔😑😐😶\n" +
            "🫡🤔🤫🫢🤭🥱🤗🫣😱🤨🧐😒\n" +
            "🙄😮‍💨😤😠😡🤬🥺😟😥😢☹️🙁\n" +
            "🫤😕🤐😰😨😧😦😮😯😲😳🤯\n" +
            "😬😓😞😖😣😩😫😵😵‍💫🫥😴😪\n" +
            "🤤🌛🌜🌚🌝🌞🫠😶‍🌫️🥴🥵🥶🤢\n" +
            "🤮🤧🤒🤕😷🤠🤑😎🤓🥸🤥🤡\n" +
            "👻💩👽🤖🎃😈👿👹👺🔥💫⭐\n" +
            "🌟✨💥💯💢💨💦🫧💤🕳️🎉🎊\n" +
            "🙈🙉🙊😺😸😹😻😼😽🙀😿😾\n" +
            "❤️🧡💛💚💙💜🤎🖤🤍♥️💘💝\n" +
            "💖💗💓💞💕💌💟❣️❤️‍🩹💔❤️‍🔥💋\n" +
            "🫂👥👤🗣️👣🧠🫀🫁🩸🦠🦷🦴\n" +
            "☠️💀👀👁️👄🫦👅👃👂🦻🦶🦵\n" +
            "🦿🦾💪👍👎👏🫶🙌👐🤲🤝🤜\n" +
            "🤛✊👊🫳🫴🫱🫲🤚👋🖐️✋🖖\n" +
            "🤟🤘✌️🤞🫰🤙🤌🤏👌🖕☝️👆\n" +
            "👇👉👈🫵✍️🤳🙏💅";

        TextOptions options = new(font)
        {
            ColorFontSupport = support,
            FallbackFontFamilies = new[] { this.noto },
            LineSpacing = 1.15F,
        };

        GlyphRenderer renderer = new();
        TextRenderer.RenderTo(renderer, text, options);
        Assert.NotEmpty(renderer.GlyphKeys);

        TextLayoutTestUtilities.TestLayout(text, options, test: test, properties: "full-string");
    }

    private sealed class LayerCaptureRenderer : GlyphRenderer
    {
        public List<(GlyphColor Color, float Opacity)> SolidLayers { get; } = [];

        public override void BeginLayer(Paint paint, FillRule fillRule, ClipQuad? clipBounds)
        {
            if (paint is SolidPaint solidPaint)
            {
                this.SolidLayers.Add((solidPaint.Color, solidPaint.Opacity));
            }

            base.BeginLayer(paint, fillRule, clipBounds);
        }
    }
}
