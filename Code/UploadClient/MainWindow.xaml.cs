using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UploadClient
{
    public partial class MainWindow : Window
    {
        private const int BufferSize = 64 * 1024;
        private const int MaxParallelUploads = 3;

        private bool isConnected = false;
        private bool isUploading = false;
        private bool isPaused = false;

        private CancellationTokenSource monitorCts;
        private CancellationTokenSource uploadCts;

        private readonly ManualResetEventSlim pauseEvent = new ManualResetEventSlim(true);

        private readonly ObservableCollection<FileItem> fileItems = new ObservableCollection<FileItem>();
        private readonly ObservableCollection<FileItem> uploadStatusItems = new ObservableCollection<FileItem>();

        private readonly List<TcpClient> activeUploadClients = new List<TcpClient>();
        private readonly object activeUploadClientsLock = new object();

        public MainWindow()
        {
            InitializeComponent();

            dgFiles.ItemsSource = fileItems;
            icUploadStatus.ItemsSource = uploadStatusItems;

            DataObject.AddPastingHandler(txtPort, txtPort_Paste);
            SetConnectionState(false);
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip;
            int port;
            if (!TryGetServerInfo(out ip, out port)) return;

            AppendLog("Đang kết nối tới Server...\n");

            if (!await PingServerAsync(ip, port, 3000))
            {
                string message = "Server chưa bật hoặc nhập sai IP/Port.";
                MessageBox.Show(message, "Kết nối thất bại", MessageBoxButton.OK, MessageBoxImage.Warning);
                AppendLog(message + "\n");
                SetConnectionState(false);
                return;
            }

            SetConnectionState(true);
            StartServerMonitor(ip, port);
            AppendLog("Kết nối Server thành công!\n");
        }

        private void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectClient("Đã ngắt kết nối khỏi Server.\n");
        }

        private void btnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Chọn file để upload";
            dialog.Multiselect = true;

            if (dialog.ShowDialog() == true)
                AddFiles(dialog.FileNames);
        }

        private void btnClearList_Click(object sender, RoutedEventArgs e)
        {
            if (fileItems.Count == 0) return;

            fileItems.Clear();
            RenumberFiles();
            AppendLog("Đã clear danh sách file chờ upload.\n");
            SetButtonState();
        }

        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            string ip;
            int port;

            if (!isConnected)
            {
                MessageBox.Show("Client chưa kết nối Server!");
                return;
            }

            List<FileItem> pending = fileItems.Where(f => f.Status == "Waiting").ToList();
            if (pending.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn file trước khi upload!");
                return;
            }

            if (!TryGetServerInfo(out ip, out port)) return;

            StopServerMonitor();
            isUploading = true;
            isPaused = false;
            pauseEvent.Set();
            uploadCts = new CancellationTokenSource();

            MovePendingFilesToUploadStatus(pending);
            SetButtonState();

            try
            {
                using (SemaphoreSlim semaphore = new SemaphoreSlim(MaxParallelUploads))
                {
                    // Quan trọng: phải chạy qua Task.Run để toàn bộ pipeline upload
                    // (bao gồm pauseEvent.Wait(token) ở dưới) thực thi trên background thread,
                    // KHÔNG capture SynchronizationContext của UI thread.
                    // Nếu gọi UploadStatusItemAsync trực tiếp, phần code trước await đầu tiên
                    // chạy ngay trên UI thread, và do mọi await sau đó post continuation
                    // trở lại UI thread, lệnh pauseEvent.Wait() (blocking) có thể bị thực thi
                    // ngay trên UI thread => khi Pause được bấm, UI thread bị block vĩnh viễn
                    // (vì Resume cũng cần UI thread để xử lý click) => client bị đơ hoàn toàn.
                    Task<bool>[] tasks = pending
                        .Select(item => Task.Run(() => UploadStatusItemAsync(ip, port, item, semaphore, uploadCts.Token)))
                        .ToArray();

                    bool[] results = await Task.WhenAll(tasks);

                    int successCount = results.Count(x => x);
                    int failCount = results.Length - successCount;

                    AppendLog($"Hoàn tất upload. Thành công: {successCount}, thất bại: {failCount}.\n");
                }
            }
            catch (Exception ex) when (IsUploadCanceled(ex))
            {
                AppendLog("Đã hủy upload.\n");
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi upload: " + ex.Message + "\n");
            }
            finally
            {
                CloseUploadConnections();

                if (uploadCts != null)
                {
                    uploadCts.Dispose();
                    uploadCts = null;
                }

                isUploading = false;
                isPaused = false;
                pauseEvent.Set();

                if (isConnected)
                    StartServerMonitor(ip, port);

                SetButtonState();
            }
        }

        private async Task<bool> UploadStatusItemAsync(
            string ip,
            int port,
            FileItem item,
            SemaphoreSlim semaphore,
            CancellationToken token)
        {
            await semaphore.WaitAsync(token);

            try
            {
                token.ThrowIfCancellationRequested();
                pauseEvent.Wait(token);

                if (!File.Exists(item.FullPath))
                {
                    SetItemStatus(item, "Failed", 0, "");
                    AppendLog("Thất bại: " + item.FileName + "\n");
                    return false;
                }

                SetItemStatus(item, "Uploading", 0, "");
                AppendLog("Đang tải: " + item.FileName + "\n");

                await UploadOneFileAsync(ip, port, item, token);

                SetItemStatus(item, "Done", 100, "");
                AppendLog("Thành công: " + item.FileName + "\n");
                return true;
            }
            catch (Exception ex) when (IsUploadCanceled(ex))
            {
                throw;
            }
            catch
            {
                SetItemStatus(item, "Failed", item.Percent, "");
                AppendLog("Thất bại: " + item.FileName + "\n");

                if (!await PingServerAsync(ip, port, 3000))
                {
                    if (uploadCts != null && !uploadCts.IsCancellationRequested)
                        uploadCts.Cancel();

                    Dispatcher.Invoke(HandleServerStopped);
                }

                return false;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void MovePendingFilesToUploadStatus(List<FileItem> pending)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => MovePendingFilesToUploadStatus(pending));
                return;
            }

            foreach (FileItem item in pending)
            {
                fileItems.Remove(item);

                item.Status = "Queued";
                item.Percent = 0;
                item.SpeedText = "";

                if (!uploadStatusItems.Contains(item))
                    uploadStatusItems.Add(item);
            }

            RenumberFiles();
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (!isUploading || isPaused) return;

            isPaused = true;
            pauseEvent.Reset();

            foreach (FileItem item in uploadStatusItems.Where(f => f.Status == "Uploading").ToList())
                SetItemStatus(item, "Paused", item.Percent, "");

            SetButtonState();
            AppendLog("Đã tạm dừng upload.\n");
        }

        private void btnResume_Click(object sender, RoutedEventArgs e)
        {
            if (!isUploading || !isPaused) return;

            isPaused = false;
            pauseEvent.Set();

            foreach (FileItem item in uploadStatusItems.Where(f => f.Status == "Paused").ToList())
                SetItemStatus(item, "Uploading", item.Percent, "");

            SetButtonState();
            AppendLog("Tiếp tục upload.\n");
        }

        private async Task UploadOneFileAsync(string ip, int port, FileItem item, CancellationToken token)
        {
            FileInfo file = new FileInfo(item.FullPath);
            TcpClient client = new TcpClient();
            AddActiveUploadClient(client);

            try
            {
                if (!await ConnectWithTimeoutAsync(client, ip, port, 5000))
                    throw new IOException("Không kết nối được tới Server.");

                using (NetworkStream stream = client.GetStream())
                using (FileStream fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(file.Name);

                    await WriteAsync(stream, Encoding.UTF8.GetBytes("FILE"), token);
                    await WriteAsync(stream, BitConverter.GetBytes(nameBytes.Length), token);
                    await WriteAsync(stream, nameBytes, token);
                    await WriteAsync(stream, BitConverter.GetBytes(file.Length), token);

                    byte[] buffer = new byte[BufferSize];
                    long sent = 0;

                    Stopwatch sw = Stopwatch.StartNew();
                    double lastUpdateSeconds = 0;

                    while (sent < file.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        pauseEvent.Wait(token);

                        int read = await fs.ReadAsync(buffer, 0, buffer.Length, token);
                        if (read <= 0) break;

                        await stream.WriteAsync(buffer, 0, read, token);
                        sent += read;

                        double percent = file.Length == 0 ? 100 : sent * 100.0 / file.Length;
                        double elapsed = sw.Elapsed.TotalSeconds;

                        if (elapsed - lastUpdateSeconds >= 0.3 || sent >= file.Length)
                        {
                            double speedMBps = elapsed > 0 ? (sent / (1024.0 * 1024.0)) / elapsed : 0;
                            SetItemStatus(item, "Uploading", percent, $"Speed: {speedMBps:0.#} MB/s");
                            lastUpdateSeconds = elapsed;
                        }
                    }

                    await stream.FlushAsync(token);
                }
            }
            finally
            {
                RemoveActiveUploadClient(client);
                client.Close();
            }
        }

        private async Task<bool> PingServerAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    if (!await ConnectWithTimeoutAsync(client, ip, port, timeoutMs)) return false;

                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] ping = Encoding.UTF8.GetBytes("ping");
                        byte[] buffer = new byte[4];

                        await stream.WriteAsync(ping, 0, ping.Length);

                        Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) != readTask) return false;

                        int read = await readTask;
                        return read > 0 && Encoding.UTF8.GetString(buffer, 0, read).Contains("pong");
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
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) != connectTask) return false;

                await connectTask;
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private Task WriteAsync(NetworkStream stream, byte[] data, CancellationToken token)
        {
            return stream.WriteAsync(data, 0, data.Length, token);
        }

        private bool TryGetServerInfo(out string ip, out int port)
        {
            ip = txtIP.Text.Trim();
            port = 0;

            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Vui lòng nhập Server IP/Host!");
                return false;
            }

            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 0 || port > 65535)
            {
                MessageBox.Show("Port chỉ được nhập số nguyên từ 0 đến 65535!");
                return false;
            }

            return true;
        }

        private void AddFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            int added = 0;

            foreach (string path in paths)
            {
                bool existsInWaiting = fileItems.Any(f => f.FullPath == path);
                bool existsInStatus = uploadStatusItems.Any(f => f.FullPath == path && f.Status != "Failed");

                if (File.Exists(path) && !existsInWaiting && !existsInStatus)
                {
                    FileInfo info = new FileInfo(path);

                    fileItems.Add(new FileItem
                    {
                        FullPath = path,
                        FileName = info.Name,
                        FileIcon = GetFileIcon(info.Extension),
                        Size = FormatSize(info.Length),
                        Status = "Waiting",
                        Percent = 0,
                        SpeedText = ""
                    });

                    added++;
                }
            }

            if (added > 0)
            {
                RenumberFiles();
                AppendLog($"Đã thêm {added} file vào danh sách chờ upload.\n");
            }

            SetButtonState();
        }

        private void RenumberFiles()
        {
            for (int i = 0; i < fileItems.Count; i++)
                fileItems[i].STT = i + 1;
        }

        private string GetFileIcon(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".mp4":
                case ".avi":
                case ".mov":
                case ".mkv":
                case ".wmv":
                case ".flv":
                    return "🎬";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                    return "🖼️";
                case ".pdf":
                    return "📄";
                case ".doc":
                case ".docx":
                    return "📝";
                case ".xls":
                case ".xlsx":
                    return "📊";
                case ".mp3":
                case ".wav":
                case ".flac":
                    return "🎵";
                case ".zip":
                case ".rar":
                case ".7z":
                    return "🗜️";
                default:
                    return "📁";
            }
        }

        private string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.#} {units[unitIndex]}";
        }

        private string GetLocalIPv4()
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (network.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return address.Address.ToString();
                }
            }

            return "127.0.0.1";
        }

        private void StartServerMonitor(string ip, int port)
        {
            StopServerMonitor();
            monitorCts = new CancellationTokenSource();
            CancellationToken token = monitorCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (!isConnected) break;

                        if (!await PingServerAsync(ip, port, 3000))
                        {
                            Dispatcher.Invoke(HandleServerStopped);
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        private void StopServerMonitor()
        {
            if (monitorCts == null) return;

            monitorCts.Cancel();
            monitorCts.Dispose();
            monitorCts = null;
        }

        private void HandleServerStopped()
        {
            if (isConnected)
                DisconnectClient("Server đã Stop hoặc mất kết nối. Client tự động ngắt.\n");
        }

        private void DisconnectClient(string message)
        {
            StopServerMonitor();
            CancelUpload();
            SetConnectionState(false);
            AppendLog(message);
        }

        private void CancelUpload()
        {
            try
            {
                if (uploadCts != null && !uploadCts.IsCancellationRequested)
                    uploadCts.Cancel();

                pauseEvent.Set();
                CloseUploadConnections();
            }
            catch
            {
            }
        }

        private void AddActiveUploadClient(TcpClient client)
        {
            lock (activeUploadClientsLock)
            {
                activeUploadClients.Add(client);
            }
        }

        private void RemoveActiveUploadClient(TcpClient client)
        {
            lock (activeUploadClientsLock)
            {
                activeUploadClients.Remove(client);
            }
        }

        private void CloseUploadConnections()
        {
            TcpClient[] clients;

            lock (activeUploadClientsLock)
            {
                clients = activeUploadClients.ToArray();
                activeUploadClients.Clear();
            }

            foreach (TcpClient client in clients)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }

        private bool IsUploadCanceled(Exception ex)
        {
            bool userCanceled = uploadCts != null && uploadCts.IsCancellationRequested;

            return ex is OperationCanceledException ||
                   userCanceled && (ex is ObjectDisposedException || ex is IOException || ex is SocketException);
        }

        private void SetConnectionState(bool connected)
        {
            isConnected = connected;

            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            txtIP.IsEnabled = !connected;
            txtPort.IsEnabled = !connected;

            lblStatus.Text = connected ? "Connected" : "Disconnected";
            lblStatus.Foreground = connected ? Brushes.Green : Brushes.Red;
            ellipseStatus.Fill = connected ? Brushes.LimeGreen : Brushes.Red;

            SetButtonState();
        }

        private void SetButtonState()
        {
            bool hasPending = fileItems.Any(f => f.Status == "Waiting");

            btnSelectFile.IsEnabled = isConnected && !isUploading;
            btnClearList.IsEnabled = isConnected && !isUploading && fileItems.Count > 0;
            btnUpload.IsEnabled = isConnected && !isUploading && hasPending;
            btnPause.IsEnabled = isUploading && !isPaused;
            btnResume.IsEnabled = isUploading && isPaused;
        }

        private void SetItemStatus(FileItem item, string status, double percent, string speedText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetItemStatus(item, status, percent, speedText));
                return;
            }

            item.Status = status;
            item.Percent = Math.Max(0, Math.Min(100, percent));
            item.SpeedText = speedText;
        }

        private void AppendLog(string message)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}");
        }

        private void txtPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsValidPortText(GetNewPortText((TextBox)sender, e.Text));
        }

        private void txtPort_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }

            string pasteText = e.DataObject.GetData(typeof(string)) as string;
            if (!IsValidPortText(GetNewPortText(txtPort, pasteText))) e.CancelCommand();
        }

        private string GetNewPortText(TextBox box, string input)
        {
            string text = box.Text.Remove(box.SelectionStart, box.SelectionLength);
            return text.Insert(box.SelectionStart, input ?? "");
        }

        private bool IsValidPortText(string text)
        {
            int port;
            return text == "" ||
                   text.Length <= 5 &&
                   int.TryParse(text, out port) &&
                   port >= 0 &&
                   port <= 65535;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e);
        }

        private void dropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                dropZoneRect.Stroke = Brushes.DodgerBlue;
                lblDropZone.Foreground = Brushes.DodgerBlue;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private void dropZone_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneStyle();
        }

        private void dropZone_Drop(object sender, DragEventArgs e)
        {
            HandleFileDrop(e);
        }

        private void HandleFileDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                AddFiles(paths);
            }

            ResetDropZoneStyle();
            e.Handled = true;
        }

        private void ResetDropZoneStyle()
        {
            dropZoneRect.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A0A0A0"));
            lblDropZone.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        }

        protected override void OnClosed(EventArgs e)
        {
            DisconnectClient("Đã đóng Client.\n");
            base.OnClosed(e);
        }
    }
}