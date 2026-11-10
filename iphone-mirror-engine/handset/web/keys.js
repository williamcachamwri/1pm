// Forwards the PC keyboard to the iPhone as a hardware keyboard. Keys are
// sent by physical position (KeyboardEvent.code -> HID usage); the iPhone's
// own keyboard layout decides which character is typed.
(function () {
  const CODE_TO_HID = (() => {
    const m = {};
    for (let i = 0; i < 26; i++) m[`Key${String.fromCharCode(65 + i)}`] = 0x04 + i;
    for (let i = 1; i <= 9; i++) m[`Digit${i}`] = 0x1d + i;
    m.Digit0 = 0x27;
    Object.assign(m, {
      Enter: 0x28, NumpadEnter: 0x28, Escape: 0x29, Backspace: 0x2a, Tab: 0x2b, Space: 0x2c,
      Minus: 0x2d, Equal: 0x2e, BracketLeft: 0x2f, BracketRight: 0x30, Backslash: 0x31,
      Semicolon: 0x33, Quote: 0x34, Backquote: 0x35, Comma: 0x36, Period: 0x37, Slash: 0x38,
      IntlBackslash: 0x64, CapsLock: 0x39, Delete: 0x4c, Home: 0x4a, End: 0x4d,
      PageUp: 0x4b, PageDown: 0x4e,
      ArrowRight: 0x4f, ArrowLeft: 0x50, ArrowDown: 0x51, ArrowUp: 0x52,
      ShiftLeft: 0xe1, ShiftRight: 0xe5, AltLeft: 0xe2, AltRight: 0xe6,
      ControlLeft: 0xe0, ControlRight: 0xe4,
    });
    return m;
  })();

  // Ctrl (or Win/Cmd) + these keys becomes Cmd + key on the iPhone, so
  // select-all, copy, paste, cut and undo work inside iOS text fields.
  // Every other Ctrl shortcut stays with the browser.
  const CMD = 0xe3;
  const CMD_KEYS = new Set(["KeyA", "KeyC", "KeyV", "KeyX", "KeyZ"]);

  function isTextField(el) {
    return !!el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
  }

  function attach({ post, enabled }) {
    const pressed = new Set();
    let chain = Promise.resolve();

    const flush = () => {
      const snapshot = [...pressed];
      chain = chain.then(() => post({ usages: snapshot })).catch(() => {});
    };
    const releaseAll = () => {
      if (!pressed.size) return;
      pressed.clear();
      flush();
    };

    // Paste event handler: Đọc clipboard của Windows/UniKey, đồng bộ sang iPhone rồi phát lệnh Cmd+V
    window.addEventListener(
      "paste",
      async (e) => {
        if (!enabled() || isTextField(e.target)) return;
        e.preventDefault();
        e.stopPropagation();
        const text = e.clipboardData?.getData("text/plain");
        if (text) {
          try {
            await fetch("/mirror/clipboard", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ text }),
            });
            await post({ usages: [CMD, 0x19] });
            await new Promise((r) => setTimeout(r, 45));
            await post({ usages: [] });
          } catch (_) {}
        }
      },
      true,
    );

    window.addEventListener(
      "keydown",
      (e) => {
        if (!enabled() || isTextField(e.target)) return;
        const shortcut = e.ctrlKey || e.metaKey;

        // Đối với tổ hợp phím dán (Ctrl+V hoặc Cmd+V): Không chặn preventDefault
        // để trình duyệt kích hoạt sự kiện "paste", giúp đồng bộ dữ liệu clipboard Windows sang iPhone trước khi dán.
        if (shortcut && e.code === "KeyV") {
          return;
        }

        if (shortcut && !CMD_KEYS.has(e.code)) return;
        const usage = CODE_TO_HID[e.code];
        if (usage === undefined) return;
        e.preventDefault();
        e.stopPropagation();
        if (e.repeat) return;
        if (shortcut) pressed.add(CMD);
        pressed.add(usage);
        flush();
      },
      true,
    );

    window.addEventListener(
      "keyup",
      (e) => {
        let changed = false;
        if (pressed.has(CMD) && !e.ctrlKey && !e.metaKey) {
          pressed.delete(CMD);
          changed = true;
        }
        const usage = CODE_TO_HID[e.code];
        if (usage !== undefined && pressed.delete(usage)) {
          e.preventDefault();
          changed = true;
        }
        if (changed) flush();
      },
      true,
    );

    window.addEventListener("blur", releaseAll);
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState !== "visible") releaseAll();
    });
    return { releaseAll };
  }

  window.HandsetKeys = { attach };
})();
