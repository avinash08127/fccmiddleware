namespace Installer
{
    partial class FCCMiddleWareInstaller
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            bindingSource1 = new BindingSource(components);
            progressBar1 = new ProgressBar();
            lblStatus = new Label();
            btnCancel = new Button();
            panelInputs = new Panel();
            panelCertificate = new Panel();
            btnInstallCert = new Button();
            btnBrowseCertPath = new Button();
            txtCertTargetPath = new TextBox();
            lblChooseCert = new Label();
            btnInstall = new Button();
            label4 = new Label();
            txtPort = new TextBox();
            txtIP = new TextBox();
            label3 = new Label();
            label2 = new Label();
            txtConnStr = new TextBox();
            button1 = new Button();
            label1 = new Label();
            txtPath = new TextBox();
            btnFinish = new Button();
            ((System.ComponentModel.ISupportInitialize)bindingSource1).BeginInit();
            panelInputs.SuspendLayout();
            panelCertificate.SuspendLayout();
            SuspendLayout();
            // 
            // bindingSource1
            // 
            bindingSource1.CurrentChanged += bindingSource1_CurrentChanged;
            // 
            // progressBar1
            // 
            progressBar1.Location = new Point(58, 220);
            progressBar1.Name = "progressBar1";
            progressBar1.Size = new Size(762, 23);
            progressBar1.TabIndex = 10;
            progressBar1.Visible = false;
            progressBar1.Click += progressBar1_Click;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(360, 253);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(0, 23);
            lblStatus.TabIndex = 11;
            // 
            // btnCancel
            // 
            btnCancel.Location = new Point(762, 470);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(106, 33);
            btnCancel.TabIndex = 12;
            btnCancel.Text = "Cancle";
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.Visible = false;
            btnCancel.Click += btnCancel_Click;
            // 
            // panelInputs
            // 
           // panelInputs.Controls.Add(panelCertificate);
            panelInputs.Controls.Add(btnInstall);
            panelInputs.Controls.Add(label4);
            panelInputs.Controls.Add(txtPort);
            panelInputs.Controls.Add(txtIP);
            panelInputs.Controls.Add(label3);
            panelInputs.Controls.Add(label2);
            panelInputs.Controls.Add(txtConnStr);
            panelInputs.Controls.Add(button1);
            panelInputs.Controls.Add(label1);
            panelInputs.Controls.Add(txtPath);
            panelInputs.Location = new Point(58, 14);
            panelInputs.Name = "panelInputs";
            panelInputs.Size = new Size(777, 423);
            panelInputs.TabIndex = 13;
            // 
            // panelCertificate
            // 
            panelCertificate.Controls.Add(btnInstallCert);
            panelCertificate.Controls.Add(btnBrowseCertPath);
            panelCertificate.Controls.Add(txtCertTargetPath);
            panelCertificate.Controls.Add(lblChooseCert);
            panelCertificate.Location = new Point(0, 3);
            panelCertificate.Name = "panelCertificate";
            panelCertificate.Size = new Size(777, 420);
            panelCertificate.TabIndex = 15;
            // 
            // btnInstallCert
            // 
            btnInstallCert.Location = new Point(279, 239);
            btnInstallCert.Name = "btnInstallCert";
            btnInstallCert.Size = new Size(167, 49);
            btnInstallCert.TabIndex = 3;
            btnInstallCert.Text = "Install Certificate";
            btnInstallCert.UseVisualStyleBackColor = true;
            btnInstallCert.Click += btnInstallCert_Click;
            // 
            // btnBrowseCertPath
            // 
            btnBrowseCertPath.Location = new Point(543, 82);
            btnBrowseCertPath.Name = "btnBrowseCertPath";
            btnBrowseCertPath.Size = new Size(94, 29);
            btnBrowseCertPath.TabIndex = 2;
            btnBrowseCertPath.Text = "Browse ...";
            btnBrowseCertPath.UseVisualStyleBackColor = true;
            btnBrowseCertPath.Click += btnBrowseCertPath_Click;
            // 
            // txtCertTargetPath
            // 
            txtCertTargetPath.Location = new Point(282, 80);
            txtCertTargetPath.Name = "txtCertTargetPath";
            txtCertTargetPath.Size = new Size(255, 30);
            txtCertTargetPath.TabIndex = 1;
            // 
            // lblChooseCert
            // 
            lblChooseCert.AutoSize = true;
            lblChooseCert.Location = new Point(15, 83);
            lblChooseCert.Name = "lblChooseCert";
            lblChooseCert.Size = new Size(261, 23);
            lblChooseCert.TabIndex = 0;
            lblChooseCert.Text = "Choose certificate install location";
            lblChooseCert.Click += lblChooseCert_Click;
            // 
            // btnInstall
            // 
            btnInstall.Location = new Point(274, 354);
            btnInstall.Name = "btnInstall";
            btnInstall.Size = new Size(209, 52);
            btnInstall.TabIndex = 18;
            btnInstall.Text = "Install";
            btnInstall.UseVisualStyleBackColor = true;
            btnInstall.Click += btnInstall_Click;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(147, 262);
            label4.Name = "label4";
            label4.Size = new Size(41, 23);
            label4.TabIndex = 17;
            label4.Text = "Port";
            // 
            // txtPort
            // 
            txtPort.Location = new Point(306, 263);
            txtPort.Name = "txtPort";
            txtPort.Size = new Size(140, 30);
            txtPort.TabIndex = 16;
            // 
            // txtIP
            // 
            txtIP.Location = new Point(306, 206);
            txtIP.Name = "txtIP";
            txtIP.Size = new Size(231, 30);
            txtIP.TabIndex = 15;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(142, 206);
            label3.Name = "label3";
            label3.Size = new Size(90, 23);
            label3.TabIndex = 14;
            label3.Text = "IP Address";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(138, 145);
            label2.Name = "label2";
            label2.Size = new Size(147, 23);
            label2.TabIndex = 13;
            label2.Text = "Connection String";
            // 
            // txtConnStr
            // 
            txtConnStr.Location = new Point(306, 145);
            txtConnStr.Name = "txtConnStr";
            txtConnStr.Size = new Size(231, 30);
            txtConnStr.TabIndex = 12;
            // 
            // button1
            // 
            button1.Location = new Point(544, 82);
            button1.Name = "button1";
            button1.Size = new Size(93, 31);
            button1.TabIndex = 11;
            button1.Text = "Browse...";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(142, 85);
            label1.Name = "label1";
            label1.Size = new Size(134, 23);
            label1.TabIndex = 10;
            label1.Text = "Installation path";
            // 
            // txtPath
            // 
            txtPath.Location = new Point(306, 82);
            txtPath.Name = "txtPath";
            txtPath.Size = new Size(231, 30);
            txtPath.TabIndex = 9;
            // 
            // btnFinish
            // 
            btnFinish.Location = new Point(633, 470);
            btnFinish.Name = "btnFinish";
            btnFinish.Size = new Size(106, 33);
            btnFinish.TabIndex = 14;
            btnFinish.Text = "Finish";
            btnFinish.UseVisualStyleBackColor = true;
            btnFinish.Visible = false;
            btnFinish.Click += btnFinish_Click;
            // 
            // FCCMiddleWareInstaller
            // 
            AutoScaleDimensions = new SizeF(9F, 23F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(901, 519);
            Controls.Add(btnFinish);
            Controls.Add(panelInputs);
            Controls.Add(btnCancel);
            Controls.Add(progressBar1);
            Controls.Add(lblStatus);
            Controls.Add(panelCertificate);
            Font = new Font("Segoe UI", 10F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Name = "FCCMiddleWareInstaller";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "FCC MiddleWare Installer";
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)bindingSource1).EndInit();
            panelInputs.ResumeLayout(false);
            panelInputs.PerformLayout();
            panelCertificate.ResumeLayout(false);
            panelCertificate.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private BindingSource bindingSource1;
        private ProgressBar progressBar1;
        private Label lblStatus;
        private Button btnCancel;
        private Panel panelInputs;
        private Button btnInstall;
        private Label label4;
        private TextBox txtPort;
        private TextBox txtIP;
        private Label label3;
        private Label label2;
        private TextBox txtConnStr;
        private Button button1;
        private Label label1;
        private TextBox txtPath;
        private Button btnFinish;
        private Panel panelCertificate;
        private Label lblChooseCert;
        private Button btnBrowseCertPath;
        private TextBox txtCertTargetPath;
        private Button btnInstallCert;
    }
}
