using System.Text;

namespace Hhub.Core;

internal sealed class PluginPipeReader(StreamReader reader, int limit)
{
    private readonly char[] _buffer = new char[4096];
    private int _position, _length;
    private bool _skipLineFeed;

    public async ValueTask<string?> ReadLineAsync()
    {
        StringBuilder? partial = null;
        while (true)
        {
            if (_position == _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
                _position = 0;
                if (_length == 0) return partial?.ToString();
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_buffer[_position] == '\n')
                {
                    _position++;
                    if (_position == _length) continue;
                }
            }

            int newline = _buffer.AsSpan(_position, _length - _position).IndexOfAny('\r', '\n');
            int end = newline >= 0 ? _position + newline : _length;
            int count = end - _position;
            if (count > limit - (partial?.Length ?? 0))
                throw new InvalidDataException("插件通信消息超过大小限制");
            if (newline >= 0)
            {
                string line = partial is null ? new string(_buffer, _position, count)
                    : partial.Append(_buffer, _position, count).ToString();
                _skipLineFeed = _buffer[end] == '\r';
                _position = end + 1;
                return line;
            }
            (partial ??= new StringBuilder()).Append(_buffer, _position, count);
            _position = end;
        }
    }
}
