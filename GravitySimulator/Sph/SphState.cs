using System;
using System.Numerics;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Struct-of-arrays SPH gas state on the CPU (presets, the CPU reference, GPU upload/download).
    /// </summary>
    public sealed class SphState
    {
        public Vector3[] Position, Velocity, Acceleration;
        public float[] H, Mass, U, DuDt, Density, Pressure, SoundSpeed, DivV, CurlV, Balsara, MuMax, Potential;
        /// <summary>Adaptive-softening coefficient xi = -h^2 S / (6 rho) (0 when h is clamped or gravity is off).</summary>
        public float[] Xi;
        public int[] Id;

        public int Count { get; set; }
        public int Capacity => Position.Length;

        public SphState(int capacity)
        {
            capacity = Math.Max(1, capacity);
            Position = new Vector3[capacity];
            Velocity = new Vector3[capacity];
            Acceleration = new Vector3[capacity];
            H = new float[capacity];
            Mass = new float[capacity];
            U = new float[capacity];
            DuDt = new float[capacity];
            Density = new float[capacity];
            Pressure = new float[capacity];
            SoundSpeed = new float[capacity];
            DivV = new float[capacity];
            CurlV = new float[capacity];
            Balsara = new float[capacity];
            MuMax = new float[capacity];
            Potential = new float[capacity];
            Xi = new float[capacity];
            Id = new int[capacity];
        }

        /// <summary>Appends a particle (its id is its index at the time of adding). Density starts from h.</summary>
        public int Add(Vector3 position, Vector3 velocity, float mass, float u, float h, float eta = SphKernel.DefaultEta)
        {
            if (Count == Capacity) throw new InvalidOperationException("SphState is full.");
            int i = Count++;
            Position[i] = position;
            Velocity[i] = velocity;
            Acceleration[i] = Vector3.Zero;
            Mass[i] = mass;
            U[i] = u;
            H[i] = h;
            float d = h / eta;
            Density[i] = mass / (d * d * d);
            Id[i] = i;
            return i;
        }

        public SphState Clone()
        {
            var c = new SphState(Capacity) { Count = Count };
            Array.Copy(Position, c.Position, Count);
            Array.Copy(Velocity, c.Velocity, Count);
            Array.Copy(Acceleration, c.Acceleration, Count);
            Array.Copy(H, c.H, Count);
            Array.Copy(Mass, c.Mass, Count);
            Array.Copy(U, c.U, Count);
            Array.Copy(DuDt, c.DuDt, Count);
            Array.Copy(Density, c.Density, Count);
            Array.Copy(Pressure, c.Pressure, Count);
            Array.Copy(SoundSpeed, c.SoundSpeed, Count);
            Array.Copy(DivV, c.DivV, Count);
            Array.Copy(CurlV, c.CurlV, Count);
            Array.Copy(Balsara, c.Balsara, Count);
            Array.Copy(MuMax, c.MuMax, Count);
            Array.Copy(Potential, c.Potential, Count);
            Array.Copy(Xi, c.Xi, Count);
            Array.Copy(Id, c.Id, Count);
            return c;
        }
    }
}
