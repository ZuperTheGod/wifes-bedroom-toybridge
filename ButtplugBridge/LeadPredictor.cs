// Toy delay compensation ("lead").
//
// Bluetooth toys run a little behind whatever they are told (often 100-300 ms). A live game
// signal can't be sent early, but the game's stroke repeats: the game moves an angle forward by
// thrust_speed every frame and the thrust follows it through one stroke per 360 degrees. The
// telemetry carries thrust_speed, so how far through the current stroke the game will be L ms
// from now is known, and the previous stroke shows what the thrust looks like at that point:
//   - each upward pass through the middle (halfway between the last top and bottom) starts a
//     stroke; it is timed precisely by interpolating between samples, because the thrust moves
//     fastest there;
//   - the speed sent with every sample is added up (degrees) since that pass, plus the speed
//     times L, and divided by the degrees the last stroke took = where in the stroke L from now;
//   - that point is looked up in the last stroke's recorded shape.
// Measured on the real game (DeepRoom 4.0 on the emulator): the stroke is not a smooth wave - it
// snaps from end to end in ~80 ms and then eases into the end - and the last stroke took 360
// degrees within 1-2% whenever the game was stroking normally. Two earlier designs failed on
// that: replaying the last stroke by TIME lagged every speed change, and assuming a cosine put
// the snaps in the wrong place (sent values ~40 ms early on average).
//
// When there is no steady stroke - a pause, a skipped or shallow stroke, the player dragging by
// hand, the start of a scene - it hands back the live value unchanged, so it is never later than
// without it.
//
// The same logic runs inside the game on phones (GamePatcher ToyDirectLeadSample, "tb_ld_");
// keep the two in step.
sealed class LeadPredictor
{
    // A turn only counts once the motion has come back this far from its extreme (0..1 units):
    // small wobbles are not strokes.
    const double Hysteresis = 0.02;
    const double FramesPerSecond = 60;

    int _dir;
    double _extremeX;
    double _topX = double.NaN, _bottomX = double.NaN;
    bool _have;
    double _lastT, _lastX, _lastSpeed;
    double _angle;                                          // the speed added up over time, degrees
    double _lastCrossT = double.NegativeInfinity, _lastCrossAngle = double.NaN;
    double _strokeDeg;                                      // degrees the last whole stroke took
    bool _steady;                                           // ...and that stroke matched the speed sent
    double _movedT, _movedX;                                // last time the thrust actually changed
    int _misses;                                            // samples in a row that didn't fit the last stroke
    readonly List<(double Angle, double X)> _current = new();
    List<(double Frac, double X)> _last = new();            // the last steady stroke, 0..1 through it

    public void Reset()
    {
        _dir = 0;
        _topX = _bottomX = double.NaN;
        _have = false;
        _angle = 0;
        _lastCrossT = double.NegativeInfinity;
        _lastCrossAngle = double.NaN;
        _strokeDeg = 0;
        _steady = false;
        _misses = 0;
        _current.Clear();
        _last = new();
    }

