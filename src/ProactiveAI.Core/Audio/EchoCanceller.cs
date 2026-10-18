namespace ProactiveAI.Core.Audio;

/// <summary>
/// Acoustic echo canceller following the multidelay block frequency-domain (MDF) design used by Speex
/// (partitioned overlap-save adaptive filter with gradient constraint, two-filter structure, and the learning-rate
/// control of Valin, "On adjusting the learning rate in frequency domain echo cancellation with double-talk",
/// IEEE TASLP 2007). The filter models the loudspeaker→microphone path from the time-aligned playback signal.
/// <list type="bullet">
/// <item>The per-bin learning rate is the estimated residual-echo-to-error ratio. The residual echo is estimated
/// from a leakage coefficient (how much error power follows the echo estimate's power), which local speech does
/// not inflate, so the filter learns quickly while only echo is present and barely moves while the local talker
/// speaks (double talk).</item>
/// <item>A background filter adapts; the foreground filter that produces the output adopts its coefficients only
/// after a statistically significant reduction of the error, and the background is restored from the foreground
/// when it is significantly worse.</item>
/// <item>A block-level residual echo suppressor attenuates blocks whose remaining error is consistent with echo.</item>
/// </list>
/// Operates on 24 kHz mono blocks of <see cref="Block"/> samples.
/// </summary>
public sealed class EchoCanceller
{
    public const int Block = 256;
    private const int N = 2 * Block;
    private const double InitialRate = 0.8;     // learning rate until the leakage estimate is meaningful
    private const double MinLeak = 0.005;
    private const double FarEndActive = 1e-6;   // about -60 dBFS mean power
    private const double Delta = 1e-6;
    private const double Regularization = 0.05;
    private const double Tiny = 1e-15;
    private static readonly double SpecAverage = (double)Block / PipelineAudio.SampleRate;
    private static readonly double Beta0 = 2.0 * Block / PipelineAudio.SampleRate;
    private static readonly double BetaMax = 0.5 * Block / PipelineAudio.SampleRate;

