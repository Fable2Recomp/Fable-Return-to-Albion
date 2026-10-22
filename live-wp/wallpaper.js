/**
 * Refii - Warmup Medallion Live Wallpaper
 * Powered by WebGL with automatic HTML5 2D Canvas fail-safe.
 * Supports Wallpaper Engine, Lively Wallpaper, and modern browsers.
 */

(function () {
	"use strict";

	// --- Configuration State ---
	const config = {
		pulseSpeed: 1.8,
		glowIntensity: 0.3,
		smokeSpeed: 0.010,
		smokeReach: 1.0,
		smokeBrightness: 1.0,
		scaleMode: 1, // 0 = Cover (fill screen), 1 = Contain (fit entire circle)
		sealScale: 0.76, // 76% height matches the in-game warmup screen padding
		playMusic: false,
		musicVolume: 0.3,
	};

	let sealWidth = 2000.0;
	let sealHeight = 2000.0;
	let startTime = performance.now();

	const canvas = document.getElementById("wallpaper-canvas");
	const audio = document.getElementById("ambient-audio");

	// --- Audio State Handling ---
	function updateAudio() {
		if (!audio) return;
		try {
			audio.volume = Math.max(0, Math.min(1, config.musicVolume));
			if (config.playMusic) {
				audio.play().catch(function () {});
			} else {
				audio.pause();
			}
		} catch (e) {
			console.warn("[Refii WP] Audio update error:", e);
		}
	}

	// --- Standalone Browser HUD References ---
	const hud = document.getElementById("browser-hud");
	const hudToggleBtn = document.getElementById("hud-toggle-btn");
	const sliderPulse = document.getElementById("slider-pulse");
	const sliderGlow = document.getElementById("slider-glow");
	const sliderSmoke = document.getElementById("slider-smoke");
	const sliderBrightness = document.getElementById("slider-brightness");
	const sliderSize = document.getElementById("slider-size");
	const selectFit = document.getElementById("select-fit");
	const checkMusic = document.getElementById("check-music");
	const sliderVolume = document.getElementById("slider-volume");

	function toggleHud() {
		if (hud) hud.classList.toggle("hud-hidden");
	}

	if (hudToggleBtn) {
		hudToggleBtn.addEventListener("click", toggleHud);
	}

	function syncHudControls() {
		if (sliderPulse) sliderPulse.value = config.pulseSpeed;
		if (sliderGlow) sliderGlow.value = config.glowIntensity;
		if (sliderSmoke) sliderSmoke.value = config.smokeSpeed;
		if (sliderBrightness) sliderBrightness.value = config.smokeBrightness;
		if (sliderSize) sliderSize.value = config.sealScale;
		if (selectFit)
			selectFit.value = config.scaleMode === 1 ? "contain" : "cover";
		if (checkMusic) checkMusic.checked = config.playMusic;
		if (sliderVolume)
			sliderVolume.value = Math.round(config.musicVolume * 100);
	}

	if (sliderPulse) {
		sliderPulse.addEventListener("input", function (e) {
			config.pulseSpeed = parseFloat(e.target.value);
		});
	}
	if (sliderGlow) {
		sliderGlow.addEventListener("input", function (e) {
			config.glowIntensity = parseFloat(e.target.value);
		});
	}
	if (sliderSmoke) {
		sliderSmoke.addEventListener("input", function (e) {
			config.smokeSpeed = parseFloat(e.target.value);
		});
	}
	if (sliderBrightness) {
		sliderBrightness.addEventListener("input", function (e) {
			config.smokeBrightness = parseFloat(e.target.value);
		});
	}
	if (sliderSize) {
		sliderSize.addEventListener("input", function (e) {
			config.sealScale = parseFloat(e.target.value);
		});
	}
	if (selectFit) {
		selectFit.addEventListener("change", function (e) {
			config.scaleMode = e.target.value === "contain" ? 1 : 0;
		});
	}
	if (checkMusic) {
		checkMusic.addEventListener("change", function (e) {
			config.playMusic = e.target.checked;
			updateAudio();
		});
	}
	if (sliderVolume) {
		sliderVolume.addEventListener("input", function (e) {
			config.musicVolume = parseFloat(e.target.value) / 100.0;
			updateAudio();
		});
	}

	// Keyboard shortcuts
	window.addEventListener("keydown", function (e) {
		if (e.key === "h" || e.key === "H") {
			toggleHud();
		} else if (e.key === "f" || e.key === "F") {
			if (!document.fullscreenElement) {
				document.documentElement
					.requestFullscreen()
					.catch(function () {});
			} else {
				document.exitFullscreen().catch(function () {});
			}
		} else if (e.key === "m" || e.key === "M") {
			config.playMusic = !config.playMusic;
			updateAudio();
			syncHudControls();
		}
	});

	window.addEventListener(
		"click",
		function () {
			if (config.playMusic && audio && audio.paused) {
				audio.play().catch(function () {});
			}
		},
		{ once: true },
	);

	// --- Wallpaper Engine Integration (Registered in Global Scope) ---
	window.wallpaperPropertyListener = {
		applyUserProperties: function (properties) {
			if (!properties) return;
			if (properties.pulse_speed) {
				config.pulseSpeed = properties.pulse_speed.value;
			}
			if (properties.glow_intensity) {
				config.glowIntensity = properties.glow_intensity.value;
			}
			if (properties.smoke_speed) {
				config.smokeSpeed = properties.smoke_speed.value;
			}
			if (properties.smoke_reach) {
				config.smokeReach = properties.smoke_reach.value;
			}
			if (properties.smokereach) {
				config.smokeReach = properties.smokereach.value / 100.0;
			}
			if (properties.smoke_brightness) {
				config.smokeBrightness = properties.smoke_brightness.value;
			}
			if (properties.scale_mode) {
				config.scaleMode =
					properties.scale_mode.value === "contain" ? 1 : 0;
			}
			if (properties.seal_scale) {
				config.sealScale = properties.seal_scale.value / 100.0;
			}
			if (properties.play_music) {
				config.playMusic = properties.play_music.value;
				updateAudio();
			}
			if (properties.music_volume) {
				config.musicVolume = properties.music_volume.value / 100.0;
				updateAudio();
			}
			syncHudControls();
		},
	};

	// --- Lively Wallpaper Integration ---
	window.livelyPropertyListener = function (name, val) {
		switch (name) {
			case "pulse_speed":
				config.pulseSpeed = val;
				break;
			case "glow_intensity":
				config.glowIntensity = val;
				break;
			case "smoke_speed":
				config.smokeSpeed = val;
				break;
			case "smoke_reach":
				config.smokeReach = val;
				break;
			case "smoke_brightness":
				config.smokeBrightness = val;
				break;
			case "scale_mode":
				config.scaleMode = val === "contain" ? 1 : 0;
				break;
			case "seal_scale":
				config.sealScale = val / 100.0;
				break;
			case "play_music":
				config.playMusic = val;
				updateAudio();
				break;
			case "music_volume":
				config.musicVolume = val / 100.0;
				updateAudio();
				break;
		}
		syncHudControls();
	};

	// =========================================================================
	// 2D CANVAS FALLBACK RENDERER
	// Activated automatically if WebGL context or shader fails to initialize.
	// =========================================================================
	function runCanvas2DFallback() {
		console.warn("[Refii WP] WebGL unavailable. Running 2D Canvas Fallback.");
		const ctx = canvas.getContext("2d");
		if (!ctx) return;

		const sealImg = new Image();
		sealImg.src = "assets/seal.png";

		function resize2D() {
			const w = Math.min(window.innerWidth || 1920, 3840);
			const h = Math.min(window.innerHeight || 1080, 2160);
			if (canvas.width !== w || canvas.height !== h) {
				canvas.width = w;
				canvas.height = h;
			}
		}

		window.addEventListener("resize", resize2D);
		resize2D();

		function render2D(now) {
			const t = (now - startTime) * 0.001;
			const w = canvas.width;
			const h = canvas.height;
			const aspect = w / h;

			// Background
			ctx.fillStyle = "#000000";
			ctx.fillRect(0, 0, w, h);

			// Blue top lighting
			const blueX = w * 0.5 + Math.sin(t * config.smokeSpeed * 10.0) * (w * 0.05);
			const blueGrad = ctx.createRadialGradient(blueX, 0, 10, blueX, 0, h * 0.85);
			blueGrad.addColorStop(0, `rgba(60, 150, 255, ${0.35 * config.smokeBrightness})`);
			blueGrad.addColorStop(0.5, `rgba(20, 80, 200, ${0.18 * config.smokeBrightness})`);
			blueGrad.addColorStop(1, "rgba(0, 0, 0, 0)");
			ctx.fillStyle = blueGrad;
			ctx.fillRect(0, 0, w, h);

			// Red bottom lighting
			const redX = w * 0.5 - Math.sin(t * config.smokeSpeed * 8.0) * (w * 0.05);
			const redGrad = ctx.createRadialGradient(redX, h, 10, redX, h, h * 0.85);
			redGrad.addColorStop(0, `rgba(255, 60, 80, ${0.35 * config.smokeBrightness})`);
			redGrad.addColorStop(0.5, `rgba(180, 20, 40, ${0.18 * config.smokeBrightness})`);
			redGrad.addColorStop(1, "rgba(0, 0, 0, 0)");
			ctx.fillStyle = redGrad;
			ctx.fillRect(0, 0, w, h);

			// Seal Rendering
			if (sealImg.complete && sealImg.naturalWidth > 0) {
				const sw = sealImg.naturalWidth;
				const sh = sealImg.naturalHeight;
				const baseScale = config.scaleMode === 0
					? Math.max(w / sw, h / sh)
					: Math.min(w / sw, h / sh);
				const scale = baseScale * config.sealScale;
				const dw = sw * scale;
				const dh = sh * scale;
				const dx = (w - dw) * 0.5;
				const dy = (h - dh) * 0.5;

				const pulse = 0.88 + 0.12 * Math.sin(t * config.pulseSpeed) * config.glowIntensity;
				ctx.filter = `brightness(${pulse})`;
				ctx.drawImage(sealImg, dx, dy, dw, dh);
				ctx.filter = "none";
			}

			requestAnimationFrame(render2D);
		}

		requestAnimationFrame(render2D);
	}

	// =========================================================================
	// WEBGL SHADER PIPELINE
	// =========================================================================
	const VS_SOURCE = `
    attribute vec2 a_position;
    void main() {
      gl_Position = vec4(a_position, 0.0, 1.0);
    }
  `;

	const FS_SOURCE = `
    precision mediump float;

    uniform vec2 u_resolution;
    uniform float u_time;
    uniform vec2 u_seal_size;
    uniform int u_scale_mode;
    uniform float u_seal_scale;

    uniform float u_pulse_speed;
    uniform float u_glow_intensity;
    uniform float u_smoke_speed;
    uniform float u_smoke_reach;
    uniform float u_smoke_brightness;

    uniform sampler2D u_noise;
    uniform sampler2D u_seal;

    const float TILT_ANGLE_DEG = 15.0;

    const vec3 BLUE_SMOKE_COLOR = vec3(0.12, 0.40, 0.85);
    const vec3 BLUE_LIGHT_CORE  = vec3(0.40, 0.75, 1.00);
    const vec2 BLUE_LIGHT_POS   = vec2(0.5, -0.05);
    const float BLUE_LIGHT_RADIUS = 0.85;
    const float BLUE_INTENSITY  = 1.35;

    const vec3 RED_SMOKE_COLOR  = vec3(0.85, 0.12, 0.25);
    const vec3 RED_LIGHT_CORE   = vec3(1.00, 0.40, 0.45);
    const vec2 RED_LIGHT_POS    = vec2(0.5, 1.05);
    const float RED_LIGHT_RADIUS  = 0.85;
    const float RED_INTENSITY   = 1.35;

    const vec3 VOID_COLOR       = vec3(0.0, 0.0, 0.0);

    const float DISTORTION_INTENSITY = 0.5;
    const float CENTER_CLEAR_RADIUS   = 0.30;
    const float CENTER_CLEAR_SOFTNESS = 0.80;

    const float MIN_PULSE = 0.5;
    const float MAX_PULSE = 1.0;
    const float PHASE_OFFSET = 0.0;

    const float BLUE_SENSITIVITY = 1.0;
    const float RED_SENSITIVITY  = 1.35;

    const vec3 BLUE_GLOW_TINT = vec3(0.30, 0.75, 1.0);
    const vec3 RED_GLOW_TINT  = vec3(1.00, 0.25, 0.35);

    vec2 rotate2D(vec2 p, float rad) {
      float c = cos(rad);
      float s = sin(rad);
      return vec2(p.x * c - p.y * s, p.x * s + p.y * c);
    }

    float detectBlue(vec4 c) {
      float cyanBias = (c.b + c.g) - (c.r * 1.75);
      float blueDom = c.b - c.r;
      float val = smoothstep(0.06, 0.35, cyanBias) *
                  smoothstep(0.0, 0.20, blueDom) *
                  smoothstep(0.24, 0.60, c.b);
      return clamp(val * BLUE_SENSITIVITY, 0.0, 1.0) * step(0.05, c.a);
    }

    float detectRed(vec4 c) {
      float redOverGreen = c.r - c.g;
      float notGold = 1.0 - smoothstep(0.15, 0.32, c.g - c.b);
      float notPurple = 1.0 - smoothstep(0.05, 0.25, c.b - c.r);
      float val = smoothstep(0.10, 0.30, redOverGreen) *
                  smoothstep(0.26, 0.58, c.r) *
                  notGold *
                  notPurple;
      return clamp(val * RED_SENSITIVITY, 0.0, 1.0) * step(0.05, c.a);
    }

    void main() {
      // Screen coordinates centered at medallion, normalized by height.
      // 1 unit in X = 1 unit in Y = 1 screen height (completely isotropic).
      // This keeps lighting, smoke scale, and center clearing locked across any aspect ratio.
      vec2 centerCoord = vec2((gl_FragCoord.x - u_resolution.x * 0.5) / u_resolution.y,
                              (u_resolution.y * 0.5 - gl_FragCoord.y) / u_resolution.y);

      // 1. SMOKE BACKGROUND PASS
      // True rigid rotation around medallion center
      float angleRad = radians(TILT_ANGLE_DEG);
      vec2 tilted = rotate2D(centerCoord, angleRad);

      // Isotropic base coordinate for seamless noise scrolling
      vec2 baseUv = centerCoord + vec2(100.0);

      vec2 flowA = vec2(u_time * u_smoke_speed * 0.6, u_time * u_smoke_speed * 0.4);
      vec2 flowB = vec2(-u_time * u_smoke_speed * 0.35, u_time * u_smoke_speed * 0.55);
      vec2 flowC = vec2(u_time * u_smoke_speed * 0.2, -u_time * u_smoke_speed * 0.3);

      float warpX = texture2D(u_noise, baseUv + flowA).r;
      float warpY = texture2D(u_noise, baseUv + flowB).r;
      vec2 warpedUv = baseUv + vec2(warpX - 0.5, warpY - 0.5) * DISTORTION_INTENSITY;

      float smoke1 = texture2D(u_noise, warpedUv + flowC).r;
      float smoke2 = texture2D(u_noise, (warpedUv * 1.55) - flowA).r;
      float rawDensity = (smoke1 * 0.6) + (smoke2 * 0.4);
      float smokeDensity = smoothstep(0.26, 0.72, rawDensity);

      // Directional lighting locked to medallion in height units (invariant across 16:9, 21:9, 32:9)
      float lightRadius = BLUE_LIGHT_RADIUS * u_smoke_reach;
      vec2 diffBlue = vec2(tilted.x * 0.65, tilted.y + 0.55);
      float distBlue = length(diffBlue);
      float blueFactor = smoothstep(0.0, 1.0, clamp(1.0 - (distBlue / lightRadius), 0.0, 1.0));
      vec3 blueLit = mix(BLUE_SMOKE_COLOR, BLUE_LIGHT_CORE * u_smoke_brightness, pow(blueFactor, 1.8));

      vec2 diffRed = vec2(tilted.x * 0.65, tilted.y - 0.55);
      float distRed = length(diffRed);
      float redFactor = smoothstep(0.0, 1.0, clamp(1.0 - (distRed / lightRadius), 0.0, 1.0));
      vec3 redLit = mix(RED_SMOKE_COLOR, RED_LIGHT_CORE * u_smoke_brightness, pow(redFactor, 1.8));

      vec3 smokeIllumination = (blueLit * (blueFactor * BLUE_INTENSITY * u_smoke_brightness)) +
                               (redLit * (redFactor * RED_INTENSITY * u_smoke_brightness));
      float totalLight = max(blueFactor * BLUE_INTENSITY, redFactor * RED_INTENSITY);

      // Center clearing mask: locked to circular medallion
      float centerDist = length(centerCoord);
      float clearRadius = CENTER_CLEAR_RADIUS / max(0.5, u_smoke_reach);
      float centerMask = smoothstep(clearRadius, clearRadius + CENTER_CLEAR_SOFTNESS, centerDist);

      float finalAlpha = smokeDensity * clamp(totalLight, 0.0, 1.0) * centerMask;
      vec3 bgColor = mix(VOID_COLOR, smokeIllumination, finalAlpha);

      // 2. MEDALLION PASS (Runes + Bloom + Pulse)
      vec2 pixelCoord = vec2(gl_FragCoord.x, u_resolution.y - gl_FragCoord.y);
      vec2 center = u_resolution * 0.5;

      float baseScale;
      if (u_scale_mode == 0) {
        baseScale = max(u_resolution.x / max(1.0, u_seal_size.x), u_resolution.y / max(1.0, u_seal_size.y));
      } else {
        baseScale = min(u_resolution.x / max(1.0, u_seal_size.x), u_resolution.y / max(1.0, u_seal_size.y));
      }

      float scale = baseScale * u_seal_scale;
      vec2 drawnSize = u_seal_size * scale;
      vec2 sealUv = (pixelCoord - center) / max(vec2(1.0), drawnSize) + vec2(0.5);

      // Branchless boundary check
      float inBounds = step(0.0, sealUv.x) * step(sealUv.x, 1.0) * step(0.0, sealUv.y) * step(sealUv.y, 1.0);
      vec2 clampedUv = clamp(sealUv, 0.0, 1.0);

      vec4 base = texture2D(u_seal, clampedUv) * inBounds;

      // 8-tap bloom kernel: calibrated tight (8px) along the carved rune edges
      vec2 px = (vec2(1.0) / u_seal_size) * 8.0;
      float bBloom = 0.0;
      float rBloom = 0.0;

      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2( px.x,  0.0), 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2(-px.x,  0.0), 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2( 0.0,   px.y), 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2( 0.0,  -px.y), 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2( px.x,  px.y) * 0.7071, 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2(-px.x,  px.y) * 0.7071, 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2( px.x, -px.y) * 0.7071, 0.0, 1.0)));
      bBloom += detectBlue(texture2D(u_seal, clamp(clampedUv + vec2(-px.x, -px.y) * 0.7071, 0.0, 1.0)));

      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2( px.x,  0.0), 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2(-px.x,  0.0), 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2( 0.0,   px.y), 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2( 0.0,  -px.y), 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2( px.x,  px.y) * 0.7071, 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2(-px.x,  px.y) * 0.7071, 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2( px.x, -px.y) * 0.7071, 0.0, 1.0)));
      rBloom += detectRed(texture2D(u_seal, clamp(clampedUv + vec2(-px.x, -px.y) * 0.7071, 0.0, 1.0)));

      bBloom *= 0.125 * inBounds;
      rBloom *= 0.125 * inBounds;

      float blueVal = detectBlue(base);
      float redVal  = detectRed(base);
      float runeMask = clamp(blueVal + redVal, 0.0, 1.0);

      // Sinusoidal pulse waveforms (0.5 to 1.0, matching Godot preparation_medallion.gdshader)
      float pBlue = mix(MIN_PULSE, MAX_PULSE, 0.5 + 0.5 * sin(u_time * u_pulse_speed));
      float pRed  = mix(MIN_PULSE, MAX_PULSE, 0.5 + 0.5 * sin(u_time * u_pulse_speed + PHASE_OFFSET));

      float boost = u_glow_intensity;
      vec3 aura = (bBloom * BLUE_GLOW_TINT * pBlue + rBloom * RED_GLOW_TINT * pRed) * (0.25 * boost);

      float activeP = mix(pRed, pBlue, step(redVal, blueVal));
      vec3 litCore = (blueVal * BLUE_GLOW_TINT + redVal * RED_GLOW_TINT) * (0.28 * boost);
      vec3 runeLit = mix(base.rgb * MIN_PULSE,
                         base.rgb * 1.15 + litCore,
                         activeP / MAX_PULSE);

      vec3 finalSeal = mix(base.rgb, runeLit, runeMask) + aura * (1.0 - runeMask * 0.5);
      vec3 composite = mix(bgColor, finalSeal, base.a);
      gl_FragColor = vec4(composite, 1.0);
    }
  `;

	function createShader(gl, type, source) {
		const shader = gl.createShader(type);
		gl.shaderSource(shader, source);
		gl.compileShader(shader);
		if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
			const info = gl.getShaderInfoLog(shader);
			gl.deleteShader(shader);
			throw new Error("Shader compile error: " + info);
		}
		return shader;
	}

	function createProgram(gl, vsSource, fsSource) {
		const vs = createShader(gl, gl.VERTEX_SHADER, vsSource);
		const fs = createShader(gl, gl.FRAGMENT_SHADER, fsSource);
		const program = gl.createProgram();
		gl.attachShader(program, vs);
		gl.attachShader(program, fs);
		gl.linkProgram(program);
		if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
			const info = gl.getProgramInfoLog(program);
			gl.deleteProgram(program);
			throw new Error("Program link error: " + info);
		}
		return program;
	}

	function loadTexture(gl, assetKey, fallbackUrl, isRepeatable, onLoaded) {
		const texture = gl.createTexture();
		gl.bindTexture(gl.TEXTURE_2D, texture);

		if (isRepeatable) {
			gl.texImage2D(
				gl.TEXTURE_2D,
				0,
				gl.RGBA,
				1,
				1,
				0,
				gl.RGBA,
				gl.UNSIGNED_BYTE,
				new Uint8Array([128, 128, 128, 255]),
			);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
		} else {
			gl.texImage2D(
				gl.TEXTURE_2D,
				0,
				gl.RGBA,
				1,
				1,
				0,
				gl.RGBA,
				gl.UNSIGNED_BYTE,
				new Uint8Array([0, 0, 0, 0]),
			);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
		}

		const img = new Image();
		img.onload = function () {
			try {
				const maxTexSize = gl.getParameter(gl.MAX_TEXTURE_SIZE) || 4096;
				let uploadSource = img;

				if (img.width > maxTexSize || img.height > maxTexSize) {
					const scale = Math.min(
						maxTexSize / img.width,
						maxTexSize / img.height,
					);
					const c2d = document.createElement("canvas");
					c2d.width = Math.floor(img.width * scale);
					c2d.height = Math.floor(img.height * scale);
					const ctx = c2d.getContext("2d");
					ctx.drawImage(img, 0, 0, c2d.width, c2d.height);
					uploadSource = c2d;
				}

				gl.bindTexture(gl.TEXTURE_2D, texture);
				gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
				gl.texImage2D(
					gl.TEXTURE_2D,
					0,
					gl.RGBA,
					gl.RGBA,
					gl.UNSIGNED_BYTE,
					uploadSource,
				);

				if (isRepeatable) {
					gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
					gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
				} else {
					gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
					gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
				}
				gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
				gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

				if (onLoaded) {
					onLoaded(img);
				}
			} catch (err) {
				console.error("[Refii WP] Failed to upload texture:", err);
			}
		};

		img.onerror = function (err) {
			console.error("[Refii WP] Failed to load image asset:", err);
		};

		img.src = fallbackUrl;
		return { texture: texture, image: img };
	}

	// =========================================================================
	// BOOTSTRAP INITIALIZATION
	// =========================================================================
	let gl = null;
	try {
		gl =
			canvas.getContext("webgl", {
				alpha: false,
				depth: false,
				stencil: false,
				antialias: false,
				preserveDrawingBuffer: false,
			}) ||
			canvas.getContext("experimental-webgl", {
				alpha: false,
				depth: false,
				stencil: false,
				antialias: false,
			});
	} catch (e) {
		console.warn("[Refii WP] WebGL context request threw:", e);
	}

	if (!gl) {
		runCanvas2DFallback();
		return;
	}

	let program;
	try {
		program = createProgram(gl, VS_SOURCE, FS_SOURCE);
		gl.useProgram(program);
	} catch (err) {
		console.error("[Refii WP] WebGL program creation failed:", err);
		runCanvas2DFallback();
		return;
	}

	// Full-screen quad buffer
	const posBuffer = gl.createBuffer();
	gl.bindBuffer(gl.ARRAY_BUFFER, posBuffer);
	gl.bufferData(
		gl.ARRAY_BUFFER,
		new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]),
		gl.STATIC_DRAW,
	);

	const aPosition = gl.getAttribLocation(program, "a_position");
	gl.enableVertexAttribArray(aPosition);
	gl.vertexAttribPointer(aPosition, 2, gl.FLOAT, false, 0, 0);

	// Uniform locations
	const uResolution = gl.getUniformLocation(program, "u_resolution");
	const uTime = gl.getUniformLocation(program, "u_time");
	const uSealSize = gl.getUniformLocation(program, "u_seal_size");
	const uScaleMode = gl.getUniformLocation(program, "u_scale_mode");
	const uSealScale = gl.getUniformLocation(program, "u_seal_scale");
	const uPulseSpeed = gl.getUniformLocation(program, "u_pulse_speed");
	const uGlowIntensity = gl.getUniformLocation(program, "u_glow_intensity");
	const uSmokeSpeed = gl.getUniformLocation(program, "u_smoke_speed");
	const uSmokeReach = gl.getUniformLocation(program, "u_smoke_reach");
	const uSmokeBrightness = gl.getUniformLocation(program, "u_smoke_brightness");
	const uNoise = gl.getUniformLocation(program, "u_noise");
	const uSeal = gl.getUniformLocation(program, "u_seal");

	// Load textures
	const noiseTex = loadTexture(gl, "noise", "assets/smoke_noise.png", true);
	const sealTex = loadTexture(
		gl,
		"seal",
		"assets/seal.png",
		false,
		function (img) {
			sealWidth = img.naturalWidth || img.width || 2000.0;
			sealHeight = img.naturalHeight || img.height || 2000.0;
		},
	);

	gl.uniform1i(uNoise, 0);
	gl.uniform1i(uSeal, 1);
	gl.uniform2f(uSealSize, sealWidth, sealHeight);

	// Resize Handler
	function resize() {
		const width = Math.min(window.innerWidth || 1920, 3840);
		const height = Math.min(window.innerHeight || 1080, 2160);

		if (canvas.width !== width || canvas.height !== height) {
			canvas.width = width;
			canvas.height = height;
			gl.viewport(0, 0, width, height);
		}
	}

	window.addEventListener("resize", resize);
	resize();

	// Render loop
	function render(now) {
		const elapsed = (now - startTime) * 0.001;

		gl.uniform2f(uResolution, canvas.width, canvas.height);
		gl.uniform1f(uTime, elapsed);
		gl.uniform2f(uSealSize, sealWidth, sealHeight);
		gl.uniform1i(uScaleMode, config.scaleMode);
		gl.uniform1f(uSealScale, config.sealScale);
		gl.uniform1f(uPulseSpeed, config.pulseSpeed);
		gl.uniform1f(uGlowIntensity, config.glowIntensity);
		gl.uniform1f(uSmokeSpeed, config.smokeSpeed);
		gl.uniform1f(uSmokeReach, config.smokeReach);
		gl.uniform1f(uSmokeBrightness, config.smokeBrightness);

		gl.activeTexture(gl.TEXTURE0);
		gl.bindTexture(gl.TEXTURE_2D, noiseTex.texture);

		gl.activeTexture(gl.TEXTURE1);
		gl.bindTexture(gl.TEXTURE_2D, sealTex.texture);

		gl.drawArrays(gl.TRIANGLES, 0, 6);
		requestAnimationFrame(render);
	}

	requestAnimationFrame(render);
	syncHudControls();
})();
