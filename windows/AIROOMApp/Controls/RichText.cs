using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Controls;

/// <summary>
/// Renders the light Markdown models write (headings, **bold**, *italic*,
/// `code`, lists) without turning links into anything clickable — model output
/// must never become a click target. Port of the macOS RichText view.
/// </summary>
public static class RichText
{
    public static TextBlock Block(string source) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Inlines = { Render(source) },
    };

    public static Span Render(string source)
    {
        var block = new Span();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) block.Inlines.Add(new LineBreak());
            var line = lines[index];

            // Headings: "# " through "###### " render as bold lines.
            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && hashes < line.Length && line[hashes] == ' ')
            {
                block.Inlines.Add(new Run
                {
                    Text = line[(hashes + 1)..],
                    FontWeight = FontWeights.Bold,
                });
                continue;
            }

            // Bullets: "- item" / "* item" render as • items.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                block.Inlines.Add(new Run { Text = "• " + trimmed[2..] });
                continue;
            }

            AppendStyled(block, line);
        }
        return block;
    }

    /// <summary>Splits one line into runs: `code`, **bold**, *italic*, plain.</summary>
    static void AppendStyled(Span target, string line)
    {
        var buffer = "";
        for (var i = 0; i < line.Length;)
        {
            if (line[i] == '`')
            {
                var end = line.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    target.Inlines.Add(new Run
                    {
                        Text = line[(i + 1)..end],
                        FontFamily = new FontFamily("Consolas"),
                    });
                    i = end + 1;
                    continue;
                }
            }
            if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
            {
                var end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    var bold = new Span { FontWeight = FontWeights.Bold };
                    AppendStyled(bold, line[(i + 2)..end]);
                    target.Inlines.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            if (line[i] == '*')
            {
                var end = line.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    target.Inlines.Add(new Run { Text = line[(i + 1)..end], FontStyle = Windows.UI.Text.FontStyle.Italic });
                    i = end + 1;
                    continue;
                }
            }
            buffer += line[i];
            i++;
        }
        Flush();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                target.Inlines.Add(new Run { Text = buffer });
                buffer = "";
            }
        }
    }
}
