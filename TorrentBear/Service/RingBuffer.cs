using System;
using System.Linq;

namespace TorrentBear.Service
{
    public class RingBuffer
    {
        private readonly long[] _buffer;
        private readonly int _capacity;
        private int _size;
        private int _index;
        public RingBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new long[capacity];
        }

        public void Reset()
        {
            _size = 0;
            _index = 0;
            for (var i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = 0;
            }
        }

        public long Average()
        {
            return _buffer.Sum() / Math.Max(_size, 1);
        }

        public void Add(long value)
        {
            _buffer[_index] = value;
            Increment(ref _index);
            if (_size != _capacity)
            {
                _size++;
            }
        }

        private void Increment(ref int index)
        {
            if (++index == _capacity)
            {
                index = 0;
            }
        }
    }
}