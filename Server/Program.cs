using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Server.Hubs;
using Server.Services;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddSignalR();

builder.Services.AddSingleton<SystemService>();
builder.Services.AddSingleton<WebcamService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<UserRepository>(); // Để lưu file users.json
builder.Services.AddSingleton<AuthService>();    // Để xử lý đăng nhập/mã chủ


builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin => true) 
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // để SignalR hoạt động
    });
});

var app = builder.Build();


// Kích hoạt CORS với policy đã định nghĩa ở trên
app.UseCors("AllowAll");

app.MapHub<ControlHub>("/controlHub");

app.MapGet("/", () => "Server điều khiển từ xa đang chạy! Hãy kết nối qua SignalR tại /controlHub");

app.Run();