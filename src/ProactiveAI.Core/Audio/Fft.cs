namespace ProactiveAI.Core.Audio;

/// <summary>In-place iterative radix-2 complex FFT for a fixed power-of-two size.</summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int[] _reverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two", nameof(n));
        }
        _n = n;
        int bits = (int)Math.Log2(n);
        _reverse = new int[n];
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }
            _reverse[i] = r;
        }
        _cos = new double[n / 2];
        _sin = new double[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            _cos[k] = Math.Cos(2 * Math.PI * k / n);
            _sin[k] = Math.Sin(2 * Math.PI * k / n);
        }
    }

    public int Size => _n;

    public void Forward(double[] re, double[] im) => Transform(re, im, inverse: false);

    public void Inverse(double[] re, double[] im)
    {
        Transform(re, im, inverse: true);
        double scale = 1.0 / _n;
        for (int i = 0; i < _n; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private void Transform(double[] re, double[] im, bool inverse)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1;
            int step = _n / size;
            for (int start = 0; start < _n; start += size)
            {
                for (int j = 0; j < half; j++)
                {
                    double wr = _cos[j * step];
                    double wi = inverse ? _sin[j * step] : -_sin[j * step];
                    int a = start + j;
                    int b = a + half;
                    double tr = wr * re[b] - wi * im[b];
                    double ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
