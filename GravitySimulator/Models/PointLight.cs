using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class PointLight : IModel
    {
        public PointLight()
        {
            Id = 0;
            Center = new Vector();
            Ambience = 1;
            LightIntensity = 1;
        }

        public PointLight(int id, Vector center, double intensity, double ambience)
        {
            Id = id;
            Center = center;
            LightIntensity = intensity;
            Ambience = ambience;
        }

        public int Id { get; set; }
        public List<Vector> Points { get; set; }
        public Vector Center { get; set; }
        public List<Facet> Facets { get; set; }

        public double LightIntensity { get; set; }
        public double Ambience { get; set; }
        public Color Colour { get; set; }

        public void Move(Vector newPosition)
        {
            throw new NotImplementedException();
        }
    }
}
