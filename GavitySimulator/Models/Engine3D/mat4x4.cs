using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models.Engine3D
{
    public class mat4x4
    {
        public float[][] m { get; set; } = new float[4][] 
        {
            new float[4],
            new float[4],
            new float[4],
            new float[4]
        };
    }
}
