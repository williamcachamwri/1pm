const MIRROR = "/mirror";
const canvas = document.getElementById("c");
const ctx = canvas.getContext("2d");
const statusEl = document.getElementById("mirror-status");

let visualRotation = 0;
let _lockW = 0;
let _lockH = 0;
let zoom = 1;
const ZOOM_STEP = 1.2;
const ZOOM_MIN = 0.5;
const ZOOM_MAX = 3;

let pendingFrame = null;
let rafScheduled = false;
let lastFrame = null;
let lastFrameLandscape = null;
let frameCount = 0;
let runGeneration = 0;
let abortStream = null;

function setStatus(text, tone) {
  if (!statusEl) return;
  statusEl.textContent = text;
  statusEl.className = "mirror-status" + (tone ? ` ${tone}` : "");
}

function fitCanvas() {
  if (!canvas.width || !canvas.height) return;
  canvas.style.width = "100%";
  canvas.style.height = "100%";
}

window.addEventListener("resize", fitCanvas);

function setZoom(value) {
  zoom = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, value));
  if (Math.abs(zoom - 1) < 0.02) zoom = 1;
  fitCanvas();
}

function touchCoords(e) {
  const rect = canvas.getBoundingClientRect();
  const sx = ((e.clientX - rect.left) * canvas.width) / rect.width;
  const sy = ((e.clientY - rect.top) * canvas.height) / rect.height;
  const cw = canvas.width;
  const ch = canvas.height;
  const sideways = Math.abs(visualRotation % 180) === 90;
  const dw = sideways ? ch : cw;
  const dh = sideways ? cw : ch;
  const dx = sx - cw / 2;
  const dy = sy - ch / 2;
  const rad = (-visualRotation * Math.PI) / 180;
  const cos = Math.cos(rad);
  const sin = Math.sin(rad);
  const bx = dx * cos - dy * sin + dw / 2;
  const by = dx * sin + dy * cos + dh / 2;
  return {
    x: Math.max(0, Math.min(65535, Math.round((bx / dw) * 65535))),
    y: Math.max(0, Math.min(65535, Math.round((by / dh) * 65535))),
  };
}

