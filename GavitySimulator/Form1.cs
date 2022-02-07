using GravitySimulator.Engines;
using GravitySimulator.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GravitySimulator
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            Load += Form1_Load;
            KeyDown += Form1_KeyDown;
        }

        Timer t = null;

        Vector sceneBounds = null;
        int width = 0;
        int height = 0;
        float focus = 0.5f;
        float initialVelocity = 0.05f; //0.5f;
        int points = 20;
        float funLevel = 1; //1 is most accurate collisions, > 1 collisions take more effort
        float density = 203877746.0f; //23877746.0f;
        float maxRadius = 0.3f;
        float globalGravity = 0f;
        float wallElasticity = 0.8f;
        float timeSkip = 0.4f; //in seconds
        int stepsPerFrame = 1;
        int timerInterval = 17;
        List<PointLight> pointLights = new List<PointLight>();
        float wallFacetSize = 0.1f;
        double lightIntensity = 1;
        double ambience = 2;
        float sceneScale = 100;

        List<Plane> BoundaryPlanes = new List<Plane>();

        private void Form1_Load(object sender, EventArgs e)
        {
            t = new Timer()
            {
                Interval = timerInterval
            };
            t.Tick += T_Tick;
            width = pictureBox1.Width;
            height = pictureBox1.Height;

            var y = Convert.ToInt32(Math.Round(Math.Sqrt(Math.Pow((width / 3), 2) + Math.Pow((height / 3), 2)), MidpointRounding.AwayFromZero));

            sceneBounds = new Vector(width / sceneScale, width / sceneScale, height / sceneScale);

            BoundaryPlanes.Add(new Plane(1, new Vector(0, 0, 0), new Vector(sceneBounds.x, sceneBounds.y, 0), Plane.PlaneAxis.xy, wallFacetSize, true)); //bottom floor OK
            BoundaryPlanes.Add(new Plane(2, new Vector(0, 0, 0), new Vector(0, sceneBounds.y, sceneBounds.z), Plane.PlaneAxis.yz, wallFacetSize, true)); //left wall OK
            BoundaryPlanes.Add(new Plane(3, new Vector(sceneBounds.x, 0, 0), new Vector(sceneBounds.x, sceneBounds.y, sceneBounds.z), Plane.PlaneAxis.yz, wallFacetSize, false)); //right wall BAD
            BoundaryPlanes.Add(new Plane(4, new Vector(0, 0, sceneBounds.z), new Vector(sceneBounds.x, sceneBounds.y, sceneBounds.z), Plane.PlaneAxis.xy, wallFacetSize, false)); //top ceiling GOOD
            BoundaryPlanes.Add(new Plane(1, new Vector(0, sceneBounds.y, 0), new Vector(sceneBounds.x, sceneBounds.y, sceneBounds.z), Plane.PlaneAxis.xz, wallFacetSize, true)); //back wall OK

            pointLights.Add(new PointLight(1, new Vector(sceneBounds.x - 100f / sceneScale, sceneBounds.y - 100f / sceneScale, sceneBounds.z - 100f / sceneScale), lightIntensity, ambience));
            pointLights.Add(new PointLight(2, new Vector(100f / sceneScale, sceneBounds.y - 100f / sceneScale, sceneBounds.z - 100f / sceneScale), lightIntensity, ambience));
            pointLights.Add(new PointLight(3, new Vector(sceneBounds.x - 100f / sceneScale, 100f / sceneScale, sceneBounds.z - 100f / sceneScale), lightIntensity, ambience));
            pointLights.Add(new PointLight(4, new Vector(100f / sceneScale, 100f / sceneScale, sceneBounds.z - 100f / sceneScale), lightIntensity, ambience));
            pointLights.Add(new PointLight(5, new Vector(sceneBounds.x / 2f, sceneBounds.y / 2f, 100f / sceneScale), lightIntensity, ambience));


            lblFocus.Text = focus.ToString();

            SetupPoints();

            t.Start();

        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up)
            {
                globalGravity += 0.1f;
            }
            else if (e.KeyCode == Keys.Down)
            {
                globalGravity -= 0.1f;
            }
            else if (e.KeyCode == Keys.Left)
            {
                focus -= 0.01f;
                if (focus < 0)
                    focus = 0;
            }
            else if (e.KeyCode == Keys.Right)
            {
                focus += 0.01f;
                if (focus > 1)
                    focus = 1;
            }

            focus = Convert.ToSingle(Math.Round(focus, 2, MidpointRounding.AwayFromZero));
            globalGravity = Convert.ToSingle(Math.Round(globalGravity, 1, MidpointRounding.AwayFromZero));

            lblGravity.Text = globalGravity.ToString();
            lblFocus.Text = focus.ToString();
        }



        public int ActivePoints => GravityEngine.Instance.Points.Count(x => x.IsAlive);

        private void SetupPoints()
        {
            GravityEngine.Instance.SetupPoints(points, sceneBounds.x, sceneBounds.y, sceneBounds.z, maxRadius, density, initialVelocity);
        }

        private void ProcessPositions()
        {
            GravityEngine.Instance.ProcessPositions(stepsPerFrame, timeSkip, globalGravity, wallElasticity, funLevel, true);
        }




        string mode = "perspective";// "perspective"; //orthographic

        //float focusInc = 0.01f;
        private void T_Tick(object sender, EventArgs e)
        {

            ProcessPositions();
            var bmp = GraphicsEngine.GetBitmap(Brushes.Black, width, height);

            if (mode == "orthographic")
            {
                DrawAxesOrthographic(bmp, false);

                foreach (var point in GravityEngine.Instance.Points.Where(x => x.IsAlive).OrderByDescending(x => x.Position.y))
                {
                    PlotPointOrthographic(bmp, point);
                }
                DrawAxesOrthographic(bmp, true);
            }
            else if (mode == "perspective")
            {
                PlotBoundaryPlanes(bmp);
                DrawLightSource(bmp, pointLights, 5f / sceneScale);
                foreach (var point in GravityEngine.Instance.Points.Where(x => x.IsAlive).OrderByDescending(x => x.Position.y))
                {
                    PlotSpherePespective(bmp, point);
                }
            }

            PostProcessOutput(bmp);

            pictureBox1.BeginInvoke((Action)(() =>
            {
                pictureBox1.Image = bmp;
            }));

            lblActivePoints.BeginInvoke((Action)(() =>
            {
                lblActivePoints.Text = ActivePoints.ToString();
            }));
        }

        private void PostProcessOutput(Bitmap bmp)
        {
            // do image processing like anti-aliasing here
        }

        private void DrawLightSource(Bitmap bmp, List<PointLight> pointLights, float lightRadius)
        {
            if (pointLights == null) return;

            foreach (var pointLight in pointLights)
            {
                var model = new SphereModel(0, pointLight.Center, lightRadius);

                var pen = new Pen(Color.FromArgb(200, Color.White), 0f);

                GraphicsEngine.DrawModelFacets3DFixedPerspective(bmp, model, pen, Color.FromArgb(150, Color.White), width, height, sceneBounds, null, focus);
            }


        }

        private void DrawAxesPerspective(Bitmap bmp)
        {
            List<Vector> pis = new List<Vector>
            {
                new Vector(0,0,0),  //bottom left foreground 0
                new Vector(0,0, sceneBounds.z), //top left foreground 1
                new Vector(0,sceneBounds.y,0), //bottom left background 2
                new Vector(0,sceneBounds.y,sceneBounds.z), //top left background 3
                new Vector(sceneBounds.x, 0, 0), //bottom right foreground 4
                new Vector(sceneBounds.x, sceneBounds.y, 0), //bottom right background 5
                new Vector(sceneBounds.x, 0, sceneBounds.z), //top right foreground 6
                new Vector(sceneBounds.x,sceneBounds.y,sceneBounds.z) //top right background 7
            };

            var axis = pis.Select(x => GraphicsEngine.Convert3DTo2DFixedPerspective(x, 0, width, height, sceneBounds, focus)).Select(x => new PointF(x.x, x.y)).ToList();
            var pen = new Pen(Color.Gray, 1);

            var brush1 = new SolidBrush(Color.FromArgb(100, 100, 100));
            var brush2 = new SolidBrush(Color.FromArgb(120, 120, 120));
            var brush3 = new SolidBrush(Color.FromArgb(140, 140, 140));
            var brush4 = new SolidBrush(Color.FromArgb(160, 160, 160));
            var brush5 = new SolidBrush(Color.FromArgb(180, 180, 180));
            var brush6 = new SolidBrush(Color.FromArgb(200, 200, 200));

            GraphicsEngine.DrawPolygon(bmp, pen, brush3, axis[0], axis[2], axis[3], axis[1]); //left panel

            GraphicsEngine.DrawPolygon(bmp, pen, brush1, axis[1], axis[3], axis[7], axis[6]); //top panel

            GraphicsEngine.DrawPolygon(bmp, pen, brush2, axis[6], axis[7], axis[5], axis[4]); //right panel

            GraphicsEngine.DrawPolygon(bmp, pen, brush6, axis[0], axis[2], axis[5], axis[4]); //bottom panel

            GraphicsEngine.DrawPolygon(bmp, pen, brush4, axis[2], axis[3], axis[7], axis[5]); //back panel
        }

        private void PlotPointPerspective(Bitmap bmp, PointInSpace point)
        {
            var centerX = 0f;
            var centerY = 0f;

            var posY = point.Position.y;
            var scale = Convert.ToInt32(Math.Round(255f * (1f - (posY / sceneBounds.y)), MidpointRounding.AwayFromZero));
            if (scale < 0) scale = 0;
            if (scale > 255) scale = 255;
            var fillBrush = new SolidBrush(Color.FromArgb(scale, scale, scale));

            var result = GraphicsEngine.Convert3DTo2DFixedPerspective(point.Position, point.Radius, width, height, sceneBounds, focus);
            centerX = result.x;
            centerY = result.y;

            var pen = new Pen(Color.Black, 1);

            GraphicsEngine.DrawCircle(bmp, centerX, centerY, result.z, pen, fillBrush);
        }

        private void PlotSpherePespective(Bitmap bmp, PointInSpace point)
        {
            var model = new SphereModel(point.Id, point.Position, point.Radius, sceneScale);

            var posY = point.Position.y;
            var scale = Convert.ToInt32(Math.Round(255f * (1f - (posY / sceneBounds.y)), MidpointRounding.AwayFromZero));
            if (scale < 0) scale = 0;
            if (scale > 255) scale = 255;

            var pen = new Pen(Color.FromArgb(scale, scale, scale), 1);

            pen = new Pen(Color.Transparent, 0f);

            GraphicsEngine.DrawModelFacets3DFixedPerspective(bmp, model, pen, Color.LightGreen, width, height, sceneBounds, pointLights, focus);

            if (point.PointCollision)
            {
                point.PointCollision = false;
                var location = GraphicsEngine.Convert3DTo2DFixedPerspective(point.Position, point.Radius, width, height, sceneBounds, focus);

                GraphicsEngine.DrawCircle(bmp, location.x - location.z, location.y - location.z, location.z, new Pen(Color.Red), Brushes.Red);
            }
        }


        private void PlotBoundaryPlanes(Bitmap bmp)
        {
            var pen = new Pen(Color.Transparent, 0f);

            foreach (var model in BoundaryPlanes)
            {
                GraphicsEngine.DrawModelFacets3DFixedPerspective(bmp, model, pen, Color.LightBlue, width, height, sceneBounds, pointLights, focus);
            }
        }

        private void PlotPointOrthographic(Bitmap bmp, PointInSpace point)
        {
            var centerX = point.Position.x;
            var centerY = point.Position.y;
            var radius = point.Radius;


            var fillBrush = new SolidBrush(Color.White);

            var result = GraphicsEngine.Convert3DTo2DOrthographic(point.Position, point.Radius, width, height, sceneBounds);
            centerX = result.x;
            centerY = result.y;

            var pen = new Pen(Color.Gray, 1);

            GraphicsEngine.DrawCircle(bmp, centerX, centerY, radius, pen, fillBrush);
        }





        void DrawAxesOrthographic(Bitmap bmp, bool isForeground)
        {


            var pen = new Pen(Color.Gray, 2);

            if (isForeground)
            {
                GraphicsEngine.DrawLine(bmp, new PointF(0, height), new PointF(2 * width / 3, height), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(0, height / 3), new PointF(width / 3, 0), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(0, height / 3), new PointF(2 * width / 3, height / 3), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(2 * width / 3, height / 3), new PointF(2 * width / 3, height), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(2 * width / 3, height / 3), new PointF(width, 0), pen);

            }
            else
            {
                GraphicsEngine.DrawLine(bmp, new PointF(0, height / 3), new PointF(0, height), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(0, height), new PointF(width / 3, 2 * height / 3), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(width / 3, 0), new PointF(width, 0), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(width, 0), new PointF(width, 2 * height / 3), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(width, 2 * height / 3), new PointF(2 * width / 3, height), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(width / 3, 0), new PointF(width / 3, 2 * height / 3), pen);
                GraphicsEngine.DrawLine(bmp, new PointF(width / 3, 2 * height / 3), new PointF(width, 2 * height / 3), pen);

            }







        }
    }
}
