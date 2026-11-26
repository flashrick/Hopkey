namespace Hopkey.Core.Search;

/// <summary>Bounded min-heap keeping the top K (score, a, b) items. No allocations after construction.</summary>
public sealed class TopN
{
    private readonly double[] _score;
    private readonly int[] _a;
    private readonly int[] _b;
    private int _count;
    public int Capacity { get; }
    public int Count => _count;
    /// <summary>Total number of pushes (to detect truncation).</summary>
    public long Pushed { get; private set; }

    public TopN(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _score = new double[Capacity];
        _a = new int[Capacity];
        _b = new int[Capacity];
    }

    public double MinScore => _count < Capacity ? double.MinValue : _score[0];

    public void Push(double score, int a, int b)
    {
        Pushed++;
        if (_count < Capacity)
        {
            int i = _count++;
            _score[i] = score; _a[i] = a; _b[i] = b;
            SiftUp(i);
        }
        else if (score > _score[0])
        {
            _score[0] = score; _a[0] = a; _b[0] = b;
            SiftDown(0);
        }
    }

    public (double Score, int A, int B) this[int i] => (_score[i], _a[i], _b[i]);

    public void Clear() { _count = 0; Pushed = 0; }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (_score[p] <= _score[i]) break;
            Swap(i, p);
            i = p;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, m = i;
            if (l < _count && _score[l] < _score[m]) m = l;
            if (r < _count && _score[r] < _score[m]) m = r;
            if (m == i) break;
            Swap(i, m);
            i = m;
        }
    }

    private void Swap(int x, int y)
    {
        (_score[x], _score[y]) = (_score[y], _score[x]);
        (_a[x], _a[y]) = (_a[y], _a[x]);
        (_b[x], _b[y]) = (_b[y], _b[x]);
    }
}
