using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace UploadServer
{
    public partial class MainWindow : Window
    {
        private TcpListener listener;
        private bool isRunning;
        private const int BufferSize = 64 * 1024;

        // Statistics tracking
        private int connectedClientsCount;
        private int receivedFilesCount;
        private long totalReceivedSize;
        private long activeConnections;

        public MainWindow()
        {
            InitializeComponent();

            txtIP.Text = GetLocalIPv4();
            SetServerState(false);
            UpdateStatisticsUI();

            DataObject.AddPastingHandler(txtPort, txtPort_Paste);
        }

        private void UpdateStatisticsUI()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateStatisticsUI());
                return;
            }

            txtConnectedClientsCount.Text = Interlocked.Read(ref activeConnections).ToString();
            txtReceivedFilesCount.Text = receivedFilesCount.ToString();
            txtTotalReceivedSize.Text = FormatSize(totalReceivedSize);
            txtServerStatus.Text = isRunning ? "Running" : "Stopped";
            txtServerStatus.Foreground = isRunning ? 
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 16, 185, 129)) : // Green
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 239, 68, 68)); // Red
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning) return;
            if (!TryReadConfig(out IPAddress ip, out int port)) return;

            try
            {
                listener = new TcpListener(ip, port);
                listener.Start();

                SetServerState(true);

                int realPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                txtPort.Text = realPort.ToString();

                UpdateStatisticsUI();
                Log($"Server started at {ip}:{realPort}");
                await AcceptClientsAsync();
            }
            catch (Exception ex)
            {
                StopServer();
            }
        }

        private async Task AcceptClientsAsync()
        {
            while (isRunning)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string remote = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            Interlocked.Increment(ref activeConnections);
            UpdateStatisticsUI();
            Log($"Client connected: {remote}");

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] commandBytes = await ReadExactAsync(stream, 4);
                    if (commandBytes == null)
                    {
                        return;
                    }

                    string command = Encoding.UTF8.GetString(commandBytes);

                    if (command == "ping")
                    {
                        byte[] pong = Encoding.UTF8.GetBytes("pong");
                        await stream.WriteAsync(pong, 0, pong.Length);
                        await stream.FlushAsync();
                    }
                    else if (command == "FILE")
                    {
                        await ReceiveFileAsync(stream, remote);
                    }
                }
            }
            catch (IOException)
            {
                Log($"Client disconnected: {remote}");
            }
            catch (SocketException)
            {
                Log($"Client disconnected: {remote}");
            }
            catch (ObjectDisposedException)
            {
                Log($"Client disconnected: {remote}");
            }
            finally
            {
                Interlocked.Decrement(ref activeConnections);
                UpdateStatisticsUI();
            }
        }

        private async Task ReceiveFileAsync(NetworkStream stream, string remote)
        {
            string savePath = null;
            string fileName = "Unknown";
            long fileSize = 0;

            try
            {
                byte[] nameLengthBytes = await ReadExactOrThrowAsync(stream, 4, "Client interrupted while sending file name length.");
                int nameLength = BitConverter.ToInt32(nameLengthBytes, 0);

                if (nameLength <= 0 || nameLength > 1024)
                    throw new InvalidDataException("Invalid file name length.");

                byte[] nameBytes = await ReadExactOrThrowAsync(stream, nameLength, "Client interrupted while sending file name.");
                fileName = Path.GetFileName(Encoding.UTF8.GetString(nameBytes));

                byte[] sizeBytes = await ReadExactOrThrowAsync(stream, 8, "Client interrupted while sending file size.");
                fileSize = BitConverter.ToInt64(sizeBytes, 0);

                if (fileSize < 0)
                    throw new InvalidDataException("Invalid file size.");

                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uploads");
                Directory.CreateDirectory(folder);

                savePath = GetUniquePath(Path.Combine(folder, fileName));

                Log($"Receiving file from {remote}: {fileName}");

                byte[] buffer = new byte[BufferSize];
                long received = 0;

                using (FileStream file = new FileStream(savePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    while (received < fileSize)
                    {
                        int needRead = (int)Math.Min(buffer.Length, fileSize - received);
                        int read = await stream.ReadAsync(buffer, 0, needRead);

                        if (read <= 0)
                            throw new IOException("Client interrupted during file upload.");

                        await file.WriteAsync(buffer, 0, read);
                        received += read;
                    }
                }

                // Update statistics on successful file reception
                receivedFilesCount++;
                Interlocked.Add(ref totalReceivedSize, fileSize);
                UpdateStatisticsUI();

                Log($"File received successfully: {Path.GetFileName(savePath)} ({FormatSize(fileSize)})");
            }
            catch (InvalidDataException ex)
            {
                DeletePartialFile(savePath);
                Log($"Invalid upload data from {remote}: {ex.Message}");
            }
            catch (IOException)
            {
                DeletePartialFile(savePath);
                Log($"Upload cancelled by {remote}: {fileName}");
            }
            catch (SocketException)
            {
                DeletePartialFile(savePath);
                Log($"Connection lost with {remote} during file upload: {fileName}");
            }
        }

        private async Task<byte[]> ReadExactOrThrowAsync(NetworkStream stream, int size, string errorMessage)
        {
            byte[] data = await ReadExactAsync(stream, size);
            if (data == null) throw new IOException(errorMessage);
            return data;
        }

        private async Task<byte[]> ReadExactAsync(NetworkStream stream, int size)
        {
            byte[] buffer = new byte[size];
            int total = 0;

            while (total < size)
            {
                int read = await stream.ReadAsync(buffer, total, size - total);
                if (read <= 0) return null;
                total += read;
            }

            return buffer;
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
            Log("Server stopped");
        }

        private void StopServer()
        {
            isRunning = false;
            listener?.Stop();
            listener = null;
            SetServerState(false);
            UpdateStatisticsUI();
        }

        private void SetServerState(bool running)
        {
            isRunning = running;
            btnStart.IsEnabled = !running;
            btnStop.IsEnabled = running;
            txtPort.IsEnabled = !running;
        }

        private bool TryReadConfig(out IPAddress ip, out int port)
        {
            ip = null;
            port = 0;
            txtIP.Text = GetLocalIPv4();

            if (!IPAddress.TryParse(txtIP.Text.Trim(), out ip))
            {
                MessageBox.Show("Không lấy được IP của máy!");
                return false;
            }

            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 0 || port > 65535)
            {
                MessageBox.Show("Port chỉ được nhập số nguyên từ 0 đến 65535!");
                return false;
            }

            return true;
        }

        private string GetLocalIPv4()
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                foreach (UnicastIPAddressInformation ip in network.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                        return ip.Address.ToString();
                }
            }

            return "127.0.0.1";
        }

        private void txtPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsValidPortText(GetNewPortText(e.Text));
        }

        private void txtPort_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            string text = e.DataObject.GetData(DataFormats.Text).ToString();
            if (!IsValidPortText(GetNewPortText(text)))
                e.CancelCommand();
        }

        private string GetNewPortText(string input)
        {
            string oldText = txtPort.Text;
            return oldText.Remove(txtPort.SelectionStart, txtPort.SelectionLength)
                          .Insert(txtPort.SelectionStart, input);
        }

        private bool IsValidPortText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;

            foreach (char c in text)
                if (!char.IsDigit(c)) return false;

            return int.TryParse(text, out int port) && port >= 0 && port <= 65535;
        }

        private void DeletePartialFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private string GetUniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            string folder = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            return Path.Combine(folder, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
        }

        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.00") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.00") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(message));
                return;
            }

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            txtLog.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopServer();
            base.OnClosed(e);
        }
    }
}
