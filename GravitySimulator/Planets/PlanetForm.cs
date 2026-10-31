using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using OpenTK.Windowing.Common;
using OpenTK.GLControl;

namespace GravitySimulator.Planets
{
    /// <summary>
    /// Planet formation mode: GL view of the simulation plus a side panel of controls and readouts.
    /// The simulation runs on its own thread; this form only reads published snapshots/stats.
    /// </summary>
    public sealed class PlanetForm : Form
    {
        private readonly PlanetSimulation _sim = new PlanetSimulation(new SimulationParameters());
        private readonly PlanetRenderer _renderer = new PlanetRenderer();
        private readonly OrbitCamera _camera = new OrbitCamera();
        private readonly Timer _frameTimer = new Timer { Interval = 15 };

        private GLControl _gl;
        private bool _glReady;
        private bool _screenshotRequested;

        private ComboBox _preset;
        private NumericUpDown _count, _mergeK, _restitution;
        private TrackBar _timeSpeed;
        private Label _timeSpeedLabel, _readout;
        private Button _start, _pause, _reset, _screenshot;

        private Point _lastMouse;
        private bool _dragging;
        private int _frames;
        private double _fps;
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

        public PlanetForm()
        {
            Text = "Gravity Simulator - Planet formation";
            ClientSize = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.Gainsboro;

            _gl = new GLControl(new GLControlSettings
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),
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

            _frameTimer.Tick += (s, e) => { if (_glReady) _gl.Invalidate(); UpdateReadout(); };
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
        }

        private Control BuildSidePanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 250,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(10),
                AutoScroll = true,
                BackColor = Color.FromArgb(28, 28, 36),
            };

            _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            _preset.Items.AddRange(new object[] { "Disc around a star", "Spinning cloud" });
            _preset.SelectedIndex = 0;

            _count = new NumericUpDown { Minimum = 10_000, Maximum = 100_000, Increment = 10_000, Value = 50_000, Width = 220, ThousandsSeparator = true };

            _timeSpeed = new TrackBar { Minimum = -4, Maximum = 4, Value = 0, TickFrequency = 1, Width = 220 };
            _timeSpeedLabel = new Label { AutoSize = true };
            _timeSpeed.ValueChanged += (s, e) => ApplyParameters();

            _mergeK = new NumericUpDown { Minimum = 0.1m, Maximum = 10m, Increment = 0.1m, DecimalPlaces = 1, Value = 1.0m, Width = 220 };
            _mergeK.ValueChanged += (s, e) => ApplyParameters();
            _restitution = new NumericUpDown { Minimum = 0m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.5m, Width = 220 };
            _restitution.ValueChanged += (s, e) => ApplyParameters();

            _start = new Button { Text = "Start", Width = 105 };
            _pause = new Button { Text = "Pause", Width = 105 };
            _reset = new Button { Text = "Reset", Width = 105 };
            _screenshot = new Button { Text = "Save screenshot", Width = 105 };
            foreach (var b in new[] { _start, _pause, _reset, _screenshot })
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Color.FromArgb(48, 48, 60);
            }
            _start.Click += (s, e) => _sim.Start();
            _pause.Click += (s, e) => _sim.Pause();
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
                Caption("Time speed"), _timeSpeed, _timeSpeedLabel,
                Caption("Merge threshold k (x escape speed)"), _mergeK,
                Caption("Restitution e (bounces)"), _restitution,
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
                // Forces the GL context to be created now so a failure is reported here, not mid-paint.
                _gl.CreateControl();
                _gl.MakeCurrent();
                _renderer.Initialize();
                _glReady = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Planet formation needs OpenGL 3.3 (core profile), which could not be initialised on this machine.\n\n" +
                    "Details: " + ex.Message,
                    "OpenGL unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                BeginInvoke(new Action(Close));
                return;
            }

            ApplyParameters();
            ResetSimulation();
            _frameTimer.Start();
        }

        private void ResetSimulation()
        {
            bool wasRunning = _sim.IsRunning;
            _sim.Pause();
            var kind = _preset.SelectedIndex == 1 ? PresetKind.SpinningCloud : PresetKind.DiscAroundStar;
            _sim.Load(Presets.Create(kind, (int)_count.Value, Environment.TickCount));
            _camera.Distance = kind == PresetKind.DiscAroundStar ? 5f : 4f;
            if (wasRunning) _sim.Start();
            _gl.Invalidate();
        }

        private void ApplyParameters()
        {
            float speed = MathF.Pow(2f, _timeSpeed.Value / 2f); // 0.25x .. 4x
            _timeSpeedLabel.Text = speed.ToString("0.##x", CultureInfo.InvariantCulture);
            var p = _sim.Parameters;
            p.TimeScale = speed;
            p.MergeThreshold = (float)_mergeK.Value;
            p.Restitution = (float)_restitution.Value;
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
            _renderer.Upload(_sim.AcquireLatest());
            _renderer.Render(_camera, _gl.ClientSize.Width, _gl.ClientSize.Height);
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
                string path = Path.Combine(dir, "planets-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png");
                using (var bmp = _renderer.ReadBackBuffer(_gl.ClientSize.Width, _gl.ClientSize.Height))
                    bmp.Save(path, ImageFormat.Png);
                _lastScreenshot = path;
            }
            catch (Exception ex)
            {
                _lastScreenshot = "screenshot failed: " + ex.Message;
            }
        }

        private string _lastScreenshot;

        private void UpdateReadout()
        {
            var s = _sim.Stats;
            var sb = new StringBuilder();
            sb.AppendLine(_sim.IsRunning ? "Running" : "Paused");
            sb.AppendLine($"Bodies    {s.Count,10:N0}");
            sb.AppendLine($"Steps/s   {s.StepsPerSecond,10:F1}");
            sb.AppendLine($"FPS       {_fps,10:F1}");
            sb.AppendLine($"Sim time  {s.SimTime,10:F2}");
            sb.AppendLine($"Steps     {s.StepCount,10:N0}");
            sb.AppendLine($"Merges    {s.Merges,10:N0}");
            sb.AppendLine($"Bounces   {s.Bounces,10:N0}");
            sb.AppendLine($"Escaped   {s.Escaped,10:N0}");
            sb.AppendLine();
            sb.AppendLine("Largest bodies (grains):");
            float grain = s.GrainMass > 0f ? s.GrainMass : 1f;
            if (s.StarMass > 0f) sb.AppendLine($"  star  {s.StarMass / grain,12:N0}");
            int rank = 1;
            foreach (float m in s.LargestMasses.Take(5))
                sb.AppendLine($"  #{rank++}    {m / grain,12:N0}");
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
            _sim.Dispose(); // stops and joins the simulation thread
            if (_glReady)
            {
                _gl.MakeCurrent();
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
