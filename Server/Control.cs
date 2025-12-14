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
using System;


namespace Server.Hubs
{
    public class ControlHub : Hub
    {
        private readonly SystemService _systemService;
        private readonly WebcamService _webcamService;
        private readonly InputService _inputService;
        private readonly IHubContext<ControlHub> _hubContext;
        private readonly IConfiguration _configuration;
        private readonly AuthService _authService; 

        public ControlHub(
            SystemService systemService, 
            WebcamService webcamService, 
            InputService inputService,
            IHubContext<ControlHub> hubContext,
            IConfiguration configuration,
            AuthService authService)
        {
            _systemService = systemService;
            _webcamService = webcamService;
            _inputService = inputService;
            _hubContext = hubContext;
            _configuration = configuration;
            _authService = authService;

        }

        // Hàm bảo vệ (Guard): Kiểm tra xem user có quyền không
        private async Task<bool> IsAuthenticated()
        {
            if (_authService.IsAuthenticated(Context.ConnectionId)) return true;
            await Clients.Caller.SendAsync("ReceiveStatus", "AUTH_FAIL", false, "Vui lòng đăng nhập để thực hiện lệnh.");
            return false;
        }
        
        // Client gọi hàm này đầu tiên để biết nên hiện form nào (Setup, Register hay Login)
        public string GetServerStatus()
        {
            if (_authService.IsAuthenticated(Context.ConnectionId)) return "AUTHENTICATED";
            if (!_authService.IsAnyUserRegistered())
            {
                if (_authService.IsRegistrationAllowed(Context.ConnectionId)) return "SETUP_REGISTER";
                return "SETUP_REQUIRED"; 
            }
            return "LOGIN_REQUIRED"; 
        }

        // Bước 1: Nộp mã khóa chủ (Master Code)
        public async Task SubmitSetupCode(string code)
        {
            if (_authService.IsAnyUserRegistered())
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "SETUP", false, "Server đã cài đặt rồi.");
                return;
            }
            if (_authService.ValidateSetupCode(Context.ConnectionId, code))
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "SETUP", true, "Mã đúng! Hãy tạo tài khoản Admin.");
            }
            else
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "SETUP", false, "Mã Khóa Chủ sai.");
            }
        }

        // Bước 2: Đăng ký tài khoản Admin đầu tiên
        public async Task RegisterUser(string username, string password)
        {
            if (!_authService.IsRegistrationAllowed(Context.ConnectionId))
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "REGISTER", false, "Chưa nhập Mã Khóa Chủ.");
                return;
            }
            if (_authService.IsUsernameTaken(username))
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "REGISTER", false, "Tên tài khoản đã tồn tại.");
                return;
            }
            if (await _authService.TryRegisterAsync(Context.ConnectionId, username, password))
            {
                _authService.TryAuthenticate(Context.ConnectionId, username, password); // Tự động login sau khi đăng ký
                await Clients.Caller.SendAsync("ReceiveStatus", "REGISTER", true, $"Tạo tài khoản {username} thành công!");
            }
            else
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "REGISTER", false, "Lỗi lưu tài khoản.");
            }
        }

        // Bước 3: Đăng nhập
        public async Task<bool> Login(string username, string password)
        {
            bool success = _authService.TryAuthenticate(Context.ConnectionId, username, password);
            if (success) await Clients.Caller.SendAsync("ReceiveStatus", "LOGIN", true, $"Chào mừng trở lại, {username}!");
            else await Clients.Caller.SendAsync("ReceiveStatus", "LOGIN", false, "Sai thông tin đăng nhập.");
            return success;
        }

        // Tự động đăng xuất khi mất kết nối
        public override Task OnDisconnectedAsync(Exception exception)
        {
            _authService.Logout(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }

        // --- NHÓM 1: HỆ THỐNG (LIST, START, KILL, SHUTDOWN) ---

        public async Task GetProcessList(bool isAppOnly)
        {
            if (!await IsAuthenticated()) return;
            var list = _systemService.ListProcessOrApp(isAppOnly);
            // Gửi kết quả về cho người gọi (Caller)
            string json = JsonHelper.ToJson(list);
            await Clients.Caller.SendAsync("ReceiveProcessList", json);
        }

        public async Task StartProcess(string path)
        {
            if (!await IsAuthenticated()) return;
            bool result = _systemService.startProcessOrApp(path);
            await Clients.Caller.SendAsync("ReceiveStatus", "START", result, result ? "Đã gửi lệnh mở" : "Lỗi mở file");
        }

        public async Task KillProcess(int id)
        {
            if (!await IsAuthenticated()) return;   
            bool result = _systemService.killProcessOrApp(id);
            await Clients.Caller.SendAsync("ReceiveStatus", "KILL", result, result ? "Đã diệt thành công" : "Không thể diệt");
        }

        public async Task ShutdownServer(bool isRestart)
        {
            if (!await IsAuthenticated()) return;
            bool result = _systemService.shutdownOrRestart(isRestart);
            await Clients.Caller.SendAsync("ReceiveStatus", "POWER", result, "Đang thực hiện lệnh nguồn...");
        }

        // --- NHÓM 2: MÀN HÌNH & WEBCAM ---

        public async Task GetScreenshot()
        {
            if (!await IsAuthenticated()) return;
            byte[] image = _webcamService.captureScreen();
            // Gửi ảnh về Client
            await Clients.Caller.SendAsync("ReceiveImage", "SCREENSHOT", image);
        }

        // Lệnh: Mở Webcam -> Quay 3s -> Gửi về -> Giữ cam mở
        public async Task RequestWebcam()
        {
            // Gửi thông báo đang xử lý
            if (!await IsAuthenticated()) return;
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
            if (!await IsAuthenticated()) return;
            _webcamService.closeWebcam();
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đã đóng Webcam.");
        }

        // --- NHÓM 3: KEYLOGGER (INPUT) ---

        public async Task StartKeyLogger()
        {
            if (!await IsAuthenticated()) return;
            string connectionId = Context.ConnectionId;
            
            // Bắt đầu lắng nghe và gửi từng phím về Client
            _inputService.StartKeyLogger(async (keyData) => 
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveKeyLog", keyData);
            });

            await Clients.Caller.SendAsync("ReceiveStatus", "KEYLOG", true, "Keylogger đã bắt đầu.");
        }

        public async Task StopKeyLogger()
        {
            if (!await IsAuthenticated()) return;
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
                - Nhập  IP của máy Server vào ô trên cùng bên phải.
                - Bấm nút 'Kết nối'. Nếu thành công, đèn sẽ chuyển xanh.
                
                LƯU Ý AN TOÀN:
                - Lệnh Shutdown/Restart và Kill Process rất nguy hiểm, hãy nhắc người dùng cẩn thận.
            ";

            if (!string.IsNullOrEmpty(apiKey))
            {
                try 
                {
                    string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";
                    
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
                            JsonHelper.ToJson(requestData), 
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