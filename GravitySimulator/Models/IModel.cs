using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public interface IModel
    {
        int Id { get; set; }
        List<Vector> Points { get; set; }
        Vector Center { get; set; }
        List<Facet> Facets { get; set; }
    }
}
