# Dynamic sky — assets and licences

Everything the shader needs by default is generated in-project:

- `dynamic_sky_cloud_shape.tres`, `dynamic_sky_cloud_detail.tres` — Godot `NoiseTexture2D`
  resources (FastNoiseLite, seamless). No third-party content.
- `dynamic_sky_cloud_volume_shape.tres`, `dynamic_sky_cloud_volume_detail.tres` —
  generated Godot `NoiseTexture3D` resources for volumetric cloud density.
- `dynamic_cloud_density.gdshaderinc` shares density sampling between sky and cloud shadows;
  `dynamic_cloud_layers.gdshaderinc` shares world-space 2D cloud sampling;
  `dynamic_cloud_shadow_density.gdshader` generates the GPU shadow mask. No third-party content.
- Sun, moon (with phase), stars and lightning are procedural in `dynamic_sky.gdshader`.

Optional textures the shader has slots for (`moon_use_texture` / `moon_texture`,
`star_use_texture` / `star_texture`). None are downloaded or checked in; all are free to use:

| Asset | Source | Licence | Notes |
| --- | --- | --- | --- |
| Moon colour map (LRO WAC, 2k–8k TIFF/EXR) | NASA SVS "CGI Moon Kit" https://svs.gsfc.nasa.gov/4720 | Public domain (NASA) | Equirectangular; sample with the shader's lat/long mapping. 2k TIFF → PNG is plenty. |
| Deep Star Maps 2020 (4k EXR 4096×2048 and up) | NASA SVS https://svs.gsfc.nasa.gov/4851 | Public domain (NASA) | Equirectangular star sky; convert to 8-bit PNG, 2048×1024 is enough for a sky. |
| Kenney Particle Pack (80 sprites: drops, flakes, smoke, flares) | https://kenney.nl/assets/particle-pack | CC0 | Only needed if the weather particle quads get textured sprites later. |
| Sky3D (Godot 4 day/night addon) | https://github.com/TokisanGames/Sky3D | MIT (bundled milky-way texture needs its own credit) | Reference implementation only; nothing copied. |
| Mahamaya Sky Shader (Godot 4 procedural sky) | https://github.com/MahamayaGames-5/Mahamaya-Sky-Shader | see repo | Reference only; nothing copied. |

NASA SVS policy: "All of our content is in the public domain (unless otherwise noted)".
