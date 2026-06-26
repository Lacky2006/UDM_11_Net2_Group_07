using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace UploadClient
{
    /// <summary>
    /// Model đại diện cho 1 file trong danh sách (1 dòng trong DataGrid
    /// và 1 khối trong panel "TRẠNG THÁI UPLOAD").
    /// Implement INotifyPropertyChanged để UI tự cập nhật khi Status/Percent thay đổi.
    /// </summary>
    public class FileItem : INotifyPropertyChanged
    {
        // Đường dẫn đầy đủ trên đĩa, dùng để đọc file khi upload.
        public string FullPath { get; set; }

        private int stt;
        public int STT
        {
            get => stt;
            set { stt = value; OnPropertyChanged(nameof(STT)); }
        }

        public string FileName { get; set; }
        public string FileIcon { get; set; }
        public string Size { get; set; }

        // "Waiting" | "Uploading" | "Paused" | "Done"
        private string status = "Waiting";
        public string Status
        {
            get => status;
            set
            {
                status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(PanelVisibility));
                OnPropertyChanged(nameof(DetailText));
            }
        }

        // Màu chấm tròn trạng thái + màu progress bar, tương ứng với ảnh mẫu.
        public Brush StatusColor
        {
            get
            {
                switch (status)
                {
                    case "Uploading":
                        return new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // vàng
                    case "Paused":
                        return new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)); // xanh dương
                    case "Done":
                        return new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)); // xanh lá
                    default:
                        return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // xám (Waiting)
                }
            }
        }

        private double percent;
        public double Percent
        {
            get => percent;
            set
            {
                percent = value;
                OnPropertyChanged(nameof(Percent));
                OnPropertyChanged(nameof(PercentText));
            }
        }

        public string PercentText => $"{percent:0}%";

        // "Speed: 2 MB/s" trong lúc upload, để trống khi Waiting.
        private string speedText = "";
        public string SpeedText
        {
            get => speedText;
            set
            {
                speedText = value;
                OnPropertyChanged(nameof(SpeedText));
                OnPropertyChanged(nameof(DetailText));
            }
        }

        // Dòng chữ nhỏ hiển thị trong panel bên phải (giống "Speed: 2 MB/s" / "Done" trong ảnh).
        public string DetailText
        {
            get
            {
                if (status == "Done") return "Done";
                if (status == "Paused") return "Paused";
                if (status == "Uploading") return string.IsNullOrEmpty(speedText) ? "Uploading..." : speedText;
                return "";
            }
        }

        // Panel bên phải chỉ hiện file đang/đã upload, ẩn file còn "Waiting" — giống ảnh mẫu
        // (document.pdf "Waiting" chỉ nằm trong bảng dưới, không xuất hiện ở panel phải).
        public Visibility PanelVisibility =>
            status == "Waiting" ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}