import { promises as fs } from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { performance } from "node:perf_hooks";

const hash = value => createHash("sha256").update(value).digest("hex");
const normalizedId = value => String(value ?? "").replaceAll("-", "").toLowerCase();
const summary = item => ({ id: normalizedId(item.Id ?? item.ItemId), name: item.Name, type: item.Type });
const sameResult = (item, expected) => item.id === normalizedId(expected.id) && item.name === expected.name && item.type === expected.type;
const searchSelector = 'button[aria-label="Search"]:visible,a[aria-label="Search"]:visible,button[title="Search"]:visible,a[title="Search"]:visible';

const captionSafe = box => box && box.width >= 1 && box.height >= 1 && box.x >= 0 && box.y >= 0
  && box.x + box.width <= 1920 && box.y + box.height <= 864;

export async function scrollResultIntoView(locator, record) {
  const before = await locator.boundingBox();
  if (captionSafe(before)) return;
  if (!before || before.width < 1 || before.height < 1 || before.width > 1920 || before.height > 864)
    throw new Error("capture.target-geometry");
  record("native-result-scroll-start", before);
  await locator.evaluate(element => element.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" }));
  await locator.scrollIntoViewIfNeeded({ timeout: 10000 });
  const after = await locator.boundingBox();
  record("native-result-scroll-end", after);
  if (!captionSafe(after)) throw new Error("capture.target-geometry");
}

export function validateOrigin(value) {
  const url = new URL(value);
  if (url.protocol !== "http:" || url.hostname !== "127.0.0.1" || url.port < 1025 || url.pathname !== "/"
    || url.search || url.hash || url.username || url.password || url.origin !== value) throw new Error("capture.origin");
  return url;
}

export function permittedRequest(urlValue, method, profile) {
  const url = new URL(urlValue);
  if (url.origin !== profile.origin) return false;
  if (method === "GET" && profile.webAssets.some(asset => "/" + asset.path === decodeURIComponent(url.pathname))) return true;
  if (url.pathname === "/" && method === "GET") return true;
  if (!profile.allowedApiPaths.includes(url.pathname)) return false;
  return method === "GET" || method === "POST" && [
    "/Users/AuthenticateByName", "/Sessions/Capabilities/Full", "/Sessions/Logout",
    "/DisplayPreferences/usersettings", `/Users/${profile.userId}/Configuration`
  ].some(endpoint => endpoint.toLowerCase() === url.pathname.toLowerCase());
}

export async function captureJellyfin(config, chromium, credentials) {
  const p = config.profile;
  validateOrigin(p.origin);
  if (!credentials || Object.keys(credentials).sort().join(",") !== "password,username"
    || typeof credentials.username !== "string" || typeof credentials.password !== "string"
    || !credentials.username || !credentials.password) throw new Error("capture.auth-schema");
  const browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
  let loginContext, loginPage, context;
  let completed = false;
  let phase = "initialization";
  const denied = new Set();
  const clientFailures = new Set();
  const nativeRequests = [];
  let nativeNavigation = [];
  let assetError;
  const assetTasks = [];
  const settings = {
    viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1, locale: "en-US",
    timezoneId: "UTC", colorScheme: "dark", reducedMotion: "reduce", serviceWorkers: "block"
  };
  async function guard(ctx) {
    ctx.on("response", response => {
      const url = new URL(response.url());
      const asset = url.origin === p.origin && p.webAssets.find(a => "/" + a.path === decodeURIComponent(url.pathname));
      if (!asset) return;
      assetTasks.push((async () => {
        try {
          if (response.status() !== 200 || hash(await response.body()) !== asset.sha256)
            throw new Error("capture.web-asset-identity");
        } catch { assetError = new Error("capture.web-asset-identity"); }
      })());
    });
    await ctx.route("**/*", route => {
      const request = route.request();
      if (permittedRequest(request.url(), request.method(), p)) return route.continue();
      const url = new URL(request.url());
      denied.add(`${request.method()} ${url.origin === p.origin ? url.pathname : `[external-origin:${url.hostname}]`}`);
      return route.abort();
    });
    await ctx.routeWebSocket("**/*", socket => {
      const url = new URL(socket.url());
      const origin = url.origin.replace(/^ws:/, "http:");
      if (origin !== p.origin || !p.allowedWebsocketPaths.includes(url.pathname)) {
        denied.add("WEBSOCKET [unapproved]");
        socket.close();
      } else socket.connectToServer();
    });
  }
  async function assertNoDenied() {
    await Promise.all(assetTasks);
    if (assetError) throw assetError;
    if (denied.size) throw new Error(`capture.network-denied: ${[...denied].join(";")}`);
  }
  try {
    loginContext = await browser.newContext(settings);
    await guard(loginContext);
    phase = "server-info";
    const publicInfo = await loginContext.request.get(p.origin + "/System/Info/Public");
    if (!publicInfo.ok()) throw new Error("capture.server-unavailable");
    const info = await publicInfo.json();
    if (info.Id !== p.serverId || info.Version !== p.serverVersion) throw new Error("capture.server-identity");
    const login = loginPage = await loginContext.newPage();
    login.on("pageerror", error => {
      const member = error.message.match(/Cannot read properties of (?:undefined|null) \(reading '([A-Za-z]{1,40})'\)/)?.[1];
      clientFailures.add(member ? `missing-${member}` : ["TypeError", "ReferenceError", "SyntaxError"].includes(error.name)
        ? error.name : "client-error");
    });
    phase = "login-navigation";
    const [usersResponse] = await Promise.all([
      login.waitForResponse(response => new URL(response.url()).pathname.toLowerCase() === "/users/public"
        && response.request().method() === "GET", { timeout: 30000 }),
      login.goto(p.origin + "/web/index.html#!/login", { waitUntil: "domcontentloaded" })
    ]);
    await login.locator("#loginPage").waitFor({ state: "visible", timeout: 30000 });
    phase = "public-users";
    if (usersResponse.status() !== 200) throw new Error(`capture.public-users-rejected-${usersResponse.status()}`);
    await usersResponse.finished();
    phase = "manual-form";
    await login.locator("#txtManualName:focus, .btnManual:visible").first().waitFor({ state: "visible", timeout: 30000 });
    const manual = login.locator(".btnManual");
    if (await manual.isVisible()) await manual.click();
    phase = "manual-input";
    await login.locator("#txtManualName").waitFor({ state: "visible", timeout: 30000 });
    await login.locator("#txtManualName").fill(credentials.username);
    await login.locator("#txtManualPassword").fill(credentials.password);
    phase = "manual-submit";
    const [authResponse] = await Promise.all([
      login.waitForResponse(response => new URL(response.url()).pathname.toLowerCase() === "/users/authenticatebyname"
        && response.request().method() === "POST", { timeout: 30000 }),
      login.locator(".manualLoginForm button[type=submit]").click()
    ]);
    phase = "authentication-response";
    if (authResponse.status() !== 200) throw new Error(`capture.authentication-rejected-${authResponse.status()}`);
    const auth = await authResponse.json();
    phase = "login-policy";
    if (normalizedId(auth.User?.Id) !== normalizedId(p.userId) || auth.User?.Policy?.IsAdministrator !== false
      || auth.User?.Policy?.EnableAllFolders !== false
      || !Array.isArray(auth.User?.Policy?.EnabledFolders)
      || auth.User.Policy.EnabledFolders.map(normalizedId).sort().join(",") !== p.libraryIds.map(normalizedId).sort().join(","))
      throw new Error("capture.account-scope");
    const realUser = auth.User;
    auth.AccessToken = undefined;
    phase = "postauth-navigation";
    try {
      await login.waitForURL(url => !url.hash.includes("login"), { timeout: 30000 });
    } catch {
      const route = new URL(login.url()).hash.split("?")[0];
      const routeKind = /login/i.test(route) ? "login" : /home/i.test(route) ? "home" : "other";
      const loginVisible = await login.locator("#loginPage:visible").count() > 0;
      const searchControls = await login.locator(searchSelector).count();
      throw new Error(`capture.postauth-navigation-${routeKind}-${loginVisible ? "login-visible" : "login-hidden"}-${searchControls ? "search-visible" : "search-hidden"}`);
    }
    phase = "postauth-search";
    await login.locator(searchSelector).waitFor({ state: "visible", timeout: 30000 });
    await assertNoDenied();
    const state = await loginContext.storageState();
    // Session state crosses contexts only in memory. No login page, secrets, state, HAR or trace is saved.
    context = await browser.newContext({ ...settings, storageState: state,
      recordVideo: { dir: config.outputDirectory, size: { width: 1920, height: 1080 } } });
    await guard(context);
    const page = await context.newPage();
    const video = page.video();
    const ledger = [], anchors = [], evidence = [];
    let detailResponse;
    const start = performance.now();
    let observationError;
    const responseTasks = [];
    page.on("response", response => {
      const url = new URL(response.url());
      const submittedQuery = [...url.searchParams].find(([key]) => key.toLowerCase() === "searchterm")?.[1];
      if (url.origin === p.origin && submittedQuery !== undefined && p.allowedApiPaths.includes(url.pathname))
        nativeRequests.push({ endpoint: url.pathname, method: response.request().method(),
          exactSubmittedQuery: submittedQuery === p.query, status: response.status() });
      if (url.origin === p.origin && [
        `/Users/${p.userId}/Items/${p.expectedResult.id}`, `/Items/${p.expectedResult.id}`
      ].some(endpoint => endpoint.toLowerCase() === url.pathname.toLowerCase())) {
        responseTasks.push((async () => {
          try {
            if (response.status() !== 200) throw new Error("capture.detail-response");
            const item = summary(await response.json());
            if (!sameResult(item, p.expectedResult)) throw new Error("capture.detail-response-identity");
            detailResponse = { endpoint: url.pathname, status: response.status(), ...item };
          } catch (error) { observationError = error; }
        })());
      }
      if (url.origin !== p.origin || url.pathname !== p.searchEndpoint || submittedQuery !== p.query) return;
      responseTasks.push((async () => {
        try {
          if (response.status() !== 200) throw new Error("capture.search-response");
          const body = await response.json();
          const raw = body.Items ?? body.SearchHints;
          if (!Array.isArray(raw)) throw new Error("capture.search-contract");
          const results = raw.map(summary);
          evidence.push({ endpoint: url.pathname, method: response.request().method(),
            submittedQuery, status: response.status(), results });
          ledger.push({ milliseconds: performance.now() - start, event: "search-response", query: p.query });
        } catch (error) { observationError = error; }
      })());
    });
    async function observe(id, locator, identity, event = "observed") {
      if (await page.locator("#loginPage:visible").count()) throw new Error("capture.recorded-auth-expired");
      await locator.waitFor({ state: "visible", timeout: 30000 });
      const box = await locator.boundingBox();
      if (!captionSafe(box)) throw new Error("capture.target-geometry");
      const region = { x: box.x / 1920, y: box.y / 1080, width: box.width / 1920, height: box.height / 1080 };
      const layout = { identity, region, viewport: { width: 1920, height: 1080 } };
      await page.screenshot({ path: path.join(config.outputDirectory, id + ".png"), animations: "disabled" });
      anchors.push({ id, targetIdentity: identity, region, screenshot: id + ".png", event,
        clickX: event === "click" ? (box.x + box.width / 2) / 1920 : null,
        clickY: event === "click" ? (box.y + box.height / 2) / 1080 : null,
        layoutSha256: hash(JSON.stringify(layout)) });
      return box;
    }
    phase = "recorded-navigation";
    await page.goto(p.origin + "/web/index.html#!/home", { waitUntil: "domcontentloaded" });
    const searchButton = page.locator(searchSelector);
    await searchButton.waitFor({ state: "visible", timeout: 30000 });
    await observe("search-click", searchButton, "native header search control", "click");
    const searchBox = await searchButton.boundingBox();
    await searchButton.click({ position: { x: searchBox.width / 2, y: searchBox.height / 2 } });
    ledger.push({ milliseconds: performance.now() - start, event: "click", target: "native header search control" });
    phase = "search-input";
    const input = page.locator("#searchTextInput:visible");
    await input.waitFor({ state: "visible", timeout: 30000 });
    await input.pressSequentially(p.query, { delay: 35 });
    if (await input.inputValue() !== p.query) throw new Error("capture.query-input");
    await input.press("Enter");
    ledger.push({ milliseconds: performance.now() - start, event: "query-submitted", query: p.query });
    phase = "search-result";
    const card = page.locator(`.card[data-id="${p.expectedResult.id}"]`);
    await card.waitFor({ state: "visible", timeout: 45000 });
    await Promise.all(responseTasks);
    if (observationError) throw observationError;
    if (!evidence.length) throw new Error("capture.native-endpoint-not-observed");
    if (!evidence.some(response => response.results.some(item => sameResult(item, p.expectedResult))))
      throw new Error("capture.expected-result");
    const typedCard = await card.getAttribute("data-type") ?? await card.locator("[data-type]").first().getAttribute("data-type");
    if (typedCard !== p.expectedResult.type || !(await card.innerText()).includes(p.expectedResult.name))
      throw new Error("capture.visible-result-mismatch");
    await observe("search", input, "native submitted query: " + p.query);
    phase = "result-scroll";
    await scrollResultIntoView(card, (event, box) => ledger.push({
      milliseconds: performance.now() - start, event, target: p.expectedResult.id, box
    }));
    phase = "search-result";
    await observe("result", card, `native ${p.expectedResult.type} card ${p.expectedResult.id} ${p.expectedResult.name}`);
    ledger.push({ milliseconds: performance.now() - start, event: "authored-result-hold", durationMilliseconds: p.resultHoldMilliseconds });
    await page.waitForTimeout(p.resultHoldMilliseconds);
    phase = "detail-link";
    nativeNavigation = await card.locator("a").evaluateAll(nodes => nodes.map(node => ({
      href: node.getAttribute("href"), action: node.getAttribute("data-action"), class: node.getAttribute("class")
    })));
    const link = card.locator(`a.itemAction.textActionButton[href*="details"][href*="id=${p.expectedResult.id}"]:visible`).first();
    phase = "detail-href";
    const href = await link.getAttribute("href");
    if (!href) throw new Error("capture.native-href");
    const destination = new URL(href, page.url());
    const detailId = destination.hash.match(/[?&]id=([a-fA-F0-9]{32})/);
    if (destination.origin !== p.origin || !detailId || normalizedId(detailId[1]) !== normalizedId(p.expectedResult.id))
      throw new Error("capture.detail-identity");
    phase = "detail-observation";
    await observe("result-click", link, `native detail link ${p.expectedResult.id}`, "click");
    const linkBox = await link.boundingBox();
    phase = "detail-click";
    await link.click({ position: { x: linkBox.width / 2, y: linkBox.height / 2 } });
    phase = "detail-navigation";
    await page.waitForURL(url => url.hash.includes(p.expectedResult.id), { timeout: 30000 });
    phase = "detail-page";
    const title = page.locator(".itemName").filter({ hasText: p.expectedResult.name }).first();
    await title.waitFor({ state: "visible", timeout: 30000 });
    if (!(await title.innerText()).includes(p.expectedResult.name)) throw new Error("capture.detail-title");
    await observe("detail", title, `native detail title ${p.expectedResult.id} ${p.expectedResult.name}`);
    ledger.push({ milliseconds: performance.now() - start, event: "native-detail-verified", id: p.expectedResult.id,
      name: p.expectedResult.name, type: p.expectedResult.type });
    ledger.push({ milliseconds: performance.now() - start, event: "authored-detail-hold", durationMilliseconds: p.resultHoldMilliseconds });
    await page.waitForTimeout(p.resultHoldMilliseconds);
    await Promise.all(responseTasks);
    if (observationError) throw observationError;
    if (!detailResponse) throw new Error("capture.native-detail-response-not-observed");
    await assertNoDenied();
    await context.close(); context = undefined;
    await video.saveAs(path.join(config.outputDirectory, "browser.webm"));
    const rawPath = await video.path();
    if (rawPath !== path.join(config.outputDirectory, "browser.webm")) await fs.unlink(rawPath);
    await fs.writeFile(path.join(config.outputDirectory, "native-evidence.json"), JSON.stringify({
      schemaVersion: 1, profile: p, serverId: p.serverId, serverVersion: info.Version, webVersion: p.webVersion,
      declaredSetupProvenance: { pluginIdentity: p.pluginIdentity, corpusSha256: p.corpusSha256,
        note: "Externally reviewed setup manifest; nonadmin client does NOT independently verify plugin/corpus generation." },
      verifiedWebAssets: p.webAssets, nonadminUserId: normalizedId(realUser.Id),
      libraryIds: p.libraryIds, observedNativeSearch: evidence, nativeDetail: p.expectedResult,
      observedNativeDetailResponse: detailResponse, detailRoute: destination.hash,
      note: "No per-hit semantic attribution, timestamp seek, playback or snippet claim."
    }, null, 2));
    await fs.writeFile(path.join(config.outputDirectory, "observations.json"), JSON.stringify({ schemaVersion: 1, anchors }, null, 2));
    await fs.writeFile(path.join(config.outputDirectory, "landmarks.json"), JSON.stringify({
      schemaVersion: 1, profile: "reviewed-jellyfin-loopback", nativeRecorderFps: 25, browserVersion: browser.version(),
      clock: "worker monotonic events; source-frame anchor matches are separate, not exact action timestamp alignment", ledger
    }, null, 2));
    completed = true;
  } catch (error) {
    if (loginPage && !loginPage.isClosed()) {
      const controls = await loginPage.locator("button,input,a").evaluateAll(nodes => nodes.slice(0,100).map(node => ({
        tag: node.tagName, id: node.id, class: node.getAttribute("class"), type: node.getAttribute("type"),
        visible: Boolean(node.getClientRects().length) && getComputedStyle(node).visibility !== "hidden",
        searchLabel: node.getAttribute("aria-label") === "Search" || node.getAttribute("title") === "Search"
      })));
      await fs.writeFile(path.join(config.outputDirectory, "readiness-diagnostic.txt"), JSON.stringify({ phase, controls, nativeRequests, nativeNavigation }, null, 2));
    }
    if (denied.size) throw new Error(`capture.network-denied: ${[...denied].join(";")}`);
    if (assetError) throw assetError;
    if (String(error.message).startsWith("capture.")) throw error;
    const reason = error.message.includes("strict mode violation") ? "ambiguous-control"
      : error.message.includes("has been closed") ? "closed-page"
      : error.message.includes("Execution context was destroyed") ? "navigation-context"
      : error.name === "TimeoutError" ? "timeout" : "failed";
    throw new Error(`capture.${phase}-${reason}${clientFailures.size ? "-" + [...clientFailures].join("-").toLowerCase() : ""}`);
  } finally {
    try {
      if (context) await context.close();
    } finally {
      try {
        if (loginContext) await loginContext.close();
      } finally {
        try { await browser.close(); } finally {
          if (!completed) {
            // A failed/expired authentication attempt is not a distributable take.
            for (const name of await fs.readdir(config.outputDirectory))
              if (/\.(webm|png|json)$/i.test(name)) await fs.unlink(path.join(config.outputDirectory, name));
          }
        }
      }
    }
  }
}
