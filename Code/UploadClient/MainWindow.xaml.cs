using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace UploadClient
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnPing_Click(object sender, RoutedEventArgs e)
        {
            TcpClient client = new TcpClient();

            await client.ConnectAsync(txtIP.Text, int.Parse(txtPort.Text));

            NetworkStream stream = client.GetStream();

            byte[] ping = Encoding.UTF8.GetBytes("ping");

            await stream.WriteAsync(ping, 0, ping.Length);

            byte[] buffer = new byte[1024];

            int bytes = await stream.ReadAsync(buffer, 0, buffer.Length);

            string response = Encoding.UTF8.GetString(buffer, 0, bytes);

            txtLog.AppendText("Server Response: " + response + "\n");

            client.Close();
        }
    }
}