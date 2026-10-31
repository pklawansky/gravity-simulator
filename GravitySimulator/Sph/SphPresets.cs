using System;
using System.Collections.Generic;
using System.Numerics;
using GravitySimulator.Planets;

namespace GravitySimulator.Sph
{
    public enum SphPresetKind
    {
        /// <summary>Turbulent sphere, barotropic EOS; fragments into filaments and forms stars (M☉, pc).</summary>
        CollapsingCloud,
        /// <summary>Two clouds meeting head-on, cooling ideal gas; a shock layer forms and fragments (M☉, pc).</summary>
        CollidingClouds,
        /// <summary>A polytropic gas star on a parabolic orbit past a black hole (M☉, AU).</summary>
        TidalDisruption,
    }

    /// <summary>Everything a preset sets up: parameters, initial gas and sinks, and display hints.</summary>
    public sealed class SphScenario
    {
        public SphPresetKind Kind { get; init; }
        public SphParameters Parameters { get; init; }
        public StellarParameters Stellar { get; init; }
        public SphState Gas { get; init; }
        /// <summary>Initial sinks (e.g. the black hole), or an empty system.</summary>
        public ParticleSystem Sinks { get; init; }
        public float BlackHoleAccretionRadius { get; init; }
        /// <summary>Black holes book accreted gas in double precision (see <see cref="SinkManager.PreciseBlackHoleAccretion"/>).</summary>
        public bool PreciseBlackHoleAccretion { get; init; }
        public UnitSystem Units { get; init; }
        public float CameraDistance { get; init; }
        /// <summary>Camera yaw and pitch set on load (radians), or null to keep the current view direction.</summary>
        public float? CameraYaw { get; init; }
        public float? CameraPitch { get; init; }
        /// <summary>The follow camera is on by default (see <see cref="FollowFraming"/>); smallest half-view it frames.</summary>
        public bool FollowCamera { get; init; }
        public float FollowMinHalfView { get; init; } = 1f;
        public float FollowHoleMargin { get; init; }
        /// <summary>Radius whose mean column density sets the exposure (0 = from the initial gas).</summary>
        public float ExposureRadius { get; init; }
        /// <summary>Δu given to gas at the centre of a supernova blast (falls off linearly to the blast radius).</summary>
        public float SupernovaDu { get; init; }
        /// <summary>Internal energy of stellar ejecta added as gas.</summary>
        public float EjectaU { get; init; }
        /// <summary>Damped relaxation steps run at load (the star of the tidal disruption preset).</summary>
        public int RelaxSteps { get; init; }
        /// <summary>After relaxation the gas is moved by this offset and given this bulk velocity.</summary>
        public Vector3 LaunchOffset { get; init; }
        public Vector3 LaunchVelocity { get; init; }
        /// <summary>Spare particle slots for ejecta.</summary>
        public int Headroom { get; init; }
    }

    /// <summary>Initial conditions for the Gas dynamics (SPH) mode.</summary>
    public static class SphPresets
    {
        public const int MinCount = 50_000, MaxCount = 500_000;

        // Collapsing cloud (M☉, pc).
        public const float CloudMass = 1000f, CloudRadius = 1f;
        public const float CloudSoundSpeed = 2f;        // 0.13 km/s in sim units (65.6 m/s)
        public const float CloudVirialRatio = 0.8f;

        // Colliding clouds (M☉, pc).
        public const float CollideMass = 300f, CollideRadius = 0.6f, CollideSpeed = 25f, CollideSoundSpeed = 5f;
        /// <summary>Cooling time of the colliding clouds' gas in local free-fall times (t_cool = β t_ff).</summary>
        public const float CollideCoolingBeta = 0.05f;
        /// <summary>Gas temperature implied by c_s = 0.33 km/s (γ = 5/3, μ = 2.33) and the cooling floor (typical of molecular clouds).</summary>
        public const float CollideInitialTemperatureK = 18.3f, CollideFloorTemperatureK = 10f;
        /// <summary>ρ_crit of the collapsing cloud in units of its mean density (stiffening well above ρ_sink).</summary>
        public const float CloudCriticalDensityFactor = 300f;

