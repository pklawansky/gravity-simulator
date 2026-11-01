using System;
using System.Numerics;
using System.Threading.Tasks;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Fusion pushing outward: each of the brightest (at most <see cref="StellarParameters.MaxRadiators"/>)
    /// stars accelerates gas and planets radially away with a = L * pi R^2 / (4 pi r^2 c m), where R and
    /// m are the pushed body's radius and mass and L = L☉ M^3.5. O(N * S).
    /// </summary>
    public sealed class RadiationPressure : IForceContributor
    {
        private readonly StellarParameters _p;
        private readonly float _lsun, _c;
        private int[] _top = Array.Empty<int>();
        private Vector3[] _srcPos = Array.Empty<Vector3>();
        private float[] _srcL = Array.Empty<float>();
        private int _sources, _n, _chunkSize;
        private float _eps2;
        private ParticleSystem _ps;
        private readonly Action<int> _chunk;

        public RadiationPressure(StellarParameters parameters)
        {
            _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _lsun = (float)_p.Units.SolarLuminosity;
            _c = (float)_p.Units.SpeedOfLight;
            _chunk = Chunk;
        }

        public void AddAccelerations(ParticleSystem ps)
        {
            int k = Math.Max(0, _p.MaxRadiators);
            if (k == 0) return;
            if (_top.Length < k) { _top = new int[k]; _srcPos = new Vector3[k]; _srcL = new float[k]; }

            // The brightest stars are the heaviest (L ~ M^3.5): keep the top k by mass, sorted descending.
            int count = 0;
            int n = ps.Count;
            for (int i = 0; i < n; i++)
            {
                if (ps.Class[i] != BodyClass.Star) continue;
                float m = ps.Mass[i];
                if (count < k) _top[count++] = i;
                else if (m > ps.Mass[_top[k - 1]]) _top[k - 1] = i;
                else continue;
                for (int j = count - 1; j > 0 && ps.Mass[_top[j]] > ps.Mass[_top[j - 1]]; j--)
                    (_top[j], _top[j - 1]) = (_top[j - 1], _top[j]);
            }
            if (count == 0) return;
            for (int s = 0; s < count; s++)
            {
                int i = _top[s];
                _srcPos[s] = ps.Position[i];
                _srcL[s] = (float)(_lsun * StellarPhysics.LuminositySolar(ps.Mass[i]));
            }

            _sources = count;
            _ps = ps;
            _n = n;
            _eps2 = _p.RadiationSoftening * _p.RadiationSoftening;
            int chunks = Math.Min(n, Environment.ProcessorCount * 4);
            _chunkSize = (n + chunks - 1) / chunks;
            if (n < 256) { for (int c = 0; c < chunks; c++) _chunk(c); }
            else Parallel.For(0, chunks, _chunk);
            _ps = null;
        }

        private void Chunk(int c)
        {
            int start = c * _chunkSize, end = Math.Min(_n, start + _chunkSize);
            var ps = _ps;
            for (int i = start; i < end; i++)
            {
                BodyClass cls = ps.Class[i];
                if (cls != BodyClass.Gas && cls != BodyClass.Planet) continue;
                float m = ps.Mass[i];
                if (m <= 0f) continue;
                float r = ps.Radius[i];
                float coeff = r * r / (4f * _c * m); // pi R^2 / (4 pi c m)
                Vector3 p = ps.Position[i];
                Vector3 acc = Vector3.Zero;
                for (int s = 0; s < _sources; s++)
                {
                    Vector3 d = p - _srcPos[s];
                    float d2 = d.LengthSquared() + _eps2;
                    if (d2 <= 0f) continue;
                    float inv = 1f / MathF.Sqrt(d2);
                    acc += d * (_srcL[s] * inv * inv * inv);
                }
                ps.Acceleration[i] += acc * coeff;
            }
        }
    }
}
