using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using GravitySimulator.Planets;
using OpenTK.GLControl;
using OpenTK.Windowing.Common;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// Gas dynamics (SPH) mode: GPU smoothed-particle hydrodynamics with self-gravity, sinks and the
    /// stellar model. Needs OpenGL 4.3; the simulation and the rendering share the GL context on the
    /// UI thread (each timer tick runs as many steps as fit in a short budget, then draws a frame).
    /// </summary>
    public sealed class SphForm : Form
    {
        private readonly SphSimulation _sim = new SphSimulation();
        private readonly SphRenderer _renderer = new SphRenderer();
        private readonly OrbitCamera _camera = new OrbitCamera();
        private readonly Timer _frameTimer = new Timer { Interval = 10 };

        private GLControl _gl;
        private bool _glReady, _running, _screenshotRequested;
        private string _gpuName = "", _lastScreenshot, _error;

        private ComboBox _preset, _colorMode;
        private NumericUpDown _count, _soundSpeed, _rhoCrit, _gamma, _cooling;
        private TrackBar _timeSpeed;
        private Label _timeSpeedLabel, _readout, _soundSpeedCaption, _rhoCritCaption, _gammaCaption, _coolingCaption;
        private Button _start, _pause, _reset, _screenshot;

        private Point _lastMouse;
        private bool _dragging;
        private int _frames;
        private double _fps;
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

        private static readonly SphPresetKind[] PresetOrder =
            { SphPresetKind.CollapsingCloud, SphPresetKind.CollidingClouds, SphPresetKind.TidalDisruption };

        private SphPresetKind SelectedPreset => PresetOrder[Math.Max(0, _preset.SelectedIndex)];

        public SphForm()
        {
            Text = "Gravity Simulator - Gas dynamics (SPH)";
            ClientSize = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.Gainsboro;

            _gl = new GLControl(new GLControlSettings
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(4, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
            })
            {
                Dock = DockStyle.Fill,
            };
            _gl.Paint += (s, e) => RenderFrame();
            _gl.Resize += (s, e) => _gl.Invalidate();
            _gl.MouseDown += (s, e) => { _dragging = e.Button == MouseButtons.Left; _lastMouse = e.Location; };
            _gl.MouseUp += (s, e) => _dragging = false;
            _gl.MouseMove += OnGlMouseMove;
            _gl.MouseWheel += (s, e) => _camera.Zoom(e.Delta / 120f);

            Controls.Add(_gl);
            Controls.Add(BuildSidePanel());

            _frameTimer.Tick += (s, e) => OnTick();
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
        }

        private Control BuildSidePanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 260,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(10),
                AutoScroll = true,
                BackColor = Color.FromArgb(28, 28, 36),
            };

            _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            foreach (var k in PresetOrder) _preset.Items.Add(SphPresets.Title(k));
            _preset.SelectedIndex = 0;
            _preset.SelectedIndexChanged += (s, e) => ApplyPresetDefaults();

            _count = new NumericUpDown { Minimum = SphPresets.MinCount, Maximum = SphPresets.MaxCount, Increment = 50_000, Value = SphPresets.DefaultCount(SphPresetKind.CollapsingCloud), Width = 230, ThousandsSeparator = true };

            _timeSpeed = new TrackBar { Minimum = -4, Maximum = 2, Value = 0, TickFrequency = 1, Width = 230 };
            _timeSpeedLabel = new Label { AutoSize = true };
            _timeSpeed.ValueChanged += (s, e) => ApplyParameters();

            _colorMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            _colorMode.Items.AddRange(new object[] { "Temperature", "Density" });
            _colorMode.SelectedIndex = 0;
            _colorMode.SelectedIndexChanged += (s, e) => { _renderer.ColorMode = _colorMode.SelectedIndex; _gl.Invalidate(); };

            _soundSpeed = new NumericUpDown { Minimum = 0.1m, Maximum = 50m, Increment = 0.5m, DecimalPlaces = 2, Value = 2m, Width = 230 };
            _soundSpeed.ValueChanged += (s, e) => ApplyParameters();
            _rhoCrit = new NumericUpDown { Minimum = 1m, Maximum = 10000m, Increment = 25m, DecimalPlaces = 0, Value = (decimal)SphPresets.CloudCriticalDensityFactor, Width = 230 };
            _rhoCrit.ValueChanged += (s, e) => ApplyParameters();
            _gamma = new NumericUpDown { Minimum = 1.01m, Maximum = 2m, Increment = 0.05m, DecimalPlaces = 3, Value = 1.667m, Width = 230 };
            _gamma.ValueChanged += (s, e) => ApplyParameters();
            _cooling = new NumericUpDown { Minimum = 0m, Maximum = 5m, Increment = 0.05m, DecimalPlaces = 3, Value = (decimal)SphPresets.CollideCoolingBeta, Width = 230 };
            _cooling.ValueChanged += (s, e) => ApplyParameters();
            _soundSpeedCaption = Caption("Isothermal sound speed c_s");
            _rhoCritCaption = Caption("ρ_crit (x mean cloud density)");
            _gammaCaption = Caption("Adiabatic index γ");
            _coolingCaption = Caption("Cooling time (x free-fall time, 0 = off)");

            _start = new Button { Text = "Start", Width = 110 };
            _pause = new Button { Text = "Pause", Width = 110 };
            _reset = new Button { Text = "Reset", Width = 110 };
            _screenshot = new Button { Text = "Save screenshot", Width = 110 };
            foreach (var b in new[] { _start, _pause, _reset, _screenshot })
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Color.FromArgb(48, 48, 60);
            }
            _start.Click += (s, e) => _running = true;
            _pause.Click += (s, e) => _running = false;
            _reset.Click += (s, e) => ResetSimulation();
            _screenshot.Click += (s, e) => { _screenshotRequested = true; _gl.Invalidate(); };

            var buttons1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            buttons1.Controls.AddRange(new Control[] { _start, _pause });
            var buttons2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            buttons2.Controls.AddRange(new Control[] { _reset, _screenshot });

            _readout = new Label { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 8.5f), Margin = new Padding(3, 12, 3, 3) };

            panel.Controls.AddRange(new Control[]
            {
                Caption("Preset (applies on Reset)"), _preset,
                Caption("Particles (applies on Reset)"), _count,
                Caption("Time speed (Courant factor)"), _timeSpeed, _timeSpeedLabel,
                Caption("Colour"), _colorMode,
                _soundSpeedCaption, _soundSpeed,
                _rhoCritCaption, _rhoCrit,
                _gammaCaption, _gamma,
                _coolingCaption, _cooling,
                buttons1, buttons2,
                Caption("Drag to orbit, wheel to zoom"),
                _readout,
            });
            return panel;
        }

        private static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(3, 10, 3, 2), ForeColor = Color.Silver };

        private void OnFormLoad(object sender, EventArgs e)
        {
            try
            {
                _gl.CreateControl();
                _gl.MakeCurrent();
                var version = OpenTK.Graphics.OpenGL4.GL.GetString(OpenTK.Graphics.OpenGL4.StringName.Version) ?? "";
                _gpuName = OpenTK.Graphics.OpenGL4.GL.GetString(OpenTK.Graphics.OpenGL4.StringName.Renderer) ?? "";
                OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.MajorVersion, out int major);
                OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.MinorVersion, out int minor);
                if (major < 4 || (major == 4 && minor < 3))
                    throw new InvalidOperationException("The driver gave OpenGL " + version + ".");
                _renderer.Initialize();
                _glReady = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Gas dynamics (SPH) needs OpenGL 4.3 (compute shaders), which could not be initialised on this machine. " +
                    "The other modes only need OpenGL 3.3 and still work.\n\nDetails: " + ex.Message,
                    "OpenGL 4.3 unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                BeginInvoke(new Action(Close));
                return;
            }

            ApplyPresetDefaults();
            ResetSimulation();
            _frameTimer.Start();
        }

        private void ResetSimulation()
        {
            if (!_glReady) return;
            _gl.MakeCurrent();
            var kind = SelectedPreset;
            try
            {
                Cursor = Cursors.WaitCursor;
                var scenario = SphPresets.Create(kind, (int)_count.Value, Environment.TickCount);
                _sim.Load(scenario);
                _renderer.Configure(scenario);
                _camera.Distance = scenario.CameraDistance;
                ApplyParameters();
                _error = null;
            }
            catch (Exception ex)
            {
                _running = false;
                _error = ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            _gl.Invalidate();
        }

        // Live parameters: time speed and the EOS of the running preset.
        private void ApplyParameters()
        {
            float speed = MathF.Pow(2f, _timeSpeed.Value / 2f); // 0.25x .. 2x
            _timeSpeedLabel.Text = speed.ToString("0.##x", CultureInfo.InvariantCulture) + "  (C = " + (0.3f * speed).ToString("0.###", CultureInfo.InvariantCulture) + ")";
            var sc = _sim.Scenario;
            if (sc == null) return;
            var p = sc.Parameters;
            p.TimeScale = speed;
            if (p.Eos == EquationOfState.Barotropic)
            {
                p.SoundSpeed = (float)_soundSpeed.Value;
                p.CriticalDensity = (float)_rhoCrit.Value * MeanDensity(sc.Kind);
            }
            else
            {
                p.Gamma = (float)_gamma.Value;
                if (_cooling.Visible) p.CoolingBeta = (float)_cooling.Value;
            }
        }

        private static float MeanDensity(SphPresetKind kind) => kind switch
        {
            SphPresetKind.CollidingClouds => SphPresets.CollideMass / (4f / 3f * MathF.PI * MathF.Pow(SphPresets.CollideRadius, 3)),
            _ => SphPresets.CloudMass / (4f / 3f * MathF.PI * MathF.Pow(SphPresets.CloudRadius, 3)),
        };

        private void ApplyPresetDefaults()
        {
            var kind = SelectedPreset;
            _count.Value = Math.Clamp(SphPresets.DefaultCount(kind), (int)_count.Minimum, (int)_count.Maximum);
            bool baro = kind == SphPresetKind.CollapsingCloud;
            _soundSpeedCaption.Visible = _soundSpeed.Visible = baro;
            _rhoCritCaption.Visible = _rhoCrit.Visible = baro;
            _gammaCaption.Visible = _gamma.Visible = !baro;
            _coolingCaption.Visible = _cooling.Visible = kind == SphPresetKind.CollidingClouds;
            _soundSpeed.Value = (decimal)SphPresets.CloudSoundSpeed;
            _rhoCrit.Value = (decimal)SphPresets.CloudCriticalDensityFactor;
            _gamma.Value = 1.667m;
            _cooling.Value = (decimal)SphPresets.CollideCoolingBeta;
        }

        private void OnTick()
        {
            if (!_glReady) return;
            if (_running && _error == null)
            {
                _gl.MakeCurrent();
                var budget = Stopwatch.StartNew();
                try
                {
                    do _sim.Step();
                    while (budget.ElapsedMilliseconds < 25);
                }
                catch (Exception ex)
                {
                    _running = false;
                    _error = ex.Message;
                }
            }
            _gl.Invalidate();
            UpdateReadout();
        }

        private void OnGlMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _camera.Rotate(e.X - _lastMouse.X, e.Y - _lastMouse.Y);
            _lastMouse = e.Location;
            _gl.Invalidate();
        }

        private void RenderFrame()
        {
            if (!_glReady) return;
            _gl.MakeCurrent();
            _renderer.Render(_sim, _camera, _gl.ClientSize.Width, _gl.ClientSize.Height);
            if (_screenshotRequested)
            {
                _screenshotRequested = false;
                SaveScreenshot();
            }
            _gl.SwapBuffers();

            _frames++;
            double secs = _fpsClock.Elapsed.TotalSeconds;
            if (secs >= 0.5)
            {
                _fps = _frames / secs;
                _frames = 0;
                _fpsClock.Restart();
            }
        }

        private void SaveScreenshot()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GravitySimulator");
                Directory.CreateDirectory(dir);
                string tag = SelectedPreset switch
                {
                    SphPresetKind.CollidingClouds => "collide",
                    SphPresetKind.TidalDisruption => "tde",
                    _ => "collapse",
                };
                string path = Path.Combine(dir, "sph-" + tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png");
                using (var bmp = _renderer.ReadBackBuffer(_gl.ClientSize.Width, _gl.ClientSize.Height))
                    bmp.Save(path, ImageFormat.Png);
                _lastScreenshot = path;
            }
            catch (Exception ex)
            {
                _lastScreenshot = "screenshot failed: " + ex.Message;
            }
        }

        private void UpdateReadout()
        {
            var s = _sim.Stats;
            var sc = _sim.Scenario;
            var c = s.ClassCounts;
            var sb = new StringBuilder();
            sb.AppendLine(_running ? "Running" : "Paused");
            if (_error != null) sb.AppendLine("Error: " + _error);
            sb.AppendLine($"Gas       {s.Gas,10:N0}");
            sb.AppendLine($"Sinks     {s.Sinks,10:N0}");
            if (c.Length >= 6)
            {
                sb.AppendLine($" protostars {c[(int)BodyClass.Planet],8:N0}");
                sb.AppendLine($" stars      {c[(int)BodyClass.Star],8:N0}");
                sb.AppendLine($" remnants   {c[(int)BodyClass.WhiteDwarf] + c[(int)BodyClass.NeutronStar],8:N0}");
                sb.AppendLine($" black holes{c[(int)BodyClass.BlackHole],8:N0}");
            }
            sb.AppendLine($"Steps/s   {s.StepsPerSecond,10:F1}");
            sb.AppendLine($"FPS       {_fps,10:F1}");
            sb.AppendLine($"Δt        {s.Dt,10:G3}");
            sb.AppendLine($"Steps     {s.Steps,10:N0}");
            if (sc != null && sc.Units == UnitSystem.SolarAu)
                sb.AppendLine($"Time      {s.SimYears * 365.25,7:F2} days");
            else
                sb.AppendLine($"Time      {s.SimYears / 1e6,7:F3} Myr");
            sb.AppendLine($"E total   {s.TotalEnergy,10:G4}");
            sb.AppendLine($" kinetic  {s.Kinetic,10:G4}");
            sb.AppendLine($" thermal  {s.Thermal,10:G4}");
            sb.AppendLine($" potential{s.Potential,10:G4}");
            sb.AppendLine($"Sinks made    {s.SinksCreated,6:N0}");
            sb.AppendLine($"Stars formed  {s.StarsFormed,6:N0}");
            sb.AppendLine($"Supernovae    {s.Supernovae,6:N0}");
            sb.AppendLine($"Top sink  {s.HeaviestSink,7:G3} M☉");
            var t = s.Times;
            sb.AppendLine();
            sb.AppendLine("GPU ms/step");
            sb.AppendLine($" grid {t.Grid,5:F1} dens {t.Density,5:F1}");
            sb.AppendLine($" force{t.Force,5:F1} grav {t.Gravity,5:F1}");
            sb.AppendLine($"CPU tree  {t.TreeBuild,5:F1} ms");
            sb.AppendLine();
            sb.AppendLine("GPU: " + _gpuName);
            if (_lastScreenshot != null)
            {
                sb.AppendLine();
                sb.AppendLine("Saved: " + Path.GetFileName(_lastScreenshot));
            }
            _readout.Text = sb.ToString();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _frameTimer.Stop();
            if (_glReady)
            {
                _gl.MakeCurrent();
                _sim.Dispose();
                _renderer.Dispose();
                _glReady = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _frameTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