        // Tidal disruption (M☉, R☉): the Sun (an n = 1.5 polytrope) and a 10⁶ M☉ Paczyński-Wiita black hole.
        public const float BlackHoleMass = 1e6f, StarMass = 1f, StarRadius = 1f, PolytropeIndex = 1.5f, Beta = 5.05f, Eccentricity = 0.9f, StartTidalRadii = 2.5f;
        /// <summary>The hole's absorbing inner boundary in Schwarzschild radii: 3 r_s, the innermost stable circular orbit (Schwarzschild and PW).</summary>
        public const float TdeAccretionRadiusInRs = 3f;
        /// <summary>Cap on the TDE time step in stellar dynamical times.</summary>
        public const float TdeMaxStepPerDynamicalTime = 1f / 40f;

        /// <summary>Units of the tidal disruption: M☉ and R☉ (time unit ~26.5 min), G = 1.</summary>
        public static readonly UnitSystem SolarRadiusUnits = new UnitSystem("M☉, R☉", 1.0, UnitSystem.SolarRadiusMetres);

        public static int DefaultCount(SphPresetKind kind) => kind switch
        {
            SphPresetKind.TidalDisruption => 50_000,
            // 150k: the shock layer fragments ~300 steps after the start, ~25 s at the ~11 steps/s this
            // count runs at in the app (200k: ~9 steps/s, ~40 s).
            SphPresetKind.CollidingClouds => 150_000,
            _ => 200_000,
        };

        public static string Title(SphPresetKind kind) => kind switch
        {
            SphPresetKind.CollapsingCloud => "Collapsing cloud (M☉, pc)",
            SphPresetKind.CollidingClouds => "Colliding clouds (M☉, pc)",
            _ => "Sun near a black hole (M☉, R☉)",
        };

        public static SphScenario Create(SphPresetKind kind, int count, int seed) => kind switch
        {
            SphPresetKind.CollidingClouds => CollidingClouds(count, seed),
            SphPresetKind.TidalDisruption => TidalDisruption(count, seed),
            _ => CollapsingCloud(count, seed),
        };

        /// <summary>
        /// Uniform sphere (1000 M☉, 1 pc) with a random divergence-free velocity field at virial ratio
        /// 0.8, barotropic EOS with c_s = 0.13 km/s. The h range, ρ_crit and the sink density are tied
        /// to the mean smoothing length so the 2 h_max grid stays efficient.
        /// </summary>
        public static SphScenario CollapsingCloud(int count, int seed)
        {
            var rng = new Random(seed);
            float m = CloudMass / count;
            float rhoMean = CloudMass / (4f / 3f * MathF.PI * CloudRadius * CloudRadius * CloudRadius);
            float eta = SphKernel.DefaultEta;
            float hMean = eta * MathF.Cbrt(m / rhoMean);
            var gas = new SphState(count + Headroom(count));
            for (int i = 0; i < count; i++)
            {
                Vector3 p = RandomInSphere(rng) * CloudRadius;
                gas.Add(p, Vector3.Zero, m, 0f, hMean);
            }
            Turbulence(gas, rng, CloudRadius, 24);
            ScaleToVirial(gas, CloudVirialRatio, 0.6 * CloudMass * CloudMass / CloudRadius);
            float rhoSink = 25f * rhoMean;
            var p0 = new SphParameters
            {
                Eos = EquationOfState.Barotropic,
                SoundSpeed = CloudSoundSpeed,
                CriticalDensity = CloudCriticalDensityFactor * rhoMean,
                HMin = hMean / 4f,
                HMax = 1.5f * hMean,
                SinkDensity = rhoSink,
                SinkRadius = 2f * eta * MathF.Cbrt(m / rhoSink),
                Theta = 0.6f,
            };
            for (int i = 0; i < count; i++) gas.U[i] = 1.5f * CloudSoundSpeed * CloudSoundSpeed;
            return new SphScenario
            {
                Kind = SphPresetKind.CollapsingCloud,
                Parameters = p0,
                Stellar = CloudStellar(m, p0.SinkRadius),
                Gas = gas,
                Sinks = new ParticleSystem(256),
                Units = UnitSystem.SolarParsec,
                CameraDistance = 3.2f,
                SupernovaDu = 2000f,
                EjectaU = 500f,
                Headroom = Headroom(count),
            };
        }

