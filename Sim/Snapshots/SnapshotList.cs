namespace StruggleGame.Sim.Snapshots;

// View over a pooled snapshot section: (backing array, valid count).
// The backing array is oversized and reused across ticks, so the public
// surface only exposes Length / [i] / foreach — never the underlying T[].
// foreach uses the struct enumerator below, which doesn't box.
public readonly struct SnapshotList<T>
{
    private readonly T[] _arr;
    private readonly int _length;

    public SnapshotList(T[] arr, int length)
    {
        _arr = arr;
        _length = length;
    }

    public static SnapshotList<T> Empty => new(System.Array.Empty<T>(), 0);

    public int Length => _length;
    public T this[int i] => _arr[i];

    public Enumerator GetEnumerator() => new(_arr, _length);

    public struct Enumerator
    {
        private readonly T[] _arr;
        private readonly int _length;
        private int _index;

        internal Enumerator(T[] arr, int length)
        {
            _arr = arr;
            _length = length;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _length;
        public T Current => _arr[_index];
    }
}
