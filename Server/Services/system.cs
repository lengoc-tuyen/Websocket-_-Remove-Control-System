using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.RuntimeInformation;


namespace Server.Services
{
    public class SystemService
    {

        // hàm này liệt kê tiến trình hoặc app (2 hàm kế ở dưới hỗ trợ)
        public List<ProcessInfo> ListRunningProcesses(bool isAppOnly = false)
        {
            List<ProcessInfo> list = new List<ProcessInfo>(); // 1 danh sách để chứa cái tiến trình
            Process[] allProcesses = Process.GetProcesses();  // lấy các tiến trình
            
            bool isWindows = IsOSPlatform(OSPlatform.Windows); // check coi phải Win ko

            if (isAppOnly && !isWindows) // nếu là mac hoặc linux
            {
                return GetAppsOnUnix(allProcesses);
            }

            foreach (Process p in allProcesses)
            {
                try
                {
                    if (p.Id == 0) continue;
                    
                    string windowTitle = p.MainWindowTitle;

                    if (isAppOnly)
                    {
                        if (string.IsNullOrEmpty(windowTitle)) // nếu ko có title thì bỏ qua
                        {
                            continue; 
                        }
                    }
                    
                    // Đóng gói dữ liệu
                    list.Add(new ProcessInfo
                    {
                        Id = p.Id,
                        Name = p.ProcessName,
                        Title = windowTitle,
                        MemoryUsage = p.WorkingSet64
                    });
                }
                catch {}
            }

            return list.OrderBy(p => p.Name).ToList();
        }

        private List<ProcessInfo> GetAppsOnUnix(Process[] allProcesses)
        {
            List<ProcessInfo> apps = new List<ProcessInfo>();
            
            string psOutput = ExecuteShellCommand("ps -axco pid", "sh"); // dùng lệnh ps.. để liệt kê tiến trình đang chạy
            
            //chúng ta sẽ chỉ lọc các tiến trình có tên App 
            // mà không phải là các daemon hệ thống.
            
            foreach (Process p in allProcesses)
            {   
                try
                {
                    if (string.IsNullOrEmpty(p.MainWindowTitle)) continue; // bỏ mấy cái ko có title như win

                    // này kiểu lọc mấy cái tiến trình hệ thống
                    if (p.MainModule?.FileName.StartsWith("/usr/bin/", StringComparison.OrdinalIgnoreCase) == true ||
                        p.MainModule?.FileName.StartsWith("/sbin/", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        continue;
                    }

                    // 3. Đóng gói dữ liệu
                    apps.Add(new ProcessInfo
                    {
                        Id = p.Id,
                        Name = p.ProcessName,
                        Title = p.MainWindowTitle,
                        MemoryUsage = p.WorkingSet64
                    });
                }
                catch {}
            }
            return apps.OrderBy(p => p.Name).ToList();
        }

        private string ExecuteShellCommand(string command, string shell = "/bin/bash")
        {
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    Arguments = $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return result;
        }

        // Start process hoặc app
        public bool StartProcess(string processPath)
        {
            if (string.IsNullOrEmpty(processPath)) return false;

            string baseName = Path.GetFileNameWithoutExtension(processPath);

            if (Process.GetProcessesByName(baseName).Length > 0)
            {
                Console.WriteLine($"Process {processPath} is already running. Skipping new instance.");
                return false; 
            }
            
            try
            {
                Process.Start(processPath); 
                return true;
            }
            catch (Exception ex)
            {
                if (IsOSPlatform(OSPlatform.Windows) && !processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string exePath = processPath + ".exe";
                        Process.Start(exePath);
                        return true;
                    }
                    catch (Exception innerEx)
                    {
                        Console.WriteLine($"Error starting process {processPath}.exe: {innerEx.Message}");
                    }
                }
                else if (IsOSPlatform(OSPlatform.OSX))
                {
                    try
                    {
                        string command = $"open -a \"{processPath}\"";
                        ExecuteShellCommand(command, "sh"); 
                        return true;
                    }
                    catch(Exception innerEx)
                    {
                        Console.WriteLine($"Error starting process {processPath} on OSX: {innerEx.Message}");
                    }
                }
                
                Console.WriteLine($"Error starting process {processPath}: {ex.Message}");
                return false;
            }
        }

        // Hàm này stop app, process
        public bool KillProcessById(int processId)
        {
            try
            {
                Process p = Process.GetProcessById(processId);
                
                if (p != null)
                {
                    if (!p.CloseMainWindow())
                    {
                        p.Kill(); // đóng bình thường ko được thì ép  nó đóng
                    }
                    p.WaitForExit(1000);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }


        // Hàm này để restart, shutdown máy (Sử dụng lệnh hệ thống thay vì API)
        public bool ShutdownComputer(bool isRestart)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "shutdown",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (IsOSPlatform(OSPlatform.Windows))
                {
                    // /r: restart, /s: shutdown, /t 0: ngay lập tức, /f: force (ép tắt ứng dụng đang chạy)
                    psi.Arguments = isRestart ? "/r /t 0 /f" : "/s /t 0 /f";
                }
                else if (IsOSPlatform(OSPlatform.OSX) || IsOSPlatform(OSPlatform.Linux))
                {
                    psi.Arguments = isRestart ? "-r now" : "-h now";
                }
                else
                {
                    Console.WriteLine("Shutdown command not implemented for this OS.");
                    return false;
                }

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing power command: {ex.Message}");
                return false;
            }
        }


    }
};

