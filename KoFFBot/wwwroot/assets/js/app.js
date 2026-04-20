window.tg = window.Telegram.WebApp;
try { window.tg.ready(); window.tg.expand(); if (window.tg.setHeaderColor) window.tg.setHeaderColor('#0f0f13'); } catch (e) { }

window.userId = window.tg.initDataUnsafe?.user?.id || 0;
window.isAdmin = false;
window.GAME_SECRET = "";

let hasLoadedInbox = false;
let pollingInterval = null;
let lastUnreadCount = 0;

window.generateSignature = async function (tgId, value) {
    const payload = `${tgId}:${value}:${window.GAME_SECRET}`;
    const encoder = new TextEncoder();
    const key = await crypto.subtle.importKey('raw', encoder.encode(window.GAME_SECRET), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const signatureBuffer = await crypto.subtle.sign('HMAC', key, encoder.encode(payload));
    return btoa(String.fromCharCode(...new Uint8Array(signatureBuffer)));
}

function showToast(msg) {
    try { window.tg.HapticFeedback.notificationOccurred('success'); } catch (e) { }
    const toast = document.getElementById('toast'); toast.innerHTML = `<span>🔔</span> ${msg}`; toast.classList.add('show'); setTimeout(() => toast.classList.remove('show'), 4000);
}
window.showToast = showToast;

function startPolling() {
    if (pollingInterval) clearInterval(pollingInterval);
    pollingInterval = setInterval(() => {
        if (window.userId === 0) return;
        fetch(`/api/webapp/inbox/unread?tgId=${window.userId}`).then(r => r.json()).then(data => {
            const currentUnread = data.unreadCount || 0;
            if (currentUnread > 0) {
                document.getElementById('inboxBadge').style.display = 'block';
                if (currentUnread > lastUnreadCount) { showToast("Новое сообщение от поддержки!"); loadProfile(true); if (document.getElementById('tab-inbox').classList.contains('active')) { loadInbox(); } }
            } else { document.getElementById('inboxBadge').style.display = 'none'; }
            lastUnreadCount = currentUnread;
        }).catch(() => { });
    }, 4000);
}

function switchTab(tabId, element) {
    document.querySelectorAll('.tab-content').forEach(el => el.classList.remove('active'));
    document.querySelectorAll('.nav-item').forEach(el => el.classList.remove('active'));
    document.getElementById('tab-' + tabId).classList.add('active'); element.classList.add('active');
    if (tabId === 'inbox') { loadInbox(); document.getElementById('inboxBadge').style.display = 'none'; }
    if (tabId === 'leaderboard') { loadLeaderboard(); }
}
window.switchTab = switchTab;

function toggleGuide(headerEl) {
    const content = headerEl.nextElementSibling; content.classList.toggle('open');
    headerEl.querySelector('span').innerText = content.classList.contains('open') ? '▲' : '▼';
}
window.toggleGuide = toggleGuide;

function checkUnread() {
    if (window.userId === 0) return;
    fetch(`/api/webapp/inbox/unread?tgId=${window.userId}`).then(r => r.json()).then(data => { if (data.unreadCount > 0) document.getElementById('inboxBadge').style.display = 'block'; }).catch(() => { });
}

function loadLeaderboard() {
    if (window.userId === 0) return;
    fetch(`/api/game/leaderboard`).then(r => r.json()).then(data => {
        const container = document.getElementById('lbContainer');
        container.innerHTML = '';
        if (!data.topPlayers || data.topPlayers.length === 0) {
            container.innerHTML = '<div style="text-align: center; color: var(--text-muted);">Пока нет рекордов. Станьте первым!</div>';
        } else {
            data.topPlayers.forEach((p, index) => {
                let rankClass = index === 0 ? 'rank-1' : (index === 1 ? 'rank-2' : (index === 2 ? 'rank-3' : ''));
                container.innerHTML += `
                        <div class="lb-item ${rankClass}">
                            <div class="lb-rank">#${index + 1}</div>
                            <div class="lb-name">${p.name}</div>
                            <div class="lb-score">${p.maxScore}</div>
                        </div>`;
            });
        }
    }).catch(() => {
        document.getElementById('lbContainer').innerHTML = '<div style="text-align: center; color: var(--danger);">Ошибка загрузки</div>';
    });
}
window.loadLeaderboard = loadLeaderboard;

function loadInbox() {
    if (window.userId === 0) return;
    fetch(`/api/webapp/inbox?tgId=${window.userId}`).then(r => r.json()).then(msgs => {
        const container = document.getElementById('chatContainer');
        if (!msgs || msgs.length === 0) { container.innerHTML = '<div style="text-align: center; color: var(--text-muted); margin-top: 20px;">Тут пока пусто.</div>'; return; }
        container.innerHTML = '';
        msgs.forEach(m => {
            const div = document.createElement('div'); div.className = 'msg-bubble ' + (m.isFromAdmin ? 'msg-admin' : 'msg-system');
            div.innerHTML = `${m.text.replace(/\n/g, '<br>')}<div class="msg-time">${m.createdAt || ''}</div>`; container.appendChild(div);
        });
        container.scrollTop = container.scrollHeight; hasLoadedInbox = true; document.getElementById('inboxBadge').style.display = 'none'; lastUnreadCount = 0;
    }).catch(() => { });
}
window.loadInbox = loadInbox;

function sendAdminMessage() {
    const input = document.getElementById('chatInput'); const text = input.value.trim(); if (!text) return; input.value = '';
    fetch('/api/webapp/send_message', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Text: text }) })
        .then(async r => { if (!r.ok) { const err = await r.text(); window.tg.showAlert(err || "Ошибка отправки."); } else { loadInbox(); showToast("Отправлено администратору"); } }).catch(() => { window.tg.showAlert("Ошибка связи."); });
}
window.sendAdminMessage = sendAdminMessage;

