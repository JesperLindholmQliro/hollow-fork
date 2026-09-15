/*
 *  Copyright 2016-2019 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

namespace Hollow.Benchmarks;

/// <summary>
/// A bounded buffer with a stream at each end: what one thread writes, another reads, and whichever
/// gets ahead waits for the other.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>PipedOutputStream</c> and <c>PipedInputStream</c>, which .NET has no equivalent of. The
/// pipes in <c>System.IO.Pipes</c> are the operating system's, so two ends in one process still send
/// the bytes through the kernel — and holding both ends of an anonymous pipe in one process means
/// sharing one handle between the two streams, so closing the writing end closes the reading end too.
/// Sixty lines of circular buffer is both closer to what Java does and less trouble.
/// </para>
/// <para>
/// No cancellation and no timeout: a reader whose writer never finishes waits forever, exactly as
/// Java's does. This is a benchmark.
/// </para>
/// </remarks>
internal sealed class InProcessPipe
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();

    private int _head;
    private int _count;
    private bool _writingComplete;

    internal InProcessPipe(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _buffer = new byte[capacity];
        Reading = new ReadingEnd(this);
        Writing = new WritingEnd(this);
    }

    /// <summary>The end to read the bytes out of.</summary>
    internal Stream Reading { get; }

    /// <summary>The end to write the bytes into.</summary>
    internal Stream Writing { get; }

    /// <summary>Says there will be no more bytes, so that the reader stops waiting for them.</summary>
    internal void CompleteWriting()
    {
        lock (_gate)
        {
            _writingComplete = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            while (!data.IsEmpty)
            {
                while (_count == _buffer.Length)
                {
                    Monitor.Wait(_gate);
                }

                int at = (_head + _count) % _buffer.Length;

                // Two limits, because the buffer wraps: what is free, and what is left before the end
                // of the array. Whichever runs out first is this pass's chunk.
                int chunk = Math.Min(data.Length, Math.Min(_buffer.Length - _count, _buffer.Length - at));

                data[..chunk].CopyTo(_buffer.AsSpan(at));
                _count += chunk;
                data = data[chunk..];

                Monitor.PulseAll(_gate);
            }
        }
    }

    private int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            while (_count == 0 && !_writingComplete)
            {
                Monitor.Wait(_gate);
            }

            if (_count == 0)
            {
                return 0;
            }

            int chunk = Math.Min(destination.Length, Math.Min(_count, _buffer.Length - _head));

            _buffer.AsSpan(_head, chunk).CopyTo(destination);
            _head = (_head + chunk) % _buffer.Length;
            _count -= chunk;

            Monitor.PulseAll(_gate);

            return chunk;
        }
    }

    /// <summary>
    /// The common parts of the two ends: a stream that cannot seek and counts what went through it.
    /// </summary>
    private abstract class End : Stream
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException("a pipe has no length");

        private long _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("a pipe cannot seek");
        }

        private protected void Advance(int count) => _position += count;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("a pipe cannot seek");

        public override void SetLength(long value) =>
            throw new NotSupportedException("a pipe has no length");
    }

    private sealed class ReadingEnd(InProcessPipe pipe) : End
    {
        public override bool CanRead => true;

        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = pipe.Read(buffer);
            Advance(read);

            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("this is the reading end");
    }

    private sealed class WritingEnd(InProcessPipe pipe) : End
    {
        public override bool CanRead => false;

        public override bool CanWrite => true;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("this is the writing end");

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            pipe.Write(buffer);
            Advance(buffer.Length);
        }

        public override void WriteByte(byte value) => Write([value]);
    }
}
