using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class Plane : IModel
    {
        public Plane(int id, Vector corner1, Vector corner2, PlaneAxis axis, float maxFacetSize = 10f, bool invertNormal = false)
        {
            Id = id;

            Points = new List<Vector>
            {
                corner1, corner2
            };

            Corner1 = corner1;
            Corner2 = corner2;

            Center = (Points[0] + Points[1]) / 2f;

            MaxFacetSize = maxFacetSize;
            Axis = axis;

            Facets = new List<Facet>();
            GenerateFacets(invertNormal);
        }

        public enum PlaneAxis
        {
            xy,
            yz,
            xz
        }

        public int Id { get; set; }
        public List<Vector> Points { get; set; }
        public List<Tuple<int, int>> PointConnections { get; set; }
        public Vector Center { get; set; }
        public List<Facet> Facets { get; set; }

        float MaxFacetSize { get; set; }
        Vector Corner1 { get; set; }
        Vector Corner2 { get; set; }

        PlaneAxis Axis { get; set; }

        private void GenerateFacets(bool invertNormal)
        {
            float startA = Axis == PlaneAxis.xy ? Corner1.x : Axis == PlaneAxis.yz ? Corner1.y : Axis == PlaneAxis.xz ? Corner1.x : 0;
            float startB = Axis == PlaneAxis.xy ? Corner1.y : Axis == PlaneAxis.yz ? Corner1.z : Axis == PlaneAxis.xz ? Corner1.z : 0;

            float initA = startA;
            float initB = startB;

            float endA = Axis == PlaneAxis.xy ? Corner2.x : Axis == PlaneAxis.yz ? Corner2.y : Axis == PlaneAxis.xz ? Corner2.x : 0;
            float endB = Axis == PlaneAxis.xy ? Corner2.y : Axis == PlaneAxis.yz ? Corner2.z : Axis == PlaneAxis.xz ? Corner2.z : 0;

            float? prevA = null;
            float? prevB = null;

            while (startA <= endA)
            {
                startB = initB;
                prevB = null;
                while (startB <= endB)
                {
                    if (prevB.HasValue && prevA.HasValue)
                    {
                        Vector r1 = new Vector(0, 0, 0);
                        Vector r2 = new Vector(0, 0, 0);
                        Vector r3 = new Vector(0, 0, 0);
                        Vector r4 = new Vector(0, 0, 0);

                        switch(Axis)
                        {
                            case PlaneAxis.xy:
                                r1.x = prevA.Value;
                                r1.y = prevB.Value;
                                r1.z = Corner1.z;
                                r2.x = prevA.Value;
                                r2.y = startB;
                                r2.z = Corner1.z;
                                r3.x = startA;
                                r3.y = startB;
                                r3.z = Corner1.z;
                                r4.x = startA;
                                r4.y = prevB.Value;
                                r4.z = Corner1.z;
                                break;
                            case PlaneAxis.yz:
                                r1.y = prevA.Value;
                                r1.z = prevB.Value;
                                r1.x = Corner1.x;
                                r2.y = prevA.Value;
                                r2.z = startB;
                                r2.x = Corner1.x;
                                r3.y = startA;
                                r3.z = startB;
                                r3.x = Corner1.x;
                                r4.y = startA;
                                r4.z = prevB.Value;
                                r4.x = Corner1.x;
                                break;
                            case PlaneAxis.xz:
                                r1.x = prevA.Value;
                                r1.z = prevB.Value;
                                r1.y = Corner1.y;
                                r2.x = prevA.Value;
                                r2.z = startB;
                                r2.y = Corner1.y;
                                r3.x = startA;
                                r3.z = startB;
                                r3.y = Corner1.y;
                                r4.x = startA;
                                r4.z = prevB.Value;
                                r4.y = Corner1.y;
                                break;
                        }

                        Facets.Add(new Facet(r1, r2, r3) { InvertNormal = invertNormal });
                        Facets.Add(new Facet(r1, r3, r4) { InvertNormal = invertNormal });
                    }

                    prevB = startB;

                    //end of loop
                    if (startB == endB)
                        break;
                    startB += MaxFacetSize;
                    startB = startB > endB ? endB : startB;
                }

                prevA = startA;

                //end of loop
                if (startA == endA)
                    break;
                startA += MaxFacetSize;
                startA = startA > endA ? endA : startA;
            }
        }
    }
}
