using Newtonsoft.Json.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Installer
{
    public partial class FCCMiddleWareInstaller : Form
    {
        CancellationTokenSource cts = new CancellationTokenSource();

        int totalFiles = 0;
        int processedFiles = 0;
        private bool replaceAllFiles = false;
        private bool skipAllFiles = false;

        public FCCMiddleWareInstaller()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            progressBar1.Visible = false;
            lblStatus.Visible = false;
            btnCancel.Visible = false;
            panelCertificate.Visible = false;
        }

        private void bindingSource1_CurrentChanged(object sender, EventArgs e)
        {

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void button1_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select installation folder";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtPath.Text = dialog.SelectedPath;
                }
            }
        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private async void btnInstall_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPath.Text))
            {
                MessageBox.Show("Please choose installation path.");
                return;
            }

            // UI changes before start
            panelInputs.Visible = false;
            btnInstall.Visible = false;
            btnCancel.Visible = true;

            progressBar1.Visible = true;
            progressBar1.Value = 0;
            progressBar1.Refresh();

            lblStatus.Visible = true;
            lblStatus.Text = "Preparing installation...";
            lblStatus.Refresh();

            await Task.Delay(100);
            try
            {
                //string sourceFolder = @"D:\PUMA\Code\Installer\pumaengergy-middleware-wss\DppMiddleWareService\bin\Debug\net8.0";
                string sourceFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppFiles");
                string configPath = Path.Combine(sourceFolder, "appsettings.json");

                UpdateConfig(configPath);

                totalFiles = CountFiles(sourceFolder);
                processedFiles = 0;

                lblStatus.Text = "Copying files...";
                lblStatus.Refresh();

                cts = new CancellationTokenSource();

                await Task.Run(() => CopyFiles(sourceFolder, txtPath.Text, cts.Token));

                progressBar1.Value = 100;
                lblStatus.Text = "Installation completed successfully!";

                MessageBox.Show("Installation Completed Successfully!",
                    "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Installation failed!";
                MessageBox.Show("Installation Failed: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                panelInputs.Visible = false;   // keep hidden
                btnCancel.Visible = false;
                progressBar1.Visible = false;

                lblStatus.Text = "Main installation completed! Continue to certificate setup.";
                lblStatus.Visible = false;

                panelCertificate.Visible = true;   // show certificate UI

                //btnFinish.Visible = true;             // show Finish button
                //progressBar1.Visible = false;         // hide progress bar now
                //lblStatus.Text = "Installation Completed Successfully!";
                //lblStatus.Visible = true;
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            cts.Cancel();
            lblStatus.Text = "Installation Cancelled!";
            btnCancel.Visible = false;
            btnInstall.Visible = true;
            panelInputs.Visible = true;
        }


        private int CountFiles(string path)
        {
            int count = Directory.GetFiles(path).Length;

            foreach (var dir in Directory.GetDirectories(path))
                count += CountFiles(dir);

            return count;
        }


        private void CopyFiles(string sourceDir, string targetDir, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (token.IsCancellationRequested) return;

                string destFile = Path.Combine(targetDir, Path.GetFileName(file));

                if (File.Exists(destFile))
                {
                    if (!replaceAllFiles && !skipAllFiles)
                    {
                        var result = MessageBox.Show(
                            $"File '{Path.GetFileName(file)}' already exists.\n\n" +
                            $"Replace = Yes\nSkip = No\nCancel = Cancel",
                            "File Exists",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Yes)
                        {
                            var applyAll = MessageBox.Show(
                                "Apply replace to all remaining files?",
                                "Replace All?",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);

                            if (applyAll == DialogResult.Yes)
                                replaceAllFiles = true;
                        }
                        else if (result == DialogResult.No)
                        {
                            var applyAll = MessageBox.Show(
                                "Apply skip to all remaining files?",
                                "Skip All?",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);

                            if (applyAll == DialogResult.Yes)
                                skipAllFiles = true;

                            processedFiles++;
                            UpdateProgress();
                            continue;
                        }
                        else if (result == DialogResult.Cancel)
                        {
                            cts.Cancel();
                            return;
                        }
                    }

                    if (skipAllFiles)
                    {
                        processedFiles++;
                        UpdateProgress();
                        continue;
                    }
                }

                File.Copy(file, destFile, true);

                processedFiles++;
                UpdateProgress();
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                if (token.IsCancellationRequested) return;

                CopyFiles(dir, Path.Combine(targetDir, Path.GetFileName(dir)), token);
            }
        }



        private void UpdateProgress()
        {
            int percent = (int)((processedFiles / (double)totalFiles) * 100);
            this.Invoke(new Action(() =>
            {
                progressBar1.Value = percent;
                lblStatus.Text = $"Processing... {percent}%";
            }));
        }



        private void UpdateConfig(string configPath)
        {
            string json = File.ReadAllText(configPath);

            JObject jsonObj = JObject.Parse(json);

            jsonObj["ConnectionStrings"]["DefaultConnection"] = txtConnStr.Text;
            jsonObj["WebSocketServer"]["Host"] = txtIP.Text;
            jsonObj["WebSocketServer"]["Port"] = txtPort.Text;

            File.WriteAllText(configPath, jsonObj.ToString());
        }

        private void panelInputs_Paint(object sender, PaintEventArgs e)
        {

        }

        private void btnFinish_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void progressBar1_Click(object sender, EventArgs e)
        {

        }

        private void lblChooseCert_Click(object sender, EventArgs e)
        {

        }

        private void btnBrowseCertPath_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select location to copy certificate";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtCertTargetPath.Text = dialog.SelectedPath;
                }
            }
        }

        private void InstallCertificateFile()
        {
            string sourceCertPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Certificates", "mycert.pfx");
            string destinationPath = Path.Combine(txtCertTargetPath.Text, "mycert.pfx");

            File.Copy(sourceCertPath, destinationPath, true);

            // Update config path
            string configPath = Path.Combine(txtPath.Text, "appsettings.json");
            UpdateCertificatePathInConfig(configPath, destinationPath);
        }

        private void UpdateCertificatePathInConfig(string configPath, string certPath)
        {
            string json = File.ReadAllText(configPath);
            JObject jsonObj = JObject.Parse(json);

            jsonObj["WebSocketSettings"]["CertificatePath"] = certPath;

            File.WriteAllText(configPath, jsonObj.ToString());
        }

        private void btnInstallCert_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(txtCertTargetPath.Text))
                {
                    MessageBox.Show("Please choose a folder for certificate installation");
                    return;
                }

                string certName = "FCCMiddlewareService";
                string certPassword = "Brain@123";
                string certFilePath = Path.Combine(txtCertTargetPath.Text, "mycert.pfx");
                string configPath = Path.Combine(txtPath.Text, "appsettings.json");

                // ---- FILE CHECK ----
                if (File.Exists(certFilePath))
                {
                    var result = MessageBox.Show(
                        "Certificate file already exists. Do you want to replace it?",
                        "Certificate Exists",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.No)
                    {
                        lblStatus.Text = "Certificate already exists. Skipping copy...";
                        lblStatus.Visible = true;

                        // Still check if installed in store
                        string thumb = FindCertificateThumbprint(certName);
                        if (!string.IsNullOrEmpty(thumb))
                        {
                            UpdateCertificateSettings(configPath, certFilePath, certPassword, thumb);
                            btnFinish.Visible = true;
                            return;
                        }
                    }
                }

                lblStatus.Visible = true;
                lblStatus.Text = "Generating certificate...";
                var certificate = GenerateSelfSignedCertificate(certName, certPassword, txtIP.Text);

                lblStatus.Text = "Copying certificate...";
                File.WriteAllBytes(certFilePath, certificate.Export(X509ContentType.Pfx, certPassword));

                // ---- STORE CHECK ----
                lblStatus.Text = "Checking certificate store...";
                string existingThumb = FindCertificateThumbprint(certName);

                string thumbprint;
                if (!string.IsNullOrEmpty(existingThumb))
                {
                    lblStatus.Text = "Certificate already installed. Skipping installation.";
                    thumbprint = existingThumb;
                }
                else
                {
                    lblStatus.Text = "Installing certificate in store...";
                    thumbprint = InstallToCertificateStore(certFilePath, certPassword);
                }

                UpdateCertificateSettings(configPath, certFilePath, certPassword, thumbprint);

                lblStatus.Text = "Certificate Installed & Config updated successfully!";
                panelCertificate.Visible = false;
                btnFinish.Visible = true;
                btnCancel.Visible = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Certificate Installation Failed: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string FindCertificateThumbprint(string certName)
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);

                foreach (var cert in store.Certificates)
                {
                    if (cert.Subject.Contains($"CN={certName}"))
                    {
                        return cert.Thumbprint;
                    }
                }
            }

            return null;
        }


        private string InstallToCertificateStore(string certFilePath, string password)
        {
            X509Certificate2 certificate = new X509Certificate2(certFilePath, password);

            using (X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(certificate);
                store.Close();
            }

            return certificate.Thumbprint;
        }


        private X509Certificate2 GenerateSelfSignedCertificate(string certName, string password, string ipAddress)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    $"CN={certName}",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                // Basic constraints
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                // Key usage
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

                // Enhanced key usage
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection {
                    new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
                        },
                        false));

                // Subject Alternative Name (SAN)
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddIpAddress(IPAddress.Parse(ipAddress));   // IP based SAN
                sanBuilder.AddDnsName(certName);                      // Optional additional DNS name
                request.CertificateExtensions.Add(sanBuilder.Build());

                // Subject Key Identifier
                request.CertificateExtensions.Add(
                    new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                // Create the certificate
                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.Now.AddDays(-1),
                    DateTimeOffset.Now.AddYears(5));

                return new X509Certificate2(
     certificate.Export(X509ContentType.Pfx, password),
     password,
     X509KeyStorageFlags.Exportable |
     X509KeyStorageFlags.PersistKeySet |
     X509KeyStorageFlags.MachineKeySet);
            }
        }



        private void UpdateCertificateSettings(string configPath, string certPath, string certPassword, string thumbprint)
        {
            string json = File.ReadAllText(configPath);
            JObject jsonObj = JObject.Parse(json);

            jsonObj["WebSocketSettings"]["CertificatePath"] = certPath;
            jsonObj["WebSocketSettings"]["CertificatePassword"] = certPassword;
            jsonObj["WebSocketSettings"]["Thumbprint"] = thumbprint;

            File.WriteAllText(configPath, jsonObj.ToString());
        }
    }
}
