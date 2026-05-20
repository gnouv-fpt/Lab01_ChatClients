using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lab01_ChatClients
{
    internal class Program
    {
        // Danh sách lưu các client đang kết nối
        static List<TcpClient> clients = new List<TcpClient>();
        // Danh sách lưu lịch sử chat
        static List<string> chatHistory = new List<string>();
        // Dùng để khóa (lock) khi có nhiều client cùng truy cập danh sách
        static readonly object _lock = new object();
        
        static readonly string HistoryFile = "history.json";
        static readonly string UploadsFolder = "Uploads";
        static bool isLocalhostOnly = false;

        static async Task Main(string[] args)
        {
            Console.Title = "TCP Chat Server";

            // Khôi phục lịch sử chat từ file JSON
            LoadHistory();

            // Tạo thư mục Uploads nếu chưa có
            if (!Directory.Exists(UploadsFolder))
            {
                Directory.CreateDirectory(UploadsFolder);
            }

            // Mở HttpListener chạy ngầm để nhận/gửi file
            _ = Task.Run(() => StartHttpFileServer());

            // Lắng nghe ở mọi IP của máy, cổng 5000 cho tin nhắn văn bản
            TcpListener server = new TcpListener(IPAddress.Any, 5000);
            server.Start();

            Console.WriteLine("=== CHAT SERVER ĐÃ BẬT ===");
            Console.WriteLine("Đang lắng nghe kết nối tại cổng 5000 (TCP)...");
            Console.WriteLine("Đang mở port 5001 (HTTP) để truyền file...");

            // Vòng lặp vô hạn để liên tục đón các client mới
            while (true)
            {
                TcpClient client = await server.AcceptTcpClientAsync();

                lock (_lock)
                {
                    clients.Add(client);
                }

                Console.WriteLine($"[+] Một client vừa kết nối! Tổng số client: {clients.Count}");

                // Tạo một luồng (thread) riêng để xử lý client này
                Thread clientThread = new Thread(() => HandleClient(client));
                clientThread.Start();
            }
        }

        static void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    string json = File.ReadAllText(HistoryFile, Encoding.UTF8);
                    var history = JsonSerializer.Deserialize<List<string>>(json);
                    if (history != null)
                    {
                        chatHistory = history;
                        Console.WriteLine($"[OK] Đã khôi phục {chatHistory.Count} tin nhắn từ lịch sử.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lỗi] Không thể đọc lịch sử: {ex.Message}");
            }
        }

        static void SaveHistory()
        {
            try
            {
                string json = JsonSerializer.Serialize(chatHistory);
                File.WriteAllText(HistoryFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lỗi] Không thể lưu lịch sử: {ex.Message}");
            }
        }

        static async Task StartHttpFileServer()
        {
            HttpListener httpListener = new HttpListener();
            try
            {
                httpListener.Prefixes.Add("http://*:5001/");
                httpListener.Start();
                Console.WriteLine("[HTTP] Đã mở port 5001 trên tất cả IP.");
            }
            catch (HttpListenerException)
            {
                Console.WriteLine("[Cảnh báo] Không đủ quyền Admin để mở port HTTP trên mọi IP (http://*:5001/).");
                Console.WriteLine("[HTTP] Đang thử tự động chuyển sang chế độ Localhost...");
                
                httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://localhost:5001/");
                httpListener.Prefixes.Add("http://127.0.0.1:5001/");
                try
                {
                    httpListener.Start();
                    isLocalhostOnly = true;
                    Console.WriteLine("[OK] Đã khởi động HTTP Server ở chế độ Localhost (127.0.0.1:5001).");
                    Console.WriteLine("       Lưu ý: Bạn chỉ có thể gửi/nhận file từ các Client chạy trên cùng máy tính này.");
                    Console.WriteLine("       Để kết nối từ máy khác, hãy chạy Server (hoặc Visual Studio) bằng quyền Administrator.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HTTP Lỗi Kịch Tỉnh] Không thể chạy HTTP Server: {ex.Message}");
                    return;
                }
            }

            while (true)
            {
                try
                {
                    HttpListenerContext context = await httpListener.GetContextAsync();
                    _ = ProcessHttpRequestAsync(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HTTP Lỗi] {ex.Message}");
                }
            }
        }

        static async Task ProcessHttpRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            try
            {
                // Xử lý Upload File
                if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/upload")
                {
                    string senderRaw = req.Headers["SenderName"];
                    string senderName = string.IsNullOrEmpty(senderRaw) ? "Ẩn danh" : Uri.UnescapeDataString(senderRaw);
                    
                    string fileRaw = req.Headers["FileName"];
                    string fileName = string.IsNullOrEmpty(fileRaw) ? $"file_{DateTime.Now.Ticks}.dat" : Uri.UnescapeDataString(fileRaw);
                    
                    // Để tránh trùng lặp tên file
                    string uniqueFileName = $"{DateTime.Now.Ticks}_{fileName}";
                    string filePath = Path.Combine(UploadsFolder, uniqueFileName);

                    using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        await req.InputStream.CopyToAsync(fs);
                    }

                    // Trả URL về cho Client
                    string ipToUse = isLocalhostOnly ? "127.0.0.1" : GetLocalIPAddress();
                    string fileUrl = $"http://{ipToUse}:5001/download/{uniqueFileName}";
                    
                    byte[] urlBytes = Encoding.UTF8.GetBytes(fileUrl);
                    res.StatusCode = 200;
                    res.ContentType = "text/plain";
                    res.ContentLength64 = urlBytes.Length;
                    await res.OutputStream.WriteAsync(urlBytes, 0, urlBytes.Length);
                    res.Close();
                    
                    Console.WriteLine($"[HTTP] Đã nhận file: {fileName} từ {senderName} và trả về URL.");
                }
                // Xử lý Download File
                else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath.StartsWith("/download/") == true)
                {
                    string fileName = req.Url.AbsolutePath.Substring("/download/".Length);
                    fileName = Uri.UnescapeDataString(fileName); // Giải mã %20 thành dấu cách, etc.
                    string filePath = Path.Combine(UploadsFolder, fileName);

                    if (File.Exists(filePath))
                    {
                        res.StatusCode = 200;
                        
                        string ext = Path.GetExtension(fileName).ToLower();
                        string contentType = "application/octet-stream";
                        if (ext == ".jpg" || ext == ".jpeg") contentType = "image/jpeg";
                        else if (ext == ".png") contentType = "image/png";
                        else if (ext == ".gif") contentType = "image/gif";
                        else if (ext == ".bmp") contentType = "image/bmp";
                        else if (ext == ".webp") contentType = "image/webp";
                        
                        res.ContentType = contentType;
                        
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            res.ContentLength64 = fs.Length;
                            await fs.CopyToAsync(res.OutputStream);
                        }
                    }
                    else
                    {
                        res.StatusCode = 404;
                    }
                    res.Close();
                }
                else
                {
                    res.StatusCode = 400;
                    res.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP Processing Lỗi] {ex.Message}");
                res.StatusCode = 500;
                res.Close();
            }
        }

        static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }

        // Hàm xử lý việc nhận và gửi tin nhắn cho từng client
        static void HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            // Bắt buộc dùng UTF8 để hỗ trợ Emoji
            StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            // 1. Vừa vào là gửi toàn bộ lịch sử chat cho client đó
            lock (_lock)
            {
                foreach (string historyMsg in chatHistory)
                {
                    writer.WriteLine(historyMsg);
                }
            }

            try
            {
                // 2. Liên tục lắng nghe tin nhắn từ client gửi lên
                while (true)
                {
                    string message = reader.ReadLine();
                    if (message == null) break; // Client đã ngắt kết nối

                    Console.WriteLine($"Nhận được: {message}");

                    // Lưu vào lịch sử
                    lock (_lock)
                    {
                        chatHistory.Add(message);
                        SaveHistory();
                    }

                    // Phát (Broadcast) tin nhắn này cho TẤT CẢ client khác
                    Broadcast(message);
                }
            }
            catch (Exception)
            {
                // Bỏ qua lỗi khi client đột ngột tắt app
            }
            finally
            {
                // 3. Xử lý khi client thoát
                lock (_lock)
                {
                    clients.Remove(client);
                }
                client.Close();
                Console.WriteLine($"[-] Một client đã thoát! Tổng số client: {clients.Count}");
            }
        }

        // Hàm gửi tin nhắn đến tất cả client đang kết nối
        public static void Broadcast(string message)
        {
            lock (_lock)
            {
                foreach (TcpClient client in clients)
                {
                    try
                    {
                        StreamWriter writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };
                        writer.WriteLine(message);
                    }
                    catch
                    {
                        // Nếu lỗi (client đã ngắt nhưng chưa kịp xóa khỏi list) thì bỏ qua
                    }
                }
            }
        }
    }
}