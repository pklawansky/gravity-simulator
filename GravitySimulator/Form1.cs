using GravitySimulator.Classic;
using GravitySimulator.Engines;
using GravitySimulator.Models;
using OpenTK.GLControl;
using OpenTK.Windowing.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
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
            FormClosing += Form1_FormClosing;
        }

        // OpenGL renderer (the GDI one below is the fallback). GRAVITYSIM_CLASSIC_RENDERER=gdi forces GDI;
        // =simulate-gl-failure asks for an impossible OpenGL version, to exercise the fallback.
        private const string RendererEnvVar = "GRAVITYSIM_CLASSIC_RENDERER";
        private GLControl _gl;
        private ClassicGlRenderer _glRenderer;
        private bool _glReady;
        private readonly ClassicFrame _glFrame = new ClassicFrame();
        private Label lblRenderer;
        private readonly ToolTip _rendererTip = new ToolTip();
        private string _rendererName = "GDI";
        private int _frames;
        private double _drawMs, _tickMs, _simSeconds;
        private readonly Stopwatch _tickClock = new Stopwatch();
        private readonly Stopwatch _statsClock = Stopwatch.StartNew();

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

            lblRenderer = new Label { AutoSize = true, Location = new Point(label4.Left, lblFocus.Bottom + 13), ForeColor = Color.DimGray };
            Controls.Add(lblRenderer);
            StartRenderer();

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



        // The GL control takes keyboard focus, and arrow keys never reach KeyDown as dialog keys: handle them here.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right)
            {
                Form1_KeyDown(this, new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Switches drawing to OpenGL when a 3.3 core context can be created; otherwise keeps the GDI
        /// renderer and says why in the renderer label. A shader error is an internal error: reported, then GDI.
        /// </summary>
        private void StartRenderer()
        {
            string choice = (Environment.GetEnvironmentVariable(RendererEnvVar) ?? "").Trim().ToLowerInvariant();
            if (choice == "gdi")
            {
                SetRendererLabel("GDI", "GDI renderer forced by " + RendererEnvVar + "=gdi.");
                return;
            }

            var version = choice == "simulate-gl-failure" ? new Version(9, 9) : new Version(3, 3);
            try
            {
                // Probe first: a GLControl's own context failure would surface as WinForms' unhandled-exception dialog.
                using (var probe = Sph.HeadlessGlContext.TryCreate(version, out string why))
                    if (probe == null) throw new InvalidOperationException(why);
                _gl = new GLControl(new GLControlSettings
                {
                    API = ContextAPI.OpenGL,
                    APIVersion = version,
                    Profile = ContextProfile.Core,
                    Flags = ContextFlags.ForwardCompatible,
                })
                {
                    Bounds = pictureBox1.Bounds,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                };
                Controls.Add(_gl);
                _gl.CreateControl(); // creates the context now, so a failure lands here
                _gl.MakeCurrent();
            }
            catch (Exception ex)
            {
                DropGlControl();
                SetRendererLabel("GDI (OpenGL unavailable)", "OpenGL 3.3 could not be initialised, so the GDI renderer is used.\n\nDetails: " + ex.Message);
                return;
            }

            try
            {
                _glRenderer = new ClassicGlRenderer();
                _glRenderer.Initialize();
                _glRenderer.SetWalls(BoundaryPlanes);
            }
            catch (Exception ex)
            {
                // The context is fine: a failure here is a bug in our shaders, not the machine.
                MessageBox.Show(this,
                    "The classic simulator's OpenGL renderer could not start because of an internal shader error (OpenGL 3.3 itself is available). " +
                    "It uses the GDI renderer instead.\n\nDetails: " + ex.Message,
                    "Internal shader error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                try { _glRenderer?.Dispose(); } catch { }
                _glRenderer = null;
                DropGlControl();
                SetRendererLabel("GDI (internal shader error)", ex.Message);
                return;
            }

            _glFrame.Bounds = new System.Numerics.Vector3(sceneBounds.x, sceneBounds.y, sceneBounds.z);
            _glFrame.LightMarkerRadius = 5f / sceneScale;
            foreach (var l in pointLights)
                _glFrame.Lights.Add(new ClassicLight { Position = new System.Numerics.Vector3(l.Center.x, l.Center.y, l.Center.z), Intensity = (float)l.LightIntensity, Ambience = (float)l.Ambience });

            _gl.Paint += (s, e) => RenderGl();
            _gl.Resize += (s, e) => _gl.Invalidate();
            pictureBox1.Visible = false;
            foreach (var label in new Control[] { label1, lblActivePoints, label3, lblGravity, label4, lblFocus, lblRenderer })
                label.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _glReady = true;
            SetRendererLabel("OpenGL", "OpenGL renderer: " + _glRenderer.GlVersion);
        }

        private void DropGlControl()
        {
            if (_gl == null) return;
            try { Controls.Remove(_gl); _gl.Dispose(); } catch { }
            _gl = null;
        }

        private void SetRendererLabel(string name, string details)
        {
            _rendererName = name;
            lblRenderer.Text = "Renderer: " + name;
            _rendererTip.SetToolTip(lblRenderer, details);
        }

        // Frame rate and costs, refreshed twice a second: frames drawn per second, the CPU time of
        // drawing one, and of the last timer tick (physics plus, for GDI, drawing), and the pace:
        // simulated seconds per real second.
        private void CountFrame(double drawMs)
        {
            _frames++;
            _drawMs += drawMs;
            double secs = _statsClock.Elapsed.TotalSeconds;
            if (secs < 0.5) return;
            lblRenderer.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Renderer: {0}\n{1:0} fps, draw {2:0.0} ms\ntick {3:0.0} ms\npace {4:0.00} sim s/s", _rendererName, _frames / secs, _drawMs / _frames, _tickMs, _simSeconds / secs);
            _frames = 0;
            _drawMs = 0;
            _simSeconds = 0;
            _statsClock.Restart();
        }

        // Snapshot of the scene for the next GL paint, taken on the tick (as the GDI renderer drew on
        // the tick). A collision flash is shown once and cleared, as before.
        private void CaptureGlFrame()
        {
            _glFrame.Focus = focus;
            _glFrame.Spheres.Clear();
            foreach (var point in GravityEngine.Instance.Points.Where(x => x.IsAlive))
            {
                _glFrame.Spheres.Add(new ClassicSphere
                {
                    Position = new System.Numerics.Vector3(point.Position.x, point.Position.y, point.Position.z),
                    Radius = point.Radius,
                    Flash = point.PointCollision,
                });
                point.PointCollision = false;
            }
        }

        private void RenderGl()
        {
            if (!_glReady) return;
            var sw = Stopwatch.StartNew();
            _gl.MakeCurrent();
            _glRenderer.Render(_glFrame, _gl.ClientSize.Width, _gl.ClientSize.Height);
            double ms = sw.Elapsed.TotalMilliseconds;
            _gl.SwapBuffers();
            CountFrame(ms);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            t?.Stop();
            if (_glReady)
            {
                _glReady = false;
                _gl.MakeCurrent();
                _glRenderer.Dispose();
            }
        }

        public int ActivePoints => GravityEngine.Instance.Points.Count(x => x.IsAlive);

        private void SetupPoints()
        {
            GravityEngine.Instance.SetupPoints(points, sceneBounds.x, sceneBounds.y, sceneBounds.z, maxRadius, density, initialVelocity);
        }

        private void ProcessPositions()
        {
            // GDI keeps its fixed timeSkip per tick: its own drawing time sets the pace, as before. OpenGL
            // ticks far more often, so it steps by the real time since the last tick at ClassicPace's rate,
            // capped at the GDI step.
            float step = timeSkip;
            if (_glReady)
            {
                step = ClassicPace.Step(_tickClock.Elapsed.TotalSeconds, stepsPerFrame * timeSkip) / stepsPerFrame;
                _tickClock.Restart();
            }
            GravityEngine.Instance.ProcessPositions(stepsPerFrame, step, globalGravity, wallElasticity, funLevel, true);
            _simSeconds += stepsPerFrame * step;
        }




        string mode = "perspective";// "perspective"; //orthographic

        //float focusInc = 0.01f;
        private void T_Tick(object sender, EventArgs e)
        {
            var tick = Stopwatch.StartNew();
            ProcessPositions();
            if (_glReady)
            {
                CaptureGlFrame();
                _gl.Invalidate();
            }
            else
            {
                var draw = Stopwatch.StartNew();
                RenderGdi();
                CountFrame(draw.Elapsed.TotalMilliseconds);
            }

            lblActivePoints.BeginInvoke((Action)(() =>
            {
                lblActivePoints.Text = ActivePoints.ToString();
            }));
            _tickMs = tick.Elapsed.TotalMilliseconds;
        }

        private void RenderGdi()
        {
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