function buyTariff(name, price) {
    window.tg.showConfirm(`Отправить заявку на тариф "${name}" за ${price} руб? Администратор пришлет реквизиты в Инбокс.`, function (agreed) {
        if (agreed) fetch('/api/webapp/buy', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, TariffName: name }) }).then(() => { showToast('Заявка отправлена! Ожидайте ответа.'); }).catch(() => { });
    });
}
window.buyTariff = buyTariff;

function showRefLink() {
    const link = `https://t.me/VPNNewsEvryDay_bot?start=${window.userId}`; const box = document.getElementById('refLink'); box.innerText = link;
    document.getElementById('refContainer').style.display = 'block'; copyTextToClipboard(link, "✅ Реферальная ссылка скопирована!");
}
window.showRefLink = showRefLink;

function copyRefLink() { copyTextToClipboard(document.getElementById('refLink').innerText, "✅ Реферальная ссылка скопирована!"); }
window.copyRefLink = copyRefLink;

function copyKey() { if (window.vpnLinkToCopy) copyTextToClipboard(window.vpnLinkToCopy, "✅ Ключ скопирован!"); }
window.copyKey = copyKey;

function copyTextToClipboard(text, successMsg) {
    if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(() => showToast(successMsg)); }
    else { const tmp = document.createElement("input"); tmp.value = text; document.body.appendChild(tmp); tmp.select(); document.execCommand("copy"); document.body.removeChild(tmp); showToast(successMsg); }
}

function formatBytesToHTML(bytes) {
    if (bytes === 0) return '0.0<span class="stat-unit">МБ</span>';
    const k = 1024; const sizes = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ']; const i = Math.floor(Math.log(bytes) / Math.log(k));
    const val = parseFloat((bytes / Math.pow(k, i)).toFixed(2)); return val + `<span class="stat-unit">${sizes[i]}</span>`;
}

