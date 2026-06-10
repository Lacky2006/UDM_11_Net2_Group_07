using System;
using System.Windows;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace UploadServer
{
    public partial class MainWindow : Window
    {
        private TcpListener listener;
        private bool isRunning = false;
        private bool isConnectedLogged = false; 

        public MainWindow() => InitializeComponent();

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning) { MessageBox.Show("Server đang chạy rồi!"); return; }

            try
            {
                int port = int.Parse(txtPort.Text);
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                isRunning = true;
                btnStart.IsEnabled = false;
                txtLog.AppendText("Server đã bắt đầu tại cổng " + port + "...\n");

                while (isRunning)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (NetworkStream stream = client.GetStream())
                            {
                                byte[] buffer = new byte[1024];
                                int bytes = await stream.ReadAsync(buffer, 0, buffer.Length);
                                string request = Encoding.UTF8.GetString(buffer, 0, bytes);

                                if (bytes > 0)
                                {
                                    
                                    string logMessage = "";
                                    if (!isConnectedLogged)
                                    {
                                        logMessage += "Có client kết nối!\n";
                                        isConnectedLogged = true;
                                    }
                                    logMessage += $"Received: {request}\n";

                                    Dispatcher.Invoke(() => txtLog.AppendText(logMessage));

                                    if (request.Contains("ping"))
                                    {
                                        byte[] response = Encoding.UTF8.GetBytes("pong");
                                        await stream.WriteAsync(response, 0, response.Length);
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Dispatcher.Invoke(() => txtLog.AppendText("Lỗi: " + ex.Message + "\n")); }
                        finally { client.Close(); }
                    });
                }
            }
            catch (Exception ex) { txtLog.AppendText("Lỗi: " + ex.Message + "\n"); }
        }
    }
}