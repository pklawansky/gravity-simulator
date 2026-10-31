using System;
using System.Drawing;
using System.Windows.Forms;
using GravitySimulator.Planets;

namespace GravitySimulator
{
    /// <summary>Startup chooser between the classic simulator and planet formation mode.</summary>
    public sealed class LauncherForm : Form
    {
        public LauncherForm()
        {
            Text = "Gravity Simulator";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 210);

            var title = new Label
            {
                Text = "Choose a mode",
                Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 18),
            };
            var classic = new Button
            {
                Text = "Classic simulator",
                Location = new Point(20, 60),
                Size = new Size(380, 50),
            };
            var planets = new Button
            {
                Text = "Planet formation",
                Location = new Point(20, 125),
                Size = new Size(380, 50),
            };
            classic.Click += (s, e) => Open(() => new Form1());
            planets.Click += (s, e) => Open(() => new PlanetForm());
            Controls.AddRange(new Control[] { title, classic, planets });
        }

        private void Open(Func<Form> create)
        {
            Form form = null;
            try
            {
                form = create();
                form.FormClosed += (s, e) => Show();
                Hide();
                form.Show(); // creates handles (and, for planets, the GL context)
            }
            catch (Exception ex)
            {
                form?.Dispose();
                Show();
                MessageBox.Show(this, "Could not open this mode:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
