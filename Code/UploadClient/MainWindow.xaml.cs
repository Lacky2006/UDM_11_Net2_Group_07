using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
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

        private bool isConnected = false;
        private bool isUploading = false;

        // Danh sách các kết nối TCP đang upload song song (mỗi file 1 TcpClient riêng).
        // Cần lock vì nhiều luồng (mỗi file 1 Task) cùng add/remove/đóng list này.
        private readonly List<TcpClient> uploadClients = new List<TcpClient>();
        private readonly object uploadClientsLock = new object();

        private CancellationTokenSource monitorCts;
        private CancellationTokenSource uploadCts;
        private readonly ObservableCollection<string> fileList = new ObservableCollection<string>();

        // Ghi nhớ các file ĐÃ upload thành công, để "Clear List" không xóa nhầm chúng.
        // Nhiều file upload song song cùng hoàn tất -> cần lock khi ghi vào HashSet này.
        private readonly HashSet<string> uploadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object uploadedFilesLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            txtIP.Text = GetLocalIPv4();
            lstFiles.ItemsSource = fileList;
            DataObject.AddPastingHandler(txtPort, txtPort_Paste);
            SetConnectionState(false);
            SetProgress(0);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            AddDroppedFiles(e);
        }

        private void dropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                dropZoneRect.Stroke = Brushes.SteelBlue;
                dropZoneRect.Fill = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFB));
                lblDropZone.Text = "📂  Thả file ra để thêm vào danh sách";
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
            ResetDropZoneStyle();
            AddDroppedFiles(e);
            e.Handled = true;
        }

        private void ResetDropZoneStyle()
        {
            dropZoneRect.Stroke = new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xBD));
            dropZoneRect.Fill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC));
            lblDropZone.Text = "📂  Kéo và thả file vào đây để thêm vào danh sách upload";
        }

        private void AddDroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] droppedFiles = (string[])e.Data.GetData(DataFormats.FileDrop);
            int added = 0;

            foreach (string file in droppedFiles)
            {
                if (File.Exists(file) && !fileList.Contains(file))
                {
                    fileList.Add(file);
                    added++;
                }
            }

            if (added > 0)
            {
                SetProgress(0);
                AppendLog($"Đã thêm {added} file từ thao tác kéo thả.\n");
                SetButtonState();
            }
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            string ip;
            int port;
            if (!TryGetServerInfo(out ip, out port)) return;

            AppendLog("Đang kết nối tới Server...\n");

            if (!await PingServerAsync(ip, port, 2000))
            {
                AppendLog("Kết nối thất bại. Kiểm tra lại IP/Port hoặc Server chưa bật.\n");
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

            if (dialog.ShowDialog() != true) return;

            int added = 0;
            foreach (string path in dialog.FileNames)
            {
                if (!fileList.Contains(path))
                {
                    fileList.Add(path);
                    added++;
                }
            }

            SetProgress(0);
            AppendLog($"Đã thêm {added} file vào danh sách.\n");
            SetButtonState();
        }

        private void btnClearList_Click(object sender, RoutedEventArgs e)
        {
            if (fileList.Count == 0) return;

            List<string> toRemove;
            lock (uploadedFilesLock)
            {
                toRemove = fileList.Where(f => !uploadedFiles.Contains(f)).ToList();
            }

            if (toRemove.Count == 0)
            {
                AppendLog("Tất cả file trong danh sách đã upload thành công, không có file nào để xóa.\n");
                return;
            }

            foreach (string f in toRemove)
            {
                fileList.Remove(f);
            }

            SetProgress(0);
            AppendLog($"Đã xóa {toRemove.Count} file chưa upload khỏi danh sách (giữ lại các file đã upload thành công).\n");
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

            if (fileList.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn file trước khi upload!");
                return;
            }

            if (!TryGetServerInfo(out ip, out port)) return;

            isUploading = true;
            uploadCts = new CancellationTokenSource();
            SetButtonState();

            try
            {
                List<string> snapshot = fileList.ToList();

                int missingCount = snapshot.Count(f => !File.Exists(f));
                if (missingCount > 0)
                {
                    AppendLog($"Bỏ qua {missingCount} file không tồn tại.\n");
                }

                List<string> alreadyUploaded;
                lock (uploadedFilesLock)
                {
                    alreadyUploaded = snapshot.Where(f => uploadedFiles.Contains(f)).ToList();
                }

                if (alreadyUploaded.Count > 0)
                {
                    AppendLog($"Bỏ qua {alreadyUploaded.Count} file đã upload thành công trước đó.\n");
                }

                string[] files = snapshot
                    .Where(File.Exists)
                    .Where(f => !alreadyUploaded.Contains(f))
                    .ToArray();

                if (files.Length == 0)
                {
                    AppendLog("Không có file mới để upload.\n");
                    return;
                }

                // Tổng dung lượng của TẤT CẢ file -> dùng để tính % chung cho toàn bộ lượt upload.
                long totalBytesToSend = files.Sum(f => new FileInfo(f).Length);
                long totalBytesSent = 0;

                SetProgress(0);
                AppendLog($"Bắt đầu upload song song {files.Length} file...\n");

                Stopwatch overallStopwatch = Stopwatch.StartNew();
                Stopwatch uiUpdateTimer = Stopwatch.StartNew();
                object progressLock = new object();

                // Callback được gọi từ NHIỀU luồng khác nhau (mỗi file 1 luồng upload song song)
                // mỗi khi 1 file gửi xong 1 khối dữ liệu (chunk).
                void OnChunkSent(long bytesDelta)
                {
                    // Interlocked.Add: cộng dồn an toàn dù nhiều luồng cùng gọi đồng thời (race condition).
                    long sent = Interlocked.Add(ref totalBytesSent, bytesDelta);

                    bool shouldUpdateUi;
                    lock (progressLock)
                    {
                        shouldUpdateUi = uiUpdateTimer.ElapsedMilliseconds > 500 || sent >= totalBytesToSend;
                        if (shouldUpdateUi) uiUpdateTimer.Restart();
                    }

                    if (shouldUpdateUi)
                    {
                        double percent = totalBytesToSend == 0 ? 100 : sent * 100.0 / totalBytesToSend;

                        double speedBps = sent / overallStopwatch.Elapsed.TotalSeconds;
                        long remainingBytes = totalBytesToSend - sent;
                        double etaSeconds = speedBps > 0 ? remainingBytes / speedBps : 0;

                        SetProgress(percent, speedBps, TimeSpan.FromSeconds(etaSeconds));
                    }
                }

                // Tạo 1 Task upload cho MỖI file -> tất cả chạy song song, không chờ file trước xong.
                List<Task> uploadTasks = files.Select(path => UploadFileAndTrackAsync(
                    ip, port, path, OnChunkSent, uploadCts.Token)).ToList();

                // Chờ TẤT CẢ task hoàn tất. Nếu 1 task lỗi, các task còn lại sẽ bị hủy (xem UploadFileAndTrackAsync).
                await Task.WhenAll(uploadTasks);

                SetProgress(100);
                AppendLog("Hoàn tất upload toàn bộ danh sách.\n");
            }
            catch (Exception ex) when (IsUploadCanceled(ex))
            {
                AppendLog("Đã hủy upload.\n");
            }
            catch (IOException ex)
            {
                AppendLog("Upload bị ngắt: " + ex.Message + "\n");
                if (!await PingServerAsync(ip, port, 1000)) HandleServerStopped();
            }
            catch (SocketException ex)
            {
                AppendLog("Lỗi kết nối: " + ex.Message + "\n");
                if (!await PingServerAsync(ip, port, 1000)) HandleServerStopped();
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
                SetButtonState();
            }
        }

        /// <summary>
        /// Bọc UploadOneFileAsync: nếu 1 file lỗi, hủy luôn các file khác đang upload song song
        /// (để tránh các luồng còn lại tiếp tục chạy "mồ côi" sau khi đã báo lỗi tổng).
        /// </summary>
        private async Task UploadFileAndTrackAsync(string ip, int port, string path, Action<long> onBytesSent, CancellationToken token)
        {
            try
            {
                await UploadOneFileAsync(ip, port, path, onBytesSent, token);
            }
            catch
            {
                try { uploadCts?.Cancel(); } catch { }
                throw;
            }
        }

        private async Task UploadOneFileAsync(string ip, int port, string path, Action<long> onBytesSent, CancellationToken token)
        {
            FileInfo file = new FileInfo(path);
            TcpClient client = new TcpClient();

            // Nhiều file (nhiều luồng) cùng thêm/xóa khỏi list này -> phải lock để tránh lỗi dữ liệu.
            lock (uploadClientsLock)
            {
                uploadClients.Add(client);
            }

            try
            {
                if (!await ConnectWithTimeoutAsync(client, ip, port, 3000))
                {
                    throw new IOException("Không kết nối được tới Server.");
                }

                AppendLog("Đang upload: " + file.Name + "\n");

                using (NetworkStream stream = client.GetStream())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(file.Name);

                    await WriteAsync(stream, Encoding.UTF8.GetBytes("FILE"), token);
                    await WriteAsync(stream, BitConverter.GetBytes(nameBytes.Length), token);
                    await WriteAsync(stream, nameBytes, token);
                    await WriteAsync(stream, BitConverter.GetBytes(file.Length), token);

                    byte[] buffer = new byte[BufferSize];
                    long sent = 0;

                    using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        while (sent < file.Length)
                        {
                            token.ThrowIfCancellationRequested();

                            int read = await fs.ReadAsync(buffer, 0, buffer.Length, token);
                            if (read <= 0) break;

                            await stream.WriteAsync(buffer, 0, read, token);
                            hasher.AppendData(buffer, 0, read);
                            sent += read;

                            onBytesSent(read);
                        }

                        await stream.FlushAsync(token);

                        // Gửi checksum SHA-256 của file (tính ngay trong lúc đọc/gửi, không cần đọc lại file).
                        byte[] hash = hasher.GetHashAndReset();
                        await WriteAsync(stream, BitConverter.GetBytes(hash.Length), token);
                        await WriteAsync(stream, hash, token);
                        await stream.FlushAsync(token);
                    }
                }

                AppendLog("Upload xong: " + file.Name + "\n");

                lock (uploadedFilesLock)
                {
                    uploadedFiles.Add(path);
                }
            }
            finally
            {
                lock (uploadClientsLock)
                {
                    uploadClients.Remove(client);
                }
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
                MessageBox.Show("Vui lòng nhập Server IP!");
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

                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.Address.ToString();
                    }
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

                        if (!await PingServerAsync(ip, port, 1500))
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
            {
                DisconnectClient("Server đã Stop hoặc mất kết nối. Client tự động ngắt.\n");
            }
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
                {
                    uploadCts.Cancel();
                }

                CloseUploadConnections();
            }
            catch { }
        }

        /// <summary>
        /// Đóng toàn bộ kết nối TCP đang upload song song (nếu có nhiều file đang chạy cùng lúc).
        /// </summary>
        private void CloseUploadConnections()
        {
            lock (uploadClientsLock)
            {
                foreach (TcpClient c in uploadClients)
                {
                    try { c.Close(); } catch { }
                }
                uploadClients.Clear();
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

            SetButtonState();
        }

        private void SetButtonState()
        {
            bool hasPendingFile;
            lock (uploadedFilesLock)
            {
                hasPendingFile = fileList.Any(f => !uploadedFiles.Contains(f));
            }

            btnSelectFile.IsEnabled = isConnected && !isUploading;
            btnClearList.IsEnabled = isConnected && !isUploading && hasPendingFile;
            btnUpload.IsEnabled = isConnected && !isUploading && hasPendingFile;
        }

        private void SetProgress(double value, double speedBps = 0, TimeSpan? eta = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(value, speedBps, eta));
                return;
            }

            value = Math.Max(0, Math.Min(100, value));
            progressUpload.Value = value;

            if (eta.HasValue && speedBps > 0)
            {
                lblProgress.Text = $"{value:0}% | {FormatSpeed(speedBps)} | ETA: {eta.Value:mm\\:ss}";
            }
            else
            {
                lblProgress.Text = $"{value:0}%";
            }
        }

        /// <summary>
        /// Tự chọn đơn vị hiển thị: KB/s nếu tốc độ dưới 1 MB/s, MB/s nếu từ 1 MB/s trở lên.
        /// </summary>
        private string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024 * 1024)
                return (bytesPerSecond / 1024.0).ToString("0.0") + " KB/s";

            return (bytesPerSecond / 1024.0 / 1024.0).ToString("0.0") + " MB/s";
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

        protected override void OnClosed(EventArgs e)
        {
            DisconnectClient("Đã đóng Client.\n");
            base.OnClosed(e);
        }
    }
}