    // tMs: when the sample was taken (any steadily increasing clock, in milliseconds);
    // x: thrust 0..1; speed: the game's thrust_speed (degrees per frame).
    public void Add(double tMs, double x, double speed)
    {
        if (_have && tMs <= _lastT) return;
        if (!_have)
        {
            _have = true;
            _extremeX = x;
            _lastT = tMs; _lastX = x; _lastSpeed = speed;
            _movedT = tMs; _movedX = x;
            return;
        }
        double prevT = _lastT, prevX = _lastX, prevAngle = _angle;
        if (Math.Abs(x - _movedX) > 0.003) { _movedT = tMs; _movedX = x; }
        _angle += Math.Max(_lastSpeed, 0) * FramesPerSecond * (tMs - prevT) / 1000;
        _lastT = tMs; _lastX = x; _lastSpeed = speed;

        // tops and bottoms
        if (_dir == 0)
        {
            if (Math.Abs(x - _extremeX) >= Hysteresis) { _dir = x > _extremeX ? 1 : -1; _extremeX = x; }
        }
        else if ((_dir > 0 && x >= _extremeX) || (_dir < 0 && x <= _extremeX))
        {
            _extremeX = x;
        }
        else if (Math.Abs(x - _extremeX) >= Hysteresis)
        {
            if (_dir > 0) _topX = _extremeX; else _bottomX = _extremeX;
            _dir = -_dir;
            _extremeX = x;
        }

        if (!double.IsNaN(_lastCrossAngle) && _current.Count < 400) _current.Add((_angle - _lastCrossAngle, x));

        // Does the thrust still follow the last stroke? Checked on every sample, so a pause or
        // the player dragging by hand switches prediction off within a few samples instead of
        // after a whole stroke. (Two misses in a row: one can be the ~33 ms sample timing
        // landing on a snap.) Late for the next pass through the middle counts as a miss too.
        if (_steady && _strokeDeg > 0 && _last.Count >= 4)
        {
            double sinceDeg = _angle - _lastCrossAngle;
            bool fits = sinceDeg <= 1.15 * _strokeDeg && Math.Abs(x - ShapeAt(sinceDeg / _strokeDeg % 1)) <= 0.3;
            _misses = fits ? 0 : _misses + 1;
            if (_misses >= 2) _steady = false;
        }

        if (double.IsNaN(_topX) || double.IsNaN(_bottomX) || _topX - _bottomX < 2 * Hysteresis) return;
        double mid = (_topX + _bottomX) / 2;
        if (!(prevX < mid && x >= mid)) return;
        double f = (mid - prevX) / (x - prevX);
        double crossT = prevT + (tMs - prevT) * f;
        double crossAngle = prevAngle + (_angle - prevAngle) * f;
        if (!double.IsNaN(_lastCrossAngle))
        {
            // One stroke is 360 degrees of the game's angle (measured: within 1-2% in normal play). A
            // stroke that disagrees with the speed by more than that wasn't a normal stroke (a
            // pause, a skipped or shallow stroke, the player dragging by hand), so prediction
            // stays off until a normal one has been seen again.
            double swept = crossAngle - _lastCrossAngle;
            double ratio = swept > 1 ? 360 / swept : 0;
            _steady = ratio >= 0.85 && ratio <= 1.18;
            _misses = 0;
            if (_steady)
            {
                _strokeDeg = swept;
                var shape = new List<(double, double)>(_current.Count);
                foreach (var (a, v) in _current)
                {
                    if (a <= swept) shape.Add((a / swept, v));
                }
                _last = shape;
            }
        }
        _current.Clear();
        _lastCrossT = crossT;
        _lastCrossAngle = crossAngle;
    }

    // The value expected leadMs after nowMs; the latest value when there is no steady stroke.
    public double Predict(double nowMs, double leadMs)
    {
        if (!_have) return 0;
        double live = _lastX;
        if (leadMs <= 0 || !_steady || _last.Count < 4 || _strokeDeg <= 0) return live;
        double degPerMs = Math.Max(_lastSpeed, 0) * FramesPerSecond / 1000;
        if (degPerMs <= 0) return live;
        double strokeMs = _strokeDeg / degPerMs;
        if (strokeMs < 120 || strokeMs > 4000) return live;
        // Still stroking? A pause produces no new passes through the middle and no movement.
        if (nowMs - _lastCrossT > 1.25 * strokeMs + 100) return live;
        if (nowMs - _movedT > Math.Max(150, 0.2 * strokeMs)) return live;

        double frac = (_angle - _lastCrossAngle + degPerMs * (nowMs - _lastT + leadMs)) / _strokeDeg;
        frac -= Math.Floor(frac);
        return ShapeAt(frac);
    }

    double ShapeAt(double frac)
    {
        var s = _last;
        if (frac <= s[0].Frac) return s[0].X;
        if (frac >= s[^1].Frac) return s[^1].X;
        int lo = 0, hi = s.Count - 1;
        while (hi - lo > 1)
        {
            int m = (lo + hi) / 2;
            if (s[m].Frac <= frac) lo = m; else hi = m;
        }
        double span = s[hi].Frac - s[lo].Frac;
        return span > 0 ? s[lo].X + (s[hi].X - s[lo].X) * (frac - s[lo].Frac) / span : s[lo].X;
    }
}
