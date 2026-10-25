const api = async (path, options = {}) => {
  const response = await fetch(path, { headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }, ...options });
  if (!response.ok) throw new Error((await response.json().catch(() => ({}))).error || `HTTP ${response.status}`);
  return response.status === 202 ? null : response.json().catch(() => ({}));
};
const tokenKey = 'soulsdlc_auth_token';
let mode = 'login';
let challengeId = '';
const $ = id => document.getElementById(id);
const code = new URLSearchParams(location.search).get('code') || '';

async function loadChallenge() {
  const challenge = await api('/auth/challenge');
  challengeId = challenge.challengeId;
  $('botQuestion').textContent = `Проверка: ${challenge.question}`;
  $('botAnswer').value = '';
}

document.querySelectorAll('[data-mode]').forEach(button => button.addEventListener('click', async () => {
  mode = button.dataset.mode;
  document.querySelectorAll('[data-mode]').forEach(x => x.classList.toggle('active', x === button));
  $('nameField').hidden = mode !== 'register';
  $('botField').hidden = mode !== 'register';
  if (mode === 'register') await loadChallenge();
  $('authSubmit').textContent = mode === 'register' ? 'Создать аккаунт' : 'Войти';
}));

async function showProfile(user) {
  $('authPanel').hidden = true;
  $('profilePanel').hidden = false;
  $('profileName').textContent = user.name;
  $('profileGreeting').textContent = user.name;
  $('profileEmail').textContent = user.email;
  if ($('profileUid')) $('profileUid').textContent = Math.abs([...user.email].reduce((n, c) => ((n << 5) - n + c.charCodeAt(0)) | 0, 0));
  if ($('profileRole')) $('profileRole').textContent = user.role === 'premium' ? 'Premium' : 'Пользователь';
  if ($('profileDevice')) $('profileDevice').textContent = user.deviceBound ? 'Привязан' : 'Не привязан';
  $('deviceHeadline').textContent = user.deviceBound ? 'Привязано' : 'Не привязано';
  renderSubscription(user.premiumUntil);
  if (code) { $('devicePanel').hidden = false; $('deviceCode').textContent = code; }
}

function renderSubscription(until) {
  const end = until ? new Date(until) : null;
  const days = end && !Number.isNaN(end.getTime()) ? Math.max(0, Math.ceil((end.getTime() - Date.now()) / 86400000)) : 0;
  $('subscriptionDays').textContent = `${days} ${days === 1 ? 'день' : days > 1 && days < 5 ? 'дня' : 'дней'}`;
  $('subscriptionCaption').textContent = days ? `Активна до ${end.toLocaleDateString('ru-RU')}` : 'Доступ не активен';
  $('premiumStatus').textContent = days ? `Активна до ${end.toLocaleDateString('ru-RU')}` : 'Не активна';
}

$('authSubmit')?.addEventListener('click', async () => {
  $('authStatus').textContent = 'Подключение...';
  try {
    const body = { email: $('email').value.trim(), password: $('password').value, name: $('name').value.trim() || 'Player', challengeId, challengeAnswer: $('botAnswer').value.trim() };
    if (mode === 'register') await api('/auth/register', { method: 'POST', body: JSON.stringify(body) });
    const user = await api('/web/login', { method: 'POST', body: JSON.stringify(body) });
    localStorage.setItem(tokenKey, user.authToken);
    await showProfile(user);
  } catch (error) {
    $('authStatus').textContent = error.message === 'name_taken' ? 'Это имя уже занято.' : error.message === 'bot_check_failed' ? 'Проверка от бота не пройдена. Решите новый пример.' : 'Проверьте email и пароль.';
    if (mode === 'register') await loadChallenge();
  }
});

function openReset() {
  $('authPanel').hidden = true;
  $('resetPanel').hidden = false;
  $('resetEmail').value = $('email').value.trim();
  $('resetStatus').textContent = '';
}
function closeReset() {
  $('resetPanel').hidden = true;
  $('authPanel').hidden = false;
  $('email').value = $('resetEmail').value.trim();
}
$('forgotPassword')?.addEventListener('click', openReset);
$('resetBack')?.addEventListener('click', closeReset);
$('sendResetCode')?.addEventListener('click', async () => {
  $('resetStatus').textContent = 'Отправляю код...';
  try {
    await api('/auth/forgot', { method: 'POST', body: JSON.stringify({ email: $('resetEmail').value.trim() }) });
    $('resetStatus').textContent = 'Код отправлен на указанный email.';
  } catch { $('resetStatus').textContent = 'Не удалось отправить код. Проверьте email.'; }
});
$('resetPasswordSubmit')?.addEventListener('click', async () => {
  $('resetStatus').textContent = 'Обновляю пароль...';
  try {
    await api('/auth/reset', { method: 'POST', body: JSON.stringify({ email: $('resetEmail').value.trim(), code: $('resetCode').value.trim(), newPassword: $('resetPassword').value }) });
    $('password').value = $('resetPassword').value;
    closeReset();
    $('authStatus').textContent = 'Пароль обновлён. Теперь войдите.';
  } catch (error) { $('resetStatus').textContent = error.message === 'weak_password' ? 'Пароль должен быть не короче 8 символов.' : 'Код неверный или истёк.'; }
});

$('logout')?.addEventListener('click', () => { localStorage.removeItem(tokenKey); location.reload(); });
$('activateKey')?.addEventListener('click', async () => {
  const token = localStorage.getItem(tokenKey);
  $('profileStatus').textContent = 'Проверяю ключ...';
  try {
    const me = await api('/web/me', { headers: { Authorization: `Bearer ${token}` } });
    const result = await api('/web/subscription/activate', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: JSON.stringify({ email: me.email, key: $('subscriptionKey').value.trim() }) });
    renderSubscription(result.premiumUntil);
    $('profileStatus').textContent = `Подписка активирована на ${result.days} дней.`;
    $('subscriptionKey').value = '';
  } catch { $('profileStatus').textContent = 'Ключ неверный или уже использован.'; }
});
$('authorizeDevice')?.addEventListener('click', async () => {
  $('deviceStatus').textContent = 'Подтверждение...';
  try {
    await api('/web/device/authorize', { method: 'POST', body: JSON.stringify({ code, authToken: localStorage.getItem(tokenKey) }) });
    $('deviceStatus').textContent = 'Вход разрешён. Можно вернуться в лаунчер.';
    $('authorizeDevice').disabled = true;
  } catch { $('deviceStatus').textContent = 'Код истёк. Запросите новый вход в лаунчере.'; }
});

(async () => {
  const token = localStorage.getItem(tokenKey);
  if (!token) return;
  try { await showProfile(await api('/web/me', { headers: { Authorization: `Bearer ${token}` } })); }
  catch { localStorage.removeItem(tokenKey); }
})();
