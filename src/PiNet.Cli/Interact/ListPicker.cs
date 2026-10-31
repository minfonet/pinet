namespace PiNet.Cli.Interact;

/// <summary>One selectable row of the interactive list picker.</summary>
/// <param name="Display">Label rendered on the live selection line.</param>
/// <param name="Value">Value handed back to the caller when the row is picked.</param>
public sealed record ListPickerItem(string Display, string Value);

/// <summary>
/// Zero-dependency interactive list picker (uses the same raw <c>ReadKey</c> +
/// <c>\r</c> in-place line rewrite technique as the line editor — ADR Stage 4).
///
/// The caller prints the numbered options once above the picker; the picker
/// renders a live selection line that shows the highlighted row. ↑/↓ (and
/// Home/End) move the highlight; typing switches to typed entry (a number or a
/// free text value, resolved by the caller as before, so scripted uses keep
/// working); Backspace returns to highlight mode. Enter picks the highlighted
/// row (empty buffer) or submits the typed value; Escape or 'q'+Enter cancels.
/// </summary>
public static class ListPicker
{
    private const string Prompt = "pick (↑/↓ move · enter select · esc cancel; or type # / id + enter) > ";

    /// <summary>
    /// Runs the picker over <paramref name="items"/>.
    /// Returns the picked item's value — the highlighted item's value when
    /// Enter is pressed with an empty buffer, otherwise the typed text verbatim
    /// (numbers and ids are resolved by the caller). Null when cancelled
    /// (Escape, 'q'+Enter, Ctrl+C, EOF).
    /// </summary>
    public static string? Pick(
        IConsoleInput input,
        IConsoleOutput output,
        IReadOnlyList<ListPickerItem> items,
        int defaultIndex = 0)
    {
        if (items.Count == 0)
            return null;

        var index = Math.Clamp(defaultIndex, 0, items.Count - 1);
        var typed = new System.Text.StringBuilder();

        void Redraw()
        {
            var line = typed.Length > 0
                ? typed.ToString()
                : $"[{index + 1}/{items.Count}] {items[index].Display}";
            output.RenderEditorLine(Prompt, line, line.Length);
        }

        Redraw();
        while (true)
        {
            var key = input.ReadKey();
            if (key is null)
            {
                output.WriteLine();
                return null; // EOF / closed input
            }

            var k = key.Value;
            if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key is ConsoleKey.C or ConsoleKey.D or ConsoleKey.Z)
            {
                output.WriteLine("^C");
                return null;
            }

            switch (k.Key)
            {
                case ConsoleKey.UpArrow:
                    index = (index - 1 + items.Count) % items.Count;
                    typed.Clear();
                    Redraw();
                    break;
                case ConsoleKey.DownArrow:
                    index = (index + 1) % items.Count;
                    typed.Clear();
                    Redraw();
                    break;
                case ConsoleKey.Home:
                    index = 0;
                    typed.Clear();
                    Redraw();
                    break;
                case ConsoleKey.End:
                    index = items.Count - 1;
                    typed.Clear();
                    Redraw();
                    break;
                case ConsoleKey.Escape:
                    output.WriteLine("(cancelled)");
                    return null;
                case ConsoleKey.Enter:
                    output.WriteLine();
                    if (typed.Length == 0)
                        return items[index].Value;
                    var text = typed.ToString().Trim();
                    return text.Equals("q", StringComparison.OrdinalIgnoreCase) ? null : text;
                case ConsoleKey.Backspace:
                    if (typed.Length > 0)
                    {
                        typed.Remove(typed.Length - 1, 1);
                        Redraw();
                    }
                    break;
                default:
                    if (k.KeyChar == '\0' || char.IsControl(k.KeyChar))
                        break;
                    typed.Append(k.KeyChar);
                    Redraw();
                    break;
            }
        }
    }
}
