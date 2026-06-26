using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace UploadServer
{
    public partial class MainWindow : Window
    {
        private TcpListener listener;
        private bool isRunning;
        private const int BufferSize = 1024 * 1024;
        private const int ClientTimeoutSeconds = 8;

        private readonly ObservableCollection<ClientInfo> clients = new ObservableCollection<ClientInfo>();
        private readonly ObservableCollection<ReceivedFileInfo> receivedFiles = new ObservableCollection<ReceivedFileInfo>();
        private readonly List<TcpClient> activeTcpClients = new List<TcpClient>();
        private readonly object activeTcpClientsLock = new object();
        private CancellationTokenSource clientCleanupCts;

        public MainWindow()
        {
            InitializeComponent();

            lvClients.ItemsSource = clients;
            lvReceivedFiles.ItemsSource = receivedFiles;

            txtIP.Text = GetLocalIPv4();
            SetServerState(false);
            UpdateCounters();

            DataObject.AddPastingHandler(txtPort, txtPort_Paste);
            Log("IP LAN: " + txtIP.Text + "\n");
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning) return;
            if (!TryReadConfig(out int port)) return;

            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();

                SetServerState(true);
                StartClientCleanupLoop();

                int realPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                string lanIp = GetLocalIPv4();

                txtIP.Text = lanIp;
                txtPort.Text = realPort.ToString();

                Log($"Server đã chạy: {lanIp}:{realPort}\n");
                await AcceptClientsAsync();
            }
            catch (Exception ex)
            {
                if (isRunning) Log("Lỗi Server: " + ex.Message + "\n");
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
                    client.NoDelay = true;

                    lock (activeTcpClientsLock)
                    {
                        activeTcpClients.Add(client);
                    }

                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (isRunning) Log("Server socket đã dừng.\n");
                    break;
                }
                catch (Exception ex)
                {
                    if (isRunning) Log("Lỗi nhận Client: " + ex.Message + "\n");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string remote = GetRemoteIp(client);

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] commandBytes = await ReadExactAsync(stream, 4);
                    if (commandBytes == null) return;

                    string command = Encoding.UTF8.GetString(commandBytes);

                    if (command == "ping")
                    {
                        AddOrRefreshClient(remote);

                        byte[] pong = Encoding.UTF8.GetBytes("pong");
                        await stream.WriteAsync(pong, 0, pong.Length);
                        await stream.FlushAsync();
                    }
                    else if (command == "FILE")
                    {
                        AddOrRefreshClient(remote);
                        await ReceiveLegacyFileAsync(stream, remote);
                    }
                    else if (command == "F2LE")
                    {
                        AddOrRefreshClient(remote);
                        await ReceivePhase4FileAsync(stream, remote);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log($"Lỗi xử lý Client {remote}: {ex.Message}\n");
            }
            finally
            {
                lock (activeTcpClientsLock)
                {
                    activeTcpClients.Remove(client);
                }
            }
        }

        private async Task ReceiveLegacyFileAsync(NetworkStream stream, string remote)
        {
            string savePath = null;
            string fileName = "Unknown";
            long fileSize = 0;
            bool hasFileHeader = false;
            ReceivedFileInfo fileInfo = null;

            try
            {
                byte[] nameLengthBytes = await ReadExactOrThrowAsync(stream, 4);
                int nameLength = BitConverter.ToInt32(nameLengthBytes, 0);

                if (nameLength <= 0 || nameLength > 1024)
                    throw new InvalidDataException();

                byte[] nameBytes = await ReadExactOrThrowAsync(stream, nameLength);
                fileName = Path.GetFileName(Encoding.UTF8.GetString(nameBytes));

                byte[] sizeBytes = await ReadExactOrThrowAsync(stream, 8);
                fileSize = BitConverter.ToInt64(sizeBytes, 0);

                if (fileSize < 0)
                    throw new InvalidDataException();

                hasFileHeader = true;

                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uploads");
                Directory.CreateDirectory(folder);

                savePath = GetUniquePath(Path.Combine(folder, fileName));
                fileInfo = AddReceivedFile(remote, fileName, FormatSize(fileSize), "Đang tải");
                Log($"Đang tải: {fileName}\n");

                byte[] buffer = new byte[BufferSize];
                long received = 0;

                using (FileStream file = new FileStream(savePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    while (received < fileSize)
                    {
                        int needRead = (int)Math.Min(buffer.Length, fileSize - received);
                        int read = await stream.ReadAsync(buffer, 0, needRead);

                        if (read <= 0)
                            throw new IOException();

                        await file.WriteAsync(buffer, 0, read);
                        received += read;
                    }
                }

                UpdateReceivedFile(fileInfo, Path.GetFileName(savePath), FormatSize(fileSize), "Thành công");
                Log($"Thành công: {Path.GetFileName(savePath)}\n");
            }
            catch
            {
                DeletePartialFile(savePath);

                if (hasFileHeader)
                {
                    if (fileInfo == null)
                        AddReceivedFile(remote, fileName, FormatSize(fileSize), "Thất bại");
                    else
                        UpdateReceivedFile(fileInfo, fileName, FormatSize(fileSize), "Thất bại");

                    Log($"Thất bại: {fileName}\n");
                }
            }
        }

        private async Task ReceivePhase4FileAsync(NetworkStream stream, string remote)
        {
            string finalPath = null;
            string tempPath = null;
            string fileName = "Unknown";
            string clientHash = "";
            long fileSize = 0;
            long offset = 0;
            bool hasFileHeader = false;
            bool deleteTempFile = false;
            ReceivedFileInfo fileInfo = null;

            try
            {
                byte[] nameLengthBytes = await ReadExactOrThrowAsync(stream, 4);
                int nameLength = BitConverter.ToInt32(nameLengthBytes, 0);

                if (nameLength <= 0 || nameLength > 1024)
                    throw new InvalidDataException();

                byte[] nameBytes = await ReadExactOrThrowAsync(stream, nameLength);
                fileName = Path.GetFileName(Encoding.UTF8.GetString(nameBytes));

                byte[] sizeBytes = await ReadExactOrThrowAsync(stream, 8);
                fileSize = BitConverter.ToInt64(sizeBytes, 0);

                if (fileSize < 0)
                    throw new InvalidDataException();

                byte[] hashLengthBytes = await ReadExactOrThrowAsync(stream, 4);
                int hashLength = BitConverter.ToInt32(hashLengthBytes, 0);

                if (hashLength <= 0 || hashLength > 256)
                    throw new InvalidDataException();

                byte[] hashBytes = await ReadExactOrThrowAsync(stream, hashLength);
                clientHash = Encoding.UTF8.GetString(hashBytes).Trim().ToLowerInvariant();

                if (clientHash.Length == 0)
                    throw new InvalidDataException();

                hasFileHeader = true;

                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uploads");
                Directory.CreateDirectory(folder);

                finalPath = Path.Combine(folder, fileName);
                tempPath = GetTempPathForResume(finalPath, clientHash);

                offset = GetResumeOffset(tempPath, fileSize);
                await WriteLongAsync(stream, offset);
                await stream.FlushAsync();

                fileInfo = AddReceivedFile(remote, fileName, FormatSize(fileSize), "Đang tải");
                Log($"Đang tải: {fileName}\n");

                byte[] buffer = new byte[BufferSize];
                long received = offset;

                using (FileStream file = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    file.SetLength(offset);
                    file.Position = offset;

                    while (received < fileSize)
                    {
                        int needRead = (int)Math.Min(buffer.Length, fileSize - received);
                        int read = await stream.ReadAsync(buffer, 0, needRead);

                        if (read <= 0)
                            throw new IOException();

                        await file.WriteAsync(buffer, 0, read);
                        received += read;
                    }
                }

                string serverHash = ComputeSha256(tempPath);

                if (!string.Equals(serverHash, clientHash, StringComparison.OrdinalIgnoreCase))
                {
                    deleteTempFile = true;
                    DeletePartialFile(tempPath);
                    UpdateReceivedFile(fileInfo, fileName, FormatSize(fileSize), "Thất bại");
                    Log($"Thất bại: {fileName}\n");
                    await SendResultAsync(stream, "FAIL", "Checksum không khớp.");
                    return;
                }

                string completedPath = GetUniquePath(finalPath);
                if (File.Exists(completedPath))
                    completedPath = GetUniquePath(completedPath);

                File.Move(tempPath, completedPath);

                UpdateReceivedFile(fileInfo, Path.GetFileName(completedPath), FormatSize(fileSize), "Thành công");
                Log($"Thành công: {Path.GetFileName(completedPath)}\n");
                await SendResultAsync(stream, "DONE", "Upload thành công.");
            }
            catch
            {
                if (deleteTempFile)
                    DeletePartialFile(tempPath);

                if (hasFileHeader)
                {
                    if (fileInfo == null)
                        AddReceivedFile(remote, fileName, FormatSize(fileSize), "Thất bại");
                    else
                        UpdateReceivedFile(fileInfo, fileName, FormatSize(fileSize), "Thất bại");

                    Log($"Thất bại: {fileName}\n");

                    try
                    {
                        await SendResultAsync(stream, "FAIL", "Upload thất bại.");
                    }
                    catch
                    {
                    }
                }
            }
        }

        private long GetResumeOffset(string tempPath, long fileSize)
        {
            try
            {
                if (!File.Exists(tempPath)) return 0;

                long length = new FileInfo(tempPath).Length;
                if (length < 0 || length > fileSize)
                {
                    File.Delete(tempPath);
                    return 0;
                }

                return length;
            }
            catch
            {
                return 0;
            }
        }

        private string GetTempPathForResume(string finalPath, string sha256)
        {
            string folder = Path.GetDirectoryName(finalPath);
            string name = Path.GetFileNameWithoutExtension(finalPath);
            string ext = Path.GetExtension(finalPath);
            string suffix = sha256.Length > 12 ? sha256.Substring(0, 12) : sha256;
            return Path.Combine(folder, $"{name}_{suffix}{ext}.part");
        }

        private async Task SendResultAsync(NetworkStream stream, string status, string message)
        {
            byte[] statusBytes = Encoding.UTF8.GetBytes(status.PadRight(4).Substring(0, 4));
            byte[] messageBytes = Encoding.UTF8.GetBytes(message ?? "");
            await stream.WriteAsync(statusBytes, 0, statusBytes.Length);
            await stream.WriteAsync(BitConverter.GetBytes(messageBytes.Length), 0, 4);
            if (messageBytes.Length > 0)
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
            await stream.FlushAsync();
        }

        private async Task WriteLongAsync(NetworkStream stream, long value)
        {
            byte[] data = BitConverter.GetBytes(value);
            await stream.WriteAsync(data, 0, data.Length);
        }

        private async Task<byte[]> ReadExactOrThrowAsync(NetworkStream stream, int size)
        {
            byte[] data = await ReadExactAsync(stream, size);
            if (data == null) throw new IOException();
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

        private string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                foreach (byte b in hash)
                    builder.Append(b.ToString("x2"));

                return builder.ToString();
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
            Log("Server đã dừng.\n");
        }

        private void StopServer()
        {
            isRunning = false;
            listener?.Stop();
            listener = null;

            StopClientCleanupLoop();

            TcpClient[] clientsToClose;
            lock (activeTcpClientsLock)
            {
                clientsToClose = activeTcpClients.ToArray();
                activeTcpClients.Clear();
            }

            foreach (TcpClient client in clientsToClose)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            ClearClients();
            SetServerState(false);
        }

        private void SetServerState(bool running)
        {
            isRunning = running;
            btnStart.IsEnabled = !running;
            btnStop.IsEnabled = running;
            txtIP.IsEnabled = !running;
            txtPort.IsEnabled = !running;

            lblServerStatus.Text = running ? "Status: Running" : "Status: Offline";
            ellServerStatus.Fill = running ? Brushes.LimeGreen : Brushes.Red;
        }

        private bool TryReadConfig(out int port)
        {
            port = 0;
            txtIP.Text = GetLocalIPv4();

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

        private string GetRemoteIp(TcpClient client)
        {
            try
            {
                IPEndPoint endPoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (endPoint != null) return endPoint.Address.ToString();
            }
            catch
            {
            }

            return "Unknown";
        }

        private void AddOrRefreshClient(string ip)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddOrRefreshClient(ip));
                return;
            }

            ClientInfo item = clients.FirstOrDefault(c => c.IPClient == ip);

            if (item == null)
            {
                item = new ClientInfo
                {
                    STT = clients.Count + 1,
                    IPClient = ip,
                    LastSeen = DateTime.Now
                };

                clients.Add(item);
                Log($"Client kết nối: {ip}\n");
            }
            else
            {
                item.LastSeen = DateTime.Now;
            }

            lvClients.Items.Refresh();
            UpdateCounters();
        }

        private void RemoveExpiredClients()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RemoveExpiredClients);
                return;
            }

            DateTime now = DateTime.Now;
            List<ClientInfo> expiredClients = clients
                .Where(c => (now - c.LastSeen).TotalSeconds > ClientTimeoutSeconds)
                .ToList();

            foreach (ClientInfo item in expiredClients)
            {
                clients.Remove(item);
                Log($"Client ngắt kết nối: {item.IPClient}\n");
            }

            if (expiredClients.Count > 0)
            {
                RenumberClients();
                UpdateCounters();
            }
        }

        private void ClearClients()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearClients);
                return;
            }

            clients.Clear();
            UpdateCounters();
        }

        private ReceivedFileInfo AddReceivedFile(string ip, string fileName, string size, string status)
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(() => AddReceivedFile(ip, fileName, size, status));

            ReceivedFileInfo item = new ReceivedFileInfo
            {
                STT = receivedFiles.Count + 1,
                IPClient = ip,
                FileName = fileName,
                Size = size,
                Status = status,
                ReceivedTime = DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy")
            };

            receivedFiles.Add(item);
            UpdateCounters();
            return item;
        }

        private void UpdateReceivedFile(ReceivedFileInfo item, string fileName, string size, string status)
        {
            if (item == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateReceivedFile(item, fileName, size, status));
                return;
            }

            item.FileName = fileName;
            item.Size = size;
            item.Status = status;
            item.ReceivedTime = DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy");
            lvReceivedFiles.Items.Refresh();
            UpdateCounters();
        }

        private void RenumberClients()
        {
            for (int i = 0; i < clients.Count; i++)
                clients[i].STT = i + 1;

            lvClients.Items.Refresh();
        }

        private void UpdateCounters()
        {
            lblClientCount.Text = clients.Count + " Client(s) connected";
            lblTotalFiles.Text = "Total files: " + receivedFiles.Count;
        }

        private void StartClientCleanupLoop()
        {
            StopClientCleanupLoop();
            clientCleanupCts = new CancellationTokenSource();
            CancellationToken token = clientCleanupCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, token);
                        if (!isRunning) break;
                        RemoveExpiredClients();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        private void StopClientCleanupLoop()
        {
            if (clientCleanupCts == null) return;

            clientCleanupCts.Cancel();
            clientCleanupCts.Dispose();
            clientCleanupCts = null;
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

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}");
            txtLog.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopServer();
            base.OnClosed(e);
        }
    }

    public class ClientInfo
    {
        public int STT { get; set; }
        public string IPClient { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public class ReceivedFileInfo
    {
        public int STT { get; set; }
        public string IPClient { get; set; }
        public string FileName { get; set; }
        public string Size { get; set; }
        public string Status { get; set; }
        public string ReceivedTime { get; set; }
    }
}
