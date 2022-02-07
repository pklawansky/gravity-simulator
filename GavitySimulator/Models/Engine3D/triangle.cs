using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models.Engine3D
{
    public class triangle
    {
        public vec3d[] p { get; set; } = new vec3d[3];
        public vec2d[] t { get; set; } = new vec2d[3];  // added a texture coord per vertex
        public string sym { get; set; }
        public short col { get; set; }
    }
}
