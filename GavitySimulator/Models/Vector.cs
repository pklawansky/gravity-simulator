using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class Vector
    {
        public Vector()
        {

        }

        public Vector(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector(Vector vector)
        {
            this.x = vector.x;
            this.y = vector.y;
            this.z = vector.z;
        }

        public float x { get; set; }
        public float y { get; set; }
        public float z { get; set; }

        public static Vector operator +(Vector a, Vector b)
        {
            return new Vector { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        }
        public static Vector operator -(Vector a, Vector b)
        {
            return new Vector { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
        }

        public static Vector operator /(Vector a, float b)
        {
            return new Vector { x = a.x / b, y = a.y / b, z = a.z / b };
        }

        public static Vector operator *(Vector a, float b)
        {
            return new Vector { x = a.x * b, y = a.y * b, z = a.z * b };
        }

        public Vector Translate(float x, float y, float z)
        {
            this.x += x;
            this.y += y;
            this.z += z;
            return this;
        }

        public float Magnitude => Convert.ToSingle(Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2) + Math.Pow(z, 2)));
        

        public override string ToString()
        {
            return string.Format("x = {0}, y = {1}, z = {2}", this.x, this.y, this.z);
        }
    }
}
