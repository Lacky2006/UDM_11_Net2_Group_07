using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace UploadServer
{
    public partial class MainWindow : Window
    {
        private TcpListener listener;
        private bool isRunning = false;
        // Log auto-scroll support (simplified)

        public MainWindow()
        {
            InitializeComponent();

            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;

            // No complex scroll setup; rely on caret + ScrollToEnd in AppendLog
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning)
            {
                MessageBox.Show("Server đang chạy rồi!");
                return;
            }

            if (!IPAddress.TryParse(txtIP.Text.Trim(), out IPAddress ipAddress))
            {
                MessageBox.Show("IP không hợp lệ!");
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

            try
            {
                listener = new TcpListener(ipAddress, port);
                listener.Start();

                isRunning = true;

                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                txtIP.IsEnabled = false;
                txtPort.IsEnabled = false;

                AppendLog($"Server đã bắt đầu tại IP {ipAddress}, cổng {port}...\n");

                await AcceptClientsAsync();
            }
            catch (Exception ex)
            {
                if (isRunning)
                {
                    AppendLog("Lỗi Server: " + ex.Message + "\n");
                }

                StopServerState();
            }
        }

        private async Task AcceptClientsAsync()
        {
            while (isRunning)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();

                    _ = Task.Run(async () =>
                    {
                        await HandleClientAsync(client);
                    });
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (isRunning)
                    {
                        AppendLog("Lỗi Socket khi chờ Client kết nối.\n");
                    }

                    break;
                }
                catch (Exception ex)
                {
                    if (isRunning)
                    {
                        AppendLog("Lỗi khi nhận Client: " + ex.Message + "\n");
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string remoteEndPoint = "Unknown";

            try
            {
                if (client.Client.RemoteEndPoint != null)
                {
                    remoteEndPoint = client.Client.RemoteEndPoint.ToString();
                }

                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    /*
                     * Protocol:
                     *
                     * Client gửi 4 bytes đầu:
                     * "ping" = kiểm tra kết nối
                     * "FILE" = upload file
                     */

                    byte[] commandBuffer = await ReadExactAsync(stream, 4);

                    if (commandBuffer == null)
                    {
                        return;
                    }

                    string command = Encoding.UTF8.GetString(commandBuffer);

                    if (command == "ping")
                    {
                        await HandlePingAsync(stream);

                        // Không log ping liên tục để tránh spam log vì Client đang monitor server mỗi 2 giây
                        return;
                    }

                    if (command == "FILE")
                    {
                        await HandleFileUploadAsync(stream, remoteEndPoint);
                        return;
                    }

                    AppendLog($"Client {remoteEndPoint} gửi lệnh không hợp lệ: {command}\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Lỗi xử lý Client {remoteEndPoint}: {ex.Message}\n");
            }
        }

        private async Task HandlePingAsync(NetworkStream stream)
        {
            byte[] response = Encoding.UTF8.GetBytes("pong");
            await stream.WriteAsync(response, 0, response.Length);
            await stream.FlushAsync();
        }

        private async Task HandleFileUploadAsync(NetworkStream stream, string remoteEndPoint)
        {
            /*
             * Protocol nhận file:
             *
             * 4 bytes  : FILE
             * 4 bytes  : độ dài tên file
             * n bytes  : tên file UTF-8
             * 8 bytes  : kích thước file
             * data     : dữ liệu file từng chunk
             */

            byte[] fileNameLengthBytes = await ReadExactAsync(stream, 4);

            if (fileNameLengthBytes == null)
            {
                AppendLog("Không đọc được độ dài tên file.\n");
                return;
            }

            int fileNameLength = BitConverter.ToInt32(fileNameLengthBytes, 0);

            if (fileNameLength <= 0 || fileNameLength > 1024)
            {
                AppendLog("Độ dài tên file không hợp lệ.\n");
                return;
            }

            byte[] fileNameBytes = await ReadExactAsync(stream, fileNameLength);

            if (fileNameBytes == null)
            {
                AppendLog("Không đọc được tên file.\n");
                return;
            }

            string fileName = Encoding.UTF8.GetString(fileNameBytes);
            fileName = Path.GetFileName(fileName);

            byte[] fileSizeBytes = await ReadExactAsync(stream, 8);

            if (fileSizeBytes == null)
            {
                AppendLog("Không đọc được kích thước file.\n");
                return;
            }

            long fileSize = BitConverter.ToInt64(fileSizeBytes, 0);

            if (fileSize < 0)
            {
                AppendLog("Kích thước file không hợp lệ.\n");
                return;
            }

            string uploadFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uploads");

            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            string savePath = Path.Combine(uploadFolder, fileName);
            savePath = GetUniqueFilePath(savePath);

            AppendLog($"Client {remoteEndPoint} bắt đầu upload file: {fileName}\n");
            AppendLog($"Dung lượng: {FormatFileSize(fileSize)}\n");
            AppendLog($"Lưu tại: {savePath}\n");

            byte[] buffer = new byte[64 * 1024];
            long receivedBytes = 0;
            int lastPercent = -1;

            using (FileStream fileStream = new FileStream(
                savePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                true))
            {
                while (receivedBytes < fileSize)
                {
                    int bytesToRead = buffer.Length;

                    long remainingBytes = fileSize - receivedBytes;

                    if (remainingBytes < bytesToRead)
                    {
                        bytesToRead = (int)remainingBytes;
                    }

                    int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                    if (bytesRead <= 0)
                    {
                        throw new IOException("Client đã ngắt kết nối khi đang upload file.");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead);

                    receivedBytes += bytesRead;

                    int percent = fileSize == 0 ? 100 : (int)(receivedBytes * 100 / fileSize);

                    if (percent != lastPercent && percent % 10 == 0)
                    {
                        lastPercent = percent;
                        AppendLog($"Đang nhận {fileName}: {percent}%\n");
                    }
                }
            }

            AppendLog($"Nhận file thành công: {Path.GetFileName(savePath)}\n");
            AppendLog("Hoàn tất upload.\n");
        }

        private async Task<byte[]> ReadExactAsync(NetworkStream stream, int size)
        {
            byte[] buffer = new byte[size];
            int totalRead = 0;

            while (totalRead < size)
            {
                int bytesRead = await stream.ReadAsync(buffer, totalRead, size - totalRead);

                if (bytesRead <= 0)
                {
                    return null;
                }

                totalRead += bytesRead;
            }

            return buffer;
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                MessageBox.Show("Server chưa chạy!");
                return;
            }

            StopServer();
        }

        private void StopServer()
        {
            try
            {
                isRunning = false;

                if (listener != null)
                {
                    listener.Stop();
                    listener = null;
                }

                StopServerState();

                AppendLog("Server đã dừng.\n");
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi khi dừng Server: " + ex.Message + "\n");
            }
        }

        private void StopServerState()
        {
            isRunning = false;

            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            txtIP.IsEnabled = true;
            txtPort.IsEnabled = true;
        }

        private string GetUniqueFilePath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return filePath;
            }

            string folder = Path.GetDirectoryName(filePath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);

            string newFileName = $"{fileNameWithoutExtension}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

            return Path.Combine(folder, newFileName);
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

        private void AppendLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(message));
                return;
            }

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
            // Move caret to end and ensure the latest text is visible
            txtLog.CaretIndex = txtLog.Text.Length;
            txtLog.ScrollToEnd();
        }
        // Removed complex ScrollViewer-based auto-scroll logic to use simple caret + ScrollToEnd

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                isRunning = false;

                if (listener != null)
                {
                    listener.Stop();
                    listener = null;
                }
            }
            catch
            {
                // Bỏ qua lỗi khi đóng app
            }

            base.OnClosed(e);
        }
    }
}