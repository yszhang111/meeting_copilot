namespace ProactiveAI.Core.Audio;

/// <summary>
/// Bounded in-memory audio history for one source on the session timeline. Used for diarization
/// windows, speaker reference clips and echo analysis. Never written to disk. Gaps (e.g. loopback
/// silence) are represented implicitly and filled with zeros on extraction so that window-relative
/// time maps linearly onto session time.
/// </summary>
public sealed class AudioTimeline
{
    private readonly object _gate = new();
    private readonly LinkedList<Block> _blocks = new();
    private readonly TimeSpan _retention;
    private readonly long _maxBytes;
    private long _bytes;

    public AudioTimeline(TimeSpan retention, long maxBytes)
    {
        _retention = retention;
        _maxBytes = maxBytes;
    }

    public long Bytes
    {
        get { lock (_gate) { return _bytes; } }
    }

    public TimeSpan? Start
    {
        get { lock (_gate) { return _blocks.First?.Value.Start; } }
    }

    public TimeSpan? End
    {
        get { lock (_gate) { return _blocks.Last?.Value.End; } }
    }

    public void Append(TimeSpan start, short[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            // Keep the timeline monotonic: a packet overlapping the previous one by jitter is shifted forward,
            // and a packet starting within rounding/jitter distance after it is joined sample-contiguously
            // (echo cancellation uses this timeline as a sample-accurate reference).
            if (_blocks.Last is { } last)
            {
                var delta = start - last.Value.End;
                if ((delta < TimeSpan.Zero && -delta < TimeSpan.FromMilliseconds(30)) || (delta > TimeSpan.Zero && delta < TimeSpan.FromMilliseconds(2)))
                {
                    start = last.Value.End;
                }
            }
            _blocks.AddLast(new Block(start, samples));
            _bytes += samples.Length * 2L;
            TrimLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _blocks.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Drops audio that ends before <paramref name="before"/>.</summary>
    public void DiscardBefore(TimeSpan before)
    {
        lock (_gate)
        {
            while (_blocks.First is { } first && first.Value.End <= before)
            {
                _bytes -= first.Value.Samples.Length * 2L;
                _blocks.RemoveFirst();
            }
        }
    }

    /// <summary>Returns samples covering [from, to) with zero fill for gaps, or null when no audio overlaps.</summary>
    public short[]? Extract(TimeSpan from, TimeSpan to)
    {
        if (to <= from)
        {
            return null;
        }
        int total = PipelineAudio.Samples(to - from);
        var result = new short[total];
        bool any = false;
        lock (_gate)
        {
            // Callers mostly read recent audio (echo reference every ~10 ms) and blocks are in time order: find
            // the first relevant block from the newest end, then copy forward so later blocks win on overlap.
            var horizon = from - TimeSpan.FromSeconds(1);
            var node = _blocks.Last;
            while (node?.Previous is { } prev && prev.Value.End >= horizon)
            {
                node = prev;
            }
            for (; node is not null; node = node.Next)
            {
                var block = node.Value;
                if (block.End <= from || block.Start >= to)
                {
                    continue;
                }
                any = true;
                int srcOffset = block.Start < from ? PipelineAudio.Samples(from - block.Start) : 0;
                int dstOffset = block.Start > from ? PipelineAudio.Samples(block.Start - from) : 0;
                int count = Math.Min(block.Samples.Length - srcOffset, total - dstOffset);
                if (count > 0 && srcOffset >= 0 && dstOffset >= 0)
                {
                    Array.Copy(block.Samples, srcOffset, result, dstOffset, count);
                }
            }
        }
        return any ? result : null;
    }

    private void TrimLocked()
    {
        if (_blocks.Last is null)
        {
            return;
        }
        var horizon = _blocks.Last.Value.End - _retention;
        while (_blocks.First is { } first && (first.Value.End <= horizon || _bytes > _maxBytes))
        {
            _bytes -= first.Value.Samples.Length * 2L;
            _blocks.RemoveFirst();
        }
    }

    private readonly record struct Block(TimeSpan Start, short[] Samples)
    {
        public TimeSpan End => Start + PipelineAudio.Duration(Samples.Length);
    }
}
