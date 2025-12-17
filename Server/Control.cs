using Microsoft.AspNetCore.SignalR;
using Server.Services;
using Server.Shared;
using Server.helper;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
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
        // [THÊM] Dịch vụ xác thực
        private readonly AuthService _authService; 

        public ControlHub(
            SystemService systemService, 
            WebcamService webcamService, 
            InputService inputService,
            IHubContext<ControlHub> hubContext,
            AuthService authService // [THÊM] Inject AuthService
        )
        {
            _systemService = systemService;
            _webcamService = webcamService;
            _inputService = inputService;
            _hubContext = hubContext;
            _authService = authService; // [THÊM] Gán AuthService
        }
        
        // --- XỬ LÝ KẾT NỐI VÀ NGẮT KẾT NỐI ---
        public override async Task OnConnectedAsync()
        {
            // [SỬA] Gửi status chung, sau đó check trạng thái Auth
            await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, true, "Kết nối SignalR thành công.");
            await GetServerStatus(); 
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // [THÊM] Xóa phiên khi người dùng ngắt kết nối
            _authService.Logout(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        // --- LOGIC AUTHENTICATION (3 BƯỚC: Setup Code -> Register -> Login) ---
        
        /// <summary>
        /// Xác định trạng thái hiện tại của Server để Client hiển thị UI thích hợp.
        /// </summary>
        public async Task GetServerStatus()
        {
            string status;
            string message;
            
            if (_authService.IsAuthenticated(Context.ConnectionId))
            {
                status = ConnectionStatus.Authenticated;
                message = "Đã xác thực. Sẵn sàng điều khiển.";
            }
            else if (_authService.IsRegistrationAllowed(Context.ConnectionId))
            {
                 // Nếu đã nhập Master Code nhưng chưa hoàn tất đăng ký
                status = ConnectionStatus.RegistrationRequired;
                message = "Đã xác nhận Master Code. Vui lòng hoàn tất đăng ký tài khoản.";
            }
            else 
            {
                // Mặc định yêu cầu Login (User phải tự nhập Master Code nếu muốn đăng ký)
                status = ConnectionStatus.LoginRequired; 
                message = "Vui lòng đăng nhập hoặc nhập Master Code để đăng ký.";
            }

            // Gửi trạng thái chi tiết về Client
            await Clients.Caller.SendAsync("ReceiveStatus", "SERVER_STATUS", true, message);
            // Client sẽ dùng code này để chuyển đổi giữa Setup/Register/Login Form
            await Clients.Caller.SendAsync("ReceiveServerStatus", status); 
        }

        /// <summary>
        /// Xử lý Master Setup Code
        /// </summary>
        public async Task SubmitSetupCode(string code)
        {
            if (_authService.ValidateSetupCode(Context.ConnectionId, code))
            {
                // Nếu code đúng, chuyển sang trạng thái chờ đăng ký
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, true, "Mã thiết lập đúng. Vui lòng đăng ký tài khoản mới.");
                await Clients.Caller.SendAsync("ReceiveServerStatus", ConnectionStatus.RegistrationRequired);
            }
            else
            {
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, false, ErrorMessages.SetupCodeInvalid);
                await Clients.Caller.SendAsync("ReceiveServerStatus", ConnectionStatus.LoginRequired); // Về trạng thái Login
            }
        }

        /// <summary>
        /// Xử lý Đăng ký tài khoản mới (Chỉ được gọi sau khi SubmitSetupCode thành công)
        /// </summary>
        public async Task RegisterUser(string username, string password)
        {
            if (!_authService.IsRegistrationAllowed(Context.ConnectionId))
            {
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, false, ErrorMessages.RegistrationNotAllowed);
                return;
            }
            
            if (await _authService.TryRegisterAsync(Context.ConnectionId, username, password))
            {
                // Đăng ký thành công, tự động đăng nhập và chuyển sang Dashboard
                _authService.TryAuthenticate(Context.ConnectionId, username, password); 
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, true, $"Đăng ký thành công tài khoản: {username}.");
                await Clients.Caller.SendAsync("ReceiveServerStatus", ConnectionStatus.Authenticated);
            }
            else
            {
                 // Lỗi có thể do tên người dùng đã tồn tại
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, false, ErrorMessages.UsernameTaken);
            }
        }

        /// <summary>
        /// Xử lý Đăng nhập
        /// </summary>
        public async Task Login(string username, string password)
        {
            if (_authService.TryAuthenticate(Context.ConnectionId, username, password))
            {
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, true, $"Đăng nhập thành công, chào mừng {username}.");
                await Clients.Caller.SendAsync("ReceiveServerStatus", ConnectionStatus.Authenticated);
            }
            else
            {
                await Clients.Caller.SendAsync("ReceiveStatus", StatusType.Auth, false, ErrorMessages.InvalidCredentials);
            }
        }


        // --- NHÓM 1: HỆ THỐNG (LIST, START, KILL, SHUTDOWN) ---

        public async Task GetProcessList(bool isAppOnly)
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            var list = _systemService.ListProcessOrApp(isAppOnly);
            // Gửi kết quả về cho người gọi (Caller)
            string json = JsonHelper.ToJson(list);
            await Clients.Caller.SendAsync("ReceiveProcessList", json);
        }

        public async Task StartProcess(string path)
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            bool result = _systemService.startProcessOrApp(path);
            await Clients.Caller.SendAsync("ReceiveStatus", "START", result, result ? "Đã gửi lệnh mở" : "Lỗi mở file");
        }

        public async Task KillProcess(int id)
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            bool result = _systemService.killProcessOrApp(id);
            await Clients.Caller.SendAsync("ReceiveStatus", "KILL", result, result ? "Đã diệt thành công" : "Không thể diệt");
        }

        public async Task ShutdownServer(bool isRestart)
        {
           if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
           
            bool result = _systemService.shutdownOrRestart(isRestart);
            await Clients.Caller.SendAsync("ReceiveStatus", "POWER", result, "Đang thực hiện lệnh nguồn...");
        }

        // --- NHÓM 2: MÀN HÌNH & WEBCAM ---

        public async Task GetScreenshot()
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            byte[] image = _webcamService.captureScreen();
            // Gửi ảnh về Client
            await Clients.Caller.SendAsync("ReceiveImage", "SCREENSHOT", image);
        }

        // [GIỮ NGUYÊN LOGIC CŨ CỦA BẠN] Lệnh: Mở Webcam -> Quay 10s -> Gửi về -> Giữ cam mở
        // [CẢNH BÁO: LOGIC NÀY KHÔNG PHẢI LIVE STREAM]
        public async Task RequestWebcam()
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đang quay video 10 giây...");

            var token = Context.ConnectionAborted;

            try
            {
                // Gọi Service để quay (chờ khoảng 3s)
                var frames = await _webcamService.RequestWebcam(10, token);

                if (frames == null || frames.Count == 0)
                {
                    await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", false, "Lỗi: Không quay được frame nào (Cam lỗi hoặc bị chiếm).");
                    return;
                }

                await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, $"Đang gửi {frames.Count} khung hình...");

                // Gửi từng frame về Client
                foreach (var frame in frames)
                {
                    await Clients.Caller.SendAsync("ReceiveImage", "WEBCAM_FRAME", frame);
                    // Delay nhẹ để Client kịp hiển thị (tạo cảm giác như đang phát video)
                    await Task.Delay(100); 
                }
                
                await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đã gửi xong video.");
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", false, "Lỗi Server: " + ex.Message);
            }
        }
        public async Task CloseWebcam()
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            _webcamService.closeWebcam();
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đã đóng Webcam.");
        }

        // --- NHÓM 3: KEYLOGGER (INPUT) ---

        public async Task StartKeyLogger()
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            string connectionId = Context.ConnectionId;
            
            _inputService.StartKeyLogger((keyData) => 
            {
                // Fire-and-forget - không await
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveKeyLog", keyData);
                    }
                    catch
                    {
                        // Bỏ qua lỗi network
                    }
                });
                
                return Task.CompletedTask;
            });

            await Clients.Caller.SendAsync("ReceiveStatus", "KEYLOG", true, "Keylogger đã bắt đầu.");
        }

        public async Task StopKeyLogger()
        {
            if (!_authService.IsAuthenticated(Context.ConnectionId)) return; // [AUTH CHECK]
            
            _inputService.StopKeyLogger();
            await Clients.Caller.SendAsync("ReceiveStatus", "KEYLOG", false, "Keylogger đã dừng.");
        }

    }
}