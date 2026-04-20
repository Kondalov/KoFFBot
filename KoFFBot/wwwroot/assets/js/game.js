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

// === СИСТЕМА ДИАГНОСТИКИ (ТЕЛЕМЕТРИЯ) ===
window.gameLogger = function (msg) {
    console.log("[DIAGNOSTICS] " + msg);
    try {
        // Передаем лог ПРЯМО В URL, так как query-параметры (?) обрезаются сервером
        let safeMsg = encodeURIComponent(msg.replace(/ /g, '_'));
        fetch('/api/webapp/profile/LOG_' + safeMsg).catch(() => { });
    } catch (e) { }
};

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

window.isStarting = false;
window.isCountingDown = false; // НОВАЯ БЛОКИРОВКА ОТ ДВОЙНЫХ КЛИКОВ

function startGameUi() {
    window.gameLogger("startGameUi_CLICKED");
    try {
        fetch('/api/game/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Signature: "" }) })
            .then(async r => {
                window.gameLogger("startGameUi_FETCH_RETURNED_Status_" + r.status);
                if (!r.ok) { const err = await r.text(); window.tg.showAlert(err); return; }
                const res = await r.json();
                document.getElementById('energyValue').innerText = res.remainingEnergy;
                document.getElementById('gameOverlay').style.display = 'flex';
                document.getElementById('gameOverOverlay').style.display = 'none';
                score = 0; level = 1;
                showCountdownAndStart();
            }).catch((e) => {
                window.gameLogger("startGameUi_FETCH_ERROR_" + e.message);
                window.tg.showAlert("Ошибка связи с сервером.");
            });
    } catch (err) {
        window.gameLogger("startGameUi_CRITICAL_ERROR_" + err.message);
    }
}
window.startGameUi = startGameUi;

function restartGame(keepScore = false) {
    window.gameLogger("restartGame_CLICKED");
    try {
        fetch('/api/game/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ TelegramId: window.userId, Signature: "" }) })
            .then(async r => {
                window.gameLogger("restartGame_FETCH_RETURNED_Status_" + r.status);
                if (!r.ok) { const err = await r.text(); window.tg.showAlert(err); return; }
                const res = await r.json();
                document.getElementById('energyValue').innerText = res.remainingEnergy;
                document.getElementById('gameOverOverlay').style.display = 'none';
                if (!keepScore) { score = 0; level = 1; }
                showCountdownAndStart();
            }).catch((e) => {
                window.gameLogger("restartGame_FETCH_ERROR_" + e.message);
                window.tg.showAlert("Ошибка связи с сервером.");
            });
    } catch (err) {
        window.gameLogger("restartGame_CRITICAL_ERROR_" + err.message);
    }
}
window.restartGame = restartGame;

function showCountdownAndStart() {
    try {
        window.gameLogger("showCountdownAndStart_STARTED");
        const counter = document.getElementById('countdownOverlay');
        counter.style.display = 'flex';
        counter.style.color = 'white';
        counter.style.fontSize = ''; // Сбрасываем размер на дефолтный

        document.getElementById('gameTarget').innerText = getBossTarget();

        // Отрисовываем стартовое поле ДО начала отсчета, чтобы экран не был пустым!
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        snake = [{ x: 10, y: 10 }, { x: 9, y: 10 }, { x: 8, y: 10 }];
        dx = 1; dy = 0; boss.active = false; controlsInverted = false;
        window.lastBossTarget = -1; // Сбрасываем блокировку спавна при новом запуске

        updateScoreUI();
        food.x = 5; food.y = 5; cdn.x = 15; cdn.y = 15; // Дефолтные позиции для визуала
        drawGame(); // Рисуем кадр под таймером

        let count = 5; counter.innerText = count;

        window.gameLogger("showCountdownAndStart_TIMER_CREATED");
        let timer = setInterval(() => {
            try {
                count--;
                window.gameLogger("TIMER_TICK_" + count);

                if (count > 0) {
                    counter.innerText = count;
                }
                else if (count === 0) {
                    window.gameLogger("TIMER_SHOWING_VZMLOM");
                    counter.innerText = 'ВЗЛОМ!';
                    counter.style.color = 'var(--accent-cyan)';
                    // Динамически сжимаем текст, чтобы он не вылезал за границы мобильного экрана
                    counter.style.fontSize = 'clamp(30px, 12vw, 80px)';
                }
                else {
                    window.gameLogger("TIMER_CLEARING_AND_INITING_ENGINE");
                    clearInterval(timer);
                    counter.style.display = 'none';
                    counter.style.fontSize = ''; // Сброс для будущих запусков
                    initGameEngine();
                }
            } catch (e) {
                window.gameLogger("TIMER_CRITICAL_ERROR_" + e.name + "_" + e.message);
            }
        }, 1000);
    } catch (e) {
        window.gameLogger("showCountdownAndStart_CRITICAL_ERROR_" + e.name + "_" + e.message);
    }
}

function initGameEngine() {
    try {
        window.gameLogger("initGameEngine_CALLED");
        snake = [{ x: 10, y: 10 }, { x: 9, y: 10 }, { x: 8, y: 10 }];
        dx = 1; dy = 0; boss.active = false; controlsInverted = false;
        updateScoreUI();
        spawnFood();
        isGameRunning = true;
        window.gameLogger("initGameEngine_STARTING_LOOP");
        gameLoop();
    } catch (e) {
        window.gameLogger("initGameEngine_CRITICAL_ERROR_" + e.name + "_" + e.message);
    }
}

function updateScoreUI() {
    try {
        document.getElementById('gameScore').innerText = score;
        document.getElementById('gameTarget').innerText = getBossTarget();
        level = Math.floor(score / 30) + 1;
        document.getElementById('gameLevel').innerText = `УР. ${level}`;

        if (score >= getBossTarget() && !boss.active) {
            document.getElementById('gameLevel').style.color = 'var(--danger)';
        } else {
            document.getElementById('gameLevel').style.color = 'var(--accent-purple)';
        }
    } catch (e) {
        window.gameLogger("updateScoreUI_CRITICAL_ERROR_" + e.name + "_" + e.message);
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
    try {
        window.gameLogger("spawnFood_CALLED");

        // 1. Спавним синюю еду, избегая змейку и босса
        let isFoodValid = false;
        while (!isFoodValid) {
            food.x = Math.floor(Math.random() * tileCount);
            food.y = Math.floor(Math.random() * tileCount);

            let conflict = false;
            if (boss.active && Math.abs(food.x - boss.x) <= 1 && Math.abs(food.y - boss.y) <= 1) conflict = true;
            for (let i = 0; i < snake.length; i++) {
                if (snake[i].x === food.x && snake[i].y === food.y) conflict = true;
            }
            if (!conflict) isFoodValid = true;
        }

        // 2. Спавним красный узел, избегая змейку, босса и свежую синюю еду
        let isCdnValid = false;
        while (!isCdnValid) {
            cdn.x = Math.floor(Math.random() * tileCount);
            cdn.y = Math.floor(Math.random() * tileCount);

            let conflict = false;
            if (boss.active && Math.abs(cdn.x - boss.x) <= 1 && Math.abs(cdn.y - boss.y) <= 1) conflict = true;
            if (cdn.x === food.x && cdn.y === food.y) conflict = true;
            for (let i = 0; i < snake.length; i++) {
                if (snake[i].x === cdn.x && snake[i].y === cdn.y) conflict = true;
            }
            if (!conflict) isCdnValid = true;
        }

        if (score >= getBossTarget() && !boss.active && window.lastBossTarget !== getBossTarget()) {
            boss.active = true;
            boss.x = 18; boss.y = 18;
            window.bossSpawnTime = Date.now();
            window.lastBossTarget = getBossTarget();
            document.getElementById('gameLevel').innerText = "⚠ БОСС ⚠";
            window.showToast(window.monthlyBossKills >= 2 ? "КРИТИЧЕСКИЙ УРОВЕНЬ! ВИРУС МУТИРОВАЛ!" : "ВНИМАНИЕ! ПОЙМАЙТЕ ВИРУС ЗА 20 СЕК!");
        }

        if (score > 0 && score % 100 === 0 && !boss.active) {
            window.showToast("СИСТЕМА ВЗЛОМАНА!");

            isGameRunning = false;
            clearTimeout(gameLoopTimer);

            const counter = document.getElementById('countdownOverlay');
            counter.style.display = 'flex';
            counter.style.flexDirection = 'column';
            counter.style.textAlign = 'center'; // Выравнивание текста по центру
            counter.style.color = 'var(--danger)';
            counter.style.fontSize = 'clamp(18px, 5vw, 35px)'; // Адаптивный шрифт под длинный текст

            let count = 5;
            counter.innerHTML = `ВНИМАНИЕ!<br>УПРАВЛЕНИЕ ИЗМЕНЕНО!<br><span style="font-size: 2em; margin-top: 10px;">${count}</span>`;

            glitchTimer = setInterval(() => {
                try {
                    count--;
                    if (count > 0) {
                        counter.innerHTML = `ВНИМАНИЕ!<br>УПРАВЛЕНИЕ ИЗМЕНЕНО!<br><span style="font-size: 2em; margin-top: 10px;">${count}</span>`;
                    } else {
                        clearInterval(glitchTimer);
                        counter.style.display = 'none';
                        counter.style.fontSize = '';
                        counter.style.textAlign = ''; // Сбрасываем стили
                        counter.style.flexDirection = '';

                        controlsInverted = true;
                        canvas.classList.add('glitch-active');
                        isGameRunning = true;
                        gameLoop();

                        setTimeout(() => {
                            controlsInverted = false;
                            canvas.classList.remove('glitch-active');
                        }, 8000);
                    }
                } catch (e) {
                    window.gameLogger("GLITCH_TIMER_CRASH_" + e.name + "_" + e.message);
                }
            }, 1000);
        }

        window.gameLogger("spawnFood_SUCCESS");
    } catch (e) {
        window.gameLogger("spawnFood_CRITICAL_ERROR_" + e.name + "_" + e.message);
    }
}

function moveBoss() {
    if (!boss.active || !isGameRunning) return;

    let head = snake[0];
    let distToHead = Math.abs(boss.x - head.x) + Math.abs(boss.y - head.y);
    let isGodMode = (window.monthlyBossKills >= 2); // Включаем "Неуловимого Босса"

    if (!isGodMode && distToHead > 10) {
        boss.x += (boss.x < tileCount / 2) ? 1 : (boss.x > tileCount / 2 ? -1 : 0);
        boss.y += (boss.y < tileCount / 2) ? 1 : (boss.y > tileCount / 2 ? -1 : 0);
        return;
    }

    let moves = [{ dx: 0, dy: -1 }, { dx: 0, dy: 1 }, { dx: -1, dy: 0 }, { dx: 1, dy: 0 }];
    let bestMove = { dx: 0, dy: 0 };
    let maxScore = -99999;

    for (let m of moves) {
        let nx = boss.x + m.dx;
        let ny = boss.y + m.dy;

        if (nx < 0 || nx >= tileCount || ny < 0 || ny >= tileCount) continue;

        let scoreForMove = Math.abs(nx - head.x) + Math.abs(ny - head.y);

        if (isGodMode) {
            // Бог: предсказывает следующий шаг змеи и боится углов как огня!
            let nextHeadX = head.x + dx;
            let nextHeadY = head.y + dy;
            scoreForMove += Math.abs(nx - nextHeadX) + Math.abs(ny - nextHeadY);

            if (nx === 0 || nx === tileCount - 1) scoreForMove -= 50;
            if (ny === 0 || ny === tileCount - 1) scoreForMove -= 50;
        } else {
            if (nx === 0 || nx === tileCount - 1) scoreForMove -= 2;
            if (ny === 0 || ny === tileCount - 1) scoreForMove -= 2;
        }

        for (let i = 1; i < snake.length; i++) {
            if (nx === snake[i].x && ny === snake[i].y) {
                scoreForMove -= 100; // Никогда не наступает на хвост
            }
        }

        if (!isGodMode) scoreForMove += Math.random();

        if (scoreForMove > maxScore) {
            maxScore = scoreForMove;
            bestMove = m;
        }
    }

    boss.x += bestMove.dx;
    boss.y += bestMove.dy;
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
    isGameRunning = false;
    window.isCountingDown = false;
    window.isStarting = false;

    // Жестко сбрасываем абсолютно все таймеры при выходе
    if (window.countdownTimer) {
        clearInterval(window.countdownTimer);
        window.countdownTimer = null;
    }
    clearTimeout(gameLoopTimer);
    if (typeof glitchTimer !== 'undefined') clearInterval(glitchTimer);

    document.getElementById('gameOverlay').style.display = 'none';
    document.getElementById('gameOverOverlay').style.display = 'none';
    document.getElementById('countdownOverlay').style.display = 'none';
    window.loadProfile(true);
}
window.exitGame = exitGame;

function gameLoop() {
    if (!isGameRunning) return;
    globalTime += 0.15;

    if (typeof window.gameTicks === 'undefined') window.gameTicks = 0;
    window.gameTicks++;

    let head = { x: snake[0].x + dx, y: snake[0].y + dy };

    if (head.x < 0 || head.x >= tileCount || head.y < 0 || head.y >= tileCount) { showGameOver(false); return; }
    for (let i = 0; i < snake.length; i++) if (head.x === snake[i].x && head.y === snake[i].y) { showGameOver(false); return; }

    snake.unshift(head);

    if (head.x === food.x && head.y === food.y) {
        score += 10; updateScoreUI(); spawnFood();
    } else { snake.pop(); }

    if (head.x === cdn.x && head.y === cdn.y) { showGameOver(false); return; }

    let speed = 120; // Базовое значение задержки

    if (boss.active) {
        let elapsedSeconds = (Date.now() - window.bossSpawnTime) / 1000;

        if (elapsedSeconds > 20) {
            // ТАЙМ-АУТ! Босс сбежал!
            boss.active = false;
            window.bossKills = (window.bossKills || 0) + 1; // Увеличиваем цель (усложняем), но без выдачи 7 дней!
            updateScoreUI();
            window.showToast("Вирус скрылся! Продолжаем взлом...");
        } else if (Math.abs(head.x - boss.x) <= 1 && Math.abs(head.y - boss.y) <= 1) {
            // Босс пойман!
            showGameOver(true); return;
        } else {
            // Логика уклонения босса (оставлена без изменений)
            let bossEvadeSpeed = (window.monthlyBossKills >= 2 || elapsedSeconds > 5) ? 1 : 2;
            if (window.gameTicks % bossEvadeSpeed === 0) moveBoss();

            // Логика скорости змейки во время босса (оставлена без изменений)
            if (elapsedSeconds <= 5) {
                speed = 90; // Первые 5 сек: комфортная скорость
            } else {
                // Следующие 15 сек: плавное ускорение от 90ms до 40ms
                let progress = (elapsedSeconds - 5) / 15;
                speed = Math.max(40, 90 - (progress * 50));
            }
        }
    } else {
        // Обычная игра: СКОРОСТЬ ЗАМЕДЛЕНА В 2 РАЗА (задержка увеличена с 60-150 до 120-300)
        let baseSpeed = Math.max(120, 300 - ((window.bossKills || 0) * 30));
        speed = Math.max(120, baseSpeed - (score * 0.4));
    }

    drawGame();
    gameLoopTimer = setTimeout(gameLoop, speed);
}

function drawGame() {
    ctx.fillStyle = 'rgba(0, 0, 0, 0.8)'; ctx.fillRect(0, 0, canvas.width, canvas.height);

    // SNI (Ранее Синяя Еда) - Отрисовка в виде светящегося ромба
    ctx.fillStyle = '#00f2ff';
    ctx.shadowBlur = 15;
    ctx.shadowColor = '#00f2ff';
    ctx.beginPath();
    let fx = food.x * gridSize + gridSize / 2;
    let fy = food.y * gridSize + gridSize / 2;
    ctx.moveTo(fx, fy - gridSize / 2 + 2); // Верх
    ctx.lineTo(fx + gridSize / 2 - 2, fy); // Право
    ctx.lineTo(fx, fy + gridSize / 2 - 2); // Низ
    ctx.lineTo(fx - gridSize / 2 + 2, fy); // Лево
    ctx.fill();

    // DPI-Система (Ранее CDN/Красная угроза) - Отрисовка в виде шипованного блока
    ctx.fillStyle = '#ff4444';
    ctx.shadowColor = '#ff4444';
    ctx.shadowBlur = 15;
    let cx = cdn.x * gridSize + 2;
    let cy = cdn.y * gridSize + 2;
    let cs = gridSize - 4;
    ctx.fillRect(cx, cy, cs, cs);
    // Внутренний крест для DPI
    ctx.fillStyle = '#ffffff';
    ctx.shadowBlur = 0;
    ctx.fillRect(cx + cs / 2 - 1, cy + 2, 2, cs - 4);
    ctx.fillRect(cx + 2, cy + cs / 2 - 1, cs - 4, 2);

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

    // === КИБЕР-ЧЕРВЬ (Змейка) ===
    ctx.shadowBlur = 0;
    for (let i = 0; i < snake.length; i++) {
        let px = snake[i].x * gridSize;
        let py = snake[i].y * gridSize;

        if (i === 0) {
            // ГОЛОВА
            ctx.fillStyle = '#00f2ff'; // Неоново-синий
            ctx.fillRect(px + 1, py + 1, gridSize - 2, gridSize - 2);

            // Глазки в зависимости от направления (dx, dy)
            ctx.fillStyle = '#ffffff';
            let eyeSize = 3;
            if (dx === 1) { // Вправо
                ctx.fillRect(px + gridSize - 5, py + 3, eyeSize, eyeSize);
                ctx.fillRect(px + gridSize - 5, py + gridSize - 6, eyeSize, eyeSize);
            } else if (dx === -1) { // Влево
                ctx.fillRect(px + 2, py + 3, eyeSize, eyeSize);
                ctx.fillRect(px + 2, py + gridSize - 6, eyeSize, eyeSize);
            } else if (dy === 1) { // Вниз
                ctx.fillRect(px + 3, py + gridSize - 5, eyeSize, eyeSize);
                ctx.fillRect(px + gridSize - 6, py + gridSize - 5, eyeSize, eyeSize);
            } else { // Вверх (по умолчанию и dy === -1)
                ctx.fillRect(px + 3, py + 2, eyeSize, eyeSize);
                ctx.fillRect(px + gridSize - 6, py + 2, eyeSize, eyeSize);
            }
        } else if (i === snake.length - 1) {
            // ХВОСТ (Уменьшенный блок)
            ctx.fillStyle = 'rgba(189, 147, 249, 0.6)';
            ctx.fillRect(px + 4, py + 4, gridSize - 8, gridSize - 8);
        } else {
            // ТЕЛО (Объемное с градиентом и бликом)
            let gradient = ctx.createLinearGradient(px, py, px + gridSize, py + gridSize);
            gradient.addColorStop(0, '#bd93f9'); // Основной фиолетовый
            gradient.addColorStop(1, '#8be9fd'); // Синий отлив
            ctx.fillStyle = gradient;
            ctx.fillRect(px + 1, py + 1, gridSize - 2, gridSize - 2);

            // Внутренний блик
            ctx.fillStyle = 'rgba(0, 0, 0, 0.3)';
            ctx.fillRect(px + 4, py + 4, gridSize - 8, gridSize - 8);
        }
    }
}