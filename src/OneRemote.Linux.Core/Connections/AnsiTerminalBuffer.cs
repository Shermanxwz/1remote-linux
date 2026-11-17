using System.Text;

namespace OneRemote.Linux.Core.Connections;

/// <summary>
/// Stateful UTF-8 + VT100/ANSI terminal text buffer that preserves split multi-byte UTF-8
/// characters (e.g. Chinese CJK output across TCP frames) and handles CR, LF, BS, Clear Screen, and CSI escapes.
/// </summary>
public sealed class AnsiTerminalBuffer
{
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly List<StringBuilder> _lines = [new StringBuilder()];
    private readonly int _maxScrollbackLines;
    private int _cursorRow;
    private int _cursorCol;
    private string _pendingEscape = string.Empty;

    public AnsiTerminalBuffer(int maxScrollbackLines = 3000)
    {
        _maxScrollbackLines = Math.Max(100, maxScrollbackLines);
    }

    public string AppendBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return RenderText();

        var charCount = _utf8Decoder.GetCharCount(bytes, flush: false);
        var chars = new char[charCount];
        var written = _utf8Decoder.GetChars(bytes, chars, flush: false);
        AppendChars(chars.AsSpan(0, written));
        return RenderText();
    }

    public string AppendText(string text)
    {
        AppendChars(text.AsSpan());
        return RenderText();
    }

    public void Clear()
    {
        lock (_lines)
        {
            _lines.Clear();
            _lines.Add(new StringBuilder());
            _cursorRow = 0;
            _cursorCol = 0;
            _pendingEscape = string.Empty;
        }
    }

    private void AppendChars(ReadOnlySpan<char> span)
    {
        lock (_lines)
        {
            var input = _pendingEscape.Length > 0
                ? (_pendingEscape + new string(span)).AsSpan()
                : span;
            _pendingEscape = string.Empty;

            var i = 0;
            while (i < input.Length)
            {
                var ch = input[i];
                if (ch == '\x1b')
                {
                    if (i + 1 >= input.Length)
                    {
                        _pendingEscape = input[i..].ToString();
                        break;
                    }

                    var next = input[i + 1];
                    if (next == '[')
                    {
                        // CSI sequence: ESC [ params finalByte (0x40..0x7E)
                        var j = i + 2;
                        while (j < input.Length && (input[j] < '@' || input[j] > '~'))
                            j++;

                        if (j >= input.Length)
                        {
                            _pendingEscape = input[i..].ToString();
                            break;
                        }

                        var cmd = input[j];
                        var paramStr = input[(i + 2)..j].ToString();
                        HandleCsi(cmd, paramStr);
                        i = j + 1;
                        continue;
                    }
                    if (next == ']')
                    {
                        // OSC sequence: ESC ] ... BEL (\x07) or ST (ESC \)
                        var j = i + 2;
                        var terminated = false;
                        while (j < input.Length)
                        {
                            if (input[j] == '\x07')
                            {
                                j++;
                                terminated = true;
                                break;
                            }
                            if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\')
                            {
                                j += 2;
                                terminated = true;
                                break;
                            }
                            j++;
                        }

                        if (!terminated)
                        {
                            _pendingEscape = input[i..].ToString();
                            break;
                        }
                        i = j;
                        continue;
                    }

                    // 2-byte escape sequence
                    i += 2;
                    continue;
                }

                switch (ch)
                {
                    case '\r':
                        _cursorCol = 0;
                        break;
                    case '\n':
                        _cursorRow++;
                        EnsureRow(_cursorRow);
                        break;
                    case '\b':
                        if (_cursorCol > 0) _cursorCol--;
                        break;
                    case '\x07': // BEL
                        break;
                    case '\t':
                        var spaces = 4 - (_cursorCol % 4);
                        for (var s = 0; s < spaces; s++) WriteChar(' ');
                        break;
                    default:
                        if (ch >= ' ')
                            WriteChar(ch);
                        break;
                }
                i++;
            }

            TrimScrollback();
        }
    }

    private void HandleCsi(char cmd, string paramStr)
    {
        switch (cmd)
        {
            case 'J':
                if (paramStr is "2" or "3")
                {
                    _lines.Clear();
                    _lines.Add(new StringBuilder());
                    _cursorRow = 0;
                    _cursorCol = 0;
                }
                else if (string.IsNullOrEmpty(paramStr) || paramStr == "0")
                {
                    EnsureRow(_cursorRow);
                    var line = _lines[_cursorRow];
                    if (_cursorCol < line.Length)
                        line.Length = _cursorCol;
                }
                break;
            case 'K':
                if (string.IsNullOrEmpty(paramStr) || paramStr == "0")
                {
                    EnsureRow(_cursorRow);
                    var line = _lines[_cursorRow];
                    if (_cursorCol < line.Length)
                        line.Length = _cursorCol;
                }
                else if (paramStr == "2")
                {
                    EnsureRow(_cursorRow);
                    _lines[_cursorRow].Clear();
                    _cursorCol = 0;
                }
                break;
            case 'H':
            case 'f':
                if (string.IsNullOrEmpty(paramStr))
                {
                    _cursorCol = 0;
                }
                break;
            case 'C':
                _cursorCol += ParsePositive(paramStr, 1);
                break;
            case 'D':
                _cursorCol = Math.Max(0, _cursorCol - ParsePositive(paramStr, 1));
                break;
            case 'P':
                EnsureRow(_cursorRow);
                var delCount = ParsePositive(paramStr, 1);
                var curLine = _lines[_cursorRow];
                if (_cursorCol < curLine.Length)
                    curLine.Remove(_cursorCol, Math.Min(delCount, curLine.Length - _cursorCol));
                break;
        }
    }

    private static int ParsePositive(string s, int fallback) =>
        int.TryParse(s, out var v) && v > 0 ? v : fallback;

    private void EnsureRow(int row)
    {
        while (_lines.Count <= row)
            _lines.Add(new StringBuilder());
    }

    private void WriteChar(char ch)
    {
        EnsureRow(_cursorRow);
        var line = _lines[_cursorRow];
        while (line.Length < _cursorCol)
            line.Append(' ');

        if (_cursorCol < line.Length)
            line[_cursorCol] = ch;
        else
            line.Append(ch);

        _cursorCol++;
    }

    private void TrimScrollback()
    {
        if (_lines.Count > _maxScrollbackLines)
        {
            var excess = _lines.Count - _maxScrollbackLines;
            _lines.RemoveRange(0, excess);
            _cursorRow = Math.Max(0, _cursorRow - excess);
        }
    }

    public string RenderText()
    {
        lock (_lines)
        {
            return string.Join('\n', _lines.Select(x => x.ToString()));
        }
    }
}
