using System.Text;

namespace Chapter.Core.Git;

/// <summary>
/// Turns chunked process output into bounded, de-duplicated progress lines.
///
/// Git uses both newlines and carriage returns for transfer status, and a final status line
/// is not required to end with either one. Keeping the buffering here shared by clone and
/// remote operations prevents one long-running path from quietly dropping the last update.
/// </summary>
internal sealed class ProgressLineParser(Action<GitOutputStream, string> line)
{
    private const int MaxBufferedLine = 160;

    private readonly Dictionary<GitOutputStream, StringBuilder> _buffers = new();
    private readonly Dictionary<GitOutputStream, string> _last = new();
    private readonly object _gate = new();

    public void Push(GitOutputChunk chunk)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(chunk.Stream, out var buffer))
            {
                buffer = new StringBuilder();
                _buffers[chunk.Stream] = buffer;
                _last[chunk.Stream] = "";
            }

            buffer.Append(chunk.Text);
            Drain(chunk.Stream, buffer, flush: false);
        }
    }

    /// <summary>Emits any unterminated line left after the process closes its streams.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            foreach (var (stream, buffer) in _buffers)
                Drain(stream, buffer, flush: true);
        }
    }

    private void Drain(GitOutputStream stream, StringBuilder buffer, bool flush)
    {
        while (true)
        {
            var terminator = FindBoundary(buffer);
            if (terminator < 0)
            {
                if (!flush && buffer.Length < MaxBufferedLine) return;
            }

            // A line without a terminator is split only to keep the bridge payload bounded.
            // Those pieces are continuations, not repeated progress updates, so they must not
            // be removed by the duplicate-line filter when two adjacent pieces happen to be
            // identical (for example, a long run of the same character).
            var forcedChunk = terminator < 0;

            // A helper can write an arbitrarily long line without a terminator. Report it
            // in bounded pieces instead of retaining an ever-growing StringBuilder or
            // sending a multi-kilobyte status message over the bridge in one event.
            var boundary = Math.Min(
                terminator < 0 ? buffer.Length : terminator,
                MaxBufferedLine);

            var message = buffer.ToString(0, boundary).Trim();
            var remove = boundary;
            while (remove < buffer.Length && buffer[remove] is '\r' or '\n') remove++;
            buffer.Remove(0, remove);

            if (message.Length > 0 && (forcedChunk || message != _last[stream]))
            {
                if (!forcedChunk) _last[stream] = message;
                try { line(stream, message); }
                catch (Exception ex)
                {
                    // Progress is observational. A UI subscriber must not terminate the
                    // underlying git operation or prevent the remaining buffers from flushing.
                    System.Diagnostics.Debug.WriteLine($"Progress subscriber failed: {ex.Message}");
                }
            }

            if (buffer.Length == 0) return;
            if (!flush && buffer.Length < MaxBufferedLine && FindBoundary(buffer) < 0) return;
        }
    }

    private static int FindBoundary(StringBuilder value)
    {
        for (var i = 0; i < value.Length; i++)
            if (value[i] is '\r' or '\n') return i;
        return -1;
    }
}