        /// <summary>
        /// Two turbulent clouds (300 M☉, 0.6 pc each, slightly off-axis) approaching head-on at
        /// 2 x 1.6 km/s along x, ideal gas γ = 5/3 at c_s = 0.33 km/s (18 K) with radiative cooling to 10 K;
        /// the shock heat is radiated away, so the compressed layer fragments under gravity into sinks.
        /// </summary>
        public static SphScenario CollidingClouds(int count, int seed)
        {
            var rng = new Random(seed);
            int half = count / 2;
            count = half * 2;
            float m = 2f * CollideMass / count;
            float rhoMean = CollideMass / (4f / 3f * MathF.PI * CollideRadius * CollideRadius * CollideRadius);
            float eta = SphKernel.DefaultEta, gamma = 5f / 3f;
            float hMean = eta * MathF.Cbrt(m / rhoMean);
            float u = CollideSoundSpeed * CollideSoundSpeed / (gamma * (gamma - 1f));
            var gas = new SphState(count + Headroom(count));
            float offset = CollideRadius + 0.15f;   // 0.3 pc gap: they meet well inside a free-fall time
            for (int c = 0; c < 2; c++)
            {
                int start = gas.Count;
                var centre = new Vector3(c == 0 ? -offset : offset, c == 0 ? 0.12f : -0.12f, 0f);
                var bulk = new Vector3(c == 0 ? CollideSpeed : -CollideSpeed, 0f, 0f);
                for (int i = 0; i < half; i++)
                    gas.Add(centre + RandomInSphere(rng) * CollideRadius, Vector3.Zero, m, u, hMean);
                var sub = new SphState(half) { Count = half };
                Array.Copy(gas.Position, start, sub.Position, 0, half);
                for (int i = 0; i < half; i++) sub.Position[i] -= centre;
                Turbulence(sub, rng, CollideRadius, 16);
                ScaleToVirial(sub, 0.3f, 0.6 * CollideMass * CollideMass / CollideRadius);
                for (int i = 0; i < half; i++) gas.Velocity[start + i] = sub.Velocity[i] + bulk;
            }
            float rhoSink = 60f * rhoMean;
            var p0 = new SphParameters
            {
                Eos = EquationOfState.IdealGas,
                Gamma = gamma,
                HMin = hMean / 5f,
                // h ≤ h_mean: the grid cell is 2 h_max, so a smaller cap keeps the dense shock layer's
                // neighbour search cheap (~2x the steps/s); only gas below the mean density is clamped.
                HMax = hMean,
                SinkDensity = rhoSink,
                SinkRadius = 2f * eta * MathF.Cbrt(m / rhoSink),
                MinInternalEnergy = 0.1f * u,
                // Radiative cooling down to 10 K (see SphReference.Cool): the shock heat
                // is radiated away within a fraction of a free-fall time, so the compressed layer stays
                // cold and dense enough to fragment into sinks.
                CoolingBeta = CollideCoolingBeta,
                CoolingFloor = CollideFloorTemperatureK / CollideInitialTemperatureK * u,
                Theta = 0.6f,
            };
            return new SphScenario
            {
                Kind = SphPresetKind.CollidingClouds,
                Parameters = p0,
                Stellar = CloudStellar(m, p0.SinkRadius),
                Gas = gas,
                Sinks = new ParticleSystem(256),
                Units = UnitSystem.SolarParsec,
                CameraDistance = 2.6f,
                // ~50° to the shock layer (which lies in the yz plane): the collision and the layer's
                // fragments are both visible (the default view looks along the layer, edge-on).
                CameraYaw = -MathF.PI / 2f + 0.8f,
                CameraPitch = 0.5f,
                // Exposure from one cloud's radius (not both clouds' rms extent), ~3.5x darker: the dense
                // shock layer stays below white, so its fragments and the stars forming in them show.
                ExposureRadius = CollideRadius,
                SupernovaDu = 2000f,
                EjectaU = 500f,
                Headroom = Headroom(count),
            };
        }

