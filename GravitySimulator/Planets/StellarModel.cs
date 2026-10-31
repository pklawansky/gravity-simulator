using System;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Extension seam: adds accelerations into <see cref="ParticleSystem.Acceleration"/> after gravity
    /// has been computed and before the velocity kick. Radiation pressure is the first implementation;
    /// an SPH pressure force would be another.
    /// </summary>
    public interface IForceContributor
    {
        void AddAccelerations(ParticleSystem ps);
    }

    /// <summary>
    /// Extension seam: per-step class/age/radius updates and events (after collisions). May remove
    /// bodies (mass 0 + compaction) and append new ones within the system's capacity.
    /// </summary>
    public interface IBodyEvolution
    {
        void Evolve(ParticleSystem ps, float dt);
    }

    /// <summary>
    /// Tunables for the sub-grid stellar model (cosmic presets). Lengths, speeds and masses are in
    /// the simulation units of <see cref="Units"/> (mass unit M☉, so masses are solar masses).
    /// </summary>
    public sealed class StellarParameters
    {
        public UnitSystem Units { get; set; } = UnitSystem.SolarParsec;
        /// <summary>Stellar ages advance this many times faster than the dynamics, so stars evolve within minutes of viewing.</summary>
        public float TimeAcceleration { get; set; } = 1f;
        /// <summary>Sink accretion radius: bound gas inside it is absorbed by stars and remnants; black holes absorb any bound body.</summary>
        public float AccretionRadius { get; set; } = 0.01f;
        /// <summary>A gas clump collapses (to a star if over 0.08 M☉) once it holds this much mass.</summary>
        public float StarFormationMass { get; set; } = StellarPhysics.FusionMass;
        /// <summary>Target mass of one ejecta gas particle (sets how many particles a shell or supernova emits).</summary>
        public float GasParticleMass { get; set; } = 0.1f;
        /// <summary>Radius given to ejecta gas particles.</summary>
        public float GasRadius { get; set; } = 0.01f;
        /// <summary>Outward speed of a white dwarf's shed shell (planetary nebula).</summary>
        public float ShellSpeed { get; set; } = 1f;
        /// <summary>Outward speed of supernova ejecta.</summary>
        public float SupernovaEjectaSpeed { get; set; } = 20f;
        /// <summary>Bodies within this distance of a supernova get an outward kick.</summary>
        public float BlastRadius { get; set; } = 0.1f;
        /// <summary>Outward kick at the centre of the blast (falls off linearly to 0 at <see cref="BlastRadius"/>).</summary>
        public float BlastSpeed { get; set; } = 5f;
        /// <summary>Most particles emitted by one event.</summary>
        public int MaxEjecta { get; set; } = 64;
        /// <summary>Radiation pressure is applied from at most this many of the brightest stars.</summary>
        public int MaxRadiators { get; set; } = 64;
        /// <summary>Softening length for radiation pressure (avoids the 1/r^2 singularity).</summary>
        public float RadiationSoftening { get; set; } = 0f;
        /// <summary>Seed for ejecta directions (deterministic runs).</summary>
        public int Seed { get; set; } = 1;
    }
}
