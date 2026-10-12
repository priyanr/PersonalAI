using System.Text;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Accumulates one stream of a sandbox exec (stdout or stderr) under a fixed character budget,
/// keeping BOTH ends of the output.
///
/// Two things here are deliberate and easy to get wrong:
///
/// 1. <b>Keep the tail, not just the head.</b> Shell output puts the interesting part at the END — the
///    compiler error, the stack trace, "ERROR: Failed building wheel". Capping by keeping the first N
///    characters and dropping the rest hands the model the banner and throws away the diagnosis. So the
///    budget is split: a smaller head (enough to see what the command started doing) and a larger rolling
///    tail, with a note recording how much was dropped in between.
///
/// 2. <b>Decode across chunk boundaries.</b> Output arrives as base64'd byte chunks cut at an arbitrary
///    byte offset, so a multi-byte UTF-8 sequence is routinely split across two chunks. Calling
///    Encoding.UTF8.GetString on each chunk independently turns every such character into U+FFFD. A single
///    stateful Decoder carries the partial sequence over to the next chunk instead.</summary>
internal sealed class BoundedOutputAccumulator
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _head = new();
    private readonly char[] _tail;
    private readonly int _headBudget;
    private int _tailStart;
    private int _tailCount;
    private long _elided;

    public BoundedOutputAccumulator(int maxChars)
    {
        var budget = Math.Max(0, maxChars);
        _headBudget = budget / 3;
        _tail = new char[budget - _headBudget];
    }

    /// <summary>True once anything had to be dropped — surfaced to the model so it knows the output it is
    /// reading is not the whole story.</summary>
    public bool Truncated => _elided > 0;

    public void Append(byte[] bytes)
    {
        if (bytes.Length == 0) return;

        var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length, flush: false)];
        var written = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
        for (var i = 0; i < written; i++)
            AppendChar(chars[i]);
    }

    private void AppendChar(char c)
    {
        if (_head.Length < _headBudget)
        {
            _head.Append(c);
            return;
        }

        if (_tail.Length == 0)
        {
            _elided++;
            return;
        }

        if (_tailCount == _tail.Length)
        {
            // Ring is full: overwrite the oldest tail character, which becomes part of the elided middle.
            _tail[_tailStart] = c;
            _tailStart = (_tailStart + 1) % _tail.Length;
            _elided++;
        }
        else
        {
            _tail[(_tailStart + _tailCount) % _tail.Length] = c;
            _tailCount++;
        }
    }

    public override string ToString()
    {
        var tail = string.Create(_tailCount, this, static (span, self) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = self._tail[(self._tailStart + i) % self._tail.Length];
        });

        return _elided == 0
            ? _head + tail
            : $"{_head}\n... {_elided:N0} characters elided ...\n{tail}";
    }
}
