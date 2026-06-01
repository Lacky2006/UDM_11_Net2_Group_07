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

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            int port = int.Parse(txtPort.Text);

            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            txtLog.AppendText("Server started\n");

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();

                _ = Task.Run(async () =>
                {
                    NetworkStream stream = client.GetStream();

                    byte[] buffer = new byte[1024];
                    int bytes = await stream.ReadAsync(buffer, 0, buffer.Length);

                    string msg = Encoding.UTF8.GetString(buffer, 0, bytes);

                    Dispatcher.Invoke(() =>
                    {
                        txtLog.AppendText("Received: " + msg + "\n");
                    });

                    if (msg == "ping")
                    {
                        byte[] pong = Encoding.UTF8.GetBytes("pong");
                        await stream.WriteAsync(pong, 0, pong.Length);
                    }

                    client.Close();
                });
            }
        }
    }
}