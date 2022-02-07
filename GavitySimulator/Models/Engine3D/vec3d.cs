using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models.Engine3D
{
    public class vec3d
    {
        public float x { get; set; } = 0;
        public float y { get; set; } = 0;
        public float z { get; set; } = 0;
        public float w { get; set; } = 1; // Need a 4th term to perform sensible matrix vector multiplication
    }
}
