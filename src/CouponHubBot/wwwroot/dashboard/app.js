(() => {
  "use strict";

  const byId = (id) => document.getElementById(id);
  const number = new Intl.NumberFormat("ru-RU", { maximumFractionDigits: 1 });
  const integer = new Intl.NumberFormat("ru-RU", { maximumFractionDigits: 0 });
  const dateTime = new Intl.DateTimeFormat("ru-RU", { timeZone: "Europe/Dublin", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  const collator = new Intl.Collator("ru", { sensitivity: "base", numeric: true });
  const primaryMetrics = [
    { key: "takeDays", title: "Остаток при взятии", unit: "дн.", sample: "takes", direction: "low", hint: "Чаще спасать горящие", icon: "↓" },
    { key: "holdHours", title: "Время в руках", unit: "ч", sample: "resolved", direction: "low", hint: "Быстрее решать судьбу купона", icon: "↓" },
    { key: "returnPct", title: "Доля возвратов", unit: "%", sample: "takes", direction: "low", hint: "Брать, чтобы использовать", icon: "↓" },
    { key: "addDays", title: "Остаток при добавлении", unit: "дн.", sample: "adds", direction: "high", hint: "Больше времени для других", icon: "↑" }
  ];
  const columns = [
    { key: "name", title: "Участник", short: "Участник", kind: "name" },
    { key: "takes", title: "Взято купонов", short: "Взято", kind: "count" },
    { key: "takeDays", title: "Остаток при взятии, дней", short: "Остаток при взятии · дн. ↓", kind: "metric", sample: "takes", direction: "low" },
    { key: "holdHours", title: "Время в руках, часов", short: "Время в руках · ч ↓", kind: "metric", sample: "resolved", direction: "low" },
    { key: "returnPct", title: "Доля возвратов, %", short: "Возвраты · % ↓", kind: "metric", sample: "takes", direction: "low" },
    { key: "adds", title: "Добавлено купонов", short: "Добавлено", kind: "count" },
    { key: "addDays", title: "Остаток при добавлении, дней", short: "Остаток при добавлении · дн. ↑", kind: "metric", sample: "adds", direction: "high" },
    { key: "lateAddPct", title: "Добавлено с остатком ≤1 дня, %", short: "Добавлено ≤1 дня · % ↓", kind: "metric", sample: "adds", direction: "low" },
    { key: "heldDayLateReturns", title: "Держали ≥24 ч → вернули ≤1 дня", short: "Держали ≥24 ч → вернули ≤1 дня", kind: "late" },
    { key: "unresolved", title: "Незакрытые взятия", short: "На руках / истекло", kind: "open" },
    { key: "returns", title: "Количество возвратов", short: "Возвратов", kind: "count", extra: true },
    { key: "resolved", title: "Закрытые взятия", short: "Закрыто взятий", kind: "count", extra: true },
    { key: "returnHoldHours", title: "Время до возврата, часов", short: "До возврата · ч", kind: "number", extra: true },
    { key: "returnDays", title: "Остаток при возврате, дней", short: "Остаток при возврате · дн.", kind: "number", extra: true },
    { key: "lateReturns", title: "Все возвраты с остатком ≤1 дня", short: "Все возвраты ≤1 дня", kind: "count", extra: true },
    { key: "lateAdds", title: "Добавлено с остатком ≤1 дня, количество", short: "Добавлено ≤1 дня · шт.", kind: "count", extra: true },
    { key: "unresolvedExpired", title: "Истекли на руках", short: "Истекли на руках", kind: "expired", extra: true },
    { key: "voidClosed", title: "Взятия, закрытые аннулированием", short: "Аннулировано на руках", kind: "count", extra: true }
  ];
  const state = { token: null, expiresAt: 0, viewerId: null, payload: null, selectedId: null, sort: "takes", descending: true, signal: "all", generation: 0, request: 0, timer: null, controller: null };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function present(value) {
    return typeof value === "number" && Number.isFinite(value);
  }

  function format(value, unit = "") {
    return present(value) ? number.format(value) + (unit ? " " + unit : "") : "—";
  }

  function tone(row, metric) {
    const value = row[metric.key];
    const baseline = state.payload?.community[metric.key];
    if (!present(value) || !present(baseline) || baseline <= 0 || row[metric.sample] < 10) return "muted";
    if (metric.key === "returnPct" && value > baseline && present(row.returnHoldHours) && row.returnHoldHours < 1) return "neutral";
    const ratio = value / baseline;
    if (metric.direction === "high") {
      if (ratio <= 0.5) return "bad";
      if (ratio <= 0.75) return "warn";
      if (ratio >= 1.1) return "good";
    } else {
      if (ratio >= 1.5) return "bad";
      if (ratio >= 1.25) return "warn";
      if (ratio <= 0.75) return "good";
    }
    return "neutral";
  }

  function countTone(row, column) {
    if (column.kind === "late") return row[column.key] >= 3 ? "bad" : row[column.key] > 0 ? "warn" : "muted";
    if (column.kind === "expired" || column.kind === "open") return row.unresolvedExpired > 0 ? "bad" : column.kind === "open" && row.unresolved > 0 ? "warn" : "muted";
    return "neutral";
  }

  function metricDescription(row, metric) {
    const count = row[metric.sample];
    const baseline = state.payload.community[metric.key];
    let description = "Действий: " + integer.format(count) + ". Среднее сообщества: " + format(baseline) + ".";
    if (!present(row[metric.key])) return "Нет подходящих действий для среднего.";
    if (count < 10) description += " Меньше 10 действий: цвет не оценивается.";
    else if (present(baseline) && baseline > 0) {
      const delta = (row[metric.key] / baseline - 1) * 100;
      description += " " + format(Math.abs(delta), "%") + (delta >= 0 ? " выше" : " ниже") + " среднего.";
    }
    if (metric.key === "returnPct" && present(row.returnHoldHours) && row.returnHoldHours < 1) description += " Возврат в среднем быстрее часа: повышенная доля не окрашивается.";
    if (metric.key === "lateAddPct") description += " До истечения оставалось не больше одного дня по Дублину; просроченные записи тоже учитываются.";
    return description;
  }

  function metricBadge(row, metric, unit = "") {
    const color = tone(row, metric);
    const badge = element("span", "metric " + color);
    badge.title = metricDescription(row, metric);
    if (["good", "warn", "bad"].includes(color)) {
      const direction = row[metric.key] > state.payload.community[metric.key] ? "↑" : "↓";
      badge.append(element("span", "metric-arrow", direction));
    }
    badge.append(element("span", "", format(row[metric.key], unit)));
    return badge;
  }

  function renderAverages() {
    const community = state.payload.community;
    byId("community-volume").textContent = integer.format(community.takes) + " взятий · " + integer.format(community.adds) + " добавлений";
    byId("averages").replaceChildren(...primaryMetrics.map((metric) => {
      const card = element("article", "average-card");
      const top = element("div", "card-top");
      top.append(element("span", "", metric.title), element("span", "card-icon", metric.icon));
      const value = element("p", "card-value", format(community[metric.key]));
      if (present(community[metric.key])) value.append(element("span", "unit", metric.unit));
      const caption = element("div", "card-caption");
      caption.append(element("span", "", metric.hint));
      const count = metric.key === "returnPct" ? integer.format(community.returns) + " / " + integer.format(community.takes) : "n = " + integer.format(community[metric.sample]);
      caption.append(element("span", "", count));
      card.append(top, value, caption);
      return card;
    }));
  }

  function renderPersonal() {
    const row = state.payload.members.find((member) => member.id === state.selectedId);
    const own = state.payload.members.some((member) => member.id === state.viewerId);
    byId("show-me").hidden = !own;
    if (!row) {
      byId("comparison-title").textContent = "Выбери имя в таблице";
      byId("personal-values").replaceChildren(element("p", "muted", "Нажми имя участника для сравнения с сообществом."));
      return;
    }
    byId("comparison-title").textContent = row.name + (row.id === state.viewerId ? " · это ты" : "") + (row.username ? " · @" + row.username : "");
    const values = primaryMetrics.map((metric) => {
      const card = element("div", "personal-metric");
      const value = element("div", "personal-number");
      value.append(metricBadge(row, metric, metric.unit));
      card.append(element("p", "personal-label", metric.title), value, element("p", "personal-baseline", "Сообщество: " + format(state.payload.community[metric.key], metric.unit) + " · n = " + integer.format(row[metric.sample])));
      return card;
    });
    values.push(element("p", "personal-detail", "Взято: " + integer.format(row.takes) + " · возвращено: " + integer.format(row.returns) + " · добавлено: " + integer.format(row.adds) + " · на руках без закрытия: " + integer.format(row.unresolved) + " (истекло: " + integer.format(row.unresolvedExpired) + ") · держали ≥24 ч и вернули ≤1 дня: " + integer.format(row.heldDayLateReturns)));
    byId("personal-values").replaceChildren(...values);
  }

  function rowSignal(row, signal) {
    if (signal === "all") return true;
    return columns.some((column) => {
      const color = column.kind === "metric" ? tone(row, column) : countTone(row, column);
      return color === signal;
    });
  }

  function filteredRows() {
    const query = byId("search").value.trim().replace(/^@/, "").toLocaleLowerCase("ru");
    const activity = byId("activity").value;
    const minimum = Number(byId("sample").value);
    return state.payload.members.filter((row) => {
      if (query && !(row.name + " " + (row.username || "")).toLocaleLowerCase("ru").includes(query)) return false;
      if (activity === "active" && row.takes + row.adds === 0) return false;
      if (activity === "takes" && row.takes === 0) return false;
      if (activity === "adds" && row.adds === 0) return false;
      const sample = activity === "takes" ? row.takes : activity === "adds" ? row.adds : row.takes + row.adds;
      return sample >= minimum && rowSignal(row, state.signal);
    }).sort((a, b) => {
      const av = a[state.sort];
      const bv = b[state.sort];
      if (av === null || av === undefined) return bv === null || bv === undefined ? collator.compare(a.name, b.name) : 1;
      if (bv === null || bv === undefined) return -1;
      const comparison = typeof av === "string" ? collator.compare(av, bv) : av - bv;
      return (state.descending ? -comparison : comparison) || collator.compare(a.name, b.name);
    });
  }

  function renderName(row) {
    const button = element("button", "member-button");
    button.type = "button";
    button.title = "Сравнить: " + row.name;
    button.setAttribute("aria-pressed", String(row.id === state.selectedId));
    const initials = row.name.trim().split(/\s+/).slice(0, 2).map((part) => Array.from(part)[0] || "").join("").toLocaleUpperCase("ru");
    const color = Array.from(row.id).reduce((sum, character) => sum + character.charCodeAt(0), 0) % 4;
    const avatar = element("span", "avatar tone-" + color, initials);
    avatar.setAttribute("aria-hidden", "true");
    const text = element("span", "member-text");
    text.append(element("span", "member-name", row.name));
    const subtitle = (row.username ? "@" + row.username : "") + (row.id === state.viewerId ? (row.username ? " · " : "") + "это ты" : "");
    if (subtitle) text.append(element("span", "member-username" + (row.id === state.viewerId ? " you-label" : ""), subtitle));
    button.append(avatar, text);
    button.addEventListener("click", () => {
      state.selectedId = row.id;
      renderPersonal();
      renderTable();
      byId("comparison-title").scrollIntoView({ block: "nearest" });
    });
    return button;
  }

  function renderCell(row, column) {
    const cell = element("td");
    if (column.kind === "name") cell.append(renderName(row));
    else if (column.kind === "metric") {
      cell.append(metricBadge(row, column));
      if (column.key === "returnPct") cell.append(element("span", "cell-note", integer.format(row.returns) + " из " + integer.format(row.takes)));
      if (column.key === "lateAddPct") cell.append(element("span", "cell-note", integer.format(row.lateAdds) + " из " + integer.format(row.adds)));
    } else {
      const badge = element("span", "metric " + countTone(row, column), format(row[column.key]));
      if (column.kind === "late") badge.title = "На руках ≥24 часов, затем возврат накануне истечения, в день истечения или позже. 1–2 случая — жёлтый, от 3 — красный.";
      if (column.kind === "expired") badge.title = "Срок уже прошёл, взятие не закрыто.";
      cell.append(badge);
      if (column.kind === "open") {
        badge.title = "Взятия без отметки об использовании, возврате или проблеме. Не входят в среднее времени в руках.";
        cell.append(element("span", "cell-note", "истекло: " + integer.format(row.unresolvedExpired)));
      }
    }
    return cell;
  }

  function renderTable() {
    if (!state.payload) return;
    const visibleColumns = columns.filter((column) => !column.extra || byId("more-columns").checked);
    const header = element("tr");
    visibleColumns.forEach((column) => {
      const th = element("th");
      th.scope = "col";
      th.setAttribute("aria-sort", state.sort === column.key ? state.descending ? "descending" : "ascending" : "none");
      const button = element("button");
      button.type = "button";
      button.title = "Сортировать: " + column.title;
      button.append(element("span", "th-label", column.short), element("span", "sort-arrow", state.sort === column.key ? state.descending ? "↓" : "↑" : "↕"));
      button.addEventListener("click", () => setSort(column.key, state.sort === column.key ? !state.descending : column.key !== "name"));
      th.append(button);
      header.append(th);
    });
    byId("table-head").replaceChildren(header);
    const rows = filteredRows();
    byId("table-body").replaceChildren(...rows.map((row) => {
      const tr = element("tr", row.id === state.selectedId ? "selected" : "");
      visibleColumns.forEach((column) => tr.append(renderCell(row, column)));
      return tr;
    }));
    byId("empty").hidden = rows.length !== 0;
    byId("row-count").textContent = "Показано " + integer.format(rows.length) + " из " + integer.format(state.payload.members.length) + " · среднее сообщества не зависит от фильтров";
    byId("sort").value = state.sort;
    byId("sort-direction").textContent = state.descending ? "↓" : "↑";
    byId("sort-direction").setAttribute("aria-label", state.descending ? "По убыванию. Изменить на возрастание" : "По возрастанию. Изменить на убывание");
  }

  function setSort(key, descending) {
    state.sort = key;
    state.descending = descending;
    renderTable();
  }

  function status(message) {
    byId("load-status").textContent = message;
    byId("load-status").hidden = !message;
  }

  function clearPrivate(message) {
    state.generation++;
    state.controller?.abort();
    clearTimeout(state.timer);
    state.token = null;
    state.expiresAt = 0;
    state.viewerId = null;
    state.payload = null;
    state.selectedId = null;
    ["averages", "personal-values", "table-head", "table-body"].forEach((id) => byId(id).replaceChildren());
    ["community-volume", "row-count", "as-of", "comparison-title"].forEach((id) => { byId(id).textContent = ""; });
    byId("search").value = "";
    byId("dashboard").hidden = true;
    byId("gate").hidden = false;
    byId("gate-message").textContent = message;
    byId("retry-auth").hidden = true;
    status("");
    loadBotLink();
  }

  async function loadBotLink() {
    if (byId("open-bot").hasAttribute("href")) return;
    try {
      const response = await fetch("/dashboard/api/info", { cache: "no-store", credentials: "omit", redirect: "error" });
      if (!response.ok) return;
      const info = await response.json();
      if (!info.botUrl) return;
      const url = new URL(info.botUrl);
      if (url.protocol !== "https:" || url.hostname !== "t.me" || url.username || url.password) return;
      byId("open-bot").href = url.href;
      byId("open-bot").hidden = false;
    } catch { /* The bot command remains available when discovery is unavailable. */ }
  }

  async function denialMessage(response, fallback) {
    const body = await response.json().catch(() => null);
    return body?.error === "dashboard_disabled" ? "Статистика пока доступна только администраторам." : fallback;
  }

  async function loadStats() {
    if (!state.token || Date.now() >= state.expiresAt) {
      clearPrivate("Сессия закончилась. Открой статистику заново через бота.");
      return;
    }
    state.controller?.abort();
    const controller = new AbortController();
    state.controller = controller;
    const generation = state.generation;
    const request = ++state.request;
    const days = Number(byId("period").value);
    byId("refresh").disabled = true;
    status("Загружаем статистику…");
    try {
      const response = await fetch("/dashboard/api/stats?days=" + days, { headers: { Authorization: "Bearer " + state.token }, cache: "no-store", credentials: "omit", redirect: "error", signal: controller.signal });
      if (generation !== state.generation || request !== state.request) return;
      if (response.status === 401 || response.status === 403) {
        const message = response.status === 403
          ? await denialMessage(response, "Доступ есть только у участников сообщества. Если это ошибка, открой бота и проверь участие в чате.")
          : "Сессия закончилась. Открой статистику заново через бота.";
        if (generation !== state.generation || request !== state.request) return;
        clearPrivate(message);
        return;
      }
      if (!response.ok) throw new Error("Statistics unavailable");
      const payload = await response.json();
      if (generation !== state.generation || request !== state.request) return;
      if (Date.now() >= state.expiresAt) {
        clearPrivate("Сессия закончилась. Открой статистику заново через бота.");
        return;
      }
      if (!Array.isArray(payload.members) || !payload.community || payload.periodDays !== days || !Number.isFinite(Date.parse(payload.asOf))) throw new Error("Unexpected statistics response");
      state.payload = payload;
      if (!state.selectedId) state.selectedId = state.viewerId;
      byId("gate").hidden = true;
      byId("dashboard").hidden = false;
      byId("period").value = String(payload.periodDays);
      renderAverages();
      renderPersonal();
      renderTable();
      byId("as-of").textContent = "Снимок: " + dateTime.format(new Date(payload.asOf)) + " · Дублин";
      status("");
    } catch (error) {
      if (error.name === "AbortError" || generation !== state.generation || request !== state.request) return;
      if (state.payload) {
        byId("period").value = String(state.payload.periodDays);
        status("Не удалось обновить данные. Показан предыдущий снимок. Можно повторить вручную.");
      } else {
        byId("gate-message").textContent = "Не удалось загрузить статистику. Проверь соединение и попробуй ещё раз.";
        byId("retry-auth").hidden = false;
      }
    } finally {
      if (request === state.request) byId("refresh").disabled = false;
    }
  }

  async function authenticate() {
    if (state.token && Date.now() < state.expiresAt) {
      await loadStats();
      return;
    }
    const app = window.Telegram?.WebApp;
    if (!app?.initData) {
      byId("gate-message").textContent = "Открой эту страницу через кнопку в боте. Telegram подтвердит личность, а бот проверит участие в сообществе.";
      loadBotLink();
      return;
    }
    app.ready();
    app.expand();
    byId("retry-auth").hidden = true;
    byId("gate-message").textContent = "Проверяем доступ через Telegram…";
    const generation = state.generation;
    try {
      const response = await fetch("/dashboard/api/session", { method: "POST", headers: { "Content-Type": "text/plain;charset=UTF-8" }, body: app.initData, cache: "no-store", credentials: "omit", redirect: "error" });
      if (generation !== state.generation) return;
      if (response.status === 401 || response.status === 403) {
        const message = response.status === 403
          ? await denialMessage(response, "Статистика доступна только участникам сообщества.")
          : "Не удалось подтвердить вход. Закрой страницу и открой её заново через бота.";
        if (generation !== state.generation) return;
        clearPrivate(message);
        return;
      }
      if (!response.ok) throw new Error("Authentication unavailable");
      const session = await response.json();
      if (generation !== state.generation) return;
      const expiresAt = Date.parse(session.expiresAt);
      if (typeof session.token !== "string" || !session.token || typeof session.viewerId !== "string" || !Number.isFinite(expiresAt) || expiresAt <= Date.now()) throw new Error("Unexpected authentication response");
      state.token = session.token;
      state.expiresAt = expiresAt;
      state.viewerId = session.viewerId;
      state.timer = setTimeout(() => clearPrivate("Сессия закончилась. Открой статистику заново через бота."), Math.min(expiresAt - Date.now(), 2147483647));
      await loadStats();
    } catch {
      if (generation !== state.generation) return;
      byId("gate-message").textContent = "Не удалось проверить доступ. Проверь соединение и попробуй ещё раз.";
      byId("retry-auth").hidden = false;
      loadBotLink();
    }
  }

  columns.forEach((column) => {
    const option = element("option", "", column.title);
    option.value = column.key;
    byId("sort").append(option);
  });
  byId("sort").value = state.sort;
  byId("sort").addEventListener("change", () => setSort(byId("sort").value, byId("sort").value !== "name"));
  byId("sort-direction").addEventListener("click", () => setSort(state.sort, !state.descending));
  ["search", "activity", "sample", "more-columns"].forEach((id) => byId(id).addEventListener(id === "search" ? "input" : "change", renderTable));
  byId("period").addEventListener("change", loadStats);
  byId("refresh").addEventListener("click", loadStats);
  byId("retry-auth").addEventListener("click", authenticate);
  byId("show-me").addEventListener("click", () => { state.selectedId = state.viewerId; renderPersonal(); renderTable(); });
  document.querySelectorAll("[data-signal]").forEach((button) => button.addEventListener("click", () => {
    state.signal = button.dataset.signal;
    document.querySelectorAll("[data-signal]").forEach((other) => {
      const active = other === button;
      other.classList.toggle("active", active);
      other.setAttribute("aria-pressed", String(active));
    });
    renderTable();
  }));
  byId("reset-filters").addEventListener("click", () => {
    byId("search").value = "";
    byId("activity").value = "all";
    byId("sample").value = "0";
    document.querySelector('[data-signal="all"]').click();
  });
  document.addEventListener("visibilitychange", () => {
    if (state.token && Date.now() >= state.expiresAt) clearPrivate("Сессия закончилась. Открой статистику заново через бота.");
  });
  window.addEventListener("pagehide", () => clearPrivate("Открой статистику заново через бота."));
  authenticate();
})();