    private readonly int _partitions;
    private readonly Fft _fft = new(N);
    private readonly double[][] _xRe;
    private readonly double[][] _xIm;
    private readonly double[][] _xPow;
    private readonly double[][] _bRe;   // background (adapting) filter
    private readonly double[][] _bIm;
    private readonly double[][] _fRe;   // foreground (output) filter
    private readonly double[][] _fIm;
    private readonly double[] _sumPow = new double[N];
    private readonly double[] _norm = new double[N];
    private readonly double[] _prevX = new double[Block];
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];
    private readonly double[] _eRe = new double[N];
    private readonly double[] _eIm = new double[N];
    private readonly double[] _yRe = new double[N];
    private readonly double[] _yIm = new double[N];
    private readonly double[] _eh = new double[N];
    private readonly double[] _yh = new double[N];
    private readonly double[] _eb = new double[Block];
    private readonly double[] _yb = new double[Block];
    private readonly double[] _ef = new double[Block];
    private int _head;
    private double _floor = Delta;
    private bool _adapted;
    private int _convergedBlocks;
    private double _erleDb;
    private double _residualRatio = 0.1;
    private double _gain = 1;
    private double _smMic;
    private double _smOut;
    private double _davg1;
    private double _davg2;
    private double _dvar1;
    private double _dvar2;
    private double _pey;
    private double _pyy = 1e-12;
    private double _leak = 1;
    private double _sumAdapt;
    private bool _rateControlled;
    private int _mismatchBlocks;

    public EchoCanceller(TimeSpan? tail = null)
    {
        var t = tail ?? TimeSpan.FromMilliseconds(300);
        _partitions = Math.Max(1, (int)Math.Ceiling(t.TotalSeconds * PipelineAudio.SampleRate / Block));
        _xRe = Alloc();
        _xIm = Alloc();
        _xPow = Alloc();
        _bRe = Alloc();
        _bIm = Alloc();
        _fRe = Alloc();
        _fIm = Alloc();
    }

    /// <summary>Smoothed linear echo reduction (dB) of the output filter while the far end is active.</summary>
    public double EchoReductionDb => _erleDb;

    /// <summary>True once the output filter consistently removes a meaningful part of the echo.</summary>
    public bool Adapted => _adapted;

    public TimeSpan Tail => PipelineAudio.Duration((long)_partitions * Block);

    /// <summary>Diagnostics: filters cleared because the output exceeded the microphone level.</summary>
    public int DivergenceResets { get; private set; }

    /// <summary>Diagnostics: background coefficients adopted by the output filter.</summary>
    public int ForegroundUpdates { get; private set; }

    /// <summary>Diagnostics: background filter restored from the foreground after being pulled away.</summary>
    public int BackgroundResets { get; private set; }

    /// <summary>Diagnostics: echo path changes detected (re-learning at the initial rate).</summary>
    public int PathChanges { get; private set; }

    /// <summary>Diagnostics: estimated fraction of the echo estimate's power left as residual echo.</summary>
    public double Leakage => _leak;

    public void Reset()
    {
        foreach (var a in _xRe.Concat(_xIm).Concat(_xPow).Concat(_bRe).Concat(_bIm).Concat(_fRe).Concat(_fIm))
        {
            Array.Clear(a);
        }
        Array.Clear(_sumPow);
        Array.Clear(_prevX);
        Array.Clear(_eh);
        Array.Clear(_yh);
        ResetAdaptation();
        _erleDb = 0;
        _residualRatio = 0.1;
        _gain = 1;
        _smMic = _smOut = 0;
    }

    /// <summary>Cancels echo of <paramref name="reference"/> from <paramref name="mic"/> (both <see cref="Block"/> samples, time aligned).</summary>
    public void Process(ReadOnlySpan<short> mic, ReadOnlySpan<short> reference, Span<short> output)
    {
        if (mic.Length != Block || reference.Length != Block || output.Length != Block)
        {
            throw new ArgumentException($"Blocks must be {Block} samples");
        }

        // Spectrum of the latest 2B reference samples becomes partition 0 (newest).
        _head = (_head + _partitions - 1) % _partitions;
        var xr = _xRe[_head];
        var xi = _xIm[_head];
        double refPower = 0;
        for (int i = 0; i < Block; i++)
        {
            double x = reference[i] / 32768.0;
            xr[i] = _prevX[i];
            xr[Block + i] = x;
            _prevX[i] = x;
            refPower += x * x;
        }
        refPower /= Block;
        Array.Clear(xi);
        _fft.Forward(xr, xi);

        // Per-bin input energy across all partitions (what the filter output depends on), regularized so a
        // single quiet bin cannot produce a huge normalized step.
        var pw = _xPow[_head];
        double meanPow = 0;
        for (int f = 0; f < N; f++)
        {
            double fresh = xr[f] * xr[f] + xi[f] * xi[f];
            _sumPow[f] = Math.Max(0, _sumPow[f] + fresh - pw[f]);
            pw[f] = fresh;
            meanPow += _sumPow[f];
        }
        _floor = Regularization * meanPow / N + Delta;

        double echoFore = Estimate(_fRe, _fIm, mic, _ef, null, out double micPower, out double errFore);
        double echoBack = Estimate(_bRe, _bIm, mic, _eb, _yb, out _, out double errBack);

        bool farEnd = refPower > FarEndActive;
        if (farEnd)
        {
            AdaptBackground(refPower * Block, errBack * Block, echoBack * Block);

            // Adopt the background only on a statistically significant reduction of the error: during local
            // speech both errors are dominated by the talker, so small differences are not significant against Sff·Dbf.
            double sff = errFore * Block, see = errBack * Block, dbf = 1e-9;
            for (int i = 0; i < Block; i++)
            {
                double d = _eb[i] - _ef[i];
                dbf += d * d;
            }
            double diff = sff - see;
            _davg1 = 0.6 * _davg1 + 0.4 * diff;
            _davg2 = 0.85 * _davg2 + 0.15 * diff;
            _dvar1 = 0.36 * _dvar1 + 0.16 * sff * dbf;
            _dvar2 = 0.7225 * _dvar2 + 0.0225 * sff * dbf;
            if (diff * Math.Abs(diff) > sff * dbf || _davg1 * Math.Abs(_davg1) > 0.5 * _dvar1 || _davg2 * Math.Abs(_davg2) > 0.25 * _dvar2)
            {
                Copy(_bRe, _bIm, _fRe, _fIm);
                Array.Copy(_eb, _ef, Block);
                errFore = errBack;
                echoFore = echoBack;
                _davg1 = _davg2 = _dvar1 = _dvar2 = 0;
                ForegroundUpdates++;
            }
            else if (-diff * Math.Abs(diff) > 4 * sff * dbf || -_davg1 * Math.Abs(_davg1) > 4 * _dvar1 || -_davg2 * Math.Abs(_davg2) > 4 * _dvar2)
            {
                Copy(_fRe, _fIm, _bRe, _bIm);
                _davg1 = _davg2 = _dvar1 = _dvar2 = 0;
                BackgroundResets++;
            }
        }

        // Divergence guard: the output never adds energy (per block: fall back to the microphone signal), and
        // filters whose output error stays well above the microphone level for ~0.3 s are cleared. Both powers
        // are smoothed over the same blocks so a pause in playback cannot look like divergence.
        _smMic = 0.97 * _smMic + 0.03 * micPower;
        _smOut = 0.97 * _smOut + 0.03 * errFore;
        bool passThrough = errFore > micPower;
        if (_smOut > 4 * _smMic && _smMic > 1e-8)
        {
            DivergenceResets++;
            foreach (var a in _bRe.Concat(_bIm).Concat(_fRe).Concat(_fIm))
            {
                Array.Clear(a);
            }
            _smOut = _smMic;
            ResetAdaptation();
        }

        // An output error persistently above the microphone signal cannot come from local speech (a correct filter
        // removes echo even during double talk): the echo path changed (device moved, volume changed). Learn it
        // again at the initial rate instead of the cautious double-talk rate.
        _mismatchBlocks = farEnd && passThrough ? _mismatchBlocks + 1 : farEnd ? Math.Max(0, _mismatchBlocks - 1) : _mismatchBlocks;
        if (_mismatchBlocks > 25 && _rateControlled)
        {
            PathChanges++;
            _rateControlled = false;
            _adapted = false;
            _convergedBlocks = 0;
            _sumAdapt = 0;
            _mismatchBlocks = 0;
        }

        if (farEnd && micPower > 1e-8)
        {
            double erle = 10 * Math.Log10((micPower + 1e-12) / (Math.Min(errFore, micPower) + 1e-12));
            _erleDb = 0.97 * _erleDb + 0.03 * erle;
            _convergedBlocks = erle > 6 ? _convergedBlocks + 1 : Math.Max(0, _convergedBlocks - 1);
            if (!_adapted && _convergedBlocks > 40)
            {
                _adapted = true;
            }
        }

        // Residual echo suppression: attenuate only blocks whose error is consistent with leftover echo.
        double target = 1;
        if (farEnd && _adapted && !passThrough && echoFore > 1e-9)
        {
            double ratio = errFore / echoFore;
            if (ratio < 2 * _residualRatio)
            {
                _residualRatio = Math.Clamp(0.95 * _residualRatio + 0.05 * ratio, 0.001, 1);
            }
            double residual = _residualRatio * echoFore;
            target = Math.Clamp(1 - 1.5 * residual / (errFore + 1e-12), 0.1, 1);
        }
        double start = _gain;
        _gain = target < _gain ? 0.5 * _gain + 0.5 * target : target; // fast release protects speech onsets
        for (int i = 0; i < Block; i++)
        {
            double g = start + (_gain - start) * (i + 1) / Block;
            double v = passThrough ? mic[i] / 32768.0 : _ef[i];
            output[i] = (short)Math.Clamp(Math.Round(v * g * 32768.0), short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Echo estimate of one filter; writes the error (and optionally the estimate) and returns the mean estimate power.</summary>
    private double Estimate(double[][] wRe, double[][] wIm, ReadOnlySpan<short> mic, double[] err, double[]? echo, out double micPower, out double errPower)
    {
        Array.Clear(_re);
        Array.Clear(_im);
        for (int p = 0; p < _partitions; p++)
        {
            var hr = _xRe[(_head + p) % _partitions];
            var hi = _xIm[(_head + p) % _partitions];
            var wr = wRe[p];
            var wi = wIm[p];
            for (int f = 0; f < N; f++)
            {
                _re[f] += wr[f] * hr[f] - wi[f] * hi[f];
                _im[f] += wr[f] * hi[f] + wi[f] * hr[f];
            }
        }
        _fft.Inverse(_re, _im);
        double m = 0, e = 0, y2 = 0;
        for (int i = 0; i < Block; i++)
        {
            double d = mic[i] / 32768.0;
            double y = _re[Block + i];
            err[i] = d - y;
            if (echo is not null)
            {
                echo[i] = y;
            }
            m += d * d;
            e += err[i] * err[i];
            y2 += y * y;
        }
        micPower = m / Block;
        errPower = e / Block;
        return y2 / Block;
    }

    /// <summary>One background update with the learning rate set from the residual-echo-to-error ratio.</summary>
    private void AdaptBackground(double sxx, double see, double syy)
    {
        Array.Clear(_eRe);
        Array.Clear(_eIm);
        Array.Copy(_eb, 0, _eRe, Block, Block);
        _fft.Forward(_eRe, _eIm);
        Array.Clear(_yRe);
        Array.Clear(_yIm);
        Array.Copy(_yb, 0, _yRe, Block, Block);
        _fft.Forward(_yRe, _yIm);

        // Leakage: regression of error-power fluctuations on echo-estimate-power fluctuations across bins. Local
        // speech raises the error but does not follow the echo estimate, so it does not inflate the estimate.
        double sey = 0;
        for (int i = 0; i < Block; i++)
        {
            sey += _eb[i] * _yb[i];
        }
        double pey = 0, pyy = 0;
        for (int f = 0; f < N; f++)
        {
            double rf = _eRe[f] * _eRe[f] + _eIm[f] * _eIm[f];
            double yf = _yRe[f] * _yRe[f] + _yIm[f] * _yIm[f];
            double eh = rf - _eh[f], yh = yf - _yh[f];
            pey += eh * yh;
            pyy += yh * yh;
            _eh[f] = (1 - SpecAverage) * _eh[f] + SpecAverage * rf;
            _yh[f] = (1 - SpecAverage) * _yh[f] + SpecAverage * yf;
        }
        pyy = Math.Sqrt(pyy);
        pey /= pyy + Tiny;
        double alpha = Math.Min(Beta0 * syy, BetaMax * see) / (see + Tiny);
        _pey = (1 - alpha) * _pey + alpha * pey;
        _pyy = (1 - alpha) * _pyy + alpha * pyy;
        _pyy = Math.Max(_pyy, 1e-12);
        _pey = Math.Clamp(_pey, MinLeak * _pyy, _pyy);
        _leak = _pey / _pyy;

        double rer = (0.0001 * sxx + 3 * _leak * syy) / (see + Tiny);
        rer = Math.Min(0.5, Math.Max(rer, sey * sey / (see * syy + Tiny)));

        // Rate control starts once the output filter demonstrably cancels echo; before that the echo estimate (and
        // with it the residual estimate) is too small and would stall convergence.
        if (!_rateControlled && _adapted && _sumAdapt > _partitions && _leak > 0.03)
        {
            _rateControlled = true;
        }
        if (_rateControlled)
        {
            for (int f = 0; f < N; f++)
            {
                double e = _eRe[f] * _eRe[f] + _eIm[f] * _eIm[f] + Tiny;
                double r = Math.Min(_leak * (_yRe[f] * _yRe[f] + _yIm[f] * _yIm[f]), 0.5 * e);
                r = 0.7 * r + 0.3 * rer * e;
                _norm[f] = r / e / (_sumPow[f] + _floor);
            }
        }
        else
        {
            double rate = InitialRate * Math.Min(1, sxx / (see + Tiny));
            _sumAdapt += rate;
            for (int f = 0; f < N; f++)
            {
                _norm[f] = rate / (_sumPow[f] + _floor);
            }
        }

        for (int p = 0; p < _partitions; p++)
        {
            var hr = _xRe[(_head + p) % _partitions];
            var hi = _xIm[(_head + p) % _partitions];
            var wr = _bRe[p];
            var wi = _bIm[p];
            for (int f = 0; f < N; f++)
            {
                // W += rate · conj(X) · E / power
                wr[f] += _norm[f] * (hr[f] * _eRe[f] + hi[f] * _eIm[f]);
                wi[f] += _norm[f] * (hr[f] * _eIm[f] - hi[f] * _eRe[f]);
            }
        }
        for (int p = 0; p < _partitions; p++)
        {
            Constrain(p);
        }
    }

    private void ResetAdaptation()
    {
        _adapted = false;
        _convergedBlocks = 0;
        _davg1 = _davg2 = _dvar1 = _dvar2 = 0;
        _pey = 0;
        _pyy = 1e-12;
        _leak = 1;
        _sumAdapt = 0;
        _rateControlled = false;
        _mismatchBlocks = 0;
    }

    /// <summary>Gradient constraint: keep the time-domain filter causal and B taps long per partition.</summary>
    private void Constrain(int p)
    {
        Array.Copy(_bRe[p], _re, N);
        Array.Copy(_bIm[p], _im, N);
        _fft.Inverse(_re, _im);
        Array.Clear(_re, Block, Block);
        Array.Clear(_im);
        _fft.Forward(_re, _im);
        Array.Copy(_re, _bRe[p], N);
        Array.Copy(_im, _bIm[p], N);
    }

    private void Copy(double[][] fromRe, double[][] fromIm, double[][] toRe, double[][] toIm)
    {
        for (int p = 0; p < _partitions; p++)
        {
            Array.Copy(fromRe[p], toRe[p], N);
            Array.Copy(fromIm[p], toIm[p], N);
        }
    }

    private double[][] Alloc()
    {
        var a = new double[_partitions][];
        for (int i = 0; i < _partitions; i++)
        {
            a[i] = new double[N];
        }
        return a;
    }
}
