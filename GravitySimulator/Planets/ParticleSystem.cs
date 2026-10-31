using System;
using System.Numerics;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Struct-of-arrays particle state for the planet formation mode (normalised units, G = 1).
    /// Arrays may be longer than <see cref="Count"/>; only the first Count entries are live.
    /// </summary>
    public sealed class ParticleSystem
    {
        public Vector3[] Position;
        public Vector3[] Velocity;
        public Vector3[] Acceleration;
        public float[] Mass;
        public float[] Radius;
        /// <summary>Stellar-model class (Planet, the zero default, when the stellar model is off).</summary>
        public BodyClass[] Class;
        /// <summary>Stellar age in simulation time units (already scaled by the stellar time acceleration).</summary>
        public float[] Age;

        public int Count { get; private set; }
        public int Capacity => Position.Length;

        public ParticleSystem(int capacity)
        {
            capacity = Math.Max(1, capacity);
            Position = new Vector3[capacity];
            Velocity = new Vector3[capacity];
            Acceleration = new Vector3[capacity];
            Mass = new float[capacity];
            Radius = new float[capacity];
            Class = new BodyClass[capacity];
            Age = new float[capacity];
        }

        public int Add(Vector3 position, Vector3 velocity, float mass, float radius, BodyClass bodyClass = BodyClass.Planet, float age = 0f)
        {
            if (Count == Capacity) Grow(Capacity * 2);
            int i = Count++;
            Position[i] = position;
            Velocity[i] = velocity;
            Acceleration[i] = Vector3.Zero;
            Mass[i] = mass;
            Radius[i] = radius;
            Class[i] = bodyClass;
            Age[i] = age;
            return i;
        }

        /// <summary>Grows the arrays (if needed) so at least <paramref name="capacity"/> entries fit without reallocating.</summary>
        public void EnsureCapacity(int capacity)
        {
            if (capacity > Capacity) Grow(capacity);
        }

        /// <summary>Removes entry <paramref name="index"/> by moving the last entry into its slot.</summary>
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int last = --Count;
            if (index != last)
            {
                Position[index] = Position[last];
                Velocity[index] = Velocity[last];
                Acceleration[index] = Acceleration[last];
                Mass[index] = Mass[last];
                Radius[index] = Radius[last];
                Class[index] = Class[last];
                Age[index] = Age[last];
            }
        }

        /// <summary>Removes every entry whose mass is zero or negative (the "dead" marker). Returns how many.</summary>
        public int Compact()
        {
            int removed = 0;
            for (int i = Count - 1; i >= 0; i--)
            {
                if (Mass[i] <= 0f)
                {
                    RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public ParticleSystem Clone()
        {
            var copy = new ParticleSystem(Capacity);
            Array.Copy(Position, copy.Position, Count);
            Array.Copy(Velocity, copy.Velocity, Count);
            Array.Copy(Acceleration, copy.Acceleration, Count);
            Array.Copy(Mass, copy.Mass, Count);
            Array.Copy(Radius, copy.Radius, Count);
            Array.Copy(Class, copy.Class, Count);
            Array.Copy(Age, copy.Age, Count);
            copy.Count = Count;
            return copy;
        }

        private void Grow(int capacity)
        {
            Array.Resize(ref Position, capacity);
            Array.Resize(ref Velocity, capacity);
            Array.Resize(ref Acceleration, capacity);
            Array.Resize(ref Mass, capacity);
            Array.Resize(ref Radius, capacity);
            Array.Resize(ref Class, capacity);
            Array.Resize(ref Age, capacity);
        }
    }
}