function loadProfile(isSilent = false) {
    if (window.userId === 0 && !isSilent) { document.getElementById('loader').style.animation = 'none'; document.getElementById('loader').innerHTML = "⚠️ Откройте через Telegram."; return; }

    fetch(`/api/game/profile?tgId=${window.userId}`).then(r => r.json()).then(gData => {
        if (!gData.isBanned) {
            document.getElementById('energyValue').innerText = gData.energy || 0;
            window.bossKills = gData.bossKills || 0;
            window.monthlyBossKills = gData.monthlyBossKills || 0; // Сохраняем лимиты месяца!
        }
    }).catch(() => { });

    fetch(`/api/webapp/profile?tgId=${window.userId}`).then(async r => {
        if (!r.ok) throw new Error(r.status === 404 ? "not_found" : "error"); return r.json();
    }).then(data => {
        if (!isSilent) {
            document.getElementById('loader').style.display = 'none';

            // === ВОТ ЭТА СТРОКА: ГАРАНТИРОВАННО ВКЛЮЧАЕТ МЕНЮ ===
            document.getElementById('bottomNav').style.display = 'flex';

            if (!document.querySelector('.tab-content.active')) { document.getElementById('tab-profile').classList.add('active'); }
        }

        window.isAdmin = data.isAdmin === true;
        window.GAME_SECRET = data.gameSecret || "";

        document.getElementById('userIdDisplay').innerText = data.telegramId || window.userId; document.getElementById('refCount').innerText = (data.referralCount || 0) + " друзей";

        if (data.hasSubscription) {
            document.getElementById('state-no-sub').style.display = 'none'; document.getElementById('state-has-sub').style.display = 'block'; document.getElementById('userName').innerText = data.firstName || "Пользователь";

            const limit = data.trafficLimit || 0; const used = data.trafficUsed || 0;

            if (limit === 0) {
                document.getElementById('trafficValue').innerHTML = formatBytesToHTML(used); document.getElementById('trafficSub').innerText = '∞ Безлимит';
                document.getElementById('trafficBar').style.width = '100%'; document.getElementById('trafficBar').className = "progress-fill fill-cyan";
            } else {
                const left = Math.max(0, limit - used); document.getElementById('trafficValue').innerHTML = formatBytesToHTML(left);
                document.getElementById('trafficSub').innerText = `из ${formatBytesToHTML(limit).replace(/<[^>]*>?/gm, '')}`;
                document.getElementById('trafficBar').className = left <= (limit * 0.1) ? "progress-fill fill-danger" : "progress-fill fill-cyan";
                document.getElementById('trafficBar').style.width = `${Math.min((used / limit) * 100, 100)}%`;
            }

            if (data.expiryDate && data.expiryDate !== null) {
                try {
                    const expDate = new Date(data.expiryDate); const diffDays = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
                    const dText = document.getElementById('daysLeft'); const tBar = document.getElementById('timeBar');
                    document.getElementById('expiryDateText').innerText = `до ${expDate.toLocaleDateString('ru-RU')}`;

                    if (diffDays > 0) {
                        dText.innerText = diffDays + ' дн.'; tBar.style.width = `${Math.max(0, Math.min((diffDays / 30) * 100, 100))}%`;
                        if (diffDays <= 3) { dText.style.color = "var(--danger)"; tBar.className = "progress-fill fill-danger"; } else { dText.style.color = "var(--text-main)"; tBar.className = "progress-fill fill-purple"; }
                    } else { dText.innerText = 'Истек'; dText.style.color = "var(--danger)"; tBar.style.width = '0%'; tBar.className = "progress-fill fill-danger"; }
                } catch (e) { document.getElementById('daysLeft').innerText = "∞"; }
            } else { document.getElementById('daysLeft').innerText = "∞"; document.getElementById('expiryDateText').innerText = "Бессрочно"; document.getElementById('timeBar').style.width = '100%'; }

            window.vpnLinkToCopy = `http://${data.serverIp || '0.0.0.0'}:8080/${data.uuid || ''}`; document.getElementById('keyLink').innerText = window.vpnLinkToCopy;
        } else { document.getElementById('state-has-sub').style.display = 'none'; document.getElementById('state-no-sub').style.display = 'block'; }

        if (!isSilent) { checkUnread(); startPolling(); }
    }).catch(e => { if (!isSilent) { document.getElementById('loader').style.animation = 'none'; document.getElementById('loader').innerHTML = e.message === "not_found" ? "⚠️ Отправьте боту /start" : "❌ Ошибка сервера."; } });
}

window.loadProfile = loadProfile;

function generateVpn(btn) {
    btn.innerHTML = "⏳ СОЗДАЕМ..."; btn.disabled = true;
    fetch('/api/webapp/generate', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Action: 'generate' }) })
        .then(res => { if (!res.ok) throw new Error("Ошибка"); setTimeout(() => loadProfile(false), 1000); })
        .catch(err => { window.tg.showAlert("❌ Нет доступных серверов резерва."); btn.innerHTML = "🚀 ЗАПУСТИТЬ ТУННЕЛЬ"; btn.disabled = false; });
}
window.generateVpn = generateVpn;

loadProfile(false);

function toggleInfoModal() {
    const modal = document.getElementById('infoModal');
    if (modal.style.display === 'flex') {
        modal.style.display = 'none';
    } else {
        modal.style.display = 'flex';
    }
}
window.toggleInfoModal = toggleInfoModal;

// === ВОССТАНОВЛЕННЫЙ РЕЖИМ БОГА ДЛЯ ЭНЕРГИИ ===
let energyCheatTaps = 0;
let energyCheatTimer = null;
window.handleCheatTap = function () {
    if (!window.isAdmin) return;
    energyCheatTaps++;
    if (energyCheatTimer) clearTimeout(energyCheatTimer);
    energyCheatTimer = setTimeout(() => { energyCheatTaps = 0; }, 2000);

    if (energyCheatTaps >= 5) {
        energyCheatTaps = 0;
        fetch('/api/game/cheat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ TelegramId: window.userId, Signature: "" })
        })
            .then(r => r.json())
            .then(res => {
                if (res.newEnergy) {
                    document.getElementById('energyValue').innerText = res.newEnergy;
                    window.showToast("👾 DEV MODE: +50 Энергии выдано!");
                }
            }).catch(() => { window.tg.showAlert("Ошибка связи с сервером."); });
    }
};