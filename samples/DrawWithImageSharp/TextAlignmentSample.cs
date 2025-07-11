// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DrawWithImageSharp;

public static class TextAlignmentSample
{
    public static void Generate(Font font)
    {
        using Image<Rgba32> img = new(1000, 1000);
        img.Mutate(x => x.Fill(Color.White));

        foreach (VerticalAlignment v in Enum.GetValues(typeof(VerticalAlignment)))
        {
            foreach (HorizontalAlignment h in Enum.GetValues(typeof(HorizontalAlignment)))
            {
                Draw(img, font, v, h);
            }
        }

        img.Save("Output/Alignment.png");
    }

    public static void Draw(Image<Rgba32> img, Font font, VerticalAlignment vert, HorizontalAlignment horiz)
    {
        Vector2 location = Vector2.Zero;

        switch (vert)
        {
            case VerticalAlignment.Top:
                location.Y = 0;
                break;
            case VerticalAlignment.Center:
                location.Y = img.Height / 2F;
                break;
            case VerticalAlignment.Bottom:
                location.Y = img.Height;
                break;
            default:
                break;
        }

        switch (horiz)
        {
            case HorizontalAlignment.Left:
                location.X = 0;
                break;
            case HorizontalAlignment.Right:
                location.X = img.Width;
                break;
            case HorizontalAlignment.Center:
                location.X = img.Width / 2F;
                break;
            default:
                break;
        }

        CustomGlyphBuilder glyphBuilder = new();

        TextRenderer renderer = new(glyphBuilder);

        TextOptions textOptions = new(font)
        {
            TabWidth = 4,
            WrappingLength = 0,
            HorizontalAlignment = horiz,
            VerticalAlignment = vert,
            Origin = location
        };

        string text = $"{horiz} x y z\n{vert} x y z";
        renderer.RenderText(text, textOptions);

        IEnumerable<IPath> shapesToDraw = glyphBuilder.Paths;
        img.Mutate(x => x.Fill(Color.Black, glyphBuilder.Paths));

        Rgba32 f = Color.Fuchsia;
        f.A = 128;
        img.Mutate(x => x.Fill(Color.Black, glyphBuilder.Paths));
        img.Mutate(x => x.Draw(f, 1, glyphBuilder.Boxes));
        img.Mutate(x => x.Draw(Color.Lime, 1, glyphBuilder.TextBox));
    }
}
