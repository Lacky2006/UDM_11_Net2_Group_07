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

private async void btnStart_Click(object sender, RoutedEventArgs e)
{
    if (isRunning) { MessageBox.Show("Server đang chạy rồi!"); return; }

    try
    {
        int port = int.Parse(txtPort.Text); // Đảm bảo đã nhập port
        listener = new TcpListener(System.Net.IPAddress.Any, port);
        listener.Start();
        isRunning = true;
        txtLog.AppendText("Server đã bắt đầu tại cổng " + port + "...\n");

        
        await Task.Run(async () => {
            while (isRunning) {
                TcpClient client = await listener.AcceptTcpClientAsync();
                HandleClient(client); 
            }
        });
    }
    catch (Exception ex)
    {
        isRunning = false;
        MessageBox.Show("Lỗi khởi tạo Server: " + ex.Message);
    }
}

private async void HandleClient(TcpClient client)
{
    try {
        using (NetworkStream stream = client.GetStream()) {
            byte[] buffer = new byte[1024];
            int bytes = await stream.ReadAsync(buffer, 0, buffer.Length);
            string request = Encoding.UTF8.GetString(buffer, 0, bytes);
            
            if (request.Contains("ping")) {
                byte[] response = Encoding.UTF8.GetBytes("pong");
                await stream.WriteAsync(response, 0, response.Length);
            }
        }
    }
    catch { /* Bỏ qua lỗi kết nối nhỏ */ }
    finally { client.Close(); }
}