using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class SphereModel : IModel
    {
        public SphereModel(int id, Vector center, float radius, float sceneScale = 1f)
        {
            Id = id;
            Center = center;
            Radius = radius;
            Points = new List<Vector>();
            PointConnections = new List<Tuple<int, int>>();
            Facets = new List<Facet>();
            //GenerateSphereModel_Manual();
            //GenerateSphereModel_Algorithm();

            int iterations = 0;

            if (radius > 5f / sceneScale) iterations++;
            if (radius > 10f / sceneScale) iterations++;
            if (radius > 30f / sceneScale) iterations++;
            if (radius > 100f / sceneScale) iterations++;

            CreateNSphere(iterations);
            //Denormalize();
        }

        public int Id { get; set; }
        public List<Vector> Points { get; set; }
        public List<Tuple<int, int>> PointConnections { get; set; }
        public Vector Center { get; set; }
        public List<Facet> Facets { get; set; }



        //void GenerateSphereModel_Manual()
        //{
        //    Points.Add(new Vector(Center).Translate(Radius, 0, 0));
        //    Points.Add(new Vector(Center).Translate(0, Radius, 0));
        //    Points.Add(new Vector(Center).Translate(-Radius, 0, 0));
        //    Points.Add(new Vector(Center).Translate(0, -Radius, 0));
        //    Points.Add(new Vector(Center).Translate(0, 0, Radius));
        //    Points.Add(new Vector(Center).Translate(0, 0, -Radius));


        //    PointConnections.Add(new Tuple<int, int>(0, 1));
        //    PointConnections.Add(new Tuple<int, int>(1, 2));
        //    PointConnections.Add(new Tuple<int, int>(2, 3));
        //    PointConnections.Add(new Tuple<int, int>(3, 0));
        //    PointConnections.Add(new Tuple<int, int>(4, 0));
        //    PointConnections.Add(new Tuple<int, int>(4, 1));
        //    PointConnections.Add(new Tuple<int, int>(4, 2));
        //    PointConnections.Add(new Tuple<int, int>(4, 3));
        //    PointConnections.Add(new Tuple<int, int>(5, 0));
        //    PointConnections.Add(new Tuple<int, int>(5, 1));
        //    PointConnections.Add(new Tuple<int, int>(5, 2));
        //    PointConnections.Add(new Tuple<int, int>(5, 3));
        //}

        //void GenerateSphereModel_Algorithm()
        //{
        //    Points.Add(new Vector(Center));
        //    int iter = 1;
        //    for (float _theta = -90; _theta <= 90; _theta += 20)
        //    {
        //        float theta = _theta / 180 * Convert.ToSingle(Math.PI);

        //        for (float _phi = 0; _phi < 360; _phi += 40)
        //        {
        //            float phi = _phi / 180 * Convert.ToSingle(Math.PI);

        //            float x = Radius * Convert.ToSingle(Math.Cos(theta) * Math.Cos(phi));
        //            float y = Radius * Convert.ToSingle(Math.Cos(theta) * Math.Sin(phi));
        //            float z = Radius * Convert.ToSingle(Math.Sin(theta));

        //            Points.Add(new Vector(Center).Translate(x, y, z));
        //            PointConnections.Add(new Tuple<int, int>(iter - 1, iter));

        //            iter++;
        //        }
        //    }
        //}


        public float Radius { get; set; }


        /*
   Create a triangular facet approximation to a sphere
   Return the number of facets created.
   The number of facets will be (4^iterations) * 8
*/
        int CreateNSphere(int iterations)
        {
            List<Vector> p = new List<Vector>
            {
                new Vector( 0, 0, 1 ),
                new Vector( 0, 0, -1 ),
                new Vector( -1, -1, 0 ),
                new Vector( 1, -1, 0 ),
                new Vector( 1, 1, 0 ),
                new Vector( -1, 1, 0 )
            };


            Normalise(p[0], p[1], p[2], p[3], p[4], p[5]);

            //have to initialize Facets

            var facets = new List<Facet>
            {
                new Facet(p[0], p[3], p[4]),
                new Facet(p[0], p[4], p[5]),
                new Facet(p[0], p[5], p[2]),
                new Facet(p[0], p[2], p[3]),
                new Facet(p[1], p[4], p[3]),
                new Facet(p[1], p[5], p[4]),
                new Facet(p[1], p[2], p[5]),
                new Facet(p[1], p[3], p[2])
            };

            /* Bisect each edge and move to the surface of a unit sphere */
            for (var it = 0; it < iterations; it++)
            {
                var newFacets = new List<Facet>();
                foreach(var f in facets)
                {
                    newFacets.AddRange(SubdivideFacet(f));
                }

                facets = newFacets;
               
            }

            Denormalize(facets);
            Facets = facets;

            return Facets.Count;
        }

        Facet[] SubdivideFacet(Facet f)
        {
            Facet[] facets = new Facet[4];

            var p12 = MidPoint(f.P1, f.P2);
            var p23 = MidPoint(f.P2, f.P3);
            var p31 = MidPoint(f.P3, f.P1);
            Normalise(p12, p23, p31);

            facets[0] = new Facet(f.P1, p12, p31);
            facets[1] = new Facet(p31, p12, p23);
            facets[2] = new Facet(f.P2, p23, p12);
            facets[3] = new Facet(f.P3, p31, p23);

            return facets;
        }

        void Denormalize(List<Facet> facets)
        {
            //now to denormalize
            foreach (var face in facets)
            {
                face.P1 = face.P1 * Radius + Center;
                face.P2 = face.P2 * Radius + Center;
                face.P3 = face.P3 * Radius + Center;
            }
        }

        /*
           Return the midpoint between two vectors
        */
        Vector MidPoint(Vector p1, Vector p2)
        {
            Vector p = new Vector();

            p.x = (p1.x + p2.x) / 2;
            p.y = (p1.y + p2.y) / 2;
            p.z = (p1.z + p2.z) / 2;

            return p;
        }

        /*
           Normalise a vector
        */
        void Normalise(params Vector[] ps)
        {
            float length;

            foreach (var p in ps)
            {
                length = Convert.ToSingle(Math.Sqrt(p.x * p.x + p.y * p.y + p.z * p.z));
                if (length != 0)
                {
                    p.x /= length;
                    p.y /= length;
                    p.z /= length;
                }
                else
                {
                    p.x = 0;
                    p.y = 0;
                    p.z = 0;
                }
            }
        }

     



    }

}
