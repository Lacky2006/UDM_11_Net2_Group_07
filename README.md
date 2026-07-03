# UDM_11_Net2_Group_07

**Cách chạy project bằng Visual Studio 2026**

Bước 1: Clone project
git clone https://github.com/Lacky2006/UDM_11_Net2_Group_07.git
Bước 2: Mở project
Mở Visual Studio 2026, chọn:
File → Open → Project/Solution
Sau đó mở project trong thư mục:
Code/UploadServer
Code/UploadClient
Bước 3: Build project
Chọn:
Build → Rebuild Solution
Nếu build thành công, file chạy sẽ nằm trong thư mục:
bin/Debug
hoặc:
bin/Release

**Cách sử dụng ứng dụng**
1. Chạy Server
Mở UploadServer.exe.
Kiểm tra IP LAN hiển thị trên giao diện.
Nhập Port, ví dụ:
9000
Nhấn Start Server.
Khi Server chạy thành công, trạng thái sẽ chuyển sang:
Status: Running
Server lúc này đã sẵn sàng nhận kết nối từ Client.
2. Chạy Client trong cùng mạng LAN
Mở UploadClient.exe.
Nhập IP của máy Server.
Ví dụ:
192.168.1.10
Nhập Port giống với Server.
Ví dụ:
9000
Nhấn Connect.
Nếu kết nối thành công, trạng thái Client sẽ chuyển sang:
Connected
Chọn file bằng nút chọn file hoặc kéo thả file vào giao diện.
Nhấn Upload để bắt đầu gửi file.
File sau khi upload sẽ được lưu ở máy Server trong thư mục:
Uploads
