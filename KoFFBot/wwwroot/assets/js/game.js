// --- ИГРОВОЙ ДВИЖОК ---
const canvas = document.getElementById("gameCanvas");
const ctx = canvas.getContext("2d");
const gridSize = 15; const tileCount = 20;
let snake = []; let dx = 0; let dy = 0; let score = 0; let level = 1;
let isGameRunning = false; let gameLoopTimer;
let food = { x: 5, y: 5 }; let cdn = { x: 10, y: 10 };
let boss = { active: false, x: 15, y: 15 };
let glitchTimer; let controlsInverted = false;
let globalTime = 0; // Для анимации пульсации

// Функция усложнения Босса
function getBossTarget() {
    return 150 + ((window.bossKills || 0) * 120);
}

// Режим Бога (Срабатывает только если window.isAdmin = true)
let cheatTaps = 0; let cheatTimer = null;
document.getElementById('gameOverOverlay').addEventListener('click', () => {
    if (!window.isAdmin) return;
    cheatTaps++; if (cheatTimer) clearTimeout(cheatTimer);
    cheatTimer = setTimeout(() => { cheatTaps = 0; }, 2000);
    if (cheatTaps >= 5) {
        cheatTaps = 0;
        score = getBossTarget();
        updateScoreUI();
        window.showToast("👾 DEV MODE: Уровень Босса активирован!");
        restartGame(true);
    }
});

function startGameUi() {
    fetch('/api/game/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Signature: "" }) })
        .then(async r => {
            if (!r.ok) { const err = await r.text(); window.tg.showAlert(err); return; }
            const res = await r.json();
            document.getElementById('energyValue').innerText = res.remainingEnergy;
            document.getElementById('gameOverlay').style.display = 'flex';
            document.getElementById('gameOverOverlay').style.display = 'none';
            score = 0; level = 1;
            showCountdownAndStart();
        }).catch(() => window.tg.showAlert("Ошибка связи с сервером."));
}
window.startGameUi = startGameUi;

function restartGame(keepScore = false) {
    fetch('/api/game/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Signature: "" }) })
        .then(async r => {
            if (!r.ok) { const err = await r.text(); window.tg.showAlert(err); return; }
            const res = await r.json();
            document.getElementById('energyValue').innerText = res.remainingEnergy;
            document.getElementById('gameOverOverlay').style.display = 'none';
            if (!keepScore) { score = 0; level = 1; }
            showCountdownAndStart();
        }).catch(() => window.tg.showAlert("Ошибка связи с сервером."));
}
window.restartGame = restartGame;

function showCountdownAndStart() {
    const counter = document.getElementById('countdownOverlay');
    counter.style.display = 'flex'; counter.style.color = 'white';
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    document.getElementById('gameTarget').innerText = getBossTarget();

    let count = 5; counter.innerText = count;
    let timer = setInterval(() => {
        count--;
        if (count > 0) { counter.innerText = count; }
        else if (count === 0) { counter.innerText = 'ВЗЛОМ!'; counter.style.color = 'var(--accent-cyan)'; }
        else {
            clearInterval(timer);
            counter.style.display = 'none';
            initGameEngine();
        }
    }, 1000);
}

function initGameEngine() {
    snake = [{ x: 10, y: 10 }, { x: 9, y: 10 }, { x: 8, y: 10 }];
    dx = 1; dy = 0; boss.active = false; controlsInverted = false;
    updateScoreUI();
    spawnFood();
    isGameRunning = true;
    gameLoop();
}

function updateScoreUI() {
    document.getElementById('gameScore').innerText = score;
    document.getElementById('gameTarget').innerText = getBossTarget();
    level = Math.floor(score / 30) + 1;
    document.getElementById('gameLevel').innerText = `УР. ${level}`;

    if (score >= getBossTarget() && !boss.active) {
        document.getElementById('gameLevel').style.color = 'var(--danger)';
    } else {
        document.getElementById('gameLevel').style.color = 'var(--accent-purple)';
    }
}

function setDir(ndx, ndy) {
    if (!isGameRunning) return;
    if (controlsInverted) { ndx = -ndx; ndy = -ndy; }
    if (ndx === 1 && dx === -1) return; if (ndx === -1 && dx === 1) return;
    if (ndy === 1 && dy === -1) return; if (ndy === -1 && dy === 1) return;
    dx = ndx; dy = ndy;
}
window.setDir = setDir;

function spawnFood() {
    food.x = Math.floor(Math.random() * tileCount); food.y = Math.floor(Math.random() * tileCount);
    cdn.x = Math.floor(Math.random() * tileCount); cdn.y = Math.floor(Math.random() * tileCount);

    if (score >= getBossTarget() && !boss.active) {
        boss.active = true; boss.x = 18; boss.y = 18;
        document.getElementById('gameLevel').innerText = "⚠ БОСС ⚠";
        window.showToast("ВНИМАНИЕ! ПОЙМАЙТЕ ВИРУС!");
    }

    if (score > 0 && score % 100 === 0) {
        window.showToast("ЗАЩИТА СЕРВЕРА АКТИВНА! ОШИБКИ ИНТЕРФЕЙСА!");
        glitchTimer = setInterval(() => {
            if (!isGameRunning) return;
            controlsInverted = true; canvas.classList.add('glitch-active');
            setTimeout(() => { controlsInverted = false; canvas.classList.remove('glitch-active'); }, 2000);
        }, 8000);
    }
}

function moveBoss() {
    if (!boss.active || !isGameRunning) return;
    let head = snake[0];
    let dist = Math.abs(boss.x - head.x) + Math.abs(boss.y - head.y);
    if (dist < 8) {
        let bdx = boss.x > head.x ? 1 : -1; let bdy = boss.y > head.y ? 1 : -1;
        if (Math.abs(boss.x - head.x) > Math.abs(boss.y - head.y)) boss.y += bdy; else boss.x += bdx;
        if (boss.x < 0) boss.x = 0; if (boss.x >= tileCount) boss.x = tileCount - 1;
        if (boss.y < 0) boss.y = 0; if (boss.y >= tileCount) boss.y = tileCount - 1;
    }
}

async function showGameOver(wonBoss) {
    isGameRunning = false; clearTimeout(gameLoopTimer); clearInterval(glitchTimer);
    document.getElementById('gameOverOverlay').style.display = 'flex';
    document.getElementById('goScore').innerText = score;

    if (wonBoss) {
        document.getElementById('goTitle').innerText = "ВИРУС ПОВЕРЖЕН!";
        document.getElementById('goTitle').style.color = "var(--success)";
        window.showToast("⏳ ИЗВЛЕЧЕНИЕ ДОСТУПА...");
        try {
            const sig = await window.generateSignature(window.userId, 1000);
            await fetch('/api/game/boss_victory', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Signature: sig }) });
            // Обновляем количество убийств в памяти
            window.bossKills = (window.bossKills || 0) + 1;
            window.tg.showAlert("🎉 ВИРУС УНИЧТОЖЕН! Вам начислено 7 дней элитного доступа!");
        } catch (e) { }
    } else {
        document.getElementById('goTitle').innerText = "ВЗЛОМ ПРЕРВАН";
        document.getElementById('goTitle').style.color = "var(--danger)";
        try {
            const sig = await window.generateSignature(window.userId, score);
            await fetch('/api/game/submit', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Score: score, Signature: sig }) });
        } catch (e) { }
    }
}

