using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _isConnected = false;
        private bool _isUploading = false;
        private bool _isPaused = false;

        private CancellationTokenSource _uploadCts;

        private readonly ObservableCollection<FileUploadItem> _waitingFiles =
            new ObservableCollection<FileUploadItem>();

        private readonly ObservableCollection<FileUploadItem> _uploadingFiles =
            new ObservableCollection<FileUploadItem>();

        private FileUploadItem _currentUploadItem;

        public MainWindow()
        {
            InitializeComponent();

            dgWaitingFiles.ItemsSource = _waitingFiles;
            lstUploading.ItemsSource = _uploadingFiles;

            SetDisconnectedState();
            UpdateButtons();
        }

        // ==========================
        // Kiểm tra nhập IP + Port
        // ==========================
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void txtPort_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        private bool IsValidInput()
        {
            IPAddress ipAddress;
            bool validIp = IPAddress.TryParse(txtServerIp.Text.Trim(), out ipAddress);

            int port;
            bool validPort = int.TryParse(txtPort.Text.Trim(), out port)
                             && port > 0
                             && port <= 65535;

            return validIp && validPort;
        }

        private void UpdateButtons()
        {
            if (btnConnect == null)
                return;

            btnConnect.IsEnabled = !_isConnected && IsValidInput();
            btnDisconnect.IsEnabled = _isConnected;

            btnSelectFiles.IsEnabled = !_isUploading;
            btnClearList.IsEnabled = _waitingFiles.Count > 0 && !_isUploading;

            btnUpload.IsEnabled = _isConnected && _waitingFiles.Count > 0 && !_isUploading;

            btnPause.IsEnabled = _isConnected && _isUploading && !_isPaused;
            btnResume.IsEnabled = _isConnected && _isUploading && _isPaused;
        }

        // ==========================
        // Connect Server
        // ==========================
        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!IsValidInput())
            {
                MessageBox.Show("Vui lòng nhập đúng Server IP và Port.",
                    "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = txtServerIp.Text.Trim();
            int port = int.Parse(txtPort.Text.Trim());

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(IPAddress.Parse(ip), port);

                _stream = _client.GetStream();

                SetConnectedState();
            }
            catch (Exception ex)
            {
                SetDisconnectedState();

                MessageBox.Show("Không kết nối được tới Server.\n\n" + ex.Message,
                    "Lỗi kết nối", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==========================
        // Disconnect Server
        // ==========================
        private void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectClient();
        }

        private void DisconnectClient()
        {
            try
            {
                if (_uploadCts != null)
                    _uploadCts.Cancel();

                if (_stream != null)
                    _stream.Close();

                if (_client != null)
                    _client.Close();
            }
            catch
            {
                // Bỏ qua lỗi khi đóng kết nối
            }
            finally
            {
                _stream = null;
                _client = null;

                _isUploading = false;
                _isPaused = false;
                _currentUploadItem = null;

                SetDisconnectedState();
            }
        }

        private void SetConnectedState()
        {
            _isConnected = true;

            txtStatus.Text = "Connected";
            statusLed.Fill = Brushes.LimeGreen;

            txtServerIp.IsEnabled = false;
            txtPort.IsEnabled = false;

            UpdateButtons();
        }

        private void SetDisconnectedState()
        {
            _isConnected = false;

            txtStatus.Text = "Disconnected";
            statusLed.Fill = Brushes.Gray;

            txtServerIp.IsEnabled = true;
            txtPort.IsEnabled = true;

            UpdateButtons();
        }

        // ==========================
        // Select Files
        // ==========================
        private void btnSelectFiles_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Chọn file để upload";
            dialog.Multiselect = true;
            dialog.Filter = "All files (*.*)|*.*";

            if (dialog.ShowDialog() == true)
            {
                AddFilesToWaitingList(dialog.FileNames);
            }
        }

        // ==========================
        // Drag & Drop
        // ==========================
        private void dropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;

            e.Handled = true;
        }

        private void dropZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);

            string[] files = paths
                .Where(p => File.Exists(p))
                .ToArray();

            AddFilesToWaitingList(files);
        }

        private void AddFilesToWaitingList(string[] filePaths)
        {
            foreach (string path in filePaths)
            {
                if (!File.Exists(path))
                    continue;

                bool existed = _waitingFiles.Any(f => f.FilePath == path)
                               || _uploadingFiles.Any(f => f.FilePath == path && f.Status != "Done");

                if (existed)
                    continue;

                FileInfo info = new FileInfo(path);

                FileUploadItem item = new FileUploadItem();
                item.Index = _waitingFiles.Count + 1;
                item.FilePath = path;
                item.FileName = info.Name;
                item.SizeBytes = info.Length;
                item.SizeText = FormatFileSize(info.Length);
                item.Status = "Waiting";
                item.Progress = 0;
                item.SubText = "Waiting";
                item.BarBrush = Brushes.Gray;
                item.CancelVisibility = Visibility.Collapsed;

                _waitingFiles.Add(item);
            }

            ReIndexWaitingFiles();
            UpdateButtons();
        }

        // ==========================
        // Clear List
        // ==========================
        private void btnClearList_Click(object sender, RoutedEventArgs e)
        {
            _waitingFiles.Clear();

            ReIndexWaitingFiles();
            UpdateButtons();
        }

        // ==========================
        // Upload
        // ==========================
        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _stream == null || _client == null)
            {
                MessageBox.Show("Bạn chưa kết nối Server.",
                    "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_waitingFiles.Count == 0)
            {
                MessageBox.Show("Chưa có file nào để upload.",
                    "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (FileUploadItem file in _waitingFiles.ToList())
            {
                file.Status = "Waiting";
                file.Progress = 0;
                file.SubText = "Waiting";
                file.BarBrush = Brushes.Gray;
                file.CancelVisibility = Visibility.Collapsed;
                file.IsCancelled = false;

                _uploadingFiles.Add(file);
            }

            _waitingFiles.Clear();

            ReIndexWaitingFiles();
            ReIndexUploadingFiles();

            _isUploading = true;
            _isPaused = false;

            _uploadCts = new CancellationTokenSource();

            UpdateButtons();

            try
            {
                await UploadFilesSequentiallyAsync(_uploadCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Upload bị hủy
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi upload:\n\n" + ex.Message,
                    "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUploading = false;
                _isPaused = false;
                _currentUploadItem = null;

                HideCancelButtons();

                UpdateButtons();
            }
        }

        private async Task UploadFilesSequentiallyAsync(CancellationToken token)
        {
            foreach (FileUploadItem item in _uploadingFiles.ToList())
            {
                if (token.IsCancellationRequested)
                    break;

                if (item.IsCancelled)
                    continue;

                if (item.Status == "Done")
                    continue;

                _currentUploadItem = item;

                try
                {
                    await EnsureConnectedAsync();

                    await UploadOneFileAsync(item, token);

                    if (!item.IsCancelled)
                    {
                        item.Status = "Done";
                        item.Progress = 100;
                        item.SubText = "Done";
                        item.BarBrush = Brushes.LimeGreen;
                        item.CancelVisibility = Visibility.Collapsed;
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    item.SubText = "Cancelled";
                    item.CancelVisibility = Visibility.Collapsed;

                    if (_uploadingFiles.Contains(item))
                        _uploadingFiles.Remove(item);

                    ReIndexUploadingFiles();

                    await ReconnectAfterCancelAsync();
                }
                catch (Exception ex)
                {
                    item.Status = "Error";
                    item.SubText = ex.Message;
                    item.BarBrush = Brushes.Red;
                    item.CancelVisibility = Visibility.Collapsed;

                    await ReconnectAfterCancelAsync();
                }
            }
        }

        private async Task UploadOneFileAsync(FileUploadItem item, CancellationToken token)
        {
            if (_stream == null)
                throw new IOException("Chưa có kết nối tới Server.");

            item.Status = "Uploading";
            item.SubText = "Speed: 0 KB/s";
            item.BarBrush = Brushes.DeepSkyBlue;

            byte[] fileNameBytes = Encoding.UTF8.GetBytes(item.FileName);

            BinaryWriter writer = new BinaryWriter(_stream, Encoding.UTF8, true);

            writer.Write(fileNameBytes.Length);
            writer.Write(fileNameBytes);
            writer.Write(item.SizeBytes);
            writer.Flush();

            byte[] buffer = new byte[8192];

            long totalSent = 0;
            long lastSent = 0;
            DateTime lastSpeedTime = DateTime.Now;

            using (FileStream fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read))
            {
                int bytesRead;

                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await WaitIfPausedAsync(item, token);

                    if (item.IsCancelled)
                        throw new OperationCanceledException();

                    if (_stream == null)
                        throw new IOException("Mất kết nối Server.");

                    await _stream.WriteAsync(buffer, 0, bytesRead, token);

                    totalSent += bytesRead;

                    double percent = totalSent * 100.0 / item.SizeBytes;
                    item.Progress = percent;

                    TimeSpan elapsed = DateTime.Now - lastSpeedTime;

                    if (elapsed.TotalMilliseconds >= 500)
                    {
                        long bytesDiff = totalSent - lastSent;
                        double speed = bytesDiff / elapsed.TotalSeconds;

                        item.SubText = "Speed: " + FormatFileSize((long)speed) + "/s";

                        lastSent = totalSent;
                        lastSpeedTime = DateTime.Now;
                    }
                }
            }

            if (_stream != null)
                await _stream.FlushAsync(token);

            item.Progress = 100;
        }

        // ==========================
        // Pause
        // ==========================
        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUploading)
                return;

            _isPaused = true;

            foreach (FileUploadItem item in _uploadingFiles)
            {
                if (item.Status != "Done" && item.Status != "Error")
                {
                    item.Status = "Paused";
                    item.SubText = "Paused";
                    item.CancelVisibility = Visibility.Visible;
                }
            }

            UpdateButtons();
        }

        // ==========================
        // Resume
        // ==========================
        private void btnResume_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUploading)
                return;

            _isPaused = false;

            foreach (FileUploadItem item in _uploadingFiles)
            {
                if (item.Status == "Paused")
                {
                    item.Status = "Waiting";
                    item.SubText = "Waiting";
                    item.CancelVisibility = Visibility.Collapsed;
                }
            }

            UpdateButtons();
        }

        private async Task WaitIfPausedAsync(FileUploadItem item, CancellationToken token)
        {
            while (_isPaused)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException();

                if (item.IsCancelled)
                    throw new OperationCanceledException();

                await Task.Delay(150, token);
            }

            if (item.Status == "Waiting" || item.Status == "Paused")
            {
                item.Status = "Uploading";
                item.SubText = "Uploading...";
            }
        }

        // ==========================
        // X hủy file khi Pause
        // ==========================
        private void btnCancelUploadItem_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;

            if (btn == null)
                return;

            FileUploadItem item = btn.Tag as FileUploadItem;

            if (item == null)
                return;

            item.IsCancelled = true;

            if (item == _currentUploadItem)
            {
                item.Status = "Cancelled";
                item.SubText = "Cancelled";
                item.CancelVisibility = Visibility.Collapsed;
            }
            else
            {
                if (_uploadingFiles.Contains(item))
                    _uploadingFiles.Remove(item);

                ReIndexUploadingFiles();
            }

            UpdateButtons();
        }

        private void HideCancelButtons()
        {
            foreach (FileUploadItem item in _uploadingFiles)
            {
                item.CancelVisibility = Visibility.Collapsed;
            }
        }

        // ==================================================
        // Nếu hủy file đang gửi giữa chừng thì reconnect
        // ==================================================
        private async Task ReconnectAfterCancelAsync()
        {
            if (!_isConnected)
                return;

            try
            {
                if (_stream != null)
                    _stream.Close();

                if (_client != null)
                    _client.Close();

                _stream = null;
                _client = null;

                await Task.Delay(300);

                await EnsureConnectedAsync();
            }
            catch
            {
                SetDisconnectedState();
            }
        }

        private async Task EnsureConnectedAsync()
        {
            if (_client != null && _client.Connected && _stream != null)
                return;

            string ip = txtServerIp.Text.Trim();
            int port = int.Parse(txtPort.Text.Trim());

            _client = new TcpClient();

            await _client.ConnectAsync(IPAddress.Parse(ip), port);

            _stream = _client.GetStream();

            SetConnectedState();
        }

        // ==========================
        // Đánh lại STT
        // ==========================
        private void ReIndexWaitingFiles()
        {
            for (int i = 0; i < _waitingFiles.Count; i++)
            {
                _waitingFiles[i].Index = i + 1;
            }

            dgWaitingFiles.Items.Refresh();
        }

        private void ReIndexUploadingFiles()
        {
            for (int i = 0; i < _uploadingFiles.Count; i++)
            {
                _uploadingFiles[i].Index = i + 1;
            }

            lstUploading.Items.Refresh();
        }

        // ==========================
        // Format dung lượng file
        // ==========================
        private string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";

            if (bytes >= 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0).ToString("F2") + " MB";

            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("F2") + " KB";

            return bytes + " B";
        }

        protected override void OnClosed(EventArgs e)
        {
            DisconnectClient();
            base.OnClosed(e);
        }
    }

    public class FileUploadItem : INotifyPropertyChanged
    {
        private int _index;
        private string _status = "";
        private double _progress;
        private string _subText = "";
        private Brush _barBrush = Brushes.Gray;
        private Visibility _cancelVisibility = Visibility.Collapsed;

        public int Index
        {
            get { return _index; }
            set
            {
                _index = value;
                OnPropertyChanged("Index");
            }
        }

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public string SizeText { get; set; }

        public bool IsCancelled { get; set; }

        public string Status
        {
            get { return _status; }
            set
            {
                _status = value;
                OnPropertyChanged("Status");
            }
        }

        public double Progress
        {
            get { return _progress; }
            set
            {
                _progress = value;
                OnPropertyChanged("Progress");
                OnPropertyChanged("ProgressText");
            }
        }

        public string ProgressText
        {
            get { return Progress.ToString("F0") + "%"; }
        }

        public string SubText
        {
            get { return _subText; }
            set
            {
                _subText = value;
                OnPropertyChanged("SubText");
            }
        }

        public Brush BarBrush
        {
            get { return _barBrush; }
            set
            {
                _barBrush = value;
                OnPropertyChanged("BarBrush");
            }
        }

        public Visibility CancelVisibility
        {
            get { return _cancelVisibility; }
            set
            {
                _cancelVisibility = value;
                OnPropertyChanged("CancelVisibility");
            }
        }

        public FileUploadItem()
        {
            FilePath = "";
            FileName = "";
            SizeText = "";
            Status = "Waiting";
            SubText = "Waiting";
            IsCancelled = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;

            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}