using System.Text;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Collects one stream of a sandbox exec (stdout or stderr) under a character budget. It keeps a small head
/// and a larger tail, because a shell puts the error at the end. One stateful decoder spans all chunks, because a
/// chunk can end in the middle of a multi-byte UTF-8 character.</summary>
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
