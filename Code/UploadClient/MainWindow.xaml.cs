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
        public MainWindow()
        {
            InitializeComponent();
            btnPing.IsEnabled = false;
        }


        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            txtLog.AppendText("Đang kiểm tra kết nối...\n");
            btnConnect.IsEnabled = false; 

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    
                    var connectTask = client.ConnectAsync(txtIP.Text, int.Parse(txtPort.Text));
                    if (await Task.WhenAny(connectTask, Task.Delay(2000)) == connectTask)
                    {
                        await connectTask; 
                        btnPing.IsEnabled = true;
                        btnPing.Background = Brushes.LightGreen;
                        txtLog.AppendText("Kết nối thành công!\n");
                    }
                    else
                    {
                        throw new Exception("Server không phản hồi!");
                    }
                }
            }
            catch (Exception ex)
            {
                txtLog.AppendText("Lỗi: " + ex.Message + "\n");
                btnPing.IsEnabled = false;
            }
            finally
            {
                btnConnect.IsEnabled = true;
            }
        }

        
        private async void btnPing_Click(object sender, RoutedEventArgs e)
        {
            txtLog.AppendText("Đang ping...\n");
            btnPing.IsEnabled = false;

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
                        string resp = Encoding.UTF8.GetString(buffer, 0, bytes);

                        txtLog.AppendText("Server: " + resp + "\n");
                    }
                }
            }
            catch (Exception ex)
            {
                txtLog.AppendText("Lỗi: " + ex.Message + "\n");
            }
            finally
            {
                btnPing.IsEnabled = true;
            }
        }
    }
}