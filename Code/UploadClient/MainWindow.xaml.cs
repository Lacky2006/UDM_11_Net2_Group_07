using Microsoft.Win32;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace UploadClient
{
    public partial class MainWindow : Window
    {
        private bool isConnected = false;
        private string selectedFilePath = "";
        private CancellationTokenSource monitorCts;

        public MainWindow()
        {
            InitializeComponent();

            SetConnectionState(false);
            SetProgress(0);
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip = txtIP.Text.Trim();

            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Vui lòng nhập Server IP!");
                return;
            }

            if (!int.TryParse(txtPort.Text.Trim(), out int port))
            {
                MessageBox.Show("Port không hợp lệ!");
                return;
            }

            if (port < 1 || port > 65535)
            {
                MessageBox.Show("Port phải nằm trong khoảng 1 đến 65535!");
                return;
            }

            AppendLog("Đang kết nối tới Server...\n");

            bool ok = await PingServerAsync(ip, port, 2000);

            if (!ok)
            {
                AppendLog("Kết nối thất bại. Server chưa bật hoặc sai IP/Port.\n");
                SetConnectionState(false);
                return;
            }

            AppendLog("Kết nối Server thành công!\n");

            SetConnectionState(true);
            StartServerMonitor(ip, port);
        }

        private void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectClient("Đã ngắt kết nối khỏi Server.\n");
        }

        private void btnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Chọn 1 file để upload";
            dialog.Multiselect = false;

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                selectedFilePath = dialog.FileName;
                txtSelectedFile.Text = selectedFilePath;

                FileInfo fileInfo = new FileInfo(selectedFilePath);

                AppendLog($"Đã chọn file: {fileInfo.Name}\n");
                AppendLog($"Dung lượng: {FormatFileSize(fileInfo.Length)}\n");

                btnUpload.IsEnabled = isConnected;
                SetProgress(0);
            }
        }

        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                MessageBox.Show("Client chưa kết nối Server!");
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedFilePath) || !File.Exists(selectedFilePath))
            {
                MessageBox.Show("Vui lòng chọn file trước khi upload!");
                return;
            }

            string ip = txtIP.Text.Trim();
            int port = int.Parse(txtPort.Text.Trim());

            FileInfo fileInfo = new FileInfo(selectedFilePath);

            btnUpload.IsEnabled = false;
            btnSelectFile.IsEnabled = false;
            SetProgress(0);

            try
            {
                AppendLog($"Bắt đầu upload file: {fileInfo.Name}\n");

                using (TcpClient client = new TcpClient())
                {
                    bool connected = await ConnectWithTimeoutAsync(client, ip, port, 3000);

                    if (!connected)
                    {
                        HandleServerStopped();
                        return;
                    }

                    using (NetworkStream stream = client.GetStream())
                    using (FileStream fileStream = new FileStream(
                        selectedFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        true))
                    {
                        /*
                         * Protocol gửi file:
                         *
                         * 4 bytes  : FILE
                         * 4 bytes  : độ dài tên file
                         * n bytes  : tên file UTF-8
                         * 8 bytes  : kích thước file
                         * data     : dữ liệu file từng chunk
                         */

                        byte[] commandBytes = Encoding.UTF8.GetBytes("FILE");
                        byte[] fileNameBytes = Encoding.UTF8.GetBytes(fileInfo.Name);
                        byte[] fileNameLengthBytes = BitConverter.GetBytes(fileNameBytes.Length);
                        byte[] fileSizeBytes = BitConverter.GetBytes(fileInfo.Length);

                        await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                        await stream.WriteAsync(fileNameLengthBytes, 0, fileNameLengthBytes.Length);
                        await stream.WriteAsync(fileNameBytes, 0, fileNameBytes.Length);
                        await stream.WriteAsync(fileSizeBytes, 0, fileSizeBytes.Length);

                        byte[] buffer = new byte[64 * 1024];
                        long sentBytes = 0;

                        while (sentBytes < fileInfo.Length)
                        {
                            int bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);

                            if (bytesRead <= 0)
                            {
                                break;
                            }

                            await stream.WriteAsync(buffer, 0, bytesRead);

                            sentBytes += bytesRead;

                            double percent = sentBytes * 100.0 / fileInfo.Length;
                            SetProgress(percent);
                        }

                        await stream.FlushAsync();

                        SetProgress(100);
                        AppendLog("Upload hoàn tất phía Client.\n");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi upload: " + ex.Message + "\n");

                bool serverStillAlive = await PingServerAsync(ip, port, 1000);

                if (!serverStillAlive)
                {
                    HandleServerStopped();
                }
            }
            finally
            {
                btnSelectFile.IsEnabled = isConnected;
                btnUpload.IsEnabled = isConnected && File.Exists(selectedFilePath);
            }
        }

        private async Task<bool> PingServerAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    bool connected = await ConnectWithTimeoutAsync(client, ip, port, timeoutMs);

                    if (!connected)
                    {
                        return false;
                    }

                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes("ping");
                        await stream.WriteAsync(data, 0, data.Length);

                        byte[] buffer = new byte[1024];

                        Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        Task timeoutTask = Task.Delay(timeoutMs);

                        Task completedTask = await Task.WhenAny(readTask, timeoutTask);

                        if (completedTask != readTask)
                        {
                            return false;
                        }

                        int bytes = await readTask;

                        if (bytes <= 0)
                        {
                            return false;
                        }

                        string response = Encoding.UTF8.GetString(buffer, 0, bytes);
                        return response.Contains("pong");
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> ConnectWithTimeoutAsync(TcpClient client, string ip, int port, int timeoutMs)
        {
            try
            {
                Task connectTask = client.ConnectAsync(ip, port);
                Task timeoutTask = Task.Delay(timeoutMs);

                Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask != connectTask)
                {
                    return false;
                }

                await connectTask;
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private void StartServerMonitor(string ip, int port)
        {
            StopServerMonitor();

            monitorCts = new CancellationTokenSource();
            CancellationToken token = monitorCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);

                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (!isConnected)
                        {
                            break;
                        }

                        bool ok = await PingServerAsync(ip, port, 1500);

                        if (!ok)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                HandleServerStopped();
                            });

                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        Dispatcher.Invoke(() =>
                        {
                            HandleServerStopped();
                        });

                        break;
                    }
                }
            }, token);
        }

        private void StopServerMonitor()
        {
            try
            {
                if (monitorCts != null)
                {
                    monitorCts.Cancel();
                    monitorCts.Dispose();
                    monitorCts = null;
                }
            }
            catch
            {
                // Bỏ qua lỗi khi dừng monitor
            }
        }

        private void HandleServerStopped()
        {
            if (!isConnected)
            {
                return;
            }

            DisconnectClient("Server đã Stop hoặc mất kết nối. Client tự động ngắt kết nối.\n");
        }

        private void DisconnectClient(string logMessage)
        {
            StopServerMonitor();

            SetConnectionState(false);
            AppendLog(logMessage);
        }

        private void SetConnectionState(bool connected)
        {
            isConnected = connected;

            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;

            btnSelectFile.IsEnabled = connected;
            btnUpload.IsEnabled = connected && File.Exists(selectedFilePath);

            txtIP.IsEnabled = !connected;
            txtPort.IsEnabled = !connected;

            if (connected)
            {
                lblStatus.Text = "Connected";
                lblStatus.Foreground = Brushes.Green;
            }
            else
            {
                lblStatus.Text = "Disconnected";
                lblStatus.Foreground = Brushes.Red;

                btnSelectFile.IsEnabled = false;
                btnUpload.IsEnabled = false;
            }
        }

        private void SetProgress(double value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(value));
                return;
            }

            if (value < 0)
            {
                value = 0;
            }

            if (value > 100)
            {
                value = 100;
            }

            progressUpload.Value = value;
            lblProgress.Text = $"{value:0}%";
        }

        private void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(message));
                return;
            }

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
            txtLog.ScrollToEnd();
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            double kb = bytes / 1024.0;

            if (kb < 1024)
            {
                return kb.ToString("0.00") + " KB";
            }

            double mb = kb / 1024.0;

            if (mb < 1024)
            {
                return mb.ToString("0.00") + " MB";
            }

            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        protected override void OnClosed(EventArgs e)
        {
            StopServerMonitor();
            base.OnClosed(e);
        }
    }
}