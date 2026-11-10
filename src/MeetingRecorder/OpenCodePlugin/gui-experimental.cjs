"use strict"
// Recorder-owned GUI bridge for OpenCode Desktop, protocol v1
// Both processes evaluate this
// file, so the top level holds only host-shared imports and pure checks; node:* modules are
// required inside main, where the filesystem is used.
const { Plugin, Ipc } = require("@opencode/plugin/gui")
const { Schema } = require("effect")

// Desktop's loader runs the bundle with only require/module/exports (no __dirname, no bundle
// path), so the extractor replaces this token with the absolute queue folder when it writes the file.
const QUEUE_DIRECTORY = "__MEETINGRECORDER_QUEUE_DIRECTORY__"
const PLUGIN_VERSION = "1.0.0"
const OWNER = "MeetingRecorder"
const REQUEST_TTL_MS = 120000
const FUTURE_SKEW_MS = 30000
const MAX_JSON_BYTES = 65536
const MAX_TITLE = 512
// Protocol defaults; tests shorten them through exports.timing. clientMs bounds every Desktop call.
const timing = { tickMs: 1000, pollMs: 500, readyDelayMs: 1000, claimMs: 30000, openMs: 20000, windowFreshMs: 3000, clientMs: 15000 }

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$/
const SESSION = /^ses_\w+$/
const ROOT = /^[A-Za-z]:\\(?:[^\\/:*?"<>|\0]+\\)*[^\\/:*?"<>|\0]+$/

const isCanonicalRoot = (value) => typeof value === "string" && ROOT.test(value) && !/(^|\\)\.{1,2}(\\|$)/.test(value)
const keyOf = (dir) => String(dir).replace(/\//g, "\\").replace(/\\+$/, "").toLowerCase()
const toMs = (value) => (typeof value === "string" && UTC.test(value) ? Date.parse(value) : NaN)
const failure = (code, extra) => Object.assign(new Error(code), { appCode: code }, extra)
const appCodeOf = (error) => (error && error.appCode) || "io-error"
const titleOf = (session) => (typeof session.title === "string" ? session.title : "")
const sameDir = (session, root) => !!session && !!session.location && keyOf(session.location.directory) === keyOf(root)
// Window identity: state receives a bare ID, IPC methods a caller object. The live shape of
// each is a live-verification item (README); both forms are accepted. Anything else is null,
// and null never registers, claims or reads work (fail closed).
const windowKey = (arg) => {
  const key = arg && typeof arg === "object" ? ("window" in arg ? arg.window : arg.id) : arg
  return typeof key === "string" || typeof key === "number" ? key : null
}

// Diagnostics go to the host log as bounded codes only: no raw errors, paths, config or SDK objects.
// Each code is reported once per process so a persistent fault cannot flood the log.
const reporter = (ctx) => {
  const seen = new Set()
  return (level, code) => {
    if (seen.has(level + code)) return
    seen.add(level + code)
    try {
      ctx.log.write(level, "OpenCode handoff bridge", { code })
    } catch {}
  }
}

// Pure request check. `ref` identifies the request for its response; `code` is set when it is rejected.
const checkRequest = (raw, now) => {
  if (!raw || typeof raw !== "object" || typeof raw.requestId !== "string" || !GUID.test(raw.requestId)) return {}
  const expires = toMs(raw.expiresAt)
  const ref = {
    id: raw.requestId,
    root: typeof raw.seriesRoot === "string" ? raw.seriesRoot : "",
    title: typeof raw.title === "string" ? raw.title : "",
    expires,
  }
  const made = toMs(raw.createdAt)
  const valid =
    raw.version === 1 &&
    isCanonicalRoot(raw.seriesRoot) &&
    typeof raw.title === "string" &&
    raw.title.length <= MAX_TITLE &&
    Number.isFinite(made) &&
    expires === made + REQUEST_TTL_MS &&
    made <= now + FUTURE_SKEW_MS
  if (!valid) return { ref, code: "invalid-request" }
  return now > expires ? { ref, code: "expired" } : { ref }
}

const Bridge = Ipc.define({
  id: "meetingrecorder-handoff.bridge",
  state: Schema.Struct({
    work: Schema.NullOr(Schema.Struct({ id: Schema.String, sessionID: Schema.String })),
  }),
  methods: {
    claim: { input: Schema.Struct({ id: Schema.String }), output: Schema.Boolean },
    status: { input: Schema.Struct({ id: Schema.String }), output: Schema.String },
    release: { input: Schema.Struct({ id: Schema.String }) },
    ack: { input: Schema.Struct({ id: Schema.String, ok: Schema.Boolean, code: Schema.String }) },
  },
})

exports.timing = timing
exports.default = Plugin.define({
  os: ["windows"],

  // Window: claims the single request, rechecks it is still live, opens its session and acks
  // only after the exact tab is open and active. Nothing navigates after unload.
  setup(ctx) {
    const bridge = ctx.main(Bridge)
    const note = reporter(ctx)
    let busy = false
    let disposed = false
    let lastId
    let startTimer
    let timer
    const stopped = () => disposed || !!(ctx.signal && ctx.signal.aborted)

    // Rejects after ms; the late result of the wrapped call is ignored.
    const within = (promise, ms) => {
      let timer
      const limit = new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("timeout")), ms)
      })
      return Promise.race([promise, limit]).finally(() => clearTimeout(timer))
    }
    const waitFor = async (check, ms) => {
      const end = Date.now() + ms
      while (!stopped() && Date.now() < end) {
        if (check()) return true
        await new Promise((resolve) => setTimeout(resolve, timing.pollMs))
      }
      return !stopped() && check()
    }
    const isActive = (sessionID) => ctx.tabs.list().some((tab) => tab.session && tab.session.id === sessionID && tab.active)

    const handle = async (work) => {
      busy = true
      try {
        if (!(await bridge.claim({ id: work.id }))) return
        // Set only after a won claim: a lost race must leave the request open to this window's next poll.
        lastId = work.id
        const verdict = await bridge.status({ id: work.id })
        if (stopped()) return
        if (verdict === "retry") {
          await bridge.release({ id: work.id })
          lastId = undefined
          return
        }
        if (verdict !== "live") return await bridge.ack({ id: work.id, ok: false, code: verdict })
        let opened = false
        // The whole open-and-activate step shares one openMs budget; a hung open acks navigation-failed in time.
        const deadline = Date.now() + timing.openMs
        try {
          await within(ctx.tabs.open({ server: "sidecar", id: work.sessionID }, { focus: true }), timing.openMs)
          opened = await waitFor(() => isActive(work.sessionID), Math.max(0, deadline - Date.now()))
        } catch {
          opened = false
        }
        if (stopped()) return
        await bridge.ack({ id: work.id, ok: opened, code: opened ? "" : "navigation-failed" })
      } catch (error) {
        note("warn", "ipc-failed")
        await bridge.release({ id: work.id }).catch(() => undefined)
      } finally {
        busy = false
      }
    }

    const poll = () => {
      const snapshot = bridge.state()
      const work = snapshot && snapshot.work
      if (busy || stopped() || !work || work.id === lastId) return
      void handle(work)
    }
    startTimer = setTimeout(() => {
      timer = setInterval(poll, timing.pollMs)
    }, timing.readyDelayMs)

    return () => {
      disposed = true
      clearTimeout(startTimer)
      clearInterval(timer)
    }
  },

  // Main: owns the single current request, the root session linkage and the heartbeat.
  main(ctx) {
    const fs = require("node:fs")
    const path = require("node:path")
    const crypto = require("node:crypto")
    const note = reporter(ctx)

    // Owned paths are never followed through links or junctions (a redirected folder could
    // otherwise receive writes meant for the queue or the series linkage).
    const linked = (target) => {
      try {
        return fs.lstatSync(target).isSymbolicLink()
      } catch {
        return false
      }
    }
    const readJson = (file) => {
      if (linked(file)) throw failure("io-error")
      let text
      try {
        text = fs.readFileSync(file)
      } catch (error) {
        // Only ENOENT means absent. Any other read error (EBUSY, EPERM, ...) is transient and retried.
        if (error.code === "ENOENT" || error.code === "ENOTDIR") return undefined
        throw failure("io-error")
      }
      if (text.length > MAX_JSON_BYTES) throw new SyntaxError("too large")
      return JSON.parse(text.toString("utf8"))
    }
    const exists = (file) => {
      if (linked(file)) throw failure("io-error")
      try {
        fs.statSync(file)
        return true
      } catch (error) {
        if (error.code === "ENOENT") return false
        throw failure("io-error")
      }
    }
    const writeAtomic = (file, value) => {
      if (linked(path.dirname(file)) || linked(file)) throw failure("io-error")
      const tmp = `${file}.${crypto.randomBytes(6).toString("hex")}.tmp`
      try {
        fs.mkdirSync(path.dirname(file), { recursive: true })
        fs.writeFileSync(tmp, JSON.stringify(value), { flag: "wx" })
        fs.renameSync(tmp, file)
      } catch (error) {
        try {
          fs.rmSync(tmp, { force: true })
        } catch {}
        throw failure("io-error")
      }
    }
    // Only the request file is removed, and only while it still names this request.
    const removeIfSame = (file, id) => {
      try {
        const current = readJson(file)
        if (current && current.requestId === id && !linked(file)) fs.rmSync(file, { force: true })
      } catch {}
    }
    const codeOf = (error) => (error instanceof SyntaxError ? "link-invalid" : appCodeOf(error))

    if (typeof QUEUE_DIRECTORY !== "string" || !path.isAbsolute(QUEUE_DIRECTORY)) {
      note("error", "bootstrap-invalid")
      return
    }
    const queue = QUEUE_DIRECTORY
    if (linked(queue)) {
      note("error", "bootstrap-invalid")
      return
    }
    fs.mkdirSync(queue, { recursive: true })

    const reqFile = path.join(queue, "request.json")
    const readyFile = path.join(queue, "ready.json")
    const responseFile = (id) => path.join(queue, `response-${id}.json`)
    const cancelFile = (id) => path.join(queue, `cancel-${id}.json`)
    const intentFile = (root) => path.join(queue, `intent-${crypto.createHash("sha256").update(keyOf(root)).digest("hex")}.json`)
    const linkDir = (root) => path.join(root, ".meetingrecorder")
    const linkFile = (root) => path.join(linkDir(root), "opencode.json")

    const windows = new Map()
    let current = null
    let busy = false
    let disposed = false
    const stopped = () => disposed || !!(ctx.signal && ctx.signal.aborted)

    const cancelled = (id) => exists(cancelFile(id))
    // Only a readable marker counts as evidence; a transient stat error reads as "no evidence".
    const hasCancelEvidence = (id) => {
      try {
        return cancelled(id)
      } catch {
        return false
      }
    }
    // undefined when the file is absent; throws on any transient read problem.
    const pendingId = () => {
      const raw = readJson(reqFile)
      return raw && raw.requestId
    }
    // "dir" | "file" | "missing" | "retry" (transient stat error, not a verdict on the root).
    const dirState = (dir) => {
      try {
        return fs.statSync(dir).isDirectory() ? "dir" : "file"
      } catch (error) {
        return error.code === "ENOENT" ? "missing" : "retry"
      }
    }
    const ownsClaim = (id, caller) => {
      const key = windowKey(caller)
      return key !== null && !!current && !current.final && current.req.id === id && !!current.claim && current.claim.window === key
    }
    const focusWindow = (id) => {
      const win = ctx.windows.get(id)
      if (!win) return
      if (win.isMinimized()) win.restore()
      win.show()
      win.focus()
    }

    const writeHeartbeat = () => {
      const now = Date.now()
      for (const [id, seen] of windows) if (now - seen > timing.windowFreshMs) windows.delete(id)
      writeAtomic(readyFile, {
        version: 1,
        pluginVersion: PLUGIN_VERSION,
        appVersion: ctx.app.version,
        updatedAt: new Date(now).toISOString(),
        windowCount: windows.size,
      })
    }
    const heartbeat = () => {
      try {
        writeHeartbeat()
      } catch (error) {
        note("warn", appCodeOf(error))
      }
    }

    const respond = (ref, status, code = null, sessionId = null, title = ref.title) =>
      writeAtomic(responseFile(ref.id), {
        version: 1,
        requestId: ref.id,
        seriesRoot: ref.root,
        status,
        sessionId,
        title,
        code,
        createdAt: new Date().toISOString(),
      })
    const failRequest = (ref, code) => {
      const cancel = code === "cancelled"
      respond(ref, cancel ? "cancelled" : "failed", cancel ? null : code)
      removeIfSame(reqFile, ref.id)
    }
    const setFinal = (status, code = null) => {
      current.final = { status, code, sessionId: current.sessionId, title: current.title }
    }
    const complete = () => {
      const { req, final } = current
      respond(req, final.status, final.code, final.sessionId, final.title)
      removeIfSame(reqFile, req.id)
      current = null
      provider.changed()
    }

    // The one stop check used before every write that precedes creation, and by the watcher.
    // Returns null when live, "retry" for a transient read problem, else a stop code.
    const liveCheck = (ref) => {
      if (stopped()) return "disposed"
      try {
        if (cancelled(ref.id)) return "cancelled"
        if (Date.now() > ref.expires) return "expired"
        return pendingId() === ref.id ? null : "replaced"
      } catch {
        return "retry"
      }
    }
    const stopOrThrow = (ref) => {
      const stop = liveCheck(ref)
      if (stop) throw failure(stop)
    }

    // Every Desktop call is bounded; a hung call becomes session-unavailable instead of stalling the tick.
    const bounded = (promise) => {
      let timer
      const limit = new Promise((_, reject) => {
        timer = setTimeout(() => reject(failure("session-unavailable")), timing.clientMs)
      })
      return Promise.race([promise, limit]).finally(() => clearTimeout(timer))
    }
    // Lets TTL, cancel, replacement and unload end a pending resolution without waiting for Desktop.
    const withStopWatch = (ref, work) => {
      let timer
      const watch = new Promise((_, reject) => {
        timer = setInterval(() => {
          const stop = liveCheck(ref)
          if (stop && stop !== "retry") reject(failure(stop))
        }, timing.tickMs)
      })
      return Promise.race([work, watch]).finally(() => clearInterval(timer))
    }

    // Only the exact SessionNotFoundError (observed in the POC) means "genuinely absent".
    const getSession = async (sessionID) => {
      let session
      try {
        session = await bounded(ctx.client.session.get({ sessionID }))
      } catch (error) {
        if (error && error._tag === "SessionNotFoundError") return null
        throw failure("session-unavailable")
      }
      if (!session || session.id !== sessionID || !SESSION.test(session.id)) throw failure("session-unavailable")
      return session
    }
    const removeIntent = (root) => {
      try {
        fs.rmSync(intentFile(root), { force: true })
      } catch {}
    }

    const readLink = (root) => {
      if (linked(linkDir(root))) throw failure("link-invalid")
      let link
      try {
        link = readJson(linkFile(root))
      } catch (error) {
        throw failure(codeOf(error))
      }
      if (link === undefined) return undefined
      if (!link || link.version !== 1 || link.owner !== OWNER || typeof link.seriesRoot !== "string" || !SESSION.test(link.sessionId)) {
        throw failure("link-invalid")
      }
      return link
    }
    const readIntent = (root) => {
      let intent
      try {
        intent = readJson(intentFile(root))
      } catch (error) {
        throw failure(codeOf(error))
      }
      if (intent === undefined) return undefined
      if (
        !intent ||
        intent.version !== 1 ||
        intent.owner !== OWNER ||
        typeof intent.seriesRoot !== "string" ||
        keyOf(intent.seriesRoot) !== keyOf(root) ||
        !SESSION.test(intent.sessionId) ||
        !Number.isFinite(toMs(intent.createdAt))
      ) {
        throw failure("link-invalid")
      }
      return intent
    }
    const newIntent = (root) => {
      const intent = {
        version: 1,
        owner: OWNER,
        seriesRoot: root,
        sessionId: `ses_${crypto.randomBytes(16).toString("hex")}`,
        createdAt: new Date().toISOString(),
      }
      try {
        writeAtomic(intentFile(root), intent)
      } catch {
        throw failure("io-error")
      }
      return intent
    }
    // Post-create durability exception: runs after a successful create, with no stop check, so
    // a created conversation is never left unlinked by unload. Every other write is stop-checked first.
    const finishLink = (root, session) => {
      if (!SESSION.test(session.id)) throw failure("session-unavailable")
      try {
        writeAtomic(linkFile(root), { version: 1, owner: OWNER, seriesRoot: root, sessionId: session.id, hostVersion: ctx.app.version })
      } catch {
        throw failure("link-save-failed", { sessionId: session.id })
      }
      removeIntent(root)
      return { sessionId: session.id, title: titleOf(session) }
    }

    // Reuse the linked root session; create only after a genuine not-found or folder mismatch,
    // and only with the durable intent's ID. A rejected create keeps the intent (no fresh ID is
    // generated), so a lost response can never produce a second conversation.
    const resolveSession = async (ref, title) => {
      const root = ref.root
      const link = readLink(root)
      if (link) {
        const existing = await getSession(link.sessionId)
        if (existing && sameDir(existing, root)) {
          stopOrThrow(ref)
          removeIntent(root)
          return { sessionId: existing.id, title: titleOf(existing) }
        }
      }
      stopOrThrow(ref)
      const intent = readIntent(root) || newIntent(root)
      const known = await getSession(intent.sessionId)
      if (known) {
        if (!sameDir(known, root)) throw failure("session-unavailable")
        stopOrThrow(ref)
        return finishLink(root, known)
      }
      stopOrThrow(ref)
      let created
      try {
        created = await bounded(ctx.client.session.create({ id: intent.sessionId, title, location: { directory: root } }))
      } catch {
        throw failure("session-unavailable")
      }
      // A server that ignores caller IDs returns its own: accepted when the folder matches (finishLink checks the form).
      if (!created || !sameDir(created, root)) throw failure("session-unavailable")
      return finishLink(root, created)
    }

    const handleRequest = async () => {
      let raw
      try {
        raw = readJson(reqFile)
      } catch (error) {
        note("warn", error instanceof SyntaxError ? "invalid-request" : appCodeOf(error))
        return
      }
      const checked = checkRequest(raw, Date.now())
      const ref = checked.ref
      if (!ref) return
      try {
        if (exists(responseFile(ref.id))) return removeIfSame(reqFile, ref.id)
        if (checked.code) return failRequest(ref, checked.code)
        if (cancelled(ref.id)) return failRequest(ref, "cancelled")
        const dir = dirState(ref.root)
        if (dir === "retry") return
        if (dir !== "dir") return failRequest(ref, "invalid-request")
      } catch (error) {
        note("warn", appCodeOf(error))
        return
      }
      current = { req: ref, claim: null, sessionId: null, title: ref.title, final: null }
      try {
        const session = await withStopWatch(ref, resolveSession(ref, ref.title))
        current.sessionId = session.sessionId
        current.title = session.title
        provider.changed()
      } catch (error) {
        const code = appCodeOf(error)
        // Unload leaves the request untouched: no response, no further writes.
        if (code === "disposed") return
        // Transient read problem: nothing is final; the next tick starts again from the request file.
        if (code === "retry") {
          current = null
          return
        }
        current.sessionId = error.sessionId || null
        if (code === "cancelled") return setFinal("cancelled")
        note("warn", code)
        setFinal("failed", code)
      }
    }

    const tick = async () => {
      if (busy || stopped()) return
      busy = true
      try {
        if (!current) return await handleRequest()
        if (current.final) return complete()
        if (current.claim && Date.now() - current.claim.at > timing.claimMs) {
          current.claim = null
          provider.changed()
        }
        if (current.claim) return
        const stop = liveCheck(current.req)
        if (!stop || stop === "retry" || stop === "disposed") return
        setFinal(stop === "cancelled" ? "cancelled" : "failed", stop === "cancelled" ? null : stop)
      } catch (error) {
        note("warn", appCodeOf(error))
      } finally {
        busy = false
      }
    }

    const provider = ctx.provide(Bridge, {
      state: (window) => {
        const key = windowKey(window)
        if (key === null) return { work: null }
        windows.set(key, Date.now())
        const open = current && !current.final && current.sessionId && (!current.claim || current.claim.window === key)
        return { work: open ? { id: current.req.id, sessionID: current.sessionId } : null }
      },
      claim: (input, caller) => {
        const key = windowKey(caller)
        if (key === null) return false
        if (!current || current.final || current.req.id !== input.id || !current.sessionId) return false
        if (current.claim) return current.claim.window === key
        if (liveCheck(current.req) !== null) return false
        current.claim = { window: key, at: Date.now() }
        focusWindow(key)
        provider.changed()
        return true
      },
      status: (input, caller) => {
        if (!ownsClaim(input.id, caller)) return "gone"
        const stop = liveCheck(current.req)
        if (stop === null) return "live"
        if (stop === "disposed") return "gone"
        return stop
      },
      release: (input, caller) => {
        if (!ownsClaim(input.id, caller)) return
        current.claim = null
        provider.changed()
      },
      ack: (input, caller) => {
        if (!ownsClaim(input.id, caller)) return
        current.claim = null
        // Ruling: an unreadable cancel marker is not evidence of cancellation, so the ack finalises as "opened"
        // (never "opened-after-cancel" without evidence). The ack must always reach a final state.
        if (input.ok) setFinal(hasCancelEvidence(input.id) ? "opened-after-cancel" : "opened")
        else if (input.code === "cancelled") setFinal("cancelled")
        else setFinal("failed", input.code || "navigation-failed")
        provider.changed()
      },
    })

    const offClose = ctx.windows.on("close", (win) => {
      const key = windowKey(win)
      if (key === null) return
      windows.delete(key)
      if (current && current.claim && current.claim.window === key) current.claim = null
      provider.changed()
    })

    // Heartbeat runs on its own timer, outside the request gate, so a slow Desktop call cannot make it stale.
    heartbeat()
    const heartbeatTimer = setInterval(heartbeat, timing.tickMs)
    const timer = setInterval(() => void tick(), timing.tickMs)
    ctx.onCleanup(() => {
      disposed = true
      clearInterval(heartbeatTimer)
      clearInterval(timer)
      if (typeof offClose === "function") offClose()
    })
  },
})
