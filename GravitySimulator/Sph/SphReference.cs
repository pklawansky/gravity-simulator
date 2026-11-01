using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using GravitySimulator.Planets;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// CPU reference implementation of the SPH kernels (plain C#, no GL): the oracle the GPU solver is
    /// tested against, and the engine of the small-N physics tests. Same math and the same order of
    /// passes as the compute shaders; optionally periodic (<see cref="SphParameters.Period"/>).
    /// <para>Step (leapfrog kick-drift-kick, global Δt): kick ½ + drift → h from the previous density
    /// → predict v, u to the end of the step → density, EOS, ∇·v, |∇×v|, Balsara → pressure force,
    /// viscosity, du/dt → self-gravity → kick ½ → next Δt.</para>
    /// </summary>
    public sealed class SphReference
    {
        private readonly SphParameters _p;
        private Vector3[] _vpred = Array.Empty<Vector3>();
        private float[] _upred = Array.Empty<float>();
        private readonly Grid _grid = new Grid();
        private BarnesHutTree _tree;
        private ParticleSystem _treePs;
        private readonly FlatTree _flat = new FlatTree();

        public SphReference(SphParameters parameters)
        {
            _p = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        public SphParameters Parameters => _p;
        /// <summary>Time step of the next <see cref="Step"/> (set by <see cref="Initialise"/> and each step).</summary>
        public float Dt { get; set; }
        public double Time { get; private set; }
        /// <summary>Barnes-Hut (the flattened walk the GPU uses) instead of the direct sum.</summary>
        public bool UseTreeGravity { get; set; } = true;

        // ---- passes ------------------------------------------------------------------------------

        /// <summary>h = η (m/ρ)^(1/3) from the current (previous step's) density, clamped.</summary>
        public void UpdateSmoothingLengths(SphState s)
        {
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Mass[i] <= 0f) continue;
                s.H[i] = SmoothingLength(s.Mass[i], s.Density[i], _p);
            }
        }

        internal static float SmoothingLength(float m, float rho, SphParameters p)
        {
            float h = p.Eta * MathF.Cbrt(m / MathF.Max(rho, 1e-30f));
            return Math.Clamp(h, p.HMin, p.HMax);
        }

        /// <summary>Velocity and internal energy predicted <paramref name="dtPredict"/> ahead: v + a dt, u + du/dt dt.</summary>
        public void Predict(SphState s, float dtPredict)
        {
            Ensure(s.Capacity);
            bool ideal = _p.Eos == EquationOfState.IdealGas;
            for (int i = 0; i < s.Count; i++)
            {
                _vpred[i] = s.Velocity[i] + s.Acceleration[i] * dtPredict;
                _upred[i] = ideal ? MathF.Max(s.U[i] + s.DuDt[i] * dtPredict, _p.MinInternalEnergy) : s.U[i];
            }
        }

        /// <summary>Density (gather, own h), equation of state, ∇·v, |∇×v| and the Balsara factor.</summary>
        public void ComputeDensity(SphState s)
        {
            Ensure(s.Capacity);
            _grid.Build(s, _p.Period);
            float selfCorrection = SphKernel.DensitySelfCorrection(_p.Eta);
            Parallel.For(0, s.Count, i =>
            {
                if (s.Mass[i] <= 0f) return;
                Vector3 xi = s.Position[i], vi = _vpred[i];
                float hi = s.H[i], support2 = 4f * hi * hi;
                float rho = 0f, div = 0f;
                Vector3 curl = Vector3.Zero;
                foreach (int j in _grid.Gather[i])
                {
                    Vector3 xij = _grid.Delta(xi, s.Position[j]);
                    float r2 = xij.LengthSquared();
                    if (r2 >= support2) continue;
                    float r = MathF.Sqrt(r2);
                    float mj = s.Mass[j];
                    rho += mj * SphKernel.W(r, hi);
                    Vector3 g = SphKernel.GradW(xij, r, hi);
                    Vector3 vij = vi - _vpred[j];
                    div -= mj * Vector3.Dot(vij, g);
                    curl -= mj * Vector3.Cross(vij, g);
                }
                rho -= selfCorrection * s.Mass[i] * SphKernel.W(0f, hi);
                s.Density[i] = rho;
                s.DivV[i] = div / rho;
                s.CurlV[i] = curl.Length() / rho;
                Eos(s, i, _upred[i]);
                float adiv = MathF.Abs(s.DivV[i]);
                s.Balsara[i] = adiv / (adiv + s.CurlV[i] + 1e-4f * s.SoundSpeed[i] / hi + 1e-30f);
            });
        }

        private void Eos(SphState s, int i, float u)
        {
            float rho = s.Density[i];
            if (_p.Eos == EquationOfState.IdealGas)
            {
                float g = _p.Gamma;
                s.Pressure[i] = (g - 1f) * rho * u;
                s.SoundSpeed[i] = MathF.Sqrt(MathF.Max(g * (g - 1f) * u, 0f));
            }
            else
            {
                float x = MathF.Pow(rho / _p.CriticalDensity, 4f / 3f);
                float cs2 = _p.SoundSpeed * _p.SoundSpeed;
                s.Pressure[i] = cs2 * rho * (1f + x);
                s.SoundSpeed[i] = MathF.Sqrt(cs2 * (1f + 7f / 3f * x));
                s.U[i] = 1.5f * s.Pressure[i] / rho; // display only (thermal energy at γ = 5/3)
            }
        }

        /// <summary>
        /// Symmetric pressure force + Monaghan viscosity with the Balsara switch, and du/dt (pdV work +
        /// viscous heating). Overwrites Acceleration, DuDt and MuMax. Call after <see cref="ComputeDensity"/>.
        /// </summary>
        public void ComputeForces(SphState s)
        {
            Ensure(s.Capacity);
            // Uses the neighbour lists built by ComputeDensity (x and h have not changed since).
            bool ideal = _p.Eos == EquationOfState.IdealGas;
            float alpha = _p.Alpha, beta = _p.Beta;
            var acc = new Vector3[s.Count];
            Parallel.For(0, s.Count, i =>
            {
                if (s.Mass[i] <= 0f) { acc[i] = Vector3.Zero; s.DuDt[i] = 0f; s.MuMax[i] = 0f; return; }
                Vector3 xi = s.Position[i], vi = _vpred[i];
                float hi = s.H[i], rhoi = s.Density[i], ci = s.SoundSpeed[i], fi = s.Balsara[i];
                float pi = s.Pressure[i] / (rhoi * rhoi);
                Vector3 a = Vector3.Zero;
                float dudt = 0f, muMax = 0f;
                // Pairs within 2 max(h_i, h_j): i's gather list plus the particles whose gather list holds i.
                for (int pass = 0; pass < 2; pass++)
                {
                    int[] list = pass == 0 ? _grid.Gather[i] : _grid.Scatter[i];
                    foreach (int j in list)
                    {
                        if (j == i) continue;
                        Vector3 xij = _grid.Delta(xi, s.Position[j]);
                        float r2 = xij.LengthSquared();
                        float hj = s.H[j];
                        float hmax = MathF.Max(hi, hj);
                        if (r2 >= 4f * hmax * hmax) continue;
                        float r = MathF.Sqrt(r2);
                        Vector3 g = 0.5f * (SphKernel.GradW(xij, r, hi) + SphKernel.GradW(xij, r, hj));
                        Vector3 vij = vi - _vpred[j];
                        float rhoj = s.Density[j];
                        float pj = s.Pressure[j] / (rhoj * rhoj);
                        float visc = 0f;
                        float vr = Vector3.Dot(vij, xij);
                        if (vr < 0f)
                        {
                            float hb = 0.5f * (hi + hj);
                            float mu = hb * vr / (r2 + 0.01f * hb * hb);
                            float cb = 0.5f * (ci + s.SoundSpeed[j]);
                            float fb = 0.5f * (fi + s.Balsara[j]);
                            visc = fb * (-alpha * cb * mu + beta * mu * mu) / (0.5f * (rhoi + rhoj));
                            muMax = MathF.Max(muMax, -mu);
                        }
                        float mj = s.Mass[j];
                        a -= mj * (pi + pj + visc) * g;
                        dudt += mj * (pi + 0.5f * visc) * Vector3.Dot(vij, g);
                    }
                }
                acc[i] = a;
                s.DuDt[i] = ideal ? dudt : 0f;
                s.MuMax[i] = muMax;
            });
            Array.Copy(acc, s.Acceleration, s.Count);
        }

        /// <summary>
        /// Adds self-gravity (Plummer softening ε = h, symmetric between particles: ε² = (h_i² + h_j²)/2),
        /// stores the potential, then adds the adaptive-softening correction (Price &amp; Monaghan 2007).
        /// With h = η (m/ρ)^(1/3) the softened potential depends on h, and so on the positions through ρ;
        /// the matching pairwise force a_i = -Σ m_j [ξ_i ∇W(h_i) + ξ_j ∇W(h_j)], with
        /// ξ = (∂Φ/∂h)(dh/dρ)/m = -h² S/(6ρ), keeps the total energy (softened potential) conserved.
        /// Without it the Evrard collapse loses ~25% of its energy as h shrinks.
        /// </summary>
        public void AddGravity(SphState s)
        {
            if (!_p.SelfGravity)
            {
                Array.Clear(s.Potential, 0, s.Count);
                Array.Clear(s.Xi, 0, s.Count);
                return;
            }
            int n = s.Count;
            if (UseTreeGravity)
            {
                if (_treePs == null || _treePs.Capacity < n)
                {
                    _treePs = new ParticleSystem(s.Capacity);
                    _tree = new BarnesHutTree(s.Capacity);
                }
                while (_treePs.Count > 0) _treePs.RemoveAt(_treePs.Count - 1);
                for (int i = 0; i < n; i++) _treePs.Add(s.Position[i], Vector3.Zero, s.Mass[i], 0f);
                _tree.Build(_treePs);
                _tree.Flatten(_flat, s.H);
                float theta = _p.Theta;
                Parallel.For(0, n, i =>
                {
                    if (s.Mass[i] <= 0f) return;
                    s.Acceleration[i] += _flat.Acceleration(s.Position[i], s.H[i], theta, out float phi, out float sum);
                    s.Potential[i] = phi;
                    s.Xi[i] = Xi(s.H[i], s.Density[i], sum, _p);
                });
            }
            else
            {
                Parallel.For(0, n, i =>
                {
                    if (s.Mass[i] <= 0f) return;
                    Vector3 xi = s.Position[i], a = Vector3.Zero;
                    float hi2 = s.H[i] * s.H[i], phi = 0f, sum = 0f;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || s.Mass[j] <= 0f) continue;
                        Vector3 d = s.Position[j] - xi;
                        float inv = 1f / MathF.Sqrt(d.LengthSquared() + 0.5f * (hi2 + s.H[j] * s.H[j]));
                        float mi3 = s.Mass[j] * inv * inv * inv;
                        a += d * mi3;
                        sum += mi3;
                        phi -= s.Mass[j] * inv;
                    }
                    s.Acceleration[i] += a;
                    s.Potential[i] = phi;
                    s.Xi[i] = Xi(s.H[i], s.Density[i], sum, _p);
                });
            }
            AddSofteningCorrection(s);
        }

        /// <summary>
        /// Paczyński &amp; Wiita (1980) pseudo-Newtonian black hole: Φ = -GM/(r - r_s), a = -GM r̂/(r - r_s)².
        /// <paramref name="toHole"/> = x_hole - x. Newtonian for r ≫ r_s, stronger close in, and it makes
        /// orbits precess (2π r_s/p per orbit to leading order, 2/3 of Schwarzschild's 3π r_s/p). r - r_s
        /// is floored at 10⁻³ r_s (gas never gets that deep: the accretion radius is several r_s).
        /// Mirrored in gravity.comp.
        /// </summary>
        public static Vector3 PaczynskiWiitaAcceleration(Vector3 toHole, float mass, float rs, out float potential)
        {
            float r = toHole.Length();
            if (!(r > 0f)) { potential = 0f; return Vector3.Zero; }
            float x = MathF.Max(r - rs, 1e-3f * rs);
            potential = -mass / x;
            return toHole * (mass / (x * x * r));
        }

        /// <summary>ξ = -h² S / (6ρ); zero when h sits on a clamp (dh/dρ = 0 there).</summary>
        internal static float Xi(float h, float rho, float softSum, SphParameters p)
        {
            if (h <= p.HMin || h >= p.HMax) return 0f;
            return -h * h * softSum / (6f * rho);
        }

        private void AddSofteningCorrection(SphState s)
        {
            // Same pairs as the force pass; the lists built there are still valid (x and h unchanged).
            var acc = new Vector3[s.Count];
            Parallel.For(0, s.Count, i =>
            {
                if (s.Mass[i] <= 0f) return;
                Vector3 xi = s.Position[i], a = Vector3.Zero;
                float hi = s.H[i], xii = s.Xi[i];
                for (int pass = 0; pass < 2; pass++)
                {
                    int[] list = pass == 0 ? _grid.Gather[i] : _grid.Scatter[i];
                    foreach (int j in list)
                    {
                        if (j == i) continue;
                        Vector3 xij = _grid.Delta(xi, s.Position[j]);
                        float r2 = xij.LengthSquared();
                        float hj = s.H[j];
                        float hmax = MathF.Max(hi, hj);
                        if (r2 >= 4f * hmax * hmax) continue;
                        float r = MathF.Sqrt(r2);
                        a -= s.Mass[j] * (xii * SphKernel.GradW(xij, r, hi) + s.Xi[j] * SphKernel.GradW(xij, r, hj));
                    }
                }
                acc[i] = a;
            });
            for (int i = 0; i < s.Count; i++) s.Acceleration[i] += acc[i];
        }

        /// <summary>
        /// Global Δt = min_i C h_i / (c_i + 1.2(α c_i + β μ_max,i)), also ≤ C sqrt(h_i/|a_i|).
        /// </summary>
        public float ComputeTimeStep(SphState s)
        {
            float c = _p.Courant * _p.TimeScale;
            float dt = _p.MaxTimeStep;
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Mass[i] <= 0f) continue;
                dt = MathF.Min(dt, ParticleTimeStep(s.H[i], s.SoundSpeed[i], s.MuMax[i], s.Acceleration[i].Length(), c, _p));
            }
            return dt;
        }

        internal static float ParticleTimeStep(float h, float cs, float muMax, float accel, float courant, SphParameters p)
        {
            float signal = cs + 1.2f * (p.Alpha * cs + p.Beta * muMax);
            float dt = signal > 0f ? courant * h / signal : float.MaxValue;
            if (accel > 0f) dt = MathF.Min(dt, courant * MathF.Sqrt(h / accel));
            return dt;
        }

        /// <summary>Iterates h and density, then computes forces and the first Δt.</summary>
        public void Initialise(SphState s, int iterations = 3)
        {
            Predict(s, 0f);
            for (int it = 0; it < iterations; it++)
            {
                ComputeDensity(s);
                UpdateSmoothingLengths(s);
            }
            ComputeDensity(s);
            ComputeForces(s);
            AddGravity(s);
            Dt = ComputeTimeStep(s);
        }

        /// <summary>Energy removed by cooling so far (Σ m Δu); <see cref="Energies"/> plus this is conserved.</summary>
        public double RadiatedEnergy { get; private set; }

        /// <summary>Cooling time t_cool = β t_ff, with the free-fall time t_ff = sqrt(3π / (32 G ρ)) (G = 1).</summary>
        public static float CoolingTime(float rho, SphParameters p) =>
            p.CoolingBeta * MathF.Sqrt(3f * MathF.PI / (32f * MathF.Max(rho, 1e-30f)));

        /// <summary>
        /// Radiative cooling over <paramref name="dt"/>: du/dt = -(u - u_floor) / t_cool, integrated exactly,
        /// u ← u_floor + (u - u_floor) exp(-Δt / t_cool). This is the cooling-time ("β-cooling")
        /// prescription of Gammie (2001, ApJ 553, 174) with the dynamical time taken as the local free-fall
        /// time, here relaxing to a floor temperature instead of zero. The exact exponential is
        /// unconditionally stable whatever Δt / t_cool the CFL step gives, never undershoots the floor and
        /// never heats (gas at or below the floor is untouched). A small β makes shocked gas nearly
        /// isothermal, as real molecular gas is (cooling times ≪ dynamical times), which lets the shocked
        /// layer of colliding clouds fragment (Whitworth et al. 1994, MNRAS 268, 291). Mirrored in
        /// common.glsl (coolU), applied after each half-kick of u on both CPU and GPU.
        /// </summary>
        public static float Cool(float u, float rho, float dt, SphParameters p)
        {
            if (!(p.CoolingBeta > 0f) || !(u > p.CoolingFloor) || !(dt > 0f)) return u;
            return p.CoolingFloor + (u - p.CoolingFloor) * MathF.Exp(-dt / CoolingTime(rho, p));
        }

        private bool Cooling => _p.Eos == EquationOfState.IdealGas && _p.CoolingBeta > 0f;

        // Half-kick of u (du/dt, floor) followed by the cooling over the same half step.
        private void KickU(SphState s, int i, float half, ref double radiated)
        {
            float u = MathF.Max(s.U[i] + s.DuDt[i] * half, _p.MinInternalEnergy);
            if (Cooling)
            {
                float cooled = Cool(u, s.Density[i], half, _p);
                radiated += s.Mass[i] * (double)(u - cooled);
                u = cooled;
            }
            s.U[i] = u;
        }

        public void Step(SphState s)
        {
            float dt = Dt, half = 0.5f * dt;
            bool ideal = _p.Eos == EquationOfState.IdealGas;
            double radiated = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Mass[i] <= 0f) continue;
                s.Velocity[i] += s.Acceleration[i] * half;
                if (ideal) KickU(s, i, half, ref radiated);
                s.Position[i] += s.Velocity[i] * dt;
            }
            UpdateSmoothingLengths(s);
            Predict(s, half);
            ComputeDensity(s);
            ComputeForces(s);
            AddGravity(s);
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Mass[i] <= 0f) continue;
                s.Velocity[i] += s.Acceleration[i] * half;
                if (ideal) KickU(s, i, half, ref radiated);
            }
            RadiatedEnergy += radiated;
            Time += dt;
            Dt = ComputeTimeStep(s);
        }

        /// <summary>Kinetic, thermal (Σ m u) and potential (½ Σ m φ from the last gravity pass) energy.</summary>
        public (double Kinetic, double Thermal, double Potential) Energies(SphState s)
        {
            double k = 0, t = 0, w = 0;
            for (int i = 0; i < s.Count; i++)
            {
                double m = s.Mass[i];
                if (m <= 0) continue;
                k += 0.5 * m * s.Velocity[i].LengthSquared();
                t += m * s.U[i];
                w += 0.5 * m * s.Potential[i];
            }
            return (k, t, w);
        }

        // ---- neighbours ---------------------------------------------------------------------------

        /// <summary>For every particle i, the particles within 2h_i (including i), found through the cell grid.</summary>
        public int[][] NeighbourLists(SphState s)
        {
            _grid.Build(s, _p.Period);
            return _grid.Gather;
        }

        /// <summary>Brute-force neighbour set: all j (including i) with |x_i - x_j| &lt; 2h_i (open boundaries).</summary>
        public static void NeighboursBruteForce(SphState s, int i, List<int> result)
        {
            result.Clear();
            Vector3 xi = s.Position[i];
            float support2 = 4f * s.H[i] * s.H[i];
            for (int j = 0; j < s.Count; j++)
                if (s.Mass[j] > 0f && (xi - s.Position[j]).LengthSquared() < support2) result.Add(j);
        }

        // ---- sinks (CPU mirror of sink_candidates.comp and accrete.comp) ----------------------------

        /// <summary>
        /// Particles denser than ρ_sink, converging (∇·v &lt; 0) and bound within r_e = min(r_sink, 2 h_max):
        /// E_th ≤ |E_grav|/2 and E_kin + E_th + E_grav &lt; 0, with E_grav ≈ -3/5 M²/r_e and E_kin relative to
        /// the centre of mass without inward radial motion.
        /// Uses the density pass's results.
        /// </summary>
        public void FindSinkCandidates(SphState s, List<SinkCandidate> result)
        {
            result.Clear();
            if (float.IsPositiveInfinity(_p.SinkDensity)) return;
            _grid.Build(s, _p.Period);
            float re = MathF.Min(_p.SinkRadius, 2f * _grid.HMax); // as the GPU: within its 27-cell search
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Mass[i] <= 0f || !(s.Density[i] > _p.SinkDensity) || !(s.DivV[i] < 0f)) continue;
                if (IsBound(s, i, re))
                    result.Add(new SinkCandidate { Index = i, Position = s.Position[i], Density = s.Density[i] });
            }
        }

        private bool IsBound(SphState s, int i, float re)
        {
            Vector3 xi = s.Position[i];
            double m = 0, mv2 = 0, eth = 0;
            Vector3 mv = Vector3.Zero;
            float re2 = re * re;
            var near = new List<int>();
            _grid.Collect(s.Position[i], re, near);
            foreach (int j in near)
            {
                if (_grid.Delta(xi, s.Position[j]).LengthSquared() >= re2) continue;
                float mj = s.Mass[j];
                m += mj;
                mv += mj * s.Velocity[j];
                eth += mj * s.U[j];
            }
            // Kinetic energy relative to the clump's centre of mass, without the inward radial part
            // (infall drives the collapse instead of resisting it).
            Vector3 vcom = mv / (float)m;
            foreach (int j in near)
            {
                Vector3 d = s.Position[j] - xi;
                if (d.LengthSquared() >= re2) continue;
                Vector3 v = s.Velocity[j] - vcom;
                float r = d.Length();
                float vr = r > 0f ? Vector3.Dot(v, d) / r : 0f;
                mv2 += s.Mass[j] * (v.LengthSquared() - (vr < 0f ? vr * vr : 0f));
            }
            double ekin = 0.5 * mv2;
            double egrav = -0.6 * m * m / re;
            return eth <= 0.5 * -egrav && ekin + eth + egrav < 0;
        }

        /// <summary>
        /// Gas absorbed by sinks (the uploaded list: existing sinks, then the forming ones). A particle
        /// inside a forming sink's radius joins the nearest forming sink; otherwise it falls into the
        /// most bound existing sink inside that sink's accretion radius. Absorbed particles get mass 0.
        /// </summary>
        public void FindAccretions(SphState s, SinkManager sinks, List<AccretionEvent> result)
        {
            result.Clear();
            var ps = sinks.Sinks;
            int existing = ps.Count;
            var forming = sinks.Forming;
            float rf2 = _p.SinkRadius * _p.SinkRadius;
            for (int i = 0; i < s.Count; i++)
            {
                float mi = s.Mass[i];
                if (mi <= 0f) continue;
                Vector3 xi = s.Position[i], vi = s.Velocity[i];
                int best = -1;
                float bestD2 = rf2;
                for (int k = 0; k < forming.Count; k++)
                {
                    float d2 = (forming[k] - xi).LengthSquared();
                    if (d2 < bestD2) { bestD2 = d2; best = existing + k; }
                }
                if (best < 0)
                {
                    float bestE = 0f;
                    for (int k = 0; k < existing; k++)
                    {
                        float ms = ps.Mass[k];
                        if (ms <= 0f) continue;
                        float racc = sinks.AccretionRadius(k);
                        Vector3 d = ps.Position[k] - xi;
                        float d2 = d.LengthSquared();
                        if (d2 >= racc * racc) continue;
                        float e = 0.5f * (ps.Velocity[k] - vi).LengthSquared() - (ms + mi) / MathF.Max(MathF.Sqrt(d2), 1e-12f);
                        // Paczyński-Wiita hole: an absorbing inner boundary, no boundness test (as accrete.comp).
                        if (_p.PseudoNewtonianBlackHoles && ps.Class[k] == BodyClass.BlackHole) e = -3e38f;
                        if (e < bestE) { bestE = e; best = k; }
                    }
                }
                if (best < 0) continue;
                result.Add(new AccretionEvent { Sink = best, Mass = mi, Position = xi, Velocity = vi });
                s.Mass[i] = 0f;
            }
        }

        private void Ensure(int capacity)
        {
            if (_vpred.Length >= capacity) return;
            _vpred = new Vector3[capacity];
            _upred = new float[capacity];
        }

        // ---- neighbour lists ------------------------------------------------------------------------
        // Cells of about 2 mean(h); each particle's gather list holds every j with r < 2 h_i, and its
        // scatter list the j with r < 2 h_j but r >= 2 h_i. Together they are exactly the pairs within
        // 2 max(h_i, h_j), at a cost that does not grow with the h contrast (the GPU uses one 2 h_max grid).

        private sealed class Grid
        {
            private readonly Dictionary<(int, int, int), (int Start, int Count)> _cells = new();
            private int[] _sorted = Array.Empty<int>();
            private (int, int, int)[] _key = Array.Empty<(int, int, int)>();
            private Vector3 _period;
            private int _nx, _ny, _nz;
            private float _cx, _cy, _cz;

            public float HMax { get; private set; }
            public int[][] Gather = Array.Empty<int[]>();
            public int[][] Scatter = Array.Empty<int[]>();

            public void Build(SphState s, Vector3 period)
            {
                int n = s.Count;
                float hmax = 0f;
                double hsum = 0;
                int live = 0;
                for (int i = 0; i < n; i++)
                {
                    if (s.Mass[i] <= 0f) continue;
                    hmax = MathF.Max(hmax, s.H[i]);
                    hsum += s.H[i];
                    live++;
                }
                HMax = hmax;
                float cell = MathF.Max(live > 0 ? (float)(2.0 * hsum / live) : 1f, 1e-12f);
                _period = period;
                (_nx, _cx) = Axis(period.X, cell);
                (_ny, _cy) = Axis(period.Y, cell);
                (_nz, _cz) = Axis(period.Z, cell);
                if (_sorted.Length < n) { _sorted = new int[s.Capacity]; _key = new (int, int, int)[s.Capacity]; }
                _cells.Clear();
                var counts = new Dictionary<(int, int, int), int>();
                for (int i = 0; i < n; i++)
                {
                    if (s.Mass[i] <= 0f) { _key[i] = (int.MinValue, 0, 0); continue; }
                    var k = CellOf(s.Position[i]);
                    _key[i] = k;
                    counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1;
                }
                int start = 0;
                foreach (var kv in counts)
                {
                    _cells[kv.Key] = (start, 0);
                    start += kv.Value;
                }
                for (int i = 0; i < n; i++)
                {
                    var k = _key[i];
                    if (k.Item1 == int.MinValue) continue;
                    var e = _cells[k];
                    _sorted[e.Start + e.Count] = i;
                    _cells[k] = (e.Start, e.Count + 1);
                }

                // Gather lists.
                if (Gather.Length < n) { Gather = new int[s.Capacity][]; Scatter = new int[s.Capacity][]; }
                var gather = Gather;
                Parallel.For(0, n, () => new List<int>(), (i, _, near) =>
                {
                    if (s.Mass[i] <= 0f) { gather[i] = Array.Empty<int>(); return near; }
                    near.Clear();
                    Collect(s.Position[i], 2f * s.H[i], near);
                    float support2 = 4f * s.H[i] * s.H[i];
                    Vector3 xi = s.Position[i];
                    int w = 0;
                    for (int t = 0; t < near.Count; t++)
                        if (Delta(xi, s.Position[near[t]]).LengthSquared() < support2) near[w++] = near[t];
                    gather[i] = near.GetRange(0, w).ToArray();
                    return near;
                }, _ => { });

                // Scatter lists: i joins j's list when j is in i's gather list but i is not in j's.
                var scount = new int[n];
                for (int i = 0; i < n; i++)
                    foreach (int j in gather[i])
                        if (j != i && Delta(s.Position[i], s.Position[j]).LengthSquared() >= 4f * s.H[j] * s.H[j]) scount[j]++;
                for (int j = 0; j < n; j++) Scatter[j] = scount[j] == 0 ? Array.Empty<int>() : new int[scount[j]];
                Array.Clear(scount, 0, n);
                for (int i = 0; i < n; i++)
                    foreach (int j in gather[i])
                        if (j != i && Delta(s.Position[i], s.Position[j]).LengthSquared() >= 4f * s.H[j] * s.H[j]) Scatter[j][scount[j]++] = i;
            }

            private static (int, float) Axis(float period, float cell)
            {
                if (!(period > 0f)) return (0, cell);
                int n = Math.Max(1, (int)MathF.Floor(period / cell));
                return (n, period / n);
            }

            private (int, int, int) CellOf(Vector3 x) => (Wrap((int)MathF.Floor(x.X / _cx), _nx), Wrap((int)MathF.Floor(x.Y / _cy), _ny), Wrap((int)MathF.Floor(x.Z / _cz), _nz));

            private static int Wrap(int c, int n) => n > 0 ? ((c % n) + n) % n : c;

            public Vector3 Delta(Vector3 xi, Vector3 xj)
            {
                Vector3 d = xi - xj;
                if (_period.X > 0f) d.X -= _period.X * MathF.Round(d.X / _period.X);
                if (_period.Y > 0f) d.Y -= _period.Y * MathF.Round(d.Y / _period.Y);
                if (_period.Z > 0f) d.Z -= _period.Z * MathF.Round(d.Z / _period.Z);
                return d;
            }

            // Offsets -k..k per axis; on a periodic axis that would wrap onto itself, every cell once.
            private static (int Lo, int Hi, bool All) Span1(float radius, float cell, int n)
            {
                int k = (int)MathF.Ceiling(radius / cell);
                if (n > 0 && 2 * k + 1 >= n) return (0, n - 1, true);
                return (-k, k, false);
            }

            /// <summary>Adds every particle in the cells overlapping the cube of half-size radius around x.</summary>
            public void Collect(Vector3 x, float radius, List<int> into)
            {
                var (cx, cy, cz) = CellOf(x);
                var sx = Span1(radius, _cx, _nx);
                var sy = Span1(radius, _cy, _ny);
                var sz = Span1(radius, _cz, _nz);
                for (int oz = sz.Lo; oz <= sz.Hi; oz++)
                    for (int oy = sy.Lo; oy <= sy.Hi; oy++)
                        for (int ox = sx.Lo; ox <= sx.Hi; ox++)
                        {
                            var key = (sx.All ? ox : Wrap(cx + ox, _nx), sy.All ? oy : Wrap(cy + oy, _ny), sz.All ? oz : Wrap(cz + oz, _nz));
                            if (!_cells.TryGetValue(key, out var e)) continue;
                            for (int t = 0; t < e.Count; t++) into.Add(_sorted[e.Start + t]);
                        }
            }
        }
    }
}
