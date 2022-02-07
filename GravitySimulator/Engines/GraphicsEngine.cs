using GravitySimulator.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Engines
{
    public class GraphicsEngine
    {
        public static Bitmap GetBitmap(Brush transparency, int width = 600, int height = 600)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (Graphics grp = Graphics.FromImage(bmp))
            {
                grp.FillRectangle(
                    transparency, 0, 0, width, height);
            }

            return bmp;
        }


        #region Drawing

        public static void DrawCircle(Bitmap bmp, float centerX, float centerY, float radius, Pen pen, Brush fillBrush)
        {
            DrawEllipse(bmp, centerX, centerY, radius, radius, pen, fillBrush);
        }

        public static void DrawEllipse(Bitmap bmp, float centerX, float centerY, float radiusX, float radiusY, Pen pen, Brush fillBrush)
        {
            Graphics g = Graphics.FromImage(bmp);
            if (fillBrush != null)
            {
                g.FillEllipse(fillBrush, centerX, centerY, radiusX * 2, radiusY * 2);
            }
            g.DrawEllipse(pen, centerX, centerY, radiusX * 2, radiusY * 2);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }

        public static void DrawLine(Bitmap bmp, PointF startPoint, PointF endPoint, Pen pen)
        {
            try
            {
                Graphics g = Graphics.FromImage(bmp);
                g.DrawLine(pen, startPoint, endPoint);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            }
            catch { }
        }

        public static void DrawPolygon(Bitmap bmp, Pen pen, Brush fillBrush, params PointF[] points)
        {
            Graphics g = Graphics.FromImage(bmp);
            if (fillBrush != null)
            {
                g.FillPolygon(fillBrush, points);
            }
            g.DrawPolygon(pen, points);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }

        #endregion

        #region Models

        //public static void DrawModelWireframe3DFixedPerspective(Bitmap bmp, IModel model, Pen pen, int imageWidth, int imageHeight, Vector bounds, float focus = 0.5f)
        //{
        //    var points = model.Points.Select(x => Convert3DTo2DFixedPerspective(x, 0, imageWidth, imageHeight, bounds, focus)).ToList();

        //    foreach (var connection in model.PointConnections)
        //    {
        //        var a = points[connection.Item1];
        //        var b = points[connection.Item2];
        //        DrawLine(bmp, new PointF(a.x, a.y), new PointF(b.x, b.y), pen);
        //    }
        //}

        public static bool IsInBounds(Vector bounds, Vector point)
        {
            return true;
            return point.x >= 0 && point.x <= bounds.x &&
                point.y >= bounds.y && point.y <= bounds.y &&
                point.z >= 0 && point.z <= bounds.z;
        }

        public static void DrawModelFacets3DFixedPerspective(Bitmap bmp, IModel model, Pen pen, Color baseColour, int imageWidth, int imageHeight, Vector bounds, 
            List<PointLight> pointLights, float focus = 0.5f)
        {
            model.Facets.ForEach(x => x.UpdateBrush(pointLights, baseColour));

            var facetsOrdered = model.Facets.Where(x => IsInBounds(bounds, x.Center)).OrderByDescending(x => x.Center.y);
            if (model.Center.z > bounds.z / 2f)
            {
                facetsOrdered = facetsOrdered.ThenByDescending(x => x.Center.z);
            }
            else
            {
                facetsOrdered = facetsOrdered.ThenBy(x => x.Center.z);
            }

            if (model.Center.x > bounds.x / 2f)
            {
                facetsOrdered = facetsOrdered.ThenByDescending(x => x.Center.x);
            }
            else
            {
                facetsOrdered = facetsOrdered.ThenBy(x => x.Center.x);
            }

            var facetPoints = facetsOrdered.Select(x => new
            {
                P1 = Convert3DTo2DFixedPerspective(x.P1, 0, imageWidth, imageHeight, bounds, focus),
                P2 = Convert3DTo2DFixedPerspective(x.P2, 0, imageWidth, imageHeight, bounds, focus),
                P3 = Convert3DTo2DFixedPerspective(x.P3, 0, imageWidth, imageHeight, bounds, focus),
                x.Brush
            }).ToList();

            foreach (var fp in facetPoints)
            {
                DrawPolygon(bmp, pen, fp.Brush, new PointF(fp.P1.x, fp.P1.y), new PointF(fp.P2.x, fp.P2.y), new PointF(fp.P3.x, fp.P3.y));
            }
        }

        #endregion

        #region Graphics Transforms

        public static Vector Convert3DTo2DOrthographic(Vector point, float radius, int imageWidth, int imageHeight, Vector bounds)
        {
            var axisx = point.x; // + 0.5f * point.y;
            var axisy = imageHeight - point.z; // - 0.5f * point.y;

            var maxXfromY = imageWidth / 3f;
            var yContributionToX = maxXfromY * point.y / bounds.y;
            axisx += yContributionToX;

            var maxYfromY = imageHeight / 3f;
            var yContributionToY = maxYfromY * point.y / bounds.y;
            axisy -= yContributionToY;

            return new Vector(axisx, axisy, radius);
        }

        public static Vector Convert3DTo2DFixedPerspective(Vector point, float radius, int imageWidth, int imageHeight, Vector bounds, float focus = 0.5f)
        {
            var center = new PointF(Convert.ToSingle(imageWidth) / 2f, Convert.ToSingle(imageHeight) / 2f);

            var scaleX = Convert.ToSingle(imageWidth) / bounds.x;
            var scaleY = Convert.ToSingle(imageHeight) / bounds.z;

            


            var axisX = point.x * scaleX;
            var axisY = Convert.ToSingle(imageHeight) - point.z * scaleY ;


            var y = point.y * scaleX;
            var yMax = bounds.y * scaleX;
            var yRatio = (y / yMax);


            var scalingFactor = (1f - (1f - focus) * yRatio);

            var _radius = radius * scalingFactor;

            var diffX = axisX - center.X;
            var diffY = axisY - center.Y;

            var newDiffX = diffX * scalingFactor;
            var newDiffY = diffY * scalingFactor;

            axisX = center.X + newDiffX;
            axisY = center.Y + newDiffY;

           

     
            return new Vector(axisX, axisY, _radius);
        }

        #endregion
    }
}
