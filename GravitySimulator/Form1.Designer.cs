namespace GravitySimulator
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.label1 = new System.Windows.Forms.Label();
            this.lblActivePoints = new System.Windows.Forms.Label();
            this.lblGravity = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.lblFocus = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.labelFPS = new System.Windows.Forms.Label();
            this.labelFPSMax = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.textBox1 = new System.Windows.Forms.TextBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.SuspendLayout();
            // 
            // pictureBox1
            // 
            this.pictureBox1.Location = new System.Drawing.Point(13, 13);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(600, 600);
            this.pictureBox1.TabIndex = 0;
            this.pictureBox1.TabStop = false;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(619, 13);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(72, 13);
            this.label1.TabIndex = 1;
            this.label1.Text = "Active Points:";
            // 
            // lblActivePoints
            // 
            this.lblActivePoints.AutoSize = true;
            this.lblActivePoints.Location = new System.Drawing.Point(698, 13);
            this.lblActivePoints.Name = "lblActivePoints";
            this.lblActivePoints.Size = new System.Drawing.Size(13, 13);
            this.lblActivePoints.TabIndex = 2;
            this.lblActivePoints.Text = "0";
            // 
            // lblGravity
            // 
            this.lblGravity.AutoSize = true;
            this.lblGravity.Location = new System.Drawing.Point(698, 26);
            this.lblGravity.Name = "lblGravity";
            this.lblGravity.Size = new System.Drawing.Size(13, 13);
            this.lblGravity.TabIndex = 4;
            this.lblGravity.Text = "0";
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(619, 26);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(43, 13);
            this.label3.TabIndex = 3;
            this.label3.Text = "Gravity:";
            // 
            // lblFocus
            // 
            this.lblFocus.AutoSize = true;
            this.lblFocus.Location = new System.Drawing.Point(698, 39);
            this.lblFocus.Name = "lblFocus";
            this.lblFocus.Size = new System.Drawing.Size(13, 13);
            this.lblFocus.TabIndex = 6;
            this.lblFocus.Text = "0";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(619, 39);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(36, 13);
            this.label4.TabIndex = 5;
            this.label4.Text = "Focus";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(619, 52);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(27, 13);
            this.label2.TabIndex = 7;
            this.label2.Text = "FPS";
            // 
            // labelFPS
            // 
            this.labelFPS.AutoSize = true;
            this.labelFPS.Location = new System.Drawing.Point(698, 52);
            this.labelFPS.Name = "labelFPS";
            this.labelFPS.Size = new System.Drawing.Size(13, 13);
            this.labelFPS.TabIndex = 8;
            this.labelFPS.Text = "0";
            // 
            // labelFPSMax
            // 
            this.labelFPSMax.AutoSize = true;
            this.labelFPSMax.Location = new System.Drawing.Point(698, 65);
            this.labelFPSMax.Name = "labelFPSMax";
            this.labelFPSMax.Size = new System.Drawing.Size(13, 13);
            this.labelFPSMax.TabIndex = 10;
            this.labelFPSMax.Text = "0";
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(619, 65);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(50, 13);
            this.label6.TabIndex = 9;
            this.label6.Text = "FPS Max";
            // 
            // textBox1
            // 
            this.textBox1.Location = new System.Drawing.Point(622, 82);
            this.textBox1.Multiline = true;
            this.textBox1.Name = "textBox1";
            this.textBox1.Size = new System.Drawing.Size(160, 198);
            this.textBox1.TabIndex = 11;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(794, 626);
            this.Controls.Add(this.textBox1);
            this.Controls.Add(this.labelFPSMax);
            this.Controls.Add(this.label6);
            this.Controls.Add(this.labelFPS);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.lblFocus);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.lblGravity);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.lblActivePoints);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.pictureBox1);
            this.Name = "Form1";
            this.Text = "Form1";
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label lblActivePoints;
        private System.Windows.Forms.Label lblGravity;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label lblFocus;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label labelFPS;
        private System.Windows.Forms.Label labelFPSMax;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.TextBox textBox1;
    }
}

