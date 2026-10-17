using System;
using System.Text.RegularExpressions;

namespace Refii.Models;

public static class Engine_ClassicWeather
{
    public static bool Supports(string pixelEntry, int pass) => pass == 0 &&
        (pixelEntry.StartsWith("PSHADER_STANDARDMATERIAL", StringComparison.Ordinal) ||
         pixelEntry.StartsWith("PSHADER_LANDSCAPEMATERIAL", StringComparison.Ordinal) ||
         pixelEntry.StartsWith("PSHADER_TRUNK", StringComparison.Ordinal) ||
         pixelEntry.StartsWith("PSHADER_GRASS", StringComparison.Ordinal));

    public static string Adapt(string source)
    {
        const string include = "#include \"res://src/Engine/level/environment/dynamic/dynamic_surface_weather.gdshaderinc\"\n" +
            "varying vec3 ds_world;\nvarying vec3 ds_normal;\nvarying float ds_exposure;\n";
        string capture = "\nds_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n" +
            "ds_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);\nds_exposure = surface_exposure(ds_world);\n";
        source = Regex.Replace(source, @"void\s+vertex\s*\(\s*\)\s*\{", match => match.Value + capture);
        source = source.Replace("#define XR_COLOUR_OUT(c)", "#define DS_BASE_COLOUR(c)", StringComparison.Ordinal);
        Match original = Regex.Match(source, @"#define DS_BASE_COLOUR\(c\)[^\r\n]*");
        string baseColour = original.Success ? original.Value : "#define DS_BASE_COLOUR(c) clamp((c).rgb, vec3(0), vec3(31.875))";
        if (original.Success) source = source.Remove(original.Index, original.Length);
        string wrapper = include + baseColour + "\n" +
            "#define XR_COLOUR_OUT(c) surface_classic(DS_BASE_COLOUR(c), ds_world, normalize(ds_normal), " +
            "normalize(CAMERA_POSITION_WORLD - ds_world), ds_exposure)\n";
        int mode = source.IndexOf("render_mode ", StringComparison.Ordinal);
        int end = source.IndexOf(';', mode) + 1;
        return source.Insert(end, "\n" + wrapper);
    }
}
