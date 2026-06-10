using System;
using System.Windows;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace UploadClient
{
    public partial class MainWindow : Window
    {
        public MainWindow() { InitializeComponent(); btnPing.IsEnabled = false; }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            txtLog.AppendText("Đang kiểm tra kết nối...\n");
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(txtIP.Text, int.Parse(txtPort.Text));
                    btnPing.IsEnabled = true;
                    btnPing.Background = Brushes.LightGreen;
                    txtLog.AppendText("Kết nối thành công!\n");
                }
            }
            catch { txtLog.AppendText("Lỗi: Server chưa bật!\n"); }
        }

        private async void btnPing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(txtIP.Text, int.Parse(txtPort.Text));
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes("ping");
                        await stream.WriteAsync(data, 0, data.Length);
                        byte[] buffer = new byte[1024];
                        int bytes = await stream.ReadAsync(buffer, 0, buffer.Length);
                        txtLog.AppendText("Server: " + Encoding.UTF8.GetString(buffer, 0, bytes) + "\n");
                    }
                }
            }
            catch (Exception ex) { txtLog.AppendText("Lỗi: " + ex.Message + "\n"); }
        }
    }
}