using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models
{
    public class Facet
    {

        public Facet()
        {
            P1 = new Vector();
            P2 = new Vector();
            P3 = new Vector();
            Brush = GetDefaultBrush();
        }

        public Facet(Vector p1, Vector p2, Vector p3)
        {
            P1 = p1;
            P2 = p2;
            P3 = p3;
            Brush = GetDefaultBrush();
        }

        public Facet(Vector p1, Vector p2, Vector p3, Brush colour)
        {
            P1 = p1;
            P2 = p2;
            P3 = p3;
            Brush = colour;
        }

        Brush GetDefaultBrush()
        {
            double ambience = 1.5; // 1.0 implies only direct light -> 2.0 implies just enough ambient up til the far side of sphere -> > 2.0 all facets are lit to varying degrees

            var y = Math.Sqrt((Math.Pow(Normal.x, 2)) + (Math.Pow(Normal.y, 2)));
            var x = Normal.z;
            var theta = Math.Atan2(y, x);

            var colour = Color.LightGreen;

            var ninetyDegrees = Math.PI / 2.0;

            var angleOfIncidence = ninetyDegrees * ambience;

            var scaleR = Convert.ToInt32(Math.Round(Convert.ToDouble(colour.R) * (angleOfIncidence - theta) / angleOfIncidence, MidpointRounding.AwayFromZero));
            var scaleG = Convert.ToInt32(Math.Round(Convert.ToDouble(colour.G) * (angleOfIncidence - theta) / angleOfIncidence, MidpointRounding.AwayFromZero));
            var scaleB = Convert.ToInt32(Math.Round(Convert.ToDouble(colour.B) * (angleOfIncidence - theta) / angleOfIncidence, MidpointRounding.AwayFromZero));

            scaleR = scaleR < 0 ? 0 : scaleR;
            scaleG = scaleG < 0 ? 0 : scaleG;
            scaleB = scaleB < 0 ? 0 : scaleB;

            return new SolidBrush(Color.FromArgb(scaleR, scaleG, scaleB));
        }

        public void UpdateBrush(List<PointLight> pointLightSources, Color baseColour)
        {
            
            if (pointLightSources == null)
            {
                Brush = new SolidBrush(baseColour);
                return;
            }

            var v1 = Normal;

            if (InvertNormal)
            {
                v1 = v1 * -1;
            }

            var ninetyDegrees = Math.PI / 2.0;

            var v1mag = v1.Magnitude;

            int totScaleR = 0;
            int totScaleG = 0;
            int totScaleB = 0;

            foreach (var pointLightSource in pointLightSources)
            {
                var v2 = pointLightSource.Center - Center;

                var dot = v1.x * v2.x + v1.y * v2.y + v1.z * v2.z;
                
                var v2mag = v2.Magnitude;

                var cos = dot / (v1mag * v2mag);

                if (cos < -1 || cos > 1)
                {
                    cos = cos < -1 ? -1 : cos > 1 ? 1 : cos;
                }

                var theta = Math.Acos(cos);


                var angleOfIncidence = ninetyDegrees * pointLightSource.Ambience;

                var newIntensityAt1m = pointLightSource.LightIntensity / (Math.Pow(v2mag, 2));

                var scaleR = Convert.ToInt32(Math.Round((Convert.ToDouble(baseColour.R) * (angleOfIncidence - theta) / angleOfIncidence) * newIntensityAt1m, MidpointRounding.AwayFromZero));
                var scaleG = Convert.ToInt32(Math.Round((Convert.ToDouble(baseColour.G) * (angleOfIncidence - theta) / angleOfIncidence) * newIntensityAt1m, MidpointRounding.AwayFromZero));
                var scaleB = Convert.ToInt32(Math.Round((Convert.ToDouble(baseColour.B) * (angleOfIncidence - theta) / angleOfIncidence) * newIntensityAt1m, MidpointRounding.AwayFromZero));

                scaleR = scaleR < 0 ? 0 : scaleR > 255 ? 255 : scaleR;
                scaleG = scaleG < 0 ? 0 : scaleG > 255 ? 255 : scaleG;
                scaleB = scaleB < 0 ? 0 : scaleB > 255 ? 255 : scaleB;

                totScaleR += scaleR;
                totScaleG += scaleG;
                totScaleB += scaleB;

            }

            totScaleR = totScaleR < 0 ? 0 : totScaleR > 255 ? 255 : totScaleR;
            totScaleG = totScaleG < 0 ? 0 : totScaleG > 255 ? 255 : totScaleG;
            totScaleB = totScaleB < 0 ? 0 : totScaleB > 255 ? 255 : totScaleB;

            Brush = new SolidBrush(Color.FromArgb(totScaleR, totScaleG, totScaleB));
        }

        public Vector P1 { get; set; }
        public Vector P2 { get; set; }
        public Vector P3 { get; set; }

        public Brush Brush { get; set; }

        public Vector Center => (P1 + P2 + P3) / 3f;

        public bool InvertNormal { get; set; }

        Vector normal = null;
        public Vector Normal
        {
            get
            {
                if (normal == null)
                {
                    var U = P2 - P1;
                    var V = P3 - P1;
                    normal = new Vector((U.y * V.z) - (U.z * V.y), (U.z * V.x) - (U.x * V.z), (U.x * V.y) - (U.y * V.x));
                }
                return normal;
            }
        }

    }
}


/*
 
    Begin Function CalculateSurfaceNormal (Input Triangle) Returns Vector

	    Set Vector U to (Triangle.p2 minus Triangle.p1)
	    Set Vector V to (Triangle.p3 minus Triangle.p1)

	    Set Normal.x to (multiply U.y by V.z) minus (multiply U.z by V.y)
	    Set Normal.y to (multiply U.z by V.x) minus (multiply U.x by V.z)
	    Set Normal.z to (multiply U.x by V.y) minus (multiply U.y by V.x)

	    Returning Normal

    End Function
     
     */
