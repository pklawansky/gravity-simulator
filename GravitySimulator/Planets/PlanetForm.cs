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

        private ComboBox _preset, _collisionModel;
        private NumericUpDown _count, _mergeK, _restitution;
        private TrackBar _timeSpeed, _discMass, _stellarAccel;
        private Label _timeSpeedLabel, _discMassLabel, _readout, _stellarAccelLabel, _discMassCaption, _stellarAccelCaption;
        private Label _mergeKCaption, _restitutionCaption;
        private Button _start, _pause, _reset, _screenshot;

        private Point _lastMouse;
        private bool _dragging;
        private int _frames;
        private double _fps;
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

        // Disc-mass slider stops, as a fraction of the star's mass.
        private static readonly float[] DiscMassSteps = { 0.001f, 0.005f, 0.01f, 0.02f, 0.05f, 0.10f, 0.20f, 0.30f };
        private const int DefaultDiscMassStep = 5;

        // Stellar time acceleration slider stops (cosmic presets).
        private static readonly float[] StellarAccelSteps = { 1f, 2f, 3f, 5f, 10f, 20f, 50f, 100f, 200f };

        private static readonly PresetKind[] PresetOrder =
            { PresetKind.DiscAroundStar, PresetKind.SpinningCloud, PresetKind.StarFormingCloud, PresetKind.DenseStarCluster };

        private PresetKind SelectedPreset => PresetOrder[Math.Max(0, _preset.SelectedIndex)];

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
            _preset.Items.AddRange(new object[] { "Disc around a star", "Spinning cloud", "Star-forming cloud (M\u2609, pc)", "Dense star cluster (M\u2609, pc)" });
            _preset.SelectedIndex = 0;

            _count = new NumericUpDown { Minimum = 1_000, Maximum = SimulationParameters.DefaultMaxLiveParticles, Increment = 10_000, Value = 50_000, Width = 220, ThousandsSeparator = true };

            _timeSpeed = new TrackBar { Minimum = -4, Maximum = 4, Value = 0, TickFrequency = 1, Width = 220 };
            _timeSpeedLabel = new Label { AutoSize = true };
            _timeSpeed.ValueChanged += (s, e) => ApplyParameters();

            _discMass = new TrackBar { Minimum = 0, Maximum = DiscMassSteps.Length - 1, Value = DefaultDiscMassStep, TickFrequency = 1, Width = 220 };
            _discMassLabel = new Label { AutoSize = true };
            _discMass.ValueChanged += (s, e) => UpdateDiscMassLabel();
            UpdateDiscMassLabel();

            _stellarAccel = new TrackBar { Minimum = 0, Maximum = StellarAccelSteps.Length - 1, Value = 2, TickFrequency = 1, Width = 220 };
            _stellarAccelLabel = new Label { AutoSize = true };
            _stellarAccel.ValueChanged += (s, e) => ApplyParameters();

            _mergeK = new NumericUpDown { Minimum = 0.1m, Maximum = 10m, Increment = 0.1m, DecimalPlaces = 1, Value = 1.0m, Width = 220 };
            _mergeK.ValueChanged += (s, e) => ApplyParameters();
            _restitution = new NumericUpDown { Minimum = 0m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.5m, Width = 220 };
            _restitution.ValueChanged += (s, e) => ApplyParameters();

            _collisionModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            _collisionModel.Items.AddRange(new object[] { "Outcomes (Leinhardt & Stewart)", "Merge or bounce" });
            _collisionModel.SelectedIndex = 0;
            _collisionModel.SelectedIndexChanged += (s, e) => { ApplyParameters(); UpdateCollisionControls(); };

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

            _discMassCaption = Caption("Disc mass, % of star (applies on Reset)");
            _stellarAccelCaption = Caption("Stellar time acceleration");
            _preset.SelectedIndexChanged += (s, e) => ApplyPresetDefaults();

            panel.Controls.AddRange(new Control[]
            {
                Caption("Preset (applies on Reset)"), _preset,
                Caption("Particles (applies on Reset)"), _count,
                _discMassCaption, _discMass, _discMassLabel,
                _stellarAccelCaption, _stellarAccel, _stellarAccelLabel,
                Caption("Time speed"), _timeSpeed, _timeSpeedLabel,
                Caption("Collision model"), _collisionModel,
                _mergeKCaption = Caption("Merge threshold k (x escape speed)"), _mergeK,
                _restitutionCaption = Caption("Restitution e (bounces)"), _restitution,
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

            // GPU gravity when OpenGL 4.3 compute is available; otherwise the CPU tree (the readout says which),
            // with the lower CPU ceiling. The Particles box maximum follows the ceiling.
            bool gpu = _sim.TryEnableGpuGravity();
            _sim.Parameters.MaxLiveParticles = gpu ? SimulationParameters.DefaultMaxLiveParticles : SimulationParameters.CpuMaxLiveParticles;
            _count.Maximum = _sim.Parameters.MaxLiveParticles;
            ApplyPresetDefaults();
            ApplyParameters();
            ResetSimulation();
            _frameTimer.Start();
        }

        private void ResetSimulation()
        {
            bool wasRunning = _sim.IsRunning;
            _sim.Pause();
            var kind = SelectedPreset;
            Presets.Configure(kind, _sim.Parameters);
            ApplyParameters();
            _sim.Load(Presets.Create(kind, (int)_count.Value, Environment.TickCount, DiscMassSteps[_discMass.Value]));
            _camera.Distance = kind switch
            {
                PresetKind.DiscAroundStar => 5f,
                PresetKind.StarFormingCloud => 7f,
                PresetKind.DenseStarCluster => 2.5f,
                _ => 4f,
            };
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
            p.CollisionModel = _collisionModel.SelectedIndex == 1 ? CollisionModel.MergeOrBounce : CollisionModel.Outcomes;
            float accel = StellarAccelSteps[_stellarAccel.Value];
            _stellarAccelLabel.Text = accel.ToString("0.##", CultureInfo.InvariantCulture) + "x";
            if (p.Stellar != null) p.Stellar.TimeAcceleration = accel;
        }

        // When the preset selection changes: per-preset particle count, merge/bounce defaults, and
        // which preset-specific sliders are shown.
        private void ApplyPresetDefaults()
        {
            var kind = SelectedPreset;
            bool cosmic = Presets.IsCosmic(kind);
            _count.Increment = cosmic ? 1_000 : 10_000;
            _count.Value = Math.Clamp(Presets.DefaultCount(kind), (int)_count.Minimum, (int)_count.Maximum);
            _mergeK.Value = (decimal)Presets.DefaultMergeThreshold(kind);
            _restitution.Value = (decimal)Presets.DefaultRestitution(kind);
            if (cosmic)
            {
                int step = Array.IndexOf(StellarAccelSteps, Presets.DefaultTimeAcceleration(kind));
                _stellarAccel.Value = step >= 0 ? step : 2;
            }
            bool disc = kind == PresetKind.DiscAroundStar;
            _discMassCaption.Visible = _discMass.Visible = _discMassLabel.Visible = disc;
            _stellarAccelCaption.Visible = _stellarAccel.Visible = _stellarAccelLabel.Visible = cosmic;
            UpdateCollisionControls();
        }

        // Merge threshold k and restitution e only matter for merge-or-bounce. The cosmic presets keep
        // them visible: their gas and stars always merge or bounce, whatever the rocky-body model.
        private void UpdateCollisionControls()
        {
            bool show = _collisionModel.SelectedIndex == 1 || Presets.IsCosmic(SelectedPreset);
            _mergeKCaption.Visible = _mergeK.Visible = _restitutionCaption.Visible = _restitution.Visible = show;
        }

        private void UpdateDiscMassLabel() =>
            _discMassLabel.Text = (DiscMassSteps[_discMass.Value] * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

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
            sb.AppendLine(GravityReadout());
            var t = _sim.LastPhaseTimes;
            if (t.Total > 0)
            {
                sb.AppendLine($"Gravity ms{t.TreeBuild + t.Gravity,10:F0}"); // tree build + forces + kick
                sb.AppendLine($"Collide ms{t.Collisions,10:F0}");
            }
            sb.AppendLine($"Sim time  {s.SimTime,10:F2}");
            sb.AppendLine($"Steps     {s.StepCount,10:N0}");
            sb.AppendLine($"Merges    {s.Merges,10:N0}");
            sb.AppendLine($"Bounces   {s.Bounces,10:N0}");
            sb.AppendLine($"Escaped   {s.Escaped,10:N0}");
            if (_sim.Parameters.CollisionModel == CollisionModel.Outcomes)
            {
                sb.AppendLine($"Part. acc.{s.PartialAccretions,10:N0}");
                sb.AppendLine($"Hit && run {s.HitAndRuns,10:N0}"); // "&&": the label treats "&" as a mnemonic
                sb.AppendLine($"Erosions  {s.Erosions,10:N0}");
                sb.AppendLine($"Disrupted {s.Disruptions,10:N0}");
                sb.AppendLine($"Fragments {s.FragmentsCreated,10:N0}");
                if (s.FragmentsFoldedAtCeiling > 0) sb.AppendLine($"Folded    {s.FragmentsFoldedAtCeiling,10:N0}"); // at the live-count ceiling
            }
            sb.AppendLine();
            if (s.StellarModel && s.ClassCounts.Length >= 6)
            {
                var c = s.ClassCounts;
                sb.AppendLine($"Time      {s.SimYears / 1e6,7:F2} Myr");
                sb.AppendLine($"Gas       {c[(int)BodyClass.Gas],10:N0}");
                sb.AppendLine($"Planets   {c[(int)BodyClass.Planet],10:N0}");
                sb.AppendLine($"Stars     {c[(int)BodyClass.Star],10:N0}");
                sb.AppendLine($"W. dwarfs {c[(int)BodyClass.WhiteDwarf],10:N0}");
                sb.AppendLine($"N. stars  {c[(int)BodyClass.NeutronStar],10:N0}");
                sb.AppendLine($"B. holes  {c[(int)BodyClass.BlackHole],10:N0}");
                sb.AppendLine();
                sb.AppendLine($"Stars formed  {s.StarsFormed,6:N0}");
                sb.AppendLine($"Supernovae    {s.Supernovae,6:N0}");
                sb.AppendLine($"BHs formed    {s.BlackHolesFormed,6:N0}");
                sb.AppendLine($"BH mergers    {s.BlackHoleMergers,6:N0}");
                sb.AppendLine($"Top star  {s.HeaviestStar,7:F1} M\u2609");
                if (s.HeaviestBlackHole > 0f) sb.AppendLine($"Top BH    {s.HeaviestBlackHole,7:F1} M\u2609");
            }
            else
            {
                sb.AppendLine("Largest bodies (grains):");
                float grain = s.GrainMass > 0f ? s.GrainMass : 1f;
                if (s.StarMass > 0f) sb.AppendLine($"  star  {s.StarMass / grain,12:N0}");
                int rank = 1;
                foreach (float m in s.LargestMasses.Take(5))
                    sb.AppendLine($"  #{rank++}    {m / grain,12:N0}");
            }
            if (_lastScreenshot != null)
            {
                sb.AppendLine();
                sb.AppendLine("Saved: " + Path.GetFileName(_lastScreenshot));
            }
            _readout.Text = sb.ToString();
        }

        // "Gravity   GPU" / "Gravity   CPU", plus the reason on a second line when the GPU is unavailable.
        private string GravityReadout()
        {
            string backend = _sim.GravityBackend;
            if (_sim.GpuGravityActive) return "Gravity          GPU";
            int open = backend.IndexOf('(');
            if (open < 0) return "Gravity          CPU";
            string why = backend.Substring(open).Trim();
            if (why.Length > 34) why = why.Substring(0, 31) + "...)";
            return "Gravity          CPU" + Environment.NewLine + " " + why;
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