        private static StellarParameters CloudStellar(float gasMass, float sinkRadius) => new StellarParameters
        {
            Units = UnitSystem.SolarParsec,
            TimeAcceleration = 20f,
            AccretionRadius = sinkRadius,
            CaptureRadius = 0.25f * sinkRadius,
            StarFormationMass = StellarPhysics.FusionMass,
            GasParticleMass = MathF.Max(gasMass * 4f, 0.02f),
            GasRadius = sinkRadius,
            ShellSpeed = 15f,
            SupernovaEjectaSpeed = 150f,
            BlastRadius = 0.25f,
            BlastSpeed = 0f, // the blast acts on the SPH gas through its internal energy instead
            MaxEjecta = 64,
            Seed = 7,
        };

        /// <summary>
        /// The Sun (1 M☉, 1 R☉; an n = 1.5, γ = 5/3 polytrope with the Lane-Emden profile and hydrostatic u,
        /// relaxed with damping) launched from 2.5 tidal radii, inbound, on a bound e = 0.9 orbit with
        /// pericentre r_t/β (β = 5.05) around a 10⁶ M☉ black hole - the eccentric-TDE setup of Hayasaki,
        /// Stone &amp; Loeb (2013, MNRAS 434, 909: 10⁶ M☉, β = 5, e ≈ 0.8-0.98, Paczyński-Wiita potential).
        /// <para>Why n = 1.5 and not n = 3 (the Eddington standard model, closer to the Sun's radiative
        /// interior): an n = 3 star's 54x central density cuts the Courant step ~2.5x and the steps/s
        /// ~2x, which pushed the first pericentre to ~36 s of viewing and the first fallback past a
        /// minute. n = 1.5 (as for the convective envelopes of low-mass stars; Guillochon &amp;
        /// Ramirez-Ruiz 2013 ran both) keeps the encounter watchable.</para>
        /// <para>How the hole swallows without full relativity: (1) the hole pulls the gas with the
        /// Paczyński &amp; Wiita (1980) potential Φ = -GM/(r - r_s) (<see cref="SphParameters.PseudoNewtonianBlackHoles"/>),
        /// which has a Schwarzschild-like capture threshold - orbits whose Newtonian pericentre is below
        /// ~4 r_s (angular momentum below 4GM/c) plunge - and apsidal precession (2π r_s/p per orbit to
        /// leading order). (2) An absorbing inner boundary at 3 r_s, the innermost stable circular orbit
        /// (<see cref="TdeAccretionRadiusInRs"/>): gas inside is accreted unconditionally, standing in for
        /// the unresolved plunge/disc accretion, as SPH TDE work uses an absorbing inner boundary (e.g.
        /// Lodato, King &amp; Pringle 2009; Guillochon &amp; Ramirez-Ruiz 2013). The rendered sphere is
        /// still the r_s horizon; 3 r_s is only the sink rule. With r_t = 100 R☉ ≈ 24 r_s and β = 5.05
        /// the pericentre is 4.7 r_s, just above the capture threshold: the low-angular-momentum side of
        /// the stretched star is captured directly at the first passage (partial direct capture, Kesden
        /// 2012) and the rest forms the tidal stream. (3) The bound orbit (period ≈ 10 stellar dynamical
        /// times) brings the stream back within the minute; its return passage, precessed by ~40°, adds
        /// little more in that time - circularisation needs more orbits than a minute of viewing holds.
        /// Headless at 50k particles: pericentre at ~20 s of wall time, ~31% of the star swallowed by
        /// 24 s, flat afterwards (the app, which also renders, runs ~1.5x slower). The capture fraction is very sensitive to β (4.95: 2%, 5.05: 32%).</para>
        /// <para>Before this the preset was a white dwarf and a 1000 M☉ Newtonian hole; before that the
        /// Sun, a 10⁵ M☉ hole and β = 1.5 on a parabolic orbit (fallback after days, never swallowed).</para>
        /// </summary>
        public static SphScenario TidalDisruption(int count, int seed)
        {
            var units = SolarRadiusUnits;
            float rStar = StarRadius * (float)units.SolarRadius;
            const float gamma = 5f / 3f;
            const double nPoly = PolytropeIndex;
            var (xi1, table) = LaneEmden(nPoly);
            float eta = SphKernel.DefaultEta;
            float m = StarMass / count;
            var rng = new Random(seed);

            // Lattice inside the unit sphere, remapped radially so the enclosed mass follows the polytrope.
            var gas = new SphState(count + Headroom(count));
            int n = (int)MathF.Ceiling(MathF.Cbrt(count * 6f / MathF.PI)) + 1;
            var pts = new List<Vector3>();
            float dx = 2f / n;
            for (int k = 0; k < n; k++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Vector3(-1f + (i + 0.5f) * dx, -1f + (j + 0.5f) * dx, -1f + (k + 0.5f) * dx);
                        if (p.LengthSquared() < 1f) pts.Add(p);
                    }
            // Keep exactly `count` points, the innermost ones.
            pts.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
            if (pts.Count > count) pts.RemoveRange(count, pts.Count - count);
            count = pts.Count;
            m = StarMass / count;
            double alpha = rStar / xi1;
            double mu1 = table[^1].Mu;
            double rhoC = StarMass / (4 * Math.PI * alpha * alpha * alpha * mu1);
            double kPoly = 4 * Math.PI * alpha * alpha * Math.Pow(rhoC, 1.0 - 1.0 / nPoly) / (nPoly + 1); // P = K ρ^(1+1/n)
            for (int i = 0; i < count; i++)
            {
                Vector3 p = pts[i];
                float r0 = p.Length();
                double frac = (i + 0.5) / count;              // enclosed mass fraction, by rank
                double xi = InverseMass(table, frac * mu1);
                double theta = ThetaAt(table, xi);
                double rho = rhoC * Math.Pow(Math.Max(theta, 1e-4), nPoly);
                double u = kPoly * Math.Pow(rho, 1.0 / nPoly) / (gamma - 1.0); // u = P / ((γ-1) ρ)
                Vector3 dir = r0 > 0f ? p / r0 : Vector3.UnitX;
                gas.Add(dir * (float)(alpha * xi), Vector3.Zero, m, (float)u, eta * MathF.Cbrt(m / (float)rho));
            }
            _ = rng;

            float rt = rStar * MathF.Cbrt(BlackHoleMass / StarMass);
            float rp = rt / Beta;
            float r0Orbit = StartTidalRadii * rt;
            // Orbit of eccentricity e in the xy plane, pericentre on +x: r = r_p (1 + e) / (1 + e cos f).
            const float e = Eccentricity;
            float semiLatus = rp * (1f + e);
            float cosF = (semiLatus / r0Orbit - 1f) / e;
            float f = -MathF.Acos(Math.Clamp(cosF, -1f, 1f));
            var pos = new Vector3(r0Orbit * MathF.Cos(f), r0Orbit * MathF.Sin(f), 0f);
            float h = MathF.Sqrt(BlackHoleMass * semiLatus);  // specific angular momentum
            float vr = BlackHoleMass / h * e * MathF.Sin(f), vt = BlackHoleMass / h * (1f + e * MathF.Cos(f));
            var rHat = Vector3.Normalize(pos);
            var tHat = new Vector3(-rHat.Y, rHat.X, 0f);
            var vel = rHat * vr + tHat * vt;

            var sinks = new ParticleSystem(16);
            float rs = (float)(2.0 * BlackHoleMass / (units.SpeedOfLight * units.SpeedOfLight));
            sinks.Add(Vector3.Zero, Vector3.Zero, BlackHoleMass, rs, BodyClass.BlackHole);

            float hStar = eta * MathF.Cbrt(m / (float)(StarMass / (4.0 / 3.0 * Math.PI * rStar * rStar * rStar)));
            float tStar = MathF.Sqrt(rStar * rStar * rStar / StarMass); // dynamical time of the Sun (~26.5 min)
            float rAcc = TdeAccretionRadiusInRs * rs;
            var p0 = new SphParameters
            {
                Eos = EquationOfState.IdealGas,
                Gamma = gamma,
                HMin = hStar / 3f,   // the stream compressed at pericentre would otherwise force tiny steps
                HMax = 1.5f * hStar, // the 2 h_max grid cost grows with the h contrast
                SinkDensity = float.PositiveInfinity,
                SinkRadius = 0f,
                MinInternalEnergy = 5e-6f * StarMass / rStar, // ~10⁻⁵ of the star's central u
                Theta = 0.6f,
                MaxTimeStep = TdeMaxStepPerDynamicalTime * tStar, // spread-out debris still resolves the hole's accretion radius
                PseudoNewtonianBlackHoles = true,
            };
            return new SphScenario
            {
                Kind = SphPresetKind.TidalDisruption,
                Parameters = p0,
                Stellar = new StellarParameters
                {
                    Units = units,
                    TimeAcceleration = 1f,
                    AccretionRadius = rAcc,
                    CaptureRadius = rAcc,
                    StarFormationMass = 1e9f,
                    GasParticleMass = m,
                    GasRadius = hStar,
                    BlastSpeed = 0f,
                },
                Gas = gas,
                Sinks = sinks,
                BlackHoleAccretionRadius = rAcc,
                PreciseBlackHoleAccretion = true,
                Units = units,
                // Frames the start point (2.5 r_t), the pericentre and the stream out to apocentre (3.8 r_t). The Sun is ~1/100 of r_t,
                // so here it is a bright dot of a few pixels; the stream and the hole fill the middle.
                CameraDistance = 7f * rt,
                CameraYaw = -MathF.PI / 2f - 0.66f, // the long axis of the orbit along the screen diagonal
                CameraPitch = 1.45f,                // nearly face-on to the orbit plane
                ExposureRadius = 3f * rt,           // bright enough that the thinning stream stays visible
                // Follow camera: a 7 R☉ half-view early on (the Sun ~14% of the view across), widening to
                // keep the hole, the core and the stream in view.
                FollowCamera = true,
                FollowMinHalfView = 7f * rStar,
                FollowHoleMargin = 3f * rs,
                SupernovaDu = 0f,
                EjectaU = 0f,
                RelaxSteps = 30,
                LaunchOffset = pos,
                LaunchVelocity = vel,
                Headroom = 1024,
            };
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static int Headroom(int count) => Math.Max(2048, count / 50);

        private static Vector3 RandomInSphere(Random rng)
        {
            Vector3 p;
            do p = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
            while (p.LengthSquared() > 1f);
            return p;
        }

        private static Vector3 RandomUnit(Random rng)
        {
            Vector3 p;
            do p = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
            while (p.LengthSquared() > 1f || p.LengthSquared() < 1e-4f);
            return Vector3.Normalize(p);
        }

        /// <summary>Random solenoidal Fourier modes, amplitude ∝ k^-2 (large scales dominate); zero mean.</summary>
        private static void Turbulence(SphState s, Random rng, float radius, int modes)
        {
            var kv = new Vector3[modes];
            var amp = new Vector3[modes];
            var phase = new float[modes];
            for (int k = 0; k < modes; k++)
            {
                float kmag = MathF.PI * (1f + 3f * (float)rng.NextDouble()) / radius;
                Vector3 dir = RandomUnit(rng);
                kv[k] = dir * kmag;
                amp[k] = Vector3.Cross(dir, RandomUnit(rng)) / (kmag * kmag);
                phase[k] = (float)(rng.NextDouble() * Math.PI * 2);
            }
            Vector3 mean = Vector3.Zero;
            for (int i = 0; i < s.Count; i++)
            {
                Vector3 v = Vector3.Zero;
                for (int k = 0; k < modes; k++) v += amp[k] * MathF.Sin(Vector3.Dot(kv[k], s.Position[i]) + phase[k]);
                s.Velocity[i] = v;
                mean += v;
            }
            mean /= Math.Max(1, s.Count);
            for (int i = 0; i < s.Count; i++) s.Velocity[i] -= mean;
        }

        private static void ScaleToVirial(SphState s, float ratio, double potentialMagnitude)
        {
            double ke = 0;
            for (int i = 0; i < s.Count; i++) ke += 0.5 * s.Mass[i] * s.Velocity[i].LengthSquared();
            if (ke <= 0) return;
            float scale = (float)Math.Sqrt(ratio * potentialMagnitude / (2 * ke));
            for (int i = 0; i < s.Count; i++) s.Velocity[i] *= scale;
        }

        /// <summary>Lane-Emden solution: returns ξ₁ and a table of (ξ, θ, μ = -ξ² θ').</summary>
        internal static (double Xi1, List<(double Xi, double Theta, double Mu)> Table) LaneEmden(double n)
        {
            var table = new List<(double, double, double)>();
            double xi = 1e-4, theta = 1 - xi * xi / 6, dtheta = -xi / 3, step = 1e-4;
            table.Add((0, 1, 0));
            double F(double x, double t, double d) => -Math.Pow(Math.Max(t, 0), n) - 2 * d / x;
            while (theta > 0)
            {
                double k1t = dtheta, k1d = F(xi, theta, dtheta);
                double k2t = dtheta + 0.5 * step * k1d, k2d = F(xi + 0.5 * step, theta + 0.5 * step * k1t, dtheta + 0.5 * step * k1d);
                double k3t = dtheta + 0.5 * step * k2d, k3d = F(xi + 0.5 * step, theta + 0.5 * step * k2t, dtheta + 0.5 * step * k2d);
                double k4t = dtheta + step * k3d, k4d = F(xi + step, theta + step * k3t, dtheta + step * k3d);
                double nt = theta + step / 6 * (k1t + 2 * k2t + 2 * k3t + k4t);
                double nd = dtheta + step / 6 * (k1d + 2 * k2d + 2 * k3d + k4d);
                if (nt <= 0)
                {
                    double frac = theta / (theta - nt);
                    xi += frac * step;
                    dtheta += frac * (nd - dtheta);
                    table.Add((xi, 0, -xi * xi * dtheta));
                    break;
                }
                xi += step;
                theta = nt;
                dtheta = nd;
                table.Add((xi, theta, -xi * xi * dtheta));
            }
            return (xi, table);
        }

        private static double InverseMass(List<(double Xi, double Theta, double Mu)> t, double mu)
        {
            int lo = 0, hi = t.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (t[mid].Mu < mu) lo = mid; else hi = mid;
            }
            double f = (mu - t[lo].Mu) / Math.Max(t[hi].Mu - t[lo].Mu, 1e-30);
            return t[lo].Xi + f * (t[hi].Xi - t[lo].Xi);
        }

        private static double ThetaAt(List<(double Xi, double Theta, double Mu)> t, double xi)
        {
            int lo = 0, hi = t.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (t[mid].Xi < xi) lo = mid; else hi = mid;
            }
            double f = (xi - t[lo].Xi) / Math.Max(t[hi].Xi - t[lo].Xi, 1e-30);
            return t[lo].Theta + f * (t[hi].Theta - t[lo].Theta);
        }
    }
}
