using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YtDlpTool.Controls;

public class MaskedTimeTextBox : TextBox
{
    private const string EmptyMask = "00:00:00";

    public MaskedTimeTextBox()
    {
        if (string.IsNullOrEmpty(Text)) Text = EmptyMask;
        MaxLength = 8;
        GotFocus += (_, _) => SelectAll();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(this, OnPasting);
        TextChanged += OnTextChanged;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the mask intact even if external code sets Text to something else
        if (Text.Length != 8 || Text[2] != ':' || Text[5] != ':')
        {
            var cleaned = NormalizeToMask(Text);
            if (cleaned != Text)
            {
                var caret = CaretIndex;
                Text = cleaned;
                CaretIndex = System.Math.Min(caret, 8);
            }
        }
    }

    private static string NormalizeToMask(string? raw)
    {
        var digits = new System.Text.StringBuilder();
        foreach (var c in raw ?? "")
            if (char.IsDigit(c)) digits.Append(c);
        while (digits.Length < 6) digits.Append('0');
        return $"{digits[0]}{digits[1]}:{digits[2]}{digits[3]}:{digits[4]}{digits[5]}";
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || !char.IsDigit(e.Text, 0)) { e.Handled = true; return; }
        var pos = CaretIndex;
        // If caret is at a colon, skip over it
        if (pos == 2 || pos == 5) pos++;
        if (pos > 7) { e.Handled = true; return; }
        // Replace the digit at pos
        var chars = Text.ToCharArray();
        if (pos >= chars.Length) { e.Handled = true; return; }
        chars[pos] = e.Text[0];
        Text = new string(chars);
        // Advance caret, skipping colons
        var next = pos + 1;
        if (next == 2 || next == 5) next++;
        CaretIndex = System.Math.Min(next, 8);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            var pos = CaretIndex - 1;
            if (pos < 0) { e.Handled = true; return; }
            if (pos == 2 || pos == 5) pos--; // skip back over colon
            if (pos < 0) { e.Handled = true; return; }
            var chars = Text.ToCharArray();
            chars[pos] = '0';
            Text = new string(chars);
            CaretIndex = pos;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var pos = CaretIndex;
            if (pos == 2 || pos == 5) pos++; // skip colon
            if (pos > 7) { e.Handled = true; return; }
            var chars = Text.ToCharArray();
            chars[pos] = '0';
            Text = new string(chars);
            CaretIndex = pos;
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (CaretIndex == 3 || CaretIndex == 6) { CaretIndex = CaretIndex - 2; e.Handled = true; }
        }
        else if (e.Key == Key.Right)
        {
            if (CaretIndex == 2 || CaretIndex == 5) { CaretIndex = CaretIndex + 1; e.Handled = true; }
        }
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var pasted = (string)e.DataObject.GetData(typeof(string));
            var normalised = NormalizeToMask(pasted);
            Text = normalised;
            CaretIndex = 0;
        }
        e.CancelCommand();
    }
}
