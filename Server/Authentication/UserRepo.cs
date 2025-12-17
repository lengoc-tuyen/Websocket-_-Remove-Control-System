using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Server.Models; 
using System.Text.Json; // [GIỮ NGUYÊN] Dù không dùng cho TXT, giữ để tránh lỗi compile nếu có tham chiếu
using System.IO; 
using System; 

namespace Server.Services
{
    public class UserRepository
    {
        private const string USER_FILE_PATH = "users.txt";

        private readonly List<User> _users = new List<User>();

        public UserRepository()
        {
            LoadUsers();
        }

        private void LoadUsers()
        {
            if (!File.Exists(USER_FILE_PATH)) return;

            try
            {
                string[] lines = File.ReadAllLines(USER_FILE_PATH);
                var loadedUsers = new List<User>();

                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    
                    if (parts.Length == 2)
                    {
                        loadedUsers.Add(new User
                        {
                            Username = parts[0].Trim(),
                            PasswordHash = parts[1].Trim()
                        });
                    }
                }

                if (loadedUsers.Any())
                {
                    _users.Clear();
                    _users.AddRange(loadedUsers);
                    Console.WriteLine($"[UserRepo] Đã tải {loadedUsers.Count} người dùng từ {USER_FILE_PATH} (TXT format).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserRepo ERROR] Lỗi tải dữ liệu người dùng: {ex.Message}");
            }
        }

        private void SaveUsers()
        {
            try
            {
                var lines = _users.Select(u => $"{u.Username},{u.PasswordHash}");
                
                File.WriteAllLines(USER_FILE_PATH, lines);

                Console.WriteLine($"[UserRepo] Đã lưu {(_users?.Count ?? 0)} người dùng vào {USER_FILE_PATH}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserRepo ERROR] Lỗi lưu dữ liệu người dùng: {ex.Message}");
            }
        }

        public bool IsAnyUserRegistered()
        {
            return _users.Any();
        }

        public bool IsUsernameTaken(string username)
        {
            return _users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }

        public User? GetUser(string username)
        {
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> AddUserAsync(string username, string password)
        {
            if (IsUsernameTaken(username)) return false;

            _users.Add(new User 
            { 
                Username = username, 
                PasswordHash = password
            });
            
            SaveUsers(); 

            await Task.Delay(1); 
            return true;
        }
    }
}