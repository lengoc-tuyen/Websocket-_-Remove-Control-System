const statusDot = document.getElementById("statusDot");
const statusText = document.getElementById("statusText");
const toggleConnectBtn = document.getElementById("toggleConnectBtn");
const statusBarText = document.getElementById("statusBarText");
const lastUpdated = document.getElementById("lastUpdated");

function setStatus(msg) {
    statusBarText.textContent = msg;
    if (lastUpdated) {
        lastUpdated.textContent = new Date().toLocaleTimeString();
    }
    console.log(msg);
}

function updateConnectionUI(connected) {
    if (connected) {
        statusDot.className = "status-dot status-connected";
        statusText.textContent = "Connected";
        toggleConnectBtn.textContent = "Ngắt kết nối";
        toggleConnectBtn.classList.remove("btn-primary");
        toggleConnectBtn.classList.add("btn-danger");
    } else {
        statusDot.className = "status-dot status-disconnected";
        statusText.textContent = "Disconnected";
        toggleConnectBtn.textContent = "Kết nối";
        toggleConnectBtn.classList.add("btn-primary");
        toggleConnectBtn.classList.remove("btn-danger");
    }
}

function getSelectedId(groupName) {
    const el = document.querySelector(`input[name="${groupName}"]:checked`);
    return el ? parseInt(el.value) : null;
}

function renderTable(tbodyId, data, groupName) {
    const tbody = document.getElementById(tbodyId);
    tbody.innerHTML = "";

    if (data.length === 0) {
        tbody.innerHTML = "<tr><td colspan='5' style='text-align:center; padding:10px;'>Không có dữ liệu</td></tr>";
        return;
    }

    data.forEach((p) => {
        const tr = document.createElement("tr");
        tr.innerHTML = `
            <td style="text-align:center;"><input type="radio" name="${groupName}" value="${p.id}"></td>
            <td>${p.id}</td>
            <td style="font-weight:bold; color:#2563eb;">${p.name}</td>
            <td>${p.title || '-'}</td>
            <td>${(p.memoryUsage / 1024 / 1024).toFixed(1)}</td>
        `;
        tbody.appendChild(tr);
    });
}

const tabButtons = document.querySelectorAll(".tab-btn");
const sections = {
    appsSection: document.getElementById("appsSection"),
    processesSection: document.getElementById("processesSection"),
    screenSection: document.getElementById("screenSection"),
    keysSection: document.getElementById("keysSection"),
    webcamSection: document.getElementById("webcamSection"),
    powerSection: document.getElementById("powerSection"),
};

function initTabs() {
    tabButtons.forEach((btn) => {
        btn.addEventListener("click", () => {
            tabButtons.forEach((b) => b.classList.remove("active"));
            btn.classList.add("active");
            const target = btn.getAttribute("data-target");
            Object.keys(sections).forEach((key) => {
                sections[key].style.display = key === target ? "block" : "none";
            });
        });
    });
}

initTabs();

// ===== Snowman Hover + Click behaviour =====
document.addEventListener("DOMContentLoaded", () => {
    const snowmanBubble = document.getElementById("snowmanBubble");
    const snowmanImg = document.querySelector("#snowman-chatbot .snowman-img");
    if (!snowmanBubble || !snowmanImg) return;

    const showBubble = (text) => {
        snowmanBubble.textContent = text;
        snowmanBubble.style.opacity = "1";
        snowmanBubble.style.transform = "translateY(0)";
    };

    const hideBubble = () => {
        snowmanBubble.style.opacity = "0";
        snowmanBubble.style.transform = "translateY(10px)";
    };

    // Hover -> hiện câu
    snowmanImg.addEventListener("mouseenter", () => {
        showBubble("Bạn cần giúp gì không?");
    });

    snowmanImg.addEventListener("mouseleave", () => {
        hideBubble();
    });

    // Click -> mở chat (không phụ thuộc hover)
    snowmanImg.addEventListener("click", () => {
        toggleChat();
    });
});


// ===== Snowman Chat Window Logic =====
function toggleChat() {
    const chat = document.getElementById("chat-window");
    if (!chat) return;

    const isOpen = chat.classList.contains("open");
    if (isOpen) {
        chat.classList.remove("open");
        setTimeout(() => {
            if (!chat.classList.contains("open"))
                chat.style.display = "none";
        }, 180);
    } else {
        chat.style.display = "flex";
        requestAnimationFrame(() => chat.classList.add("open"));
    }
}

function sendChatUI() {
    const input = document.getElementById("chat-input");
    const box = document.getElementById("chat-messages");
    if (!input || !box) return;

    const text = input.value.trim();
    if (!text) return;

    const userMsg = document.createElement("div");
    userMsg.className = "msg-user";
    userMsg.textContent = text;
    box.appendChild(userMsg);

    input.value = "";

    const botMsg = document.createElement("div");
    botMsg.className = "msg-bot";
    botMsg.textContent = "Mình nhận được rồi nè ☃️";
    setTimeout(() => {
        box.appendChild(botMsg);
        box.scrollTop = box.scrollHeight;
    }, 300);

    box.scrollTop = box.scrollHeight;
}