async function postJson(path, payload) {
  try {
    await fetch(`${MIRROR}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
  } catch (_) {
    /* stream may be down */
  }
}

let activePointer = null;
let lastCoords = { x: 0, y: 0 };

// The proxy opens one connection per request, so concurrent fetches can land
// out of order and break drags. Send touches strictly in sequence, collapsing
// queued moves into the newest one.
const touchQueue = [];
let touchBusy = false;

function sendTouch(type, coords, isMove = false) {
  const last = touchQueue[touchQueue.length - 1];
  const event = { type, x: coords.x, y: coords.y, isMove };
  if (isMove && last && last.isMove) touchQueue[touchQueue.length - 1] = event;
  else touchQueue.push(event);
  pumpTouches();
}

async function pumpTouches() {
  if (touchBusy) return;
  touchBusy = true;
  while (touchQueue.length) {
    const { type, x, y } = touchQueue.shift();
    await postJson("/touch", { type, x, y });
  }
  touchBusy = false;
}

// iOS system gestures (Home bar, Control Center, Notification Center) only
// trigger when the contact begins on the screen edge itself.
const EDGE = 2500;
function snapToEdge(c) {
  return {
    x: c.x < EDGE ? 0 : c.x > 65535 - EDGE ? 65535 : c.x,
    y: c.y < EDGE ? 0 : c.y > 65535 - EDGE ? 65535 : c.y,
  };
}

// Injected touches arrive too slowly for iOS to read a bottom-edge flick as
// the Home gesture, so a drag that starts on the bottom edge is kept local and
// turned into the Home button (or a double press for the app switcher when
// the finger pauses before lifting).
const HOME_SWIPE_MIN = 5000;
const SWITCHER_HOLD_MS = 450;
let edgeSwipe = null;

async function pressHome(times = 1) {
  for (let i = 0; i < times; i++) {
    if (i) await new Promise((r) => setTimeout(r, 120));
    await postJson("/button", { name: "home", state: "press" });
  }
}

function releaseActive() {
  if (activePointer === null) return;
  activePointer = null;
  if (edgeSwipe) {
    const swipe = edgeSwipe;
    edgeSwipe = null;
    if (swipe.armed) {
      const paused = performance.now() - swipe.lastMoveAt > SWITCHER_HOLD_MS;
      pressHome(paused ? 2 : 1);
    } else {
      sendTouch("contact", swipe.start);
      sendTouch("release", swipe.start);
    }
    return;
  }
  sendTouch("release", lastCoords);
}

// Listen on the whole document so a drag can start on the black margin just
// below the picture; coordinates clamp onto the screen edge.
document.addEventListener("pointerdown", (e) => {
  if (e.button !== 0) return;
  e.preventDefault();
  canvas.focus({ preventScroll: true });
  try {
    document.body.setPointerCapture(e.pointerId);
  } catch (_) {}
  activePointer = e.pointerId;
  lastCoords = snapToEdge(touchCoords(e));
  if (lastCoords.y === 65535) {
    edgeSwipe = { start: lastCoords, armed: false, lastMoveAt: performance.now(), lastY: lastCoords.y };
    return;
  }
  sendTouch("contact", lastCoords);
});

function onMove(e) {
  if (e.pointerId !== activePointer) return;
  e.preventDefault();
  lastCoords = touchCoords(e);
  if (edgeSwipe) {
    if (edgeSwipe.start.y - lastCoords.y > HOME_SWIPE_MIN) edgeSwipe.armed = true;
    if (Math.abs(lastCoords.y - edgeSwipe.lastY) > 600) {
      edgeSwipe.lastMoveAt = performance.now();
      edgeSwipe.lastY = lastCoords.y;
    }
    return;
  }
  sendTouch("contact", lastCoords, true);
}

function onUp(e) {
  if (e.pointerId !== activePointer) return;
  e.preventDefault();
  lastCoords = touchCoords(e);
  releaseActive();
}

window.addEventListener("pointermove", onMove);
window.addEventListener("pointerup", onUp);
window.addEventListener("pointercancel", onUp);
document.body.addEventListener("lostpointercapture", (e) => {
  if (e.pointerId === activePointer) releaseActive();
});
window.addEventListener("blur", releaseActive);
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState !== "visible") releaseActive();
});

document.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  pressHome();
});

window.HandsetKeys.attach({
  post: (payload) => postJson("/key", payload),
  enabled: () => {
    try {
      return window.parent.handsetKeyboardOn !== false;
    } catch (_) {
      return true;
    }
  },
});

window.addEventListener("message", (e) => {
  if (e.origin !== window.location.origin) return;
  const d = e.data;
  if (!d || d.source !== "handset") return;
  if (d.cmd === "set-rotation") {
    visualRotation = ((d.deg || 0) % 360 + 360) % 360;
    _lockW = _lockH = 0;
    if (lastFrame) drawFrame(lastFrame);
    fitCanvas();
  } else if (d.cmd === "rotate") {
    visualRotation = ((visualRotation + (d.deg || 0)) % 360 + 360) % 360;
    _lockW = _lockH = 0;
    if (lastFrame) drawFrame(lastFrame);
    fitCanvas();
  } else if (d.cmd === "reset-rotation") {
    visualRotation = 0;
    _lockW = _lockH = 0;
    if (lastFrame) drawFrame(lastFrame);
    fitCanvas();
  } else if (d.cmd === "zoom-in") {
    setZoom(zoom * ZOOM_STEP);
  } else if (d.cmd === "zoom-out") {
    setZoom(zoom / ZOOM_STEP);
  } else if (d.cmd === "zoom-reset") {
    setZoom(1);
  } else if (d.cmd === "set-audio") {
    if (d.enabled) {
      startAudio();
    } else {
      stopAudio();
    }
  } else if (d.cmd === "reload") {
    beginRun();
  }
});

// ==================== BỘ GIẢI MÃ VÀ PHÁT ÂM THANH HI-FI WINDOWS (ELASTIC RING BUFFER) ====================
let audioCtx = null;
let audioAbort = null;
let audioActive = false;
let audioScriptNode = null;
let audioGainNode = null;

// Tham số Ring Buffer (48kHz stereo)
const RING_BUFFER_SIZE = 48000 * 2; // 1 giây âm thanh stereo (96,000 samples)
const ringBufferL = new Float32Array(RING_BUFFER_SIZE);
const ringBufferR = new Float32Array(RING_BUFFER_SIZE);
let ringWritePos = 0;
let ringReadPos = 0;
let ringBufferedFrames = 0;
let isPrebuffering = true;
const PREBUFFER_FRAMES = 2400; // 50ms pre-buffering (5 packet) để chống giật hoàn toàn
const MAX_LATENCY_FRAMES = 5760; // 120ms tối đa để giữ đồng bộ khẩu hình video (lip-sync)
const TARGET_LATENCY_FRAMES = 3840; // 80ms đích khi cần drift correction

async function startAudio() {
  if (audioActive) return;
  audioActive = true;
  audioAbort = new AbortController();
  isPrebuffering = true;
  ringWritePos = 0;
  ringReadPos = 0;
  ringBufferedFrames = 0;

  try {
    audioCtx = new (window.AudioContext || window.webkitAudioContext)({
      sampleRate: 48000,
      latencyHint: "interactive",
    });
    await audioCtx.resume();

    // Gain node để khuếch đại âm thanh trong trẻo, to rõ sống động
    audioGainNode = audioCtx.createGain();
    audioGainNode.gain.value = 1.35; // Tăng âm lượng đầu ra thêm 35% cho âm thanh dày và rõ nét
    audioGainNode.connect(audioCtx.destination);

    // ScriptProcessorNode 1024 frames (~21.3ms) cho luồng phát liên tục, triệt tiêu hoàn toàn tiếng lách tách
    audioScriptNode = audioCtx.createScriptProcessor(1024, 0, 2);
    audioScriptNode.onaudioprocess = (e) => {
      const outL = e.outputBuffer.getChannelData(0);
      const outR = e.outputBuffer.getChannelData(1);
      const req = outL.length;

      if (!audioActive || isPrebuffering || ringBufferedFrames < req) {
        outL.fill(0);
        outR.fill(0);
        isPrebuffering = true;
        return;
      }

      // Giữ đồng bộ hình - tiếng (lip-sync): nếu bộ đệm dồn quá 120ms
      if (ringBufferedFrames > MAX_LATENCY_FRAMES) {
        const excess = ringBufferedFrames - TARGET_LATENCY_FRAMES;
        ringReadPos = (ringReadPos + excess) % RING_BUFFER_SIZE;
        ringBufferedFrames -= excess;
      }

      for (let i = 0; i < req; i++) {
        outL[i] = ringBufferL[ringReadPos];
        outR[i] = ringBufferR[ringReadPos];
        ringReadPos = (ringReadPos + 1) % RING_BUFFER_SIZE;
      }
      ringBufferedFrames -= req;
    };
    audioScriptNode.connect(audioGainNode);
  } catch (e) {
    stopAudio();
    return;
  }

  const playPcm = (pcmBytes) => {
    if (!audioActive) return;
    const i16 = new Int16Array(pcmBytes.buffer, pcmBytes.byteOffset, pcmBytes.byteLength >> 1);
    const frames = i16.length >> 1;
    if (frames === 0) return;

    const INV = 1 / 32768;
    for (let i = 0, j = 0; i < frames; i++, j += 2) {
      ringBufferL[ringWritePos] = i16[j] * INV;
      ringBufferR[ringWritePos] = i16[j + 1] * INV;
      ringWritePos = (ringWritePos + 1) % RING_BUFFER_SIZE;
    }
    ringBufferedFrames += frames;
    if (isPrebuffering && ringBufferedFrames >= PREBUFFER_FRAMES) {
      isPrebuffering = false;
    }
  };

  while (audioActive) {
    try {
      const resp = await fetch(`${MIRROR}/audio.bin`, { signal: audioAbort.signal });
      if (!resp.ok) {
        await new Promise((r) => setTimeout(r, 600));
        continue;
      }
      const reader = resp.body.getReader();
      let buf = new Uint8Array(0);
      while (audioActive) {
        const { value, done } = await reader.read();
        if (done) break;
        const merged = new Uint8Array(buf.length + value.length);
        merged.set(buf);
        merged.set(value, buf.length);
        buf = merged;
        while (buf.length >= 4) {
          const len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
          if (buf.length < 4 + len) break;
          const pcmBytes = buf.slice(4, 4 + len);
          buf = buf.subarray(4 + len);
          if (pcmBytes.length) {
            playPcm(pcmBytes);
          }
        }
      }
    } catch (e) {
      if (!audioActive) break;
      await new Promise((r) => setTimeout(r, 600));
    }
  }
}

function stopAudio() {
  audioActive = false;
  isPrebuffering = true;
  ringBufferedFrames = 0;
  if (audioAbort) {
    audioAbort.abort();
    audioAbort = null;
  }
  if (audioScriptNode) {
    try {
      audioScriptNode.disconnect();
    } catch (_) {}
    audioScriptNode = null;
  }
  if (audioGainNode) {
    try {
      audioGainNode.disconnect();
    } catch (_) {}
    audioGainNode = null;
  }
  if (audioCtx) {
    try {
      audioCtx.close();
    } catch (_) {}
    audioCtx = null;
  }
}

async function fetchCodecWithRetry() {
  const delays = [300, 600, 1000, 1500, 2000, 2500, 3000, 3000, 3000, 3000];
  let lastErr = "";
  for (let attempt = 0; attempt < 50; attempt++) {
    if (attempt > 0) {
      setStatus(`Waiting for stream to start (retry ${attempt})…`);
      const delay = delays[Math.min(attempt - 1, delays.length - 1)];
      await new Promise((r) => setTimeout(r, delay));
      if (attempt % 2 === 0) {
        // Kích hoạt PLI để iPhone gửi lại IDR chứa VPS/SPS/PPS parameter sets
        fetch(`${MIRROR}/pli`, { method: "POST", cache: "no-store" }).catch(() => {});
      }
    }
    try {
      const resp = await fetch(`${MIRROR}/codec`, { cache: "no-store" });
      if (!resp.ok) {
        if (resp.status === 503) {
          lastErr = "Waiting for stream on iPhone…";
          setStatus("Stream starting on the iPhone…");
          continue;
        }
        lastErr = `HTTP ${resp.status}`;
        continue;
      }
      const info = await resp.json();
      if (!info.codec || !info.description) {
        lastErr = "waiting for video parameters";
        continue;
      }
      const description = Uint8Array.from(atob(info.description), (c) => c.charCodeAt(0));
      return { codec: info.codec.trim(), description };
    } catch (err) {
      lastErr = err.message || String(err);
    }
  }
  throw new Error(lastErr || "codec unreachable");
}

function getActiveDimensions(codedW, codedH) {
  // Apple's hardware encoder on iOS 17/18 pads to 16-pixel macroblocks:
  // - iPhone 14 Pro, 15, 15 Pro, 16, 16e: coded 1184x2576 -> active 1179x2556 (crop 5px right, 20px bottom)
  // - iPhone 14/15/16 Plus & 14/15 Pro Max: coded 1296x2800 -> active 1290x2796 (crop 6px right, 4px bottom)
  // - iPhone 16 Pro: coded 1216x2624 -> active 1206x2622 (crop 10px right, 2px bottom)
  // - iPhone 16 Pro Max: coded 1328x2880 -> active 1320x2868 (crop 8px right, 12px bottom)
  // - iPhone 13 / 13 Pro / 14: coded 1184x2544 -> active 1170x2532 (crop 14px right, 12px bottom)
  // - iPhone 12/13 mini: coded 1088x2352 -> active 1080x2340 (crop 8px right, 12px bottom)
  // - iPhone SE: coded 752x1344 -> active 750x1334 (crop 2px right, 10px bottom)
  const isLandscape = codedW > codedH;
  const pw = isLandscape ? codedH : codedW;
  const ph = isLandscape ? codedW : codedH;

  let aw = pw;
  let ah = ph;

  if (pw === 1184 && ph === 2576) {
    aw = 1179;
    ah = 2556;
  } else if (pw === 1296 && ph === 2800) {
    aw = 1290;
    ah = 2796;
  } else if (pw === 1216 && ph === 2624) {
    aw = 1206;
    ah = 2622;
  } else if (pw === 1328 && ph === 2880) {
    aw = 1320;
    ah = 2868;
  } else if (pw === 1184 && ph === 2544) {
    aw = 1170;
    ah = 2532;
  } else if (pw === 1088 && ph === 2352) {
    aw = 1080;
    ah = 2340;
  } else if (pw === 752 && ph === 1344) {
    aw = 750;
    ah = 1334;
  }

  return {
    width: isLandscape ? ah : aw,
    height: isLandscape ? aw : ah,
  };
}

function drawFrame(f) {
  const sideways = Math.abs(visualRotation % 180) === 90;
  const fw = f.displayWidth;
  const fh = f.displayHeight;
  const targetW = sideways ? fh : fw;
  const targetH = sideways ? fw : fh;

  // Lock canvas size to largest seen so iOS 2752<->2736 height oscillations never cause jitter
  if ((targetW > targetH) !== (_lockW > _lockH)) {
    _lockW = _lockH = 0;
  }
  if (targetW > _lockW || targetH > _lockH || targetW !== canvas.width || targetH !== canvas.height) {
    _lockW = Math.max(_lockW, targetW);
    _lockH = Math.max(_lockH, targetH);
    canvas.width = _lockW;
    canvas.height = _lockH;
    fitCanvas();
  }

  const dstW = canvas.width;
  const dstH = canvas.height;
  const fpW = sideways ? dstH : dstW;
  const fpH = sideways ? dstW : dstH;
  ctx.save();
  ctx.translate(dstW / 2, dstH / 2);
  if (visualRotation) ctx.rotate((visualRotation * Math.PI) / 180);

  ctx.drawImage(f, -fpW / 2, -fpH / 2, fpW, fpH);
  ctx.restore();
}

function drawPending() {
  rafScheduled = false;
  const f = pendingFrame;
  pendingFrame = null;
  if (!f) return;
  const landscape = f.displayWidth > f.displayHeight;
  if (lastFrameLandscape !== null && landscape !== lastFrameLandscape) {
    visualRotation = 0;
    _lockW = _lockH = 0;
  }
  lastFrameLandscape = landscape;
  try {
    drawFrame(f);
    if (frameCount === 1) setStatus("Live", "ok");
  } catch (_) {}
  if (lastFrame && lastFrame !== f) {
    try {
      lastFrame.close();
    } catch (_) {}
  }
  lastFrame = f;
}

async function run(generation) {
  setStatus("Connecting to stream…");
  frameCount = 0;
  const startedAt = performance.now();
  const watchdog = window.setInterval(() => {
    if (generation !== runGeneration) {
      window.clearInterval(watchdog);
      return;
    }
    if (frameCount > 0) {
      window.clearInterval(watchdog);
      return;
    }
    if (performance.now() - startedAt > 12000) {
      window.clearInterval(watchdog);
      setStatus("No picture yet — press Restart screen on the right", "warn");
    }
  }, 1000);

  const { codec, description } = await fetchCodecWithRetry();
  if (generation !== runGeneration) return;

  const decoderConfig = { codec, description, optimizeForLatency: true };
  const support = await VideoDecoder.isConfigSupported({ codec, description });
  if (!support.supported) {
    setStatus("This browser cannot decode the iPhone video stream.", "warn");
    return;
  }

  let needsResync = false;
  let autoRestartUsed = false;

  const buildDecoder = () =>
    new VideoDecoder({
      output: (frame) => {
        // Fix MediaFoundation 720px height bug on Windows & crop 5px right/20px bottom padding
        const active = getActiveDimensions(frame.codedWidth || frame.displayWidth, frame.codedHeight || frame.displayHeight);
        let frameToDraw = frame;
        try {
          frameToDraw = new VideoFrame(frame, {
            visibleRect: { x: 0, y: 0, width: active.width, height: active.height },
            displayWidth: active.width,
            displayHeight: active.height,
          });
          frame.close();
        } catch (_) {
          frameToDraw = frame;
        }

        if (pendingFrame) {
          try {
            pendingFrame.close();
          } catch (_) {}
        }
        pendingFrame = frameToDraw;
        if (!rafScheduled) {
          rafScheduled = true;
          requestAnimationFrame(drawPending);
        }
        frameCount += 1;
      },
      error: () => {
        needsResync = true;
        // Yêu cầu iPhone gửi lại IDR Keyframe ngay lập tức, tránh restart stream làm chập chờn Wi-Fi
        fetch(`${MIRROR}/pli`, { method: "POST", cache: "no-store" }).catch(() => {});
      },
    });

  let decoder = buildDecoder();
  decoder.configure(decoderConfig);
  setStatus("Waiting for video…");

  let resp = null;
  const streamDelays = [300, 600, 1200, 2000, 3000, 5000, 8000, 10000];
  for (let a = 0; a <= streamDelays.length; a++) {
    if (generation !== runGeneration) return;
    if (a > 0) {
      setStatus(`Waiting for video stream (retry ${a})…`);
      await new Promise((r) => setTimeout(r, streamDelays[a - 1]));
      if (a === 2) {
        await fetch(`${MIRROR}/pli`, { method: "POST", cache: "no-store" }).catch(() => {});
      }
    }
    try {
      const r = await fetch(`${MIRROR}/stream.bin`, { cache: "no-store" });
      if (r.ok) {
        resp = r;
        // Yêu cầu PLI ngay lập tức khi vừa kết nối /stream.bin để iPhone phát IDR đầu tiên trong 50ms
        fetch(`${MIRROR}/pli`, { method: "POST", cache: "no-store" }).catch(() => {});
        break;
      }
      if (r.status === 503) {
        setStatus("Stream starting on the iPhone…");
        continue;
      }
      setStatus(`Stream error (HTTP ${r.status})`, "warn");
    } catch (err) {
      setStatus(err.message || "Stream unreachable", "warn");
    }
  }
  if (!resp || generation !== runGeneration) {
    setStatus("Cannot reach the video stream. Press Restart screen.", "warn");
    return;
  }

  const reader = resp.body.getReader();
  abortStream = () => reader.cancel().catch(() => {});
  let buf = new Uint8Array(0);
  let timestamp = 0;
  let gotKey = false;

  while (generation === runGeneration) {
    const { value, done } = await reader.read();
    if (done) break;
    const merged = new Uint8Array(buf.length + value.length);
    merged.set(buf);
    merged.set(value, buf.length);
    buf = merged;
    while (buf.length >= 4) {
      const len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
      if (buf.length < 4 + len) break;
      const type = buf[4];
      const data = buf.slice(5, 4 + len);
      buf = buf.subarray(4 + len);

      if (type === 2) {
        try {
          decoder.close();
        } catch (_) {}
        decoder = buildDecoder();
        decoder.configure(decoderConfig);
        needsResync = false;
        gotKey = true;
      } else if (type === 0) {
        gotKey = true;
        if (needsResync) {
          try {
            decoder.close();
          } catch (_) {}
          decoder = buildDecoder();
          decoder.configure(decoderConfig);
          needsResync = false;
        }
      }
      if (!gotKey || needsResync) continue;
      if (decoder.state !== "configured") {
        try {
          decoder.close();
        } catch (_) {}
        decoder = buildDecoder();
        decoder.configure(decoderConfig);
        needsResync = true;
        continue;
      }
      try {
        decoder.decode(
          new EncodedVideoChunk({
            type: type === 0 || type === 2 ? "key" : "delta",
            timestamp,
            data,
          }),
        );
        timestamp += 16666;
      } catch (_) {
        needsResync = true;
      }
    }
  }
  if (generation === runGeneration) {
    setStatus("Stream interrupted — reconnecting…");
    await new Promise((r) => setTimeout(r, 600));
    if (generation === runGeneration) {
      beginRun();
    }
  }
}

window.isStreamActive = () => frameCount > 0;

function beginRun() {
  runGeneration += 1;
  const generation = runGeneration;
  if (abortStream) {
    abortStream();
    abortStream = null;
  }
  if (pendingFrame) {
    try {
      pendingFrame.close();
    } catch (_) {}
    pendingFrame = null;
  }
  if (lastFrame) {
    try {
      lastFrame.close();
    } catch (_) {}
    lastFrame = null;
  }
  frameCount = 0;
  run(generation).catch((err) => {
    setStatus(err.message || "Mirror failed", "warn");
  });
}

beginRun();
