let connection = null;
let isConnected = false;

const serverIpInput = document.getElementById("serverIpInput");

function buildConnectionUrl(input) {
    let url = input.trim();

    if (!url.startsWith("http")) {
        url = "http://" + url;
    }

    const colonCount = (url.match(/:/g) || []).length;
    if (colonCount < 2) {
        url += ":5000";
    }

    if (!url.endsWith("/controlHub")) {
        if (url.endsWith("/")) url = url.slice(0, -1);
        url += "/controlHub";
    }

    return url;
}

async function connect() {
    const rawIp = serverIpInput.value;
    const finalUrl = buildConnectionUrl(rawIp);

    setStatus("Đang kết nối tới: " + finalUrl);

    connection = new signalR.HubConnectionBuilder()
        .withUrl(finalUrl)
        .withAutomaticReconnect()
        .build();

    connection.on("ReceiveProcessList", (json) => {
        try {
            const list = JSON.parse(json);
            if (document.getElementById("appsSection").style.display !== "none") {
                renderTable("appsTableBody", list, "selectedApp");
            } else {
                renderTable("processesTableBody", list, "selectedProcess");
            }
            setStatus(`Đã tải ${list.length} mục.`);
        } catch (e) {
            console.error("Lỗi parse JSON:", e);
            setStatus("Lỗi dữ liệu từ Server.");
        }
    });

    connection.on("ReceiveStatus", (type, success, message) => {
        setStatus(`[${type}] ${message}`);
        if (!success) alert(`Lỗi từ Server: ${message}`);
    });

    connection.on("ReceiveImage", (type, base64Data) => {
        const src = "data:image/jpeg;base64," + base64Data;
        if (type === "SCREENSHOT") {
            document.getElementById("screenPreview").src = src;
            setStatus("Đã nhận ảnh màn hình.");
        } else if (type === "WEBCAM_FRAME") {
            document.getElementById("webcamPreview").src = src;
        }
    });


    connection.on("ReceiveKeyLog", (key) => {
        const area = document.getElementById("keylogArea");
        area.value += key;
        area.scrollTop = area.scrollHeight;
    });

    connection.on("ReceiveChatMessage", (message) => {
        if (window.ui && window.ui.addChatMessage) {
            ui.showTyping(false);
            ui.addChatMessage(message, 'bot');
        }
   });

    try {
        await connection.start();
        isConnected = true;
        updateConnectionUI(true);
        setStatus("Kết nối thành công!");
    } catch (err) {
        console.error(err);
        setStatus("Kết nối thất bại: " + err.toString());
        alert("Không thể kết nối tới Server. Hãy kiểm tra IP và chắc chắn Server đang chạy.");
    }

    connection.onclose(() => {
        isConnected = false;
        updateConnectionUI(false);
        setStatus("Mất kết nối với Server.");
    });
}

async function disconnect() {
    if (connection) await connection.stop();
    isConnected = false;
    updateConnectionUI(false);
    setStatus("Đã ngắt kết nối.");
}

function checkConn() {
    if (!isConnected) {
        alert("Vui lòng kết nối tới Server trước!");
        return false;
    }
    return true;
}

function wireActionButtons() {
    document.getElementById("refreshAppsBtn").addEventListener("click", () => {
        if (checkConn()) {
            setStatus("Đang tải danh sách App...");
            connection.invoke("GetProcessList", true);
        }
    });

    document.getElementById("refreshProcessesBtn").addEventListener("click", () => {
        if (checkConn()) {
            setStatus("Đang tải danh sách Process...");
            connection.invoke("GetProcessList", false);
        }
    });

    document.getElementById("startAppBtn").addEventListener("click", () => {
        const name = document.getElementById("appNameInput").value;
        if (checkConn() && name) connection.invoke("StartProcess", name);
        else if (!name) alert("Vui lòng nhập tên ứng dụng!");
    });

    document.getElementById("stopSelectedAppBtn").addEventListener("click", () => {
        const id = getSelectedId("selectedApp");
        if (checkConn()) {
            if (id) connection.invoke("KillProcess", id);
            else alert("Vui lòng chọn một App trong danh sách!");
        }
    });

    document.getElementById("stopSelectedProcessBtn").addEventListener("click", () => {
        const id = getSelectedId("selectedProcess");
        if (checkConn()) {
            if (id) connection.invoke("KillProcess", id);
            else alert("Vui lòng chọn một Process trong danh sách!");
        }
    });

    document.getElementById("captureScreenBtn").addEventListener("click", () => {
        if (checkConn()) {
            setStatus("Đang yêu cầu chụp màn hình...");
            connection.invoke("GetScreenshot");
        }
    });

    document.getElementById("webcamOnBtn").addEventListener("click", () => {
        if (checkConn()) {
            setStatus("Đang yêu cầu Webcam...");
            connection.invoke("RequestWebcamProof");
        }
    });

    document.getElementById("webcamOffBtn").addEventListener("click", () => {
        if (checkConn()) connection.invoke("CloseWebcam");
    });

    document.getElementById("startKeylogBtn").addEventListener("click", () => {
        if (checkConn()) connection.invoke("StartKeyLogger");
    });

    document.getElementById("stopKeylogBtn").addEventListener("click", () => {
        if (checkConn()) connection.invoke("StopKeyLogger");
    });

    document.getElementById("clearKeylogBtn").addEventListener("click", () => {
        document.getElementById("keylogArea").value = "";
    });

    document.getElementById("restartBtn").addEventListener("click", () => {
        if (checkConn() && confirm("CẢNH BÁO: Bạn có chắc muốn RESTART máy Server ngay lập tức?")) {
            connection.invoke("ShutdownServer", true);
        }
    });

    document.getElementById("shutdownBtn").addEventListener("click", () => {
        if (checkConn() && confirm("CẢNH BÁO: Bạn có chắc muốn TẮT MÁY Server ngay lập tức?")) {
            connection.invoke("ShutdownServer", false);
        }
    });

    const sendChatBtn = document.getElementById("sendChatBtn");
    if (sendChatBtn) {
        sendChatBtn.addEventListener("click", () => {
            const input = document.getElementById("chatInput");
            const text = input.value.trim();
            if (!text) return;

            // 1. Hiện tin nhắn user
            if(window.ui && window.ui.addChatMessage) {
                ui.addChatMessage(text, 'user');
            }
            input.value = "";

            // 2. Check kết nối
            if (!isConnected) {
                if(window.ui) ui.addChatMessage("⚠️ Chưa kết nối Server!", 'bot');
                return;
            }

            // 3. Gửi lên Server
            if(window.ui) ui.showTyping(true);
            
            connection.invoke("ChatWithAi", text).catch(err => {
                if(window.ui) {
                    ui.showTyping(false);
                    ui.addChatMessage("Lỗi: " + err.toString(), 'bot');
                }
            });
        });
    }
}

// Gắn sự kiện cho nút Connect chính
if(toggleConnectBtn) {
    toggleConnectBtn.addEventListener("click", () => {
        if (!isConnected) connect();
        else disconnect();
    });
}

// Khởi tạo các sự kiện khi trang web load xong
document.addEventListener("DOMContentLoaded", wireActionButtons);