function exitGame() {
    isGameRunning = false; clearTimeout(gameLoopTimer); clearInterval(glitchTimer);
    document.getElementById('gameOverlay').style.display = 'none';
    document.getElementById('gameOverOverlay').style.display = 'none';
    document.getElementById('countdownOverlay').style.display = 'none';
    window.loadProfile(true);
}
window.exitGame = exitGame;

function gameLoop() {
    if (!isGameRunning) return;
    globalTime += 0.15;

    let head = { x: snake[0].x + dx, y: snake[0].y + dy };

    if (head.x < 0 || head.x >= tileCount || head.y < 0 || head.y >= tileCount) { showGameOver(false); return; }
    for (let i = 0; i < snake.length; i++) if (head.x === snake[i].x && head.y === snake[i].y) { showGameOver(false); return; }

    snake.unshift(head);

    if (head.x === food.x && head.y === food.y) {
        score += 10; updateScoreUI(); spawnFood();
    } else { snake.pop(); }

    if (head.x === cdn.x && head.y === cdn.y) { showGameOver(false); return; }

    if (boss.active && Math.abs(head.x - boss.x) <= 1 && Math.abs(head.y - boss.y) <= 1) {
        showGameOver(true); return;
    }

    if (Math.floor(globalTime * 10) % 10 === 0) moveBoss();

    drawGame();
    // Ускоряем базу игры за каждого убитого босса (максимум до 30ms)
    let baseSpeed = Math.max(30, 150 - ((window.bossKills || 0) * 15));
    let speed = Math.max(30, baseSpeed - (score * 0.4));
    gameLoopTimer = setTimeout(gameLoop, speed);
}

function drawGame() {
    ctx.fillStyle = 'rgba(0, 0, 0, 0.8)'; ctx.fillRect(0, 0, canvas.width, canvas.height);

    // SNI (Синяя Еда)
    ctx.fillStyle = '#00f2ff'; ctx.shadowBlur = 10; ctx.shadowColor = '#00f2ff';
    ctx.fillRect(food.x * gridSize + 2, food.y * gridSize + 2, gridSize - 4, gridSize - 4);

    // CDN (Красная угроза)
    ctx.fillStyle = '#ff4444'; ctx.shadowColor = '#ff4444';
    ctx.fillRect(cdn.x * gridSize + 2, cdn.y * gridSize + 2, gridSize - 4, gridSize - 4);

    // === БОСС (Анимированный Вирус) ===
    if (boss.active) {
        let bx = (boss.x * gridSize) + (gridSize / 2);
        let by = (boss.y * gridSize) + (gridSize / 2);

        let pulse = Math.sin(globalTime) * 3;
        let radius = (gridSize * 1.2) + pulse;

        ctx.shadowBlur = 25;
        ctx.shadowColor = '#bd93f9';

        ctx.beginPath();
        for (let i = 0; i < 8; i++) {
            let angle = (i * Math.PI) / 4 + (globalTime * 0.5);
            let tx = bx + Math.cos(angle) * (radius * 1.5);
            let ty = by + Math.sin(angle) * (radius * 1.5);
            ctx.moveTo(bx, by);
            ctx.lineTo(tx, ty);
        }
        ctx.strokeStyle = '#bd93f9';
        ctx.lineWidth = 3;
        ctx.stroke();

        ctx.beginPath();
        ctx.arc(bx, by, radius, 0, Math.PI * 2);
        ctx.fillStyle = '#8a2be2';
        ctx.fill();

        ctx.beginPath();
        ctx.arc(bx, by, radius * 0.4, 0, Math.PI * 2);
        ctx.fillStyle = '#ff0055';
        ctx.fill();
    }

    // Змейка
    ctx.shadowBlur = 0;
    for (let i = 0; i < snake.length; i++) {
        ctx.fillStyle = i === 0 ? '#ffffff' : 'var(--accent-purple)';
        ctx.fillRect(snake[i].x * gridSize + 1, snake[i].y * gridSize + 1, gridSize - 2, gridSize - 2);
    }
}