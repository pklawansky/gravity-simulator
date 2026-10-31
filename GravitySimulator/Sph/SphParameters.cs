using System;
using System.Numerics;

namespace GravitySimulator.Sph
{
    /// <summary>Equation of state of the gas.</summary>
    public enum EquationOfState
    {
        /// <summary>P = (γ-1) ρ u, with u evolved from pdV work and viscous heating.</summary>
        IdealGas = 0,
        /// <summary>P = c_s² ρ [1 + (ρ/ρ_crit)^(4/3)] (Bate, Bonnell &amp; Bromm 2003): isothermal, stiffening when dense.</summary>
        Barotropic = 1,
    }

    /// <summary>
    /// Tunables shared by the GPU solver and the CPU reference (simulation units, G = 1).
    /// </summary>
    public sealed class SphParameters
    {
        public EquationOfState Eos { get; set; } = EquationOfState.IdealGas;
        /// <summary>Adiabatic index for the ideal-gas EOS.</summary>
        public float Gamma { get; set; } = 5f / 3f;
        /// <summary>Isothermal sound speed c_s for the barotropic EOS.</summary>
        public float SoundSpeed { get; set; } = 1f;
        /// <summary>ρ_crit for the barotropic EOS (where it stiffens).</summary>
        public float CriticalDensity { get; set; } = 1e30f;

        /// <summary>Monaghan (1992) artificial viscosity α (with a Balsara switch).</summary>
        public float Alpha { get; set; } = 1f;
        /// <summary>Monaghan (1992) artificial viscosity β.</summary>
        public float Beta { get; set; } = 2f;
        /// <summary>
        /// h = η (m/ρ)^(1/3). 1.5 (~113 neighbours) rather than the cubic-spline value 1.2 (~58): the
        /// Wendland C2 kernel needs the larger neighbour count (Dehnen &amp; Aly 2012); at 1.2 the Sod
        /// star state is ~45% off in pressure and the sound speed 3.5% slow.
        /// </summary>
        public float Eta { get; set; } = SphKernel.DefaultEta;
        public float HMin { get; set; } = 1e-6f;
        public float HMax { get; set; } = 1e6f;

        /// <summary>Courant factor C of the time step.</summary>
        public float Courant { get; set; } = 0.3f;
        /// <summary>Multiplies the Courant factor (UI time speed).</summary>
        public float TimeScale { get; set; } = 1f;
        public float MaxTimeStep { get; set; } = float.MaxValue;

        public bool SelfGravity { get; set; } = true;
        /// <summary>Barnes-Hut opening angle.</summary>
        public float Theta { get; set; } = 0.6f;
        /// <summary>Floor on the internal energy u.</summary>
        public float MinInternalEnergy { get; set; } = 0f;

        /// <summary>A converging, bound particle denser than this becomes a sink (∞ = no sinks).</summary>
        public float SinkDensity { get; set; } = float.PositiveInfinity;
        /// <summary>Accretion radius of gas-formed sinks; also the radius a new sink gathers its gas from.</summary>
        public float SinkRadius { get; set; } = 0f;

        /// <summary>CPU reference only: periodic box lengths per axis (0 = open). The GPU solver is open.</summary>
        public Vector3 Period { get; set; } = Vector3.Zero;

        public SphParameters Clone() => (SphParameters)MemberwiseClone();
    }
}
