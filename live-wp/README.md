# Refii - Warmup Medallion Live Wallpaper

A real-time WebGL live wallpaper replicating the Fable II / Refii shader preparation warmup screen, complete with:
- Procedural scrolling domain-warped volumetric smoke with directional blue and red illumination.
- Color-separated Will/Skill (blue) and Strength (red) rune glow with dynamic out-of-phase sine wave pulsing.
- Soft 8-tap bloom radiating from the runes.
- High-resolution 4K medallion artwork with responsive framing (`cover` or `contain` modes).
- Dynamic custom user properties (pulse speed, glow intensity, smoke speed, brightness, audio volume).

---

## 🖥️ Supported Platforms & Wallpaper Apps

### 1. Windows: Wallpaper Engine (Steam)
1. Open **Wallpaper Engine**.
2. Click **Wallpaper Editor** (or bottom-left *Open Wallpaper* -> *Open from file*).
3. Select `project.json` inside this `live-wp` folder, or drag and drop the `live-wp` folder into the Wallpaper Editor.
4. From the editor, click **File** -> **Apply Wallpaper**.
5. You can now customize Rune Pulse Speed, Glow, Smoke, and Music directly in Wallpaper Engine's properties panel.
6. (Optional) To share with others, use **Workshop** -> **Publish to Steam Workshop**.

### 2. Windows: Lively Wallpaper (Open Source / Free)
1. Open **Lively Wallpaper**.
2. Click the **+ (Add Wallpaper)** button.
3. Browse and select either `index.html` or drag the entire `live-wp` directory into Lively.
4. Customize settings directly via Lively's *Customise Wallpaper* panel.

### 3. Linux Desktop (linux-wallpaperengine / Komorebi / WebKit)
- **Using `linux-wallpaperengine`**:
  ```bash
  linux-wallpaperengine --assets-dir /path/to/live-wp
  ```
- **Using any web/HTML5 wallpaper runner** (e.g., `komorebi`, `swww` + webview, or Electron background):
  Simply point the runner to `index.html`.

### 4. Android Mobile
- **Method A (Recommended via Wallpaper Engine Mobile)**:
  1. Install the free **Wallpaper Engine** companion app on your Android device from Google Play or F-Droid/GitHub.
  2. In Wallpaper Engine on your PC, click **Mobile** and pair with your phone over local Wi-Fi.
  3. Send this wallpaper to your mobile device with one click!
  4. The wallpaper automatically adapts to portrait phone aspect ratios (19.5:9, 20:9) with centered medallion framing.
- **Method B (Standalone Web / Live Wallpaper app)**:
  - Any Android HTML5 live wallpaper wrapper app from Google Play (such as *Web Live Wallpaper*) can load this folder directly.

### 5. Web Browser / Standalone Preview
- Double-click or open `index.html` in any modern web browser (Edge, Chrome, Firefox, Safari).
- **Keyboard Shortcuts**:
  - `H`: Toggle configuration HUD panel.
  - `F`: Toggle Fullscreen.
- Alternatively, host on GitHub Pages or any static file server for instant browser viewing.

---

## 📁 File Structure
```
live-wp/
├── assets/
│   ├── seal.png               # High-res (4444x2500) medallion texture
│   └── smoke_noise.png        # Seamless 512x512 procedural simplex noise
├── index.html                 # Canvas & document structure
├── style.css                  # Responsive full-screen styling & browser HUD
├── wallpaper.js               # WebGL engine, GLSL shaders & property listeners
├── project.json               # Wallpaper Engine package descriptor
├── LivelyInfo.json            # Lively Wallpaper descriptor
├── LivelyProperties.json      # Lively Wallpaper user properties
├── preview.jpg                # Thumbnail preview image
└── README.md                  # Documentation & usage guide
```
