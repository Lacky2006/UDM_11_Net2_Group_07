using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
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

        private bool isConnected = false;
        private bool isUploading = false;
        private TcpClient uploadClient;
        private CancellationTokenSource monitorCts;
        private CancellationTokenSource uploadCts;
        private readonly ObservableCollection<string> fileList = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();
            txtIP.Text = GetLocalIPv4();
            lstFiles.ItemsSource = fileList;
            DataObject.AddPastingHandler(txtPort, txtPort_Paste);
            SetConnectionState(false);
            SetProgress(0);
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

            fileList.Clear();
            SetProgress(0);
            AppendLog("Đã clear toàn bộ danh sách file.\n");
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
                string[] files = fileList.ToArray();

                for (int i = 0; i < files.Length; i++)
                {
                    uploadCts.Token.ThrowIfCancellationRequested();

                    if (!File.Exists(files[i]))
                    {
                        AppendLog("Bỏ qua file không tồn tại: " + files[i] + "\n");
                        continue;
                    }

                    AppendLog($"Upload {i + 1}/{files.Length}: {Path.GetFileName(files[i])}\n");
                    await UploadOneFileAsync(ip, port, files[i], uploadCts.Token);
                    SetProgress(100);
                    AppendLog("Upload xong: " + Path.GetFileName(files[i]) + "\n");
                }

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
                CloseUploadConnection();

                if (uploadCts != null)
                {
                    uploadCts.Dispose();
                    uploadCts = null;
                }

                isUploading = false;
                SetButtonState();
            }
        }

        private async Task UploadOneFileAsync(string ip, int port, string path, CancellationToken token)
        {
            FileInfo file = new FileInfo(path);
            TcpClient client = new TcpClient();
            uploadClient = client;

            try
            {
                if (!await ConnectWithTimeoutAsync(client, ip, port, 3000))
                {
                    throw new IOException("Không kết nối được tới Server.");
                }

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
                    SetProgress(0);

                    while (sent < file.Length)
                    {
                        token.ThrowIfCancellationRequested();

                        int read = await fs.ReadAsync(buffer, 0, buffer.Length, token);
                        if (read <= 0) break;

                        await stream.WriteAsync(buffer, 0, read, token);
                        sent += read;

                        double percent = file.Length == 0 ? 100 : sent * 100.0 / file.Length;
                        SetProgress(percent);
                    }

                    await stream.FlushAsync(token);
                }
            }
            finally
            {
                if (uploadClient == client) uploadClient = null;
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

                CloseUploadConnection();
            }
            catch { }
        }

        private void CloseUploadConnection()
        {
            try
            {
                if (uploadClient != null)
                {
                    uploadClient.Close();
                    uploadClient = null;
                }
            }
            catch { }
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
            bool hasFile = fileList.Count > 0;

            btnSelectFile.IsEnabled = isConnected && !isUploading;
            btnClearList.IsEnabled = isConnected && !isUploading && hasFile;
            btnUpload.IsEnabled = isConnected && !isUploading && hasFile;
        }

        private void SetProgress(double value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(value));
                return;
            }

            value = Math.Max(0, Math.Min(100, value));
